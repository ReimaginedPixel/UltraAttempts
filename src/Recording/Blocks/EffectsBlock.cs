using System;
using System.Collections.Generic;
using System.IO;

namespace UltraAttempts.Recording
{
    // Where an effect's poses are kept: in the world, or on V1 (its camera: whatever sits on the viewmodel; its
    // body: what goes about with it, the dust of a heavy fall). Near: made next to its camera and left in the world
    // there (a muzzle flash at the barrel, a punch landing), kept from the camera as it was then.
    public enum EffectFrame : byte
    {
        World = 0,
        Camera = 1,
        Body = 2,
        Near = 3,
    }

    // One of the game's own prefabs that an effect (or a thing) was a copy of, to copy it again in a replay: its name,
    // and a level object's path when what was copied was in the level rather than an asset (a gun's own particles).
    // How many objects it has tells same-named prefabs apart.
    public sealed class EffectPrefab
    {
        public string Name = "";
        public string Path = "";
        public int Parts;
    }

    // One of the game's own effects (WorldDetail = Everything): a muzzle flash, a slide's sparks, an explosion's fire,
    // a punch landing, blood. Which prefab it was, when it showed, when its particles stopped coming and when it went,
    // and where it was in its frame: keys while it moves, like a ThingTrack's (in the world: centimetres; on V1:
    // millimetres from its camera or body).
    public sealed class EffectTrack
    {
        public int Prefab;                // index into RawAttempt.EffectPrefabs
        public EffectFrame Frame;
        public int Start;                 // the attempt sample it showed at
        public int Stop = -1;             // ...its particles stopped coming at (-1: they didn't)
        public int End = -1;              // ...it went at (destroyed or switched off; -1: still there when the attempt ended)
        public float ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f;  // its lossyScale when it showed
        public uint Tint;                 // RGBA the game gave its particles (dust the colour of the floor); 0: their own

        public int Count;
        public int[] Sample = new int[2];             // the attempt sample of each key (increasing)
        public int[] PosX = new int[2], PosY = new int[2], PosZ = new int[2];
        public short[] RotX = new short[2], RotY = new short[2], RotZ = new short[2], RotW = new short[2];  // * 32767, w >= 0
        public ushort[] Scale = new ushort[2];        // its size relative to ScaleX..Z, * 1000

        public void Add(int sample, int px, int py, int pz, short rx, short ry, short rz, short rw, ushort scale)
        {
            if (Count == Sample.Length)
            {
                Reserve(Count * 2);
            }
            Sample[Count] = sample;
            PosX[Count] = px;
            PosY[Count] = py;
            PosZ[Count] = pz;
            RotX[Count] = rx;
            RotY[Count] = ry;
            RotZ[Count] = rz;
            RotW[Count] = rw;
            Scale[Count] = scale;
            Count++;
        }

        public void Reserve(int capacity)
        {
            if (capacity <= Sample.Length)
            {
                return;
            }
            Array.Resize(ref Sample, capacity);
            Array.Resize(ref PosX, capacity);
            Array.Resize(ref PosY, capacity);
            Array.Resize(ref PosZ, capacity);
            Array.Resize(ref RotX, capacity);
            Array.Resize(ref RotY, capacity);
            Array.Resize(ref RotZ, capacity);
            Array.Resize(ref RotW, capacity);
            Array.Resize(ref Scale, capacity);
        }

        public EffectTrack Clone()
        {
            var copy = (EffectTrack)MemberwiseClone();
            copy.Sample = (int[])Sample.Clone();
            copy.PosX = (int[])PosX.Clone();
            copy.PosY = (int[])PosY.Clone();
            copy.PosZ = (int[])PosZ.Clone();
            copy.RotX = (short[])RotX.Clone();
            copy.RotY = (short[])RotY.Clone();
            copy.RotZ = (short[])RotZ.Clone();
            copy.RotW = (short[])RotW.Clone();
            copy.Scale = (ushort[])Scale.Clone();
            return copy;
        }
    }

