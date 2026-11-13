using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // One attempt, ready to play: positions in metres, rotations as quaternions. Every attempt starts at
    // replay time 0 (from the level's start or its checkpoint), like Geometry Dash's attempts.
    internal sealed class Ghost
    {
        public int Number;          // what the HUD calls it ("ATTEMPT 512")
        public int Order;           // 0..count-1, oldest first (colours)
        public AttemptEnd EndReason;
        public AttemptStart StartKind;
        public float Duration;
        public float LevelTime;     // the level timer when it ended (a finish's time)
        public bool Cheats;
        public int Rate;
        public int Count;
        public Color Color;
        public bool IsBest;

        public float CapsuleHeight;
        public float CapsuleRadius;
        public float CapsuleCenterY;

        public Vector3[] Body;
        public Vector3[] Eye;       // camera offset from Body
        public Quaternion[] Look;
        public byte[] Flags;
        public byte[] Hp;

        public WeaponChange[] Weapons = Array.Empty<WeaponChange>();
        public string[] WeaponNames = Array.Empty<string>();
        private UpChange[] ups = Array.Empty<UpChange>();

        public struct WeaponChange
        {
            public int Sample;
            public int Slot;
            public int Variant;
            public int Name;
        }

        private struct UpChange
        {
            public int Sample;
            public Vector3 Up;
        }

        public struct Pose
        {
            public Vector3 Body;
            public Vector3 Eye;      // world position of the eyes
            public Quaternion Look;
            public Vector3 Up;
            public int Sample;       // the sample at or before this moment
            public SampleFlags Flags;
            public byte Hp;
        }

        public bool Dies => EndReason == AttemptEnd.Death || EndReason == AttemptEnd.Restart || EndReason == AttemptEnd.Quit;

        public bool Finished => EndReason == AttemptEnd.Finish;

        public bool AliveAt(float t) => t >= 0f && t < Duration;

        // Off the main thread: only plain maths here (no Unity calls).
        public static Ghost FromRaw(RawAttempt raw, SessionHeader header)
        {
            int n = raw.Count;
            var g = new Ghost
            {
                Number = raw.Number,
                EndReason = raw.End,
                StartKind = raw.Start,
                Rate = Math.Max(1, raw.SampleRate),
                Count = n,
                LevelTime = raw.LevelTimeEnd,
                Cheats = raw.Cheats,
                CapsuleHeight = header?.CapsuleHeight ?? 3.5f,
                CapsuleRadius = header?.CapsuleRadius ?? 0.5f,
                CapsuleCenterY = header?.CapsuleCenterY ?? 0f,
                Body = new Vector3[n],
                Eye = new Vector3[n],
                Look = new Quaternion[n],
                Flags = new byte[n],
                Hp = new byte[n],
            };
            g.Duration = n <= 1 ? 0f : (n - 1) / (float)g.Rate;
            for (int i = 0; i < n; i++)
            {
                g.Body[i] = new Vector3(raw.PosX[i] * 0.01f, raw.PosY[i] * 0.01f, raw.PosZ[i] * 0.01f);
                g.Eye[i] = new Vector3(raw.EyeX[i] * 0.01f, raw.EyeY[i] * 0.01f, raw.EyeZ[i] * 0.01f);
                float x = raw.RotX[i], y = raw.RotY[i], z = raw.RotZ[i], w = raw.RotW[i];
                float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                g.Look[i] = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f);
            }
            Array.Copy(raw.Flags, g.Flags, n);
            Array.Copy(raw.Hp, g.Hp, n);

            var weapons = new List<WeaponChange>();
            var upChanges = new List<UpChange>();
            foreach (AttemptEvent e in raw.Events)
            {
                if (e.Kind == EventKind.Weapon)
                {
                    weapons.Add(new WeaponChange { Sample = e.Sample, Slot = e.A, Variant = e.B, Name = e.C });
                }
                else if (e.Kind == EventKind.Up)
                {
                    var up = new Vector3(e.A / 127f, e.B / 127f, e.C / 127f);
                    float len = (float)Math.Sqrt(up.x * up.x + up.y * up.y + up.z * up.z);
                    upChanges.Add(new UpChange { Sample = e.Sample, Up = len > 0.01f ? up / len : new Vector3(0f, 1f, 0f) });
                }
            }
            g.Weapons = weapons.ToArray();
            g.ups = upChanges.ToArray();
            g.WeaponNames = raw.WeaponNames.ToArray();
            return g;
        }

        // Where it is t seconds in (clamped to its last sample).
        public Pose PoseAt(float t)
        {
            float f = Mathf.Clamp(t * Rate, 0f, Count - 1);
            int i = (int)f;
            int j = Mathf.Min(i + 1, Count - 1);
            float a = f - i;
            if (j != i && (Flags[j] & (byte)SampleFlags.Snap) != 0)
            {
                a = 0f;  // (teleported: stay put until the jump)
            }
            Vector3 body = Vector3.LerpUnclamped(Body[i], Body[j], a);
            return new Pose
            {
                Body = body,
                Eye = body + Vector3.LerpUnclamped(Eye[i], Eye[j], a),
                Look = Quaternion.Slerp(Look[i], Look[j], a),
                Up = UpAt(i),
                Sample = i,
                Flags = (SampleFlags)Flags[a < 0.5f ? i : j],
                Hp = Hp[i],
            };
        }

        public Pose EndPose => PoseAt(Duration);

        public Vector3 UpAt(int sample)
        {
            Vector3 up = Vector3.up;
            for (int k = 0; k < ups.Length && ups[k].Sample <= sample; k++)
            {
                up = ups[k].Up;
            }
            return up;
        }

        // The weapon held at a sample (Slot 0: none).
        public WeaponChange WeaponAt(int sample)
        {
            int lo = 0, hi = Weapons.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Weapons[mid].Sample <= sample)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found >= 0 ? Weapons[found] : default;
        }

        public string WeaponName(WeaponChange w) => w.Slot > 0 && w.Name >= 0 && w.Name < WeaponNames.Length ? WeaponNames[w.Name] : null;

        // Lost HP within the last few samples (flashes red).
        public bool HurtAt(int sample)
        {
            int from = Mathf.Max(0, sample - Mathf.Max(1, Rate / 6));
            for (int k = from; k < sample; k++)
            {
                if (Hp[k + 1] < Hp[k])
                {
                    return true;
                }
            }
            return false;
        }
    }
}
