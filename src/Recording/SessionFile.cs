using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UltraAttempts.Recording
{
    // What a session file says about itself: the level, and V1's size there (for the ghosts).
    public sealed class SessionHeader
    {
        public long CreatedUtcTicks;
        public string Scene = "";
        public string LevelName = "";
        public int Difficulty;
        public float CapsuleHeight = 3.5f;
        public float CapsuleRadius = 0.5f;
        public float CapsuleCenterY;
    }

    public sealed class SessionInfo
    {
        public string Path;
        public SessionHeader Header;
        public int Attempts;
        public bool Complete;
        public bool Closed;         // ended without a win (the attempt count was reset)
        public float FinishLevelTime;
        // Where the readable part of the file ends (a crash can leave half a block after it).
        public long ValidLength;
    }

    public sealed class SessionData
    {
        public SessionInfo Info;
        public List<RawAttempt> Attempts = new List<RawAttempt>();
    }

    // A session ("run") is every attempt at one level, from the first try until the level is
    // finished, in one file: a header, then blocks appended as attempts end:
    //   "UKAT" u16 version, header fields
    //   block: u8 type, i32 length, payload
    //     1 Attempt   payload = AttemptCodec (each attempt has its own format version, so a run begun
    //                 with an older UltraAttempts carries on with newer attempts in the same file)
    //     2 Complete  payload = i32 winning attempt, f32 level time, i64 utc ticks
    //     3 Closed    payload = i64 utc ticks: the run ended without a win (its attempt count was reset)
    public static class SessionFile
    {
        public const string Extension = ".ukattempts";
        private const ushort FormatVersion = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("UKAT");

        public const byte BlockAttempt = 1;
        public const byte BlockComplete = 2;
        public const byte BlockClosed = 3;

        public static void WriteHeader(Stream stream, SessionHeader header)
        {
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(header.CreatedUtcTicks);
                w.Write(header.Scene ?? "");
                w.Write(header.LevelName ?? "");
                w.Write(header.Difficulty);
                w.Write(header.CapsuleHeight);
                w.Write(header.CapsuleRadius);
                w.Write(header.CapsuleCenterY);
            }
        }

        public static void WriteBlock(Stream stream, byte type, byte[] payload)
        {
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(type);
                w.Write(payload.Length);
                w.Write(payload);
            }
        }

        public static byte[] ClosedPayload(long utcTicks) => BitConverter.GetBytes(utcTicks);

        public static byte[] CompletePayload(int attempt, float levelTime, long utcTicks)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(attempt);
                w.Write(levelTime);
                w.Write(utcTicks);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static SessionHeader ReadHeader(BinaryReader r)
        {
            byte[] magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
            {
                throw new InvalidDataException("not an UltraAttempts session file");
            }
            ushort version = r.ReadUInt16();
            if (version > FormatVersion)
            {
                throw new InvalidDataException($"session format {version} is newer than this version of UltraAttempts");
            }
            return new SessionHeader
            {
                CreatedUtcTicks = r.ReadInt64(),
                Scene = r.ReadString(),
                LevelName = r.ReadString(),
                Difficulty = r.ReadInt32(),
                CapsuleHeight = r.ReadSingle(),
                CapsuleRadius = r.ReadSingle(),
                CapsuleCenterY = r.ReadSingle(),
            };
        }

        // The header and what the blocks add up to, without decoding any attempt.
        public static SessionInfo Scan(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new BinaryReader(stream, Encoding.UTF8))
            {
                var info = new SessionInfo { Path = path, Header = ReadHeader(r) };
                info.ValidLength = stream.Position;
                while (TryReadBlockHeader(r, out byte type, out int length))
                {
                    if (type == BlockComplete && length >= 8)
                    {
                        long start = stream.Position;
                        r.ReadInt32();
                        info.FinishLevelTime = r.ReadSingle();
                        stream.Position = start + length;
                        info.Complete = true;
                    }
                    else
                    {
                        stream.Position += length;
                        if (type == BlockAttempt)
                        {
                            info.Attempts++;
                        }
                        else if (type == BlockClosed)
                        {
                            info.Closed = true;
                        }
                    }
                    info.ValidLength = stream.Position;
                }
                return info;
            }
        }

        // Every attempt in the file, decoded. A damaged attempt is skipped (onError says which). world:
        // false leaves out the enemies and doors (most of a recent attempt's size once decoded).
        public static SessionData Load(string path, Action<string> onError = null, bool world = true)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0)
                    {
                        break;
                    }
                    read += got;
                }
                if (read < bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }
            }

            using (var ms = new MemoryStream(bytes, writable: false))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                var data = new SessionData { Info = new SessionInfo { Path = path, Header = ReadHeader(r) } };
                data.Info.ValidLength = ms.Position;
                while (TryReadBlockHeader(r, out byte type, out int length))
                {
                    int start = (int)ms.Position;
                    if (type == BlockAttempt)
                    {
                        data.Info.Attempts++;
                        try
                        {
                            data.Attempts.Add(AttemptCodec.Decode(bytes, start, length, world));
                        }
                        catch (Exception e)
                        {
                            onError?.Invoke($"{System.IO.Path.GetFileName(path)}: skipped a damaged attempt ({e.Message})");
                        }
                    }
                    else if (type == BlockComplete && length >= 8)
                    {
                        r.ReadInt32();
                        data.Info.FinishLevelTime = r.ReadSingle();
                        data.Info.Complete = true;
                    }
                    else if (type == BlockClosed)
                    {
                        data.Info.Closed = true;
                    }
                    ms.Position = start + length;
                    data.Info.ValidLength = ms.Position;
                }
                return data;
            }
        }

        private static bool TryReadBlockHeader(BinaryReader r, out byte type, out int length)
        {
            type = 0;
            length = 0;
            Stream s = r.BaseStream;
            if (s.Length - s.Position < 5)
            {
                return false;
            }
            type = r.ReadByte();
            length = r.ReadInt32();
            return length >= 0 && s.Length - s.Position >= length;
        }
    }
}