    // Something else that was a copy of a prefab: a thing's look (Index into Looks: a projectile caught as it was
    // made) or a beam (Index into WorldEvents).
    public struct EffectLink
    {
        public int Index;
        public int Prefab;
    }

    // Block 3 (the world's): the game's own effects, and which prefabs things and beams were copies of.
    public sealed partial class RawAttempt
    {
        public List<EffectPrefab> EffectPrefabs = new List<EffectPrefab>();
        public List<EffectTrack> Effects = new List<EffectTrack>();
        public List<EffectLink> LookPrefabs = new List<EffectLink>();
        public List<EffectLink> BeamPrefabs = new List<EffectLink>();

        public bool HasEffects => Effects.Count > 0 || LookPrefabs.Count > 0 || BeamPrefabs.Count > 0;

        public int AddEffectPrefab(string name, string path, int parts)
        {
            name = name ?? "";
            path = path ?? "";
            for (int i = 0; i < EffectPrefabs.Count; i++)
            {
                EffectPrefab p = EffectPrefabs[i];
                if (p.Name == name && p.Path == path && p.Parts == parts)
                {
                    return i;
                }
            }
            EffectPrefabs.Add(new EffectPrefab { Name = name, Path = path, Parts = parts });
            return EffectPrefabs.Count - 1;
        }

        partial void CloneEffects(RawAttempt copy)
        {
            copy.EffectPrefabs = new List<EffectPrefab>(EffectPrefabs);  // (prefabs never change once made)
            copy.Effects = Effects.ConvertAll(e => e.Clone());
            copy.LookPrefabs = new List<EffectLink>(LookPrefabs);
            copy.BeamPrefabs = new List<EffectLink>(BeamPrefabs);
        }
    }

    // On disk: the prefabs; each effect's prefab, frame, times, scale and tint; then its keys channel by channel (the
    // first of each from the effect before's, the rest as the difference from where the two before predict it: a
    // muzzle flash where the last one was, or a scrape going on steadily, costs next to nothing); then the links.
    internal static class EffectsBlock
    {
        private const byte Uniform = 1;   // (flags: one scale for all three axes)
        private const byte Tinted = 2;
        private const byte Stops = 4;     // (its particles stopped: else -1, not written)
        private const byte Ends = 8;      // (it went: else -1, not written)

        public static bool Has(RawAttempt a) => a.HasEffects;

        public static void Encode(AttemptCodec.VarWriter w, RawAttempt a)
        {
            w.UInt((uint)a.EffectPrefabs.Count);
            foreach (EffectPrefab prefab in a.EffectPrefabs)
            {
                EffectPrefab p = prefab ?? new EffectPrefab();
                w.String(p.Name ?? "");
                w.String(p.Path ?? "");
                w.UInt((uint)Math.Max(0, p.Parts));
            }

            List<EffectTrack> effects = a.Effects;
            w.UInt((uint)effects.Count);
            int lastStart = 0;
            foreach (EffectTrack e in effects)
            {
                w.UInt((uint)e.Prefab);
                w.Byte((byte)e.Frame);
                bool uniform = Same(e.ScaleX, e.ScaleY) && Same(e.ScaleX, e.ScaleZ);
                w.Byte((byte)((uniform ? Uniform : 0) | (e.Tint != 0 ? Tinted : 0) | (e.Stop != -1 ? Stops : 0) | (e.End != -1 ? Ends : 0)));
                w.Int(e.Start - lastStart);
                lastStart = e.Start;
                if (e.Stop != -1)
                {
                    w.Int(e.Stop - e.Start);
                }
                if (e.End != -1)
                {
                    w.Int(e.End - e.Start);
                }
                w.Float(e.ScaleX);
                if (!uniform)
                {
                    w.Float(e.ScaleY);
                    w.Float(e.ScaleZ);
                }
                if (e.Tint != 0)
                {
                    w.UInt(e.Tint);
                }
                w.UInt((uint)e.Count);
            }
            foreach (EffectTrack e in effects)
            {
                Keys(w, e.Sample, e.Count, e.Start);
            }
            Channel(w, effects, e => e.PosX);
            Channel(w, effects, e => e.PosY);
            Channel(w, effects, e => e.PosZ);
            Channel(w, effects, e => e.RotX);
            Channel(w, effects, e => e.RotY);
            Channel(w, effects, e => e.RotZ);
            Channel(w, effects, e => e.RotW);
            Channel(w, effects, e => e.Scale);

            Links(w, a.LookPrefabs);
            Links(w, a.BeamPrefabs);
        }

