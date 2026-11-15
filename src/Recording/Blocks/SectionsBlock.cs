using System.Collections.Generic;
using System.IO;

namespace UltraAttempts.Recording
{
    // A part of the level switched on or off (its GameObject's activeSelf), at an attempt sample. The first one for
    // each part is how it was found: at sample 0 for every part the level had when the attempt started.
    public struct SectionChange
    {
        public int Sample;
        public int Section;     // index into RawAttempt.SectionPaths
        public bool On;
    }

    // Block 2 (the world's): which parts of the level were switched on, and when (SectionRecorder). ULTRAKILL switches
    // areas on as you reach them and off behind you; a replay shows the level as the attempt it shows had it.
    public sealed partial class RawAttempt
    {
        // The parts, by SectionRecorder's paths (Game.PathOf's, with a checkpoint's spare rooms left out).
        public List<string> SectionPaths = new List<string>();
        public List<SectionChange> SectionChanges = new List<SectionChange>();

        partial void CloneSections(RawAttempt copy)
        {
            copy.SectionPaths = new List<string>(SectionPaths);
            copy.SectionChanges = new List<SectionChange>(SectionChanges);
        }
    }

    // The paths, each as how much of the one before it it starts with and the rest (they're sorted, so neighbours
    // share most of theirs); then the changes, each as a step from the one before (its sample, and its part with
    // on or off in the lowest bit). Every part's first change at sample 0 is a run of the same two bytes.
    internal static class SectionsBlock
    {
        public static bool Has(RawAttempt a) => a.SectionPaths.Count > 0;

        public static void Encode(AttemptCodec.VarWriter w, RawAttempt a)
        {
            w.UInt((uint)a.SectionPaths.Count);
            string last = "";
            foreach (string path in a.SectionPaths)
            {
                string p = path ?? "";
                int shared = 0;
                while (shared < p.Length && shared < last.Length && p[shared] == last[shared])
                {
                    shared++;
                }
                w.UInt((uint)shared);
                w.String(p.Substring(shared));
                last = p;
            }
            w.UInt((uint)a.SectionChanges.Count);
            int lastSample = 0, lastSection = 0;
            foreach (SectionChange c in a.SectionChanges)
            {
                w.Int(c.Sample - lastSample);
                w.Int(((c.Section - lastSection) << 1) | (c.On ? 1 : 0));
                lastSample = c.Sample;
                lastSection = c.Section;
            }
        }

        // The paths read last: the next attempt's are nearly always the same (every attempt of a visit to a level has the
        // same parts), so those strings are kept rather than made again for each of a thousand attempts.
        private static string[] lastRead = new string[0];

        public static void Decode(AttemptCodec.VarReader r, RawAttempt a)
        {
            int paths = r.Count(2);
            var list = new List<string>(paths);
            string[] known = lastRead;
            bool same = known.Length == paths;
            string last = "";
            for (int i = 0; i < paths; i++)
            {
                uint shared = r.UInt();
                if (shared > last.Length)
                {
                    throw new InvalidDataException("bad section path");
                }
                string rest = r.String();
                int n = (int)shared;
                if (i < known.Length && known[i].Length == n + rest.Length && string.CompareOrdinal(known[i], 0, last, 0, n) == 0
                    && string.CompareOrdinal(known[i], n, rest, 0, rest.Length) == 0)
                {
                    last = known[i];
                }
                else
                {
                    last = last.Substring(0, n) + rest;
                    same = false;
                }
                list.Add(last);
            }
            if (!same)
            {
                lastRead = list.ToArray();
            }
            int count = r.Count(2);
            var changes = new List<SectionChange>(count);
            int sample = 0, section = 0;
            for (int i = 0; i < count; i++)
            {
                sample += r.Int();
                int step = r.Int();
                section += step >> 1;
                if (sample < 0 || section < 0 || section >= paths)
                {
                    throw new InvalidDataException("bad section change");
                }
                changes.Add(new SectionChange { Sample = sample, Section = section, On = (step & 1) != 0 });
            }
            a.SectionPaths = list;
            a.SectionChanges = changes;
        }
    }
}
