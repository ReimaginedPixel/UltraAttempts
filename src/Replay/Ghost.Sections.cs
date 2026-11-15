using System.Collections.Generic;
using UltraAttempts.Recording;

namespace UltraAttempts.Replay
{
    internal sealed partial class Ghost
    {
        // Which parts of the level it had switched on, and when (SectionRecorder; null: recorded before them, or without
        // its world). Kept with it, like its sounds: it's small, and wanted at every cut.
        public GhostSections Sections;
    }

    // An attempt's parts of the level, ready to play: each one's state when it started, then the changes after. Plain
    // data, made off the main thread (SectionPlayback finds the parts in the level).
    internal sealed class GhostSections
    {
        public string[] Paths;      // one array for every attempt with the same list (every attempt of a visit to the level)
        public byte[] Known;        // a bit each: its state when it started is known...
        public byte[] On;           // ...and it was on
        public int[] At;            // every change after the start, in order: its sample...
        public int[] What;          // ...and its part << 1 | on

        private static readonly object gate = new object();
        private static readonly List<string[]> recent = new List<string[]>();   // the last few lists made

        public bool KnownAtStart(int i) => (Known[i >> 3] & (1 << (i & 7))) != 0;

        public bool OnAtStart(int i) => (On[i >> 3] & (1 << (i & 7))) != 0;

        public static GhostSections FromRaw(RawAttempt raw)
        {
            int n = raw.SectionPaths != null ? raw.SectionPaths.Count : 0;
            if (n == 0 || raw.SectionChanges == null)
            {
                return null;
            }
            var s = new GhostSections
            {
                Paths = Shared(raw.SectionPaths),
                Known = new byte[(n + 7) / 8],
                On = new byte[(n + 7) / 8],
            };
            var later = new List<SectionChange>();
            foreach (SectionChange c in raw.SectionChanges)
            {
                if (c.Section < 0 || c.Section >= n)
                {
                    continue;
                }
                if (c.Sample > 0)
                {
                    later.Add(c);
                    continue;
                }
                byte bit = (byte)(1 << (c.Section & 7));
                s.Known[c.Section >> 3] |= bit;
                if (c.On)
                {
                    s.On[c.Section >> 3] |= bit;
                }
                else
                {
                    s.On[c.Section >> 3] &= (byte)~bit;
                }
            }
            // (in order of when, keeping the order of two at once)
            for (int i = 1; i < later.Count; i++)
            {
                SectionChange c = later[i];
                int j = i - 1;
                for (; j >= 0 && later[j].Sample > c.Sample; j--)
                {
                    later[j + 1] = later[j];
                }
                later[j + 1] = c;
            }
            s.At = new int[later.Count];
            s.What = new int[later.Count];
            for (int i = 0; i < later.Count; i++)
            {
                s.At[i] = later[i].Sample;
                s.What[i] = (later[i].Section << 1) | (later[i].On ? 1 : 0);
            }
            return s;
        }

        // The same list as an attempt read just before (they nearly always are): one array for them all.
        private static string[] Shared(List<string> paths)
        {
            lock (gate)
            {
                foreach (string[] r in recent)
                {
                    if (Same(r, paths))
                    {
                        return r;
                    }
                }
                string[] made = paths.ToArray();
                recent.Insert(0, made);
                if (recent.Count > 4)
                {
                    recent.RemoveAt(recent.Count - 1);
                }
                return made;
            }
        }

        private static bool Same(string[] a, List<string> b)
        {
            if (a.Length != b.Count)
            {
                return false;
            }
            for (int i = a.Length - 1; i >= 0; i--)
            {
                if (!string.Equals(a[i], b[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