        public static void Decode(AttemptCodec.VarReader r, RawAttempt a)
        {
            int prefabs = r.Count(3);
            var found = new List<EffectPrefab>(prefabs);
            for (int i = 0; i < prefabs; i++)
            {
                found.Add(new EffectPrefab { Name = r.String(), Path = r.String(), Parts = (int)Math.Min(int.MaxValue, r.UInt()) });
            }

            int count = r.Count(9);  // (prefab, frame, flags, when, a scale, how many keys: at least 9 bytes)
            var effects = new List<EffectTrack>(count);
            int lastStart = 0;
            long keys = 0;
            for (int k = 0; k < count; k++)
            {
                var e = new EffectTrack();
                uint prefab = r.UInt();
                if (prefab >= (uint)prefabs)
                {
                    throw new InvalidDataException("bad effect");
                }
                e.Prefab = (int)prefab;
                e.Frame = (EffectFrame)r.Byte();
                byte flags = r.Byte();
                lastStart += r.Int();
                e.Start = lastStart;
                e.Stop = (flags & Stops) != 0 ? e.Start + r.Int() : -1;
                e.End = (flags & Ends) != 0 ? e.Start + r.Int() : -1;
                e.ScaleX = r.Float();
                e.ScaleY = (flags & Uniform) != 0 ? e.ScaleX : r.Float();
                e.ScaleZ = (flags & Uniform) != 0 ? e.ScaleX : r.Float();
                e.Tint = (flags & Tinted) != 0 ? r.UInt() : 0u;
                int n = (int)Math.Min(int.MaxValue, r.UInt());
                keys += n;
                if (keys * 9 > r.Remaining)
                {
                    throw new EndOfStreamException("attempt data is cut short");  // (nine channels: at least 9 bytes a key)
                }
                e.Reserve(n);
                e.Count = n;
                effects.Add(e);
            }
            foreach (EffectTrack e in effects)
            {
                Unkeys(r, e.Sample, e.Count, e.Start);
            }
            Unchannel(r, effects, e => e.PosX);
            Unchannel(r, effects, e => e.PosY);
            Unchannel(r, effects, e => e.PosZ);
            Unchannel(r, effects, e => e.RotX);
            Unchannel(r, effects, e => e.RotY);
            Unchannel(r, effects, e => e.RotZ);
            Unchannel(r, effects, e => e.RotW);
            Unchannel(r, effects, e => e.Scale);

            List<EffectLink> looks = Unlinks(r, prefabs, a.Looks.Count);
            List<EffectLink> beams = Unlinks(r, prefabs, a.WorldEvents.Count);
            a.EffectPrefabs = found;
            a.Effects = effects;
            a.LookPrefabs = looks;
            a.BeamPrefabs = beams;
        }

        // Each effect's own last key at the end of the attempt (lastSample): nothing after it, and what went or
        // stopped after it hadn't yet.
        public static void Trim(RawAttempt a, int lastSample)
        {
            foreach (EffectTrack e in a.Effects)
            {
                e.Start = Math.Min(e.Start, lastSample);
                e.Stop = e.Stop > lastSample ? -1 : e.Stop;
                e.End = e.End > lastSample ? -1 : e.End;
                for (int k = 0; k < e.Count; k++)
                {
                    e.Sample[k] = Math.Min(e.Sample[k], lastSample);
                }
            }
        }

