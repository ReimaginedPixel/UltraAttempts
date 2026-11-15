using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // An attempt's effects (format 5's block 3), ready to play: decoded off the main thread with the rest of its world
    // (only plain maths here). EffectPlayback copies the game's own prefabs for them.
    internal sealed partial class GhostWorld
    {
        public EffectPrefab[] EffectPrefabs = Array.Empty<EffectPrefab>();
        public EffectPath[] Effects = Array.Empty<EffectPath>();     // by when they show
        public int[] LookPrefab = Array.Empty<int>();                // each look's prefab (-1: none recorded)
        public int[] MomentPrefab = Array.Empty<int>();              // each moment's (a beam's) prefab (-1: none recorded)

        // What the main thread found for them (EffectPlayback fills these in).
        public GameObject[] FoundPrefabs;
        public bool[] PrefabsLooked;
        public GameObject[] FoundForLooks;
        public bool[] LooksLooked;

        // (after the rest of its world: the beams are matched to their moments)
        private void FillEffects(RawAttempt raw, int rate)
        {
            LookPrefab = new int[Looks.Length];
            MomentPrefab = new int[Moments.Length];
            for (int i = 0; i < LookPrefab.Length; i++)
            {
                LookPrefab[i] = -1;
            }
            for (int i = 0; i < MomentPrefab.Length; i++)
            {
                MomentPrefab[i] = -1;
            }
            if (raw.EffectPrefabs == null || raw.EffectPrefabs.Count == 0)
            {
                return;
            }
            EffectPrefabs = raw.EffectPrefabs.ToArray();
            int prefabs = EffectPrefabs.Length;
            foreach (EffectLink link in raw.LookPrefabs)
            {
                if (link.Index >= 0 && link.Index < LookPrefab.Length && link.Prefab >= 0 && link.Prefab < prefabs)
                {
                    LookPrefab[link.Index] = link.Prefab;
                }
            }
            foreach (EffectLink link in raw.BeamPrefabs)
            {
                if (link.Index < 0 || link.Index >= raw.WorldEvents.Count || link.Prefab < 0 || link.Prefab >= prefabs || raw.WorldEvents[link.Index].Kind != WorldEventKind.Beam)
                {
                    continue;
                }
                // (its moment: made from the same event the same way, so the same to the bit)
                WorldMoment m = WorldMoment.Of(raw.WorldEvents[link.Index], rate, Looks.Length, Paths.Length);
                for (int i = FirstMomentFrom(m.Time); i < Moments.Length && Moments[i].Time == m.Time; i++)
                {
                    if (Moments[i].Kind == WorldEventKind.Beam && MomentPrefab[i] < 0 && Moments[i].From == m.From && Moments[i].To == m.To)
                    {
                        MomentPrefab[i] = link.Prefab;
                        break;
                    }
                }
            }
            if (raw.Effects != null && raw.Effects.Count > 0)
            {
                var effects = new List<EffectPath>(raw.Effects.Count);
                foreach (EffectTrack track in raw.Effects)
                {
                    EffectPath e = EffectPath.FromTrack(track, rate, prefabs);
                    if (e != null)
                    {
                        effects.Add(e);
                        Weight += e.Count;
                    }
                }
                effects.Sort((a, b) => a.From.CompareTo(b.From));
                Effects = effects.ToArray();
            }
        }

        // The first effect showing at or after `time`.
        public int FirstEffectFrom(float time)
        {
            int lo = 0, hi = Effects.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Effects[mid].From < time)
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
    }

    // One of the game's own effects, ready to play: when it showed, when its particles stopped and when it went (in
    // seconds into the attempt), and its keys in metres in its frame.
    internal sealed class EffectPath
    {
        private const float JumpDistance = 10f;

        public int Prefab;
        public EffectFrame Frame;
        public float From;
        public float Stop = float.PositiveInfinity;   // its particles stopped coming (never: they didn't)
        public float To = float.PositiveInfinity;     // it went (never: still there when the attempt ended)
        public Vector3 BaseScale = Vector3.one;
        public bool Tinted;
        public Color Tint;
        public int Count;
        public float[] Time;
        public Vector3[] Pos;
        public Quaternion[] Rot;
        public float[] Scale;

        public bool Lasts => float.IsPositiveInfinity(To);

        public static EffectPath FromTrack(EffectTrack track, int rate, int prefabs)
        {
            if (track == null || track.Prefab < 0 || track.Prefab >= prefabs || track.Sample == null || track.PosX == null || track.PosY == null
                || track.PosZ == null || track.RotX == null || track.RotY == null || track.RotZ == null || track.RotW == null || track.Scale == null)
            {
                return null;
            }
            int n = track.Count;
            foreach (int length in new[] { track.Sample.Length, track.PosX.Length, track.PosY.Length, track.PosZ.Length, track.RotX.Length,
                track.RotY.Length, track.RotZ.Length, track.RotW.Length, track.Scale.Length })
            {
                n = Math.Min(n, length);
            }
            if (n < 1)
            {
                return null;
            }
            float units = track.Frame == EffectFrame.World ? 0.01f : 0.001f;
            var e = new EffectPath
            {
                Prefab = track.Prefab,
                Frame = track.Frame == EffectFrame.Camera || track.Frame == EffectFrame.Body || track.Frame == EffectFrame.Near ? track.Frame : EffectFrame.World,
                From = Math.Max(0, track.Start) / (float)rate,
                BaseScale = new Vector3(Sane(track.ScaleX), Sane(track.ScaleY), Sane(track.ScaleZ)),
                Tinted = track.Tint != 0,
                Tint = new Color((track.Tint >> 24) / 255f, ((track.Tint >> 16) & 255) / 255f, ((track.Tint >> 8) & 255) / 255f, 1f),
                Count = n,
                Time = new float[n],
                Pos = new Vector3[n],
                Rot = new Quaternion[n],
                Scale = new float[n],
            };
            if (track.Frame != e.Frame)
            {
                units = 0.01f;  // (a frame this version doesn't know: in the world)
            }
            float last = e.From;
            for (int k = 0; k < n; k++)
            {
                last = Math.Max(last, track.Sample[k] / (float)rate);  // (in order, whatever the file says)
                e.Time[k] = last;
                e.Pos[k] = new Vector3(track.PosX[k] * units, track.PosY[k] * units, track.PosZ[k] * units);
                float x = track.RotX[k], y = track.RotY[k], z = track.RotZ[k], w = track.RotW[k];
                float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                e.Rot[k] = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f);
                e.Scale[k] = track.Scale[k] / 1000f;
            }
            if (track.End >= 0)
            {
                e.To = Math.Max(e.From, track.End / (float)rate);
            }
            if (track.Stop >= 0)
            {
                e.Stop = Math.Max(e.From, track.Stop / (float)rate);
            }
            return e;
        }

        private static float Sane(float scale) => float.IsNaN(scale) || float.IsInfinity(scale) || Math.Abs(scale) < 1e-4f || Math.Abs(scale) > 1e4f ? 1f : scale;

        // How it was t seconds into the attempt, in its frame (held at its first and last keys).
        public void At(float t, out Vector3 pos, out Quaternion rot, out float scale)
        {
            int lo = 0, hi = Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Time[mid] <= t)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            int i = lo, j = Math.Min(i + 1, Count - 1);
            float span = Time[j] - Time[i];
            if (j == i || t <= Time[i] || span <= 1e-5f || (Pos[j] - Pos[i]).sqrMagnitude > JumpDistance * JumpDistance)
            {
                pos = Pos[i];
                rot = Rot[i];
                scale = Scale[i];
                return;
            }
            float a = Math.Min(1f, (t - Time[i]) / span);
            pos = Vector3.LerpUnclamped(Pos[i], Pos[j], a);
            rot = Quaternion.Slerp(Rot[i], Rot[j], a);
            scale = Scale[i] + (Scale[j] - Scale[i]) * a;
        }
    }
}
