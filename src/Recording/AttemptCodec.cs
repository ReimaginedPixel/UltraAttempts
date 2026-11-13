using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UltraAttempts.Recording
{
    // An attempt's bytes on disk: each channel stored on its own (all flags, then all HP, then all
    // positions, ...), positions and rotations as what's left after predicting each sample from the two
    // before it (moving at a steady speed, or turning at a steady rate, leaves nothing), the eyes as
    // deltas, all as zigzag varints, then the whole thing deflated. Standing still costs about nothing.
    //
    // Version 2 adds the world after the events: the door paths, then the enemies, packed the same way
    // (one column per channel, across every enemy). Version 1 (UltraAttempts 1.0) still reads, with no world.
    public static class AttemptCodec
    {
        public const byte Version = 2;

        public static byte[] Encode(RawAttempt a)
        {
            int enemySamples = 0;
            foreach (EnemyTrack e in a.Enemies)
            {
                enemySamples += e.Count;
            }
            var w = new VarWriter(64 + a.Count * 12 + enemySamples * 12);
            w.Byte(Version);
            w.UInt((uint)a.Number);
            w.Byte((byte)a.Start);
            w.Byte((byte)a.End);
            w.Long(a.StartedUtcTicks);
            w.UInt((uint)a.SampleRate);
            w.Float(a.LevelTimeStart);
            w.Float(a.LevelTimeEnd);
            w.Byte((byte)((a.Cheats ? 1 : 0) | (a.MajorAssists ? 2 : 0) | (a.WeaponsRecorded ? 4 : 0)));

            w.UInt((uint)a.WeaponNames.Count);
            foreach (string name in a.WeaponNames)
            {
                w.String(name);
            }

            int n = a.Count;
            w.UInt((uint)n);
            w.Bytes(a.Flags, n);
            w.Bytes(a.Hp, n);
            Predicted(w, a.PosX, n);
            Predicted(w, a.PosY, n);
            Predicted(w, a.PosZ, n);
            Deltas(w, a.EyeX, n);
            Deltas(w, a.EyeY, n);
            Deltas(w, a.EyeZ, n);
            Predicted(w, a.RotX, n);
            Predicted(w, a.RotY, n);
            Predicted(w, a.RotZ, n);
            Predicted(w, a.RotW, n);

            w.UInt((uint)a.Events.Count);
            int lastSample = 0;
            foreach (AttemptEvent e in a.Events)
            {
                w.UInt((uint)(e.Sample - lastSample));
                lastSample = e.Sample;
                w.Byte((byte)e.Kind);
                w.Int(e.A);
                w.Int(e.B);
                w.Int(e.C);
            }

            EncodeWorld(w, a);

            using (var output = new MemoryStream(w.Length / 2 + 16))
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(w.Buffer, 0, w.Length);
                }
                return output.ToArray();
            }
        }

        // world: false skips the enemies and doors (they're the bulk of a version-2 attempt in memory).
        public static RawAttempt Decode(byte[] data, int offset, int length, bool world = true)
        {
            byte[] raw;
            using (var input = new MemoryStream(data, offset, length, writable: false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(length * 4))
            {
                deflate.CopyTo(output);
                raw = output.ToArray();
            }

            var r = new VarReader(raw);
            byte version = r.Byte();
            if (version > Version)
            {
                throw new InvalidDataException($"attempt format {version} is newer than this version of UltraAttempts ({Version})");
            }
            var a = new RawAttempt
            {
                Number = (int)r.UInt(),
                Start = (AttemptStart)r.Byte(),
                End = (AttemptEnd)r.Byte(),
                StartedUtcTicks = r.Long(),
                SampleRate = (int)r.UInt(),
                LevelTimeStart = r.Float(),
                LevelTimeEnd = r.Float(),
            };
            byte bits = r.Byte();
            a.Cheats = (bits & 1) != 0;
            a.MajorAssists = (bits & 2) != 0;
            a.WeaponsRecorded = (bits & 4) != 0;

            int names = r.Count(1);
            for (int i = 0; i < names; i++)
            {
                a.WeaponNames.Add(r.String());
            }

            int n = r.Count(12);  // (flags and HP, then ten varints: at least 12 bytes a sample)
            a.Reserve(Math.Max(n, 1));
            a.Count = n;
            r.Bytes(a.Flags, n);
            r.Bytes(a.Hp, n);
            Unpredicted(r, a.PosX, n);
            Unpredicted(r, a.PosY, n);
            Unpredicted(r, a.PosZ, n);
            Undeltas(r, a.EyeX, n);
            Undeltas(r, a.EyeY, n);
            Undeltas(r, a.EyeZ, n);
            Unpredicted(r, a.RotX, n);
            Unpredicted(r, a.RotY, n);
            Unpredicted(r, a.RotZ, n);
            Unpredicted(r, a.RotW, n);

            int events = r.Count(5);
            int sample = 0;
            for (int i = 0; i < events; i++)
            {
                sample += (int)r.UInt();
                a.Events.Add(new AttemptEvent
                {
                    Sample = sample,
                    Kind = (EventKind)r.Byte(),
                    A = r.Int(),
                    B = r.Int(),
                    C = r.Int(),
                });
            }

            if (version >= 2 && world)
            {
                DecodeWorld(r, a);
            }
            return a;
        }

        // Version 2: the enemy stride, the door paths, then the enemies: a name table, each enemy's
        // header, then each channel for every enemy in turn (positions and rotations predicted like V1's,
        // animations and health as deltas).
        private static void EncodeWorld(VarWriter w, RawAttempt a)
        {
            w.UInt((uint)Math.Max(1, a.EnemyStride));

            w.UInt((uint)a.DoorPaths.Count);
            foreach (string path in a.DoorPaths)
            {
                w.String(path);
            }

            List<EnemyTrack> tracks = a.Enemies;
            var names = new List<string>();
            var nameIndex = new int[tracks.Count];
            for (int k = 0; k < tracks.Count; k++)
            {
                string name = tracks[k].Name ?? "";
                int index = names.IndexOf(name);
                if (index < 0)
                {
                    index = names.Count;
                    names.Add(name);
                }
                nameIndex[k] = index;
            }
            w.UInt((uint)names.Count);
            foreach (string name in names)
            {
                w.String(name);
            }

            w.UInt((uint)tracks.Count);
            int lastId = 0, lastStart = 0;
            for (int k = 0; k < tracks.Count; k++)
            {
                EnemyTrack e = tracks[k];
                w.Int(e.Id - lastId);
                lastId = e.Id;
                w.Int(e.Type);
                w.UInt((uint)nameIndex[k]);
                w.Float(e.Scale);
                w.Int(e.Start - lastStart);
                lastStart = e.Start;
                w.Byte((byte)(e.Died ? 1 : 0));
                w.UInt((uint)e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.PosX, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.PosY, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.PosZ, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.RotX, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.RotY, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.RotZ, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Predicted(w, e.RotW, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Deltas(w, e.Anim, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Deltas(w, e.AnimTime, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Deltas(w, e.Health, e.Count);
            }
        }

        private static void DecodeWorld(VarReader r, RawAttempt a)
        {
            a.EnemyStride = Math.Max(1, (int)r.UInt());

            int doors = r.Count(1);
            for (int i = 0; i < doors; i++)
            {
                a.DoorPaths.Add(r.String());
            }

            var names = new string[r.Count(1)];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = r.String();
            }

            int count = r.Count(10);
            var tracks = new List<EnemyTrack>(count);
            int lastId = 0, lastStart = 0;
            long samples = 0;
            for (int k = 0; k < count; k++)
            {
                var e = new EnemyTrack();
                lastId += r.Int();
                e.Id = lastId;
                e.Type = r.Int();
                uint name = r.UInt();
                if (name >= names.Length)
                {
                    throw new InvalidDataException("bad enemy name");
                }
                e.Name = names[name];
                e.Scale = r.Float();
                lastStart += r.Int();
                e.Start = lastStart;
                e.Died = r.Byte() != 0;
                int n = (int)r.UInt();
                samples += n;
                if (n < 0 || samples * 10 > r.Remaining)
                {
                    throw new EndOfStreamException("attempt data is cut short");  // (ten channels: at least 10 bytes a sample)
                }
                e.Reserve(n);
                e.Count = n;
                tracks.Add(e);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.PosX, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.PosY, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.PosZ, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.RotX, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.RotY, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.RotZ, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Unpredicted(r, e.RotW, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Undeltas(r, e.Anim, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Undeltas(r, e.AnimTime, e.Count);
            }
            foreach (EnemyTrack e in tracks)
            {
                Undeltas(r, e.Health, e.Count);
            }
            a.Enemies = tracks;
        }

        // Each sample minus where the two before it say it'd be (2b - a). Positions are kept within
        // +-10^8 cm by the recorder, so none of this overflows.
        private static int Prediction(int i, int previous, int beforeThat) => i == 0 ? 0 : i == 1 ? previous : 2 * previous - beforeThat;

        private static void Predicted(VarWriter w, int[] values, int n)
        {
            for (int i = 0; i < n; i++)
            {
                w.Int(values[i] - Prediction(i, i > 0 ? values[i - 1] : 0, i > 1 ? values[i - 2] : 0));
            }
        }

        private static void Predicted(VarWriter w, short[] values, int n)
        {
            for (int i = 0; i < n; i++)
            {
                w.Int(values[i] - Prediction(i, i > 0 ? values[i - 1] : 0, i > 1 ? values[i - 2] : 0));
            }
        }

        private static void Unpredicted(VarReader r, int[] values, int n)
        {
            for (int i = 0; i < n; i++)
            {
                values[i] = r.Int() + Prediction(i, i > 0 ? values[i - 1] : 0, i > 1 ? values[i - 2] : 0);
            }
        }

        private static void Unpredicted(VarReader r, short[] values, int n)
        {
            for (int i = 0; i < n; i++)
            {
                values[i] = (short)(r.Int() + Prediction(i, i > 0 ? values[i - 1] : 0, i > 1 ? values[i - 2] : 0));
            }
        }
        private static void Deltas(VarWriter w, short[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                w.Int(values[i] - last);
                last = values[i];
            }
        }

        private static void Undeltas(VarReader r, short[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                last += r.Int();
                values[i] = (short)last;
            }
        }

        // Animation hashes: the same one for a while, then any other (wrapping around is fine both ways).
        private static void Deltas(VarWriter w, int[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                w.Int(unchecked(values[i] - last));
                last = values[i];
            }
        }

        private static void Undeltas(VarReader r, int[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                last = unchecked(last + r.Int());
                values[i] = last;
            }
        }

        // Animation times: a looping one goes from 65535 round to 0, so the step is taken modulo 65536.
        private static void Deltas(VarWriter w, ushort[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                w.Int((short)(values[i] - last));
                last = values[i];
            }
        }

        private static void Undeltas(VarReader r, ushort[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                last = (ushort)(last + r.Int());
                values[i] = (ushort)last;
            }
        }

        private static void Deltas(VarWriter w, byte[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                w.Int(values[i] - last);
                last = values[i];
            }
        }

        private static void Undeltas(VarReader r, byte[] values, int n)
        {
            int last = 0;
            for (int i = 0; i < n; i++)
            {
                last = (byte)(last + r.Int());
                values[i] = (byte)last;
            }
        }

        private sealed class VarWriter
        {
            public byte[] Buffer;
            public int Length;

            public VarWriter(int capacity)
            {
                Buffer = new byte[Math.Max(capacity, 64)];
            }

            private void Ensure(int extra)
            {
                if (Length + extra > Buffer.Length)
                {
                    Array.Resize(ref Buffer, Math.Max(Buffer.Length * 2, Length + extra));
                }
            }

            public void Byte(byte value)
            {
                Ensure(1);
                Buffer[Length++] = value;
            }

            public void Bytes(byte[] values, int count)
            {
                Ensure(count);
                Array.Copy(values, 0, Buffer, Length, count);
                Length += count;
            }

            public void UInt(uint value)
            {
                Ensure(5);
                while (value >= 0x80)
                {
                    Buffer[Length++] = (byte)(value | 0x80);
                    value >>= 7;
                }
                Buffer[Length++] = (byte)value;
            }

            // Zigzag, so small negative numbers stay small.
            public void Int(int value) => UInt((uint)((value << 1) ^ (value >> 31)));

            public void Long(long value)
            {
                Ensure(8);
                for (int i = 0; i < 8; i++)
                {
                    Buffer[Length++] = (byte)(value >> (8 * i));
                }
            }

            public void Float(float value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }
                Bytes(bytes, 4);
            }

            public void String(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
                UInt((uint)bytes.Length);
                Bytes(bytes, bytes.Length);
            }
        }

        private sealed class VarReader
        {
            private readonly byte[] buffer;
            private int position;

            public VarReader(byte[] buffer)
            {
                this.buffer = buffer;
            }

            public int Remaining => buffer.Length - position;

            private void Need(int count)
            {
                if (position + count > buffer.Length)
                {
                    throw new EndOfStreamException("attempt data is cut short");
                }
            }

            // How many of something follow, each at least minBytes long: more than the data could hold
            // is damage, refused before anything that size is allocated.
            public int Count(int minBytes)
            {
                uint count = UInt();
                if ((long)count * minBytes > Remaining)
                {
                    throw new EndOfStreamException("attempt data is cut short");
                }
                return (int)count;
            }

            public byte Byte()
            {
                Need(1);
                return buffer[position++];
            }

            public void Bytes(byte[] target, int count)
            {
                Need(count);
                Array.Copy(buffer, position, target, 0, count);
                position += count;
            }

            public uint UInt()
            {
                uint value = 0;
                for (int shift = 0; shift < 35; shift += 7)
                {
                    byte b = Byte();
                    value |= (uint)(b & 0x7F) << shift;
                    if (b < 0x80)
                    {
                        return value;
                    }
                }
                throw new InvalidDataException("bad varint");
            }

            public int Int()
            {
                uint value = UInt();
                return (int)(value >> 1) ^ -(int)(value & 1);
            }

            public long Long()
            {
                Need(8);
                long value = 0;
                for (int i = 0; i < 8; i++)
                {
                    value |= (long)buffer[position++] << (8 * i);
                }
                return value;
            }

            public float Float()
            {
                Need(4);
                var bytes = new byte[4];
                Array.Copy(buffer, position, bytes, 0, 4);
                position += 4;
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }
                return BitConverter.ToSingle(bytes, 0);
            }

            public string String()
            {
                int length = (int)UInt();
                if (length < 0)
                {
                    throw new InvalidDataException("bad string length");
                }
                Need(length);
                string value = Encoding.UTF8.GetString(buffer, position, length);
                position += length;
                return value;
            }
        }
    }
}
