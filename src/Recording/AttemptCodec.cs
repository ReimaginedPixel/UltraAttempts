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
    // (one column per channel, across every enemy). Version 3 adds everything else after that (WorldDetail =
    // Everything): the things that moved, what they looked like, and the explosions, beams and breaks. Versions 1
    // (UltraAttempts 1.0, no world) and 2 (no things) still read.
    public static class AttemptCodec
    {
        public const byte Version = 3;

        public static byte[] Encode(RawAttempt a)
        {
            int enemySamples = 0;
            foreach (EnemyTrack e in a.Enemies)
            {
                enemySamples += e.Count;
            }
            foreach (ThingTrack t in a.Things)
            {
                enemySamples += t.Count;
            }
            var w = new VarWriter(64 + a.Count * 12 + enemySamples * 12 + a.WorldEvents.Count * 24);
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
            EncodeThings(w, a);

            using (var output = new MemoryStream(w.Length / 2 + 16))
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(w.Buffer, 0, w.Length);
                }
                return output.ToArray();
            }
        }

        // world: false skips the enemies, doors and things (they're the bulk of a recent attempt in memory).
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
                if (version >= 3)
                {
                    DecodeThings(r, a);
                }
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

        // Version 3: one table of every name used (looks, meshes, materials, level paths), the looks, the level
        // paths, the things (each one's header, then each channel for every thing in turn: key samples and
        // positions and rotations predicted, sizes as deltas), then the events, one after another.
        private static void EncodeThings(VarWriter w, RawAttempt a)
        {
            var strings = new List<string>();
            var index = new Dictionary<string, int>();
            foreach (ThingLook look in a.Looks)
            {
                Name(look?.Name, strings, index);
                foreach (LookPart part in look?.Parts ?? NoParts)
                {
                    Name(part?.Mesh, strings, index);
                    foreach (string material in part?.Materials ?? Array.Empty<string>())
                    {
                        Name(material, strings, index);
                    }
                }
            }
            foreach (string path in a.ThingPaths)
            {
                Name(path, strings, index);
            }
            w.UInt((uint)strings.Count);
            foreach (string s in strings)
            {
                w.String(s);
            }

            w.UInt((uint)a.Looks.Count);
            foreach (ThingLook look in a.Looks)
            {
                ThingLook l = look ?? new ThingLook();
                w.UInt((uint)index[l.Name ?? ""]);
                w.UInt(l.Color);
                w.Float(l.CenterX);
                w.Float(l.CenterY);
                w.Float(l.CenterZ);
                w.Float(l.SizeX);
                w.Float(l.SizeY);
                w.Float(l.SizeZ);
                List<LookPart> parts = l.Parts ?? NoParts;
                w.UInt((uint)parts.Count);
                foreach (LookPart part in parts)
                {
                    LookPart p = part ?? new LookPart();
                    w.UInt((uint)index[p.Mesh ?? ""]);
                    w.UInt((uint)Math.Max(0, p.Vertices));
                    string[] materials = p.Materials ?? Array.Empty<string>();
                    w.UInt((uint)materials.Length);
                    foreach (string material in materials)
                    {
                        w.UInt((uint)index[material ?? ""]);
                    }
                    w.Float(p.PX);
                    w.Float(p.PY);
                    w.Float(p.PZ);
                    w.Float(p.RX);
                    w.Float(p.RY);
                    w.Float(p.RZ);
                    w.Float(p.RW);
                    w.Float(p.SX);
                    w.Float(p.SY);
                    w.Float(p.SZ);
                }
            }

            w.UInt((uint)a.ThingPaths.Count);
            foreach (string path in a.ThingPaths)
            {
                w.UInt((uint)index[path ?? ""]);
            }

            List<ThingTrack> things = a.Things;
            w.UInt((uint)things.Count);
            int lastId = 0, lastEnd = 0;
            foreach (ThingTrack t in things)
            {
                w.Int(t.Id - lastId);
                lastId = t.Id;
                w.Byte((byte)t.Kind);
                w.UInt((uint)(t.Look + 1));
                w.UInt((uint)(t.Path + 1));
                w.Float(t.ScaleX);
                w.Float(t.ScaleY);
                w.Float(t.ScaleZ);
                w.Int(t.End - lastEnd);
                lastEnd = t.End;
                w.Byte((byte)(t.Gone ? 1 : 0));
                w.UInt((uint)t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.Sample, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.PosX, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.PosY, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.PosZ, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.RotX, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.RotY, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.RotZ, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Predicted(w, t.RotW, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Deltas(w, t.Scale, t.Count);
            }

            w.UInt((uint)a.WorldEvents.Count);
            var last = new WorldEvent();
            foreach (WorldEvent e in a.WorldEvents)
            {
                w.Int(e.Sample - last.Sample);
                w.Byte((byte)e.Kind);
                w.Int(e.X - last.X);
                w.Int(e.Y - last.Y);
                w.Int(e.Z - last.Z);
                w.Int(e.ToX - e.X);
                w.Int(e.ToY - e.Y);
                w.Int(e.ToZ - e.Z);
                w.Int(e.Size);
                w.Int(e.Life);
                w.UInt(e.Color);
                w.UInt((uint)(e.Look + 1));
                w.UInt((uint)(e.Path + 1));
                w.Int(e.RotX);
                w.Int(e.RotY);
                w.Int(e.RotZ);
                w.Int(e.RotW);
                last = e;
            }
        }

        private static readonly List<LookPart> NoParts = new List<LookPart>();

        private static void Name(string s, List<string> strings, Dictionary<string, int> index)
        {
            s = s ?? "";
            if (!index.ContainsKey(s))
            {
                index[s] = strings.Count;
                strings.Add(s);
            }
        }

        private static void DecodeThings(VarReader r, RawAttempt a)
        {
            var strings = new string[r.Count(1)];
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i] = r.String();
            }
            string Str(uint i) => i < strings.Length ? strings[i] : throw new InvalidDataException("bad name");

            int looks = r.Count(27);  // (name, colour, bounds, parts: at least 27 bytes a look)
            for (int i = 0; i < looks; i++)
            {
                var look = new ThingLook
                {
                    Name = Str(r.UInt()),
                    Color = r.UInt(),
                    CenterX = r.Float(),
                    CenterY = r.Float(),
                    CenterZ = r.Float(),
                    SizeX = r.Float(),
                    SizeY = r.Float(),
                    SizeZ = r.Float(),
                };
                int parts = r.Count(43);  // (mesh, vertices, materials, ten floats)
                for (int p = 0; p < parts; p++)
                {
                    var part = new LookPart { Mesh = Str(r.UInt()), Vertices = (int)Math.Min(int.MaxValue, r.UInt()) };
                    var materials = new string[r.Count(1)];
                    for (int m = 0; m < materials.Length; m++)
                    {
                        materials[m] = Str(r.UInt());
                    }
                    part.Materials = materials;
                    part.PX = r.Float();
                    part.PY = r.Float();
                    part.PZ = r.Float();
                    part.RX = r.Float();
                    part.RY = r.Float();
                    part.RZ = r.Float();
                    part.RW = r.Float();
                    part.SX = r.Float();
                    part.SY = r.Float();
                    part.SZ = r.Float();
                    look.Parts.Add(part);
                }
                a.Looks.Add(look);
            }

            int paths = r.Count(1);
            for (int i = 0; i < paths; i++)
            {
                a.ThingPaths.Add(Str(r.UInt()));
            }

            int count = r.Count(19);
            var things = new List<ThingTrack>(count);
            int lastId = 0, lastEnd = 0;
            long keys = 0;
            for (int k = 0; k < count; k++)
            {
                var t = new ThingTrack();
                lastId += r.Int();
                t.Id = lastId;
                t.Kind = (ThingKind)r.Byte();
                t.Look = (int)r.UInt() - 1;
                t.Path = (int)r.UInt() - 1;
                if (t.Look < -1 || t.Look >= a.Looks.Count || t.Path < -1 || t.Path >= a.ThingPaths.Count)
                {
                    throw new InvalidDataException("bad thing");
                }
                t.ScaleX = r.Float();
                t.ScaleY = r.Float();
                t.ScaleZ = r.Float();
                lastEnd += r.Int();
                t.End = lastEnd;
                t.Gone = r.Byte() != 0;
                int n = (int)r.UInt();
                keys += n;
                if (n < 0 || keys * 9 > r.Remaining)
                {
                    throw new EndOfStreamException("attempt data is cut short");  // (nine channels: at least 9 bytes a key)
                }
                t.Reserve(n);
                t.Count = n;
                things.Add(t);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.Sample, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.PosX, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.PosY, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.PosZ, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.RotX, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.RotY, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.RotZ, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Unpredicted(r, t.RotW, t.Count);
            }
            foreach (ThingTrack t in things)
            {
                Undeltas(r, t.Scale, t.Count);
            }
            a.Things = things;

            int events = r.Count(17);
            var e = new WorldEvent();
            for (int i = 0; i < events; i++)
            {
                e.Sample += r.Int();
                e.Kind = (WorldEventKind)r.Byte();
                e.X += r.Int();
                e.Y += r.Int();
                e.Z += r.Int();
                e.ToX = e.X + r.Int();
                e.ToY = e.Y + r.Int();
                e.ToZ = e.Z + r.Int();
                e.Size = r.Int();
                e.Life = r.Int();
                e.Color = r.UInt();
                e.Look = (int)r.UInt() - 1;
                e.Path = (int)r.UInt() - 1;
                if (e.Look < -1 || e.Look >= a.Looks.Count || e.Path < -1 || e.Path >= a.ThingPaths.Count)
                {
                    throw new InvalidDataException("bad world event");
                }
                e.RotX = (short)r.Int();
                e.RotY = (short)r.Int();
                e.RotZ = (short)r.Int();
                e.RotW = (short)r.Int();
                a.WorldEvents.Add(e);
            }
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