        private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        // (the second-order guess AttemptCodec makes for V1's own samples; wrapping round is fine both ways)
        private static int Guess(int i, int previous, int beforeThat) => unchecked(i == 1 ? previous : 2 * previous - beforeThat);

        private static void Keys(AttemptCodec.VarWriter w, int[] values, int n, int first)
        {
            for (int i = 0; i < n; i++)
            {
                w.Int(unchecked(values[i] - (i == 0 ? first : Guess(i, values[i - 1], i > 1 ? values[i - 2] : 0))));
            }
        }

        private static void Unkeys(AttemptCodec.VarReader r, int[] values, int n, int first)
        {
            for (int i = 0; i < n; i++)
            {
                values[i] = unchecked(r.Int() + (i == 0 ? first : Guess(i, values[i - 1], i > 1 ? values[i - 2] : 0)));
            }
        }

        // One channel of every effect's keys: each one's first from the last one's first, the rest guessed.
        private static void Channel(AttemptCodec.VarWriter w, List<EffectTrack> effects, Func<EffectTrack, int[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                int[] values = of(e);
                Keys(w, values, e.Count, first);
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Channel(AttemptCodec.VarWriter w, List<EffectTrack> effects, Func<EffectTrack, short[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                short[] values = of(e);
                for (int i = 0; i < e.Count; i++)
                {
                    w.Int(unchecked(values[i] - (i == 0 ? first : Guess(i, values[i - 1], i > 1 ? values[i - 2] : 0))));
                }
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Channel(AttemptCodec.VarWriter w, List<EffectTrack> effects, Func<EffectTrack, ushort[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                ushort[] values = of(e);
                for (int i = 0; i < e.Count; i++)
                {
                    w.Int(values[i] - (i == 0 ? first : values[i - 1]));
                }
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Unchannel(AttemptCodec.VarReader r, List<EffectTrack> effects, Func<EffectTrack, int[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                int[] values = of(e);
                Unkeys(r, values, e.Count, first);
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Unchannel(AttemptCodec.VarReader r, List<EffectTrack> effects, Func<EffectTrack, short[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                short[] values = of(e);
                for (int i = 0; i < e.Count; i++)
                {
                    values[i] = unchecked((short)(r.Int() + (i == 0 ? first : Guess(i, values[i - 1], i > 1 ? values[i - 2] : 0))));
                }
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Unchannel(AttemptCodec.VarReader r, List<EffectTrack> effects, Func<EffectTrack, ushort[]> of)
        {
            int first = 0;
            foreach (EffectTrack e in effects)
            {
                ushort[] values = of(e);
                for (int i = 0; i < e.Count; i++)
                {
                    values[i] = unchecked((ushort)(r.Int() + (i == 0 ? first : values[i - 1])));
                }
                first = e.Count > 0 ? values[0] : first;
            }
        }

        private static void Links(AttemptCodec.VarWriter w, List<EffectLink> links)
        {
            w.UInt((uint)links.Count);
            int last = 0;
            foreach (EffectLink link in links)
            {
                w.Int(link.Index - last);
                last = link.Index;
                w.UInt((uint)link.Prefab);
            }
        }

        // (each to a prefab there is and a look or beam there is: the things and events are read before this block)
        private static List<EffectLink> Unlinks(AttemptCodec.VarReader r, int prefabs, int indices)
        {
            int count = r.Count(2);
            var links = new List<EffectLink>(count);
            int last = 0;
            for (int i = 0; i < count; i++)
            {
                last += r.Int();
                uint prefab = r.UInt();
                if (prefab >= (uint)prefabs || last < 0 || last >= indices)
                {
                    throw new InvalidDataException("bad effect link");
                }
                links.Add(new EffectLink { Index = last, Prefab = (int)prefab });
            }
            return links;
        }
    }
}
