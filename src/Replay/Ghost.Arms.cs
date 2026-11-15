using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // An attempt's arms, ready to play (block 1): which arm it had out when, what each arm's animator did, and where
    // the whiplash's end was while it was out. Recordings from before have none: they punch from the samples' flags,
    // with your arm now, and never throw the whiplash.
    internal sealed partial class Ghost
    {
        public bool ArmsRecorded;
        public ArmSwitch[] ArmSwitches = Array.Empty<ArmSwitch>();
        public ArmMove[] ArmMoves = Array.Empty<ArmMove>();       // the arm's animator, in order
        public ArmMove[] WhipMoves = Array.Empty<ArmMove>();      // the whiplash's
        public float[] Punches = Array.Empty<float>();            // seconds in, each time it punched

        // The whiplash out, at the samples it was (in order): its phase, its end, and the sample its throw began.
        private int[] hookSamples = Array.Empty<int>();
        private HookPhase[] hookPhases = Array.Empty<HookPhase>();
        private Vector3[] hookPoints = Array.Empty<Vector3>();
        private int[] hookThrown = Array.Empty<int>();

        public bool ThrowsWhiplash => hookSamples.Length > 0;

        // (off the main thread, from FromRaw: plain maths)
        private static void ArmsFromRaw(RawAttempt raw, Ghost g)
        {
            var punches = new List<float>();
            float last = float.NegativeInfinity;
            for (int i = 0; i < g.Count; i++)
            {
                float t = i / (float)g.Rate;
                if ((g.Flags[i] & (byte)SampleFlags.Punch) != 0 && t - last >= 0.15f)
                {
                    punches.Add(t);
                    last = t;
                }
            }
            g.Punches = punches.ToArray();
            if (!raw.ArmsRecorded)
            {
                return;
            }
            g.ArmsRecorded = true;
            g.ArmSwitches = InOrder(raw.ArmSwitches, s => s.Sample);
            var arm = new List<ArmMove>();
            var whip = new List<ArmMove>();
            foreach (ArmMove m in raw.ArmMoves)
            {
                (m.Part == ArmPart.Whiplash ? whip : arm).Add(m);
            }
            g.ArmMoves = InOrder(arm, m => m.Sample);
            g.WhipMoves = InOrder(whip, m => m.Sample);

            HookPath h = raw.Hook;
            int n = h?.Count ?? 0;
            g.hookSamples = new int[n];
            g.hookPhases = new HookPhase[n];
            g.hookPoints = new Vector3[n];
            g.hookThrown = new int[n];
            for (int i = 0; i < n; i++)
            {
                g.hookSamples[i] = h.Sample[i];
                g.hookPhases[i] = (HookPhase)h.Phase[i];
                g.hookPoints[i] = new Vector3(h.X[i] * 0.01f, h.Y[i] * 0.01f, h.Z[i] * 0.01f);
                bool goesOn = i > 0 && h.Sample[i] == h.Sample[i - 1] + 1 && !(g.hookPhases[i] == HookPhase.Throwing && g.hookPhases[i - 1] != HookPhase.Throwing);
                g.hookThrown[i] = goesOn ? g.hookThrown[i - 1] : h.Sample[i];
            }
        }

        // (recorded in order; this only makes sure, keeping the order of the same moment)
        private static T[] InOrder<T>(List<T> items, Func<T, int> sample)
        {
            T[] sorted = items.ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                T item = sorted[i];
                int at = sample(item);
                int j = i - 1;
                for (; j >= 0 && sample(sorted[j]) > at; j--)
                {
                    sorted[j + 1] = sorted[j];
                }
                sorted[j + 1] = item;
            }
            return sorted;
        }

        // The arm it had out at a sample (None: recorded before arms were, or none out); since: when it switched to it.
        public ArmKind ArmAt(int sample, out int since)
        {
            since = 0;
            ArmKind arm = ArmKind.None;
            int lo = 0, hi = ArmSwitches.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (ArmSwitches[mid].Sample <= sample)
                {
                    arm = ArmSwitches[mid].Arm;
                    since = ArmSwitches[mid].Sample;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return arm;
        }

        // The first move after a sample.
        public static int FirstMoveAfter(ArmMove[] moves, int sample)
        {
            int lo = 0, hi = moves.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (moves[mid].Sample <= sample)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        // Seconds since it last punched, at t (infinity: it hadn't).
        public float SincePunch(float t)
        {
            int lo = 0, hi = Punches.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Punches[mid] <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found >= 0 ? t - Punches[found] : float.PositiveInfinity;
        }

        // Where the whiplash's end was t seconds in, if it was out of the hand (between samples, the way between), its
        // phase, and how long ago this throw began.
        public bool HookAt(float t, out Vector3 point, out HookPhase phase, out float thrownAgo)
        {
            point = default;
            phase = HookPhase.InHand;
            thrownAgo = 0f;
            if (hookSamples.Length == 0 || t < 0f || t > Duration)
            {
                return false;
            }
            float f = t * Rate;
            int sample = (int)f;
            int lo = 0, hi = hookSamples.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (hookSamples[mid] <= sample)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (found < 0 || hookSamples[found] != sample)
            {
                return false;
            }
            phase = hookPhases[found];
            point = hookPoints[found];
            if (found + 1 < hookSamples.Length && hookSamples[found + 1] == sample + 1 && !Snapped(sample + 1))
            {
                point = Vector3.Lerp(point, hookPoints[found + 1], f - sample);
            }
            thrownAgo = Mathf.Max(0f, t - hookThrown[found] / (float)Rate);
            return true;
        }
    }
}
