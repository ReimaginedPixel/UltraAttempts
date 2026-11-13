using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UltraAttempts.Recording
{
    // An attempt's bytes on disk: each channel stored on its own (all flags, then all HP, then all
    // positions, ...), positions and rotations as what's left after predicting each sample from the two
    // before it (moving at a steady speed, or turning at a steady rate, leaves nothing), the eyes as
    // deltas, all as zigzag varints, then the whole thing deflated. Standing still costs about nothing.
    public static class AttemptCodec
    {
        public const byte Version = 1;

        public static byte[] Encode(RawAttempt a)
        {
            var w = new VarWriter(64 + a.Count * 12);
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

            using (var output = new MemoryStream(w.Length / 2 + 16))
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(w.Buffer, 0, w.Length);
                }
                return output.ToArray();
            }
        }

        public static RawAttempt Decode(byte[] data, int offset, int length)
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

            int names = (int)r.UInt();
            for (int i = 0; i < names; i++)
            {
                a.WeaponNames.Add(r.String());
            }

            int n = (int)r.UInt();
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

            int events = (int)r.UInt();
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
            return a;
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

            private void Need(int count)
            {
                if (position + count > buffer.Length)
                {
                    throw new EndOfStreamException("attempt data is cut short");
                }
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
                Need(length);
                string value = Encoding.UTF8.GetString(buffer, position, length);
                position += length;
                return value;
            }
        }
    }
}
