using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // One attempt, ready to play: positions in metres, rotations as quaternions. Every attempt starts at
    // replay time 0 (from the level's start or its checkpoint), like Geometry Dash's attempts.
    internal sealed partial class Ghost
    {
        public int Number;          // what the HUD calls it ("ATTEMPT 512")
        public int Order;           // 0..count-1, oldest first (colours)
        public int Index;           // its place in the replay's list (per-frame caches)
        public AttemptEnd EndReason;
        public AttemptStart StartKind;
        public float Duration;
        public float LevelTime;     // the level timer when it ended (a finish's time)
        public bool Cheats;
        public int Rate;
        public int Count;
        public Color Color;
        public bool IsBest;

        // When it was played: a run can go on for days (and across game launches).
        public long StartedUtcTicks;
        public int Run;             // which run it's from
        public int Day;             // which day of its run (1: the day the run started; 0: unknown)
        public int RunDays;         // how many days its run spans
        public string When = "";    // "13 NOV 18:42", local time ("": unknown)

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
        private int[] snaps = Array.Empty<int>();       // the samples it teleported into, in order

        // The world it saw (recordings from before 1.1 have none): its doors, always here; the enemies it met
        // and everything else (WorldDetail = Everything), in World while the WorldCache keeps it (else read
        // again from Source when it's wanted).
        public string[] DoorPaths = Array.Empty<string>();
        public DoorChange[] Doors = Array.Empty<DoorChange>();    // in order; sample 0 has every door's start
        public Dictionary<int, int> MostAtOnce = NoEnemies;        // enemy type -> most of it around at once
        public int EnemyCount;
        public int ThingCount;
        public bool HasEverything;                                 // things, explosions or beams recorded
        public GhostWorld World;
        public WorldSource Source;
        public bool WorldFailed;
        public int WorldUsed;
        private float[] kills = Array.Empty<float>();             // when each enemy it met died, in order

        // The game's own sounds as it heard them (Recording.RecordSounds; kept, unlike its world).
        public GhostSounds Sounds;

        private static readonly Dictionary<int, int> NoEnemies = new Dictionary<int, int>();

        public struct WeaponChange
        {
            public int Sample;
            public int Slot;
            public int Variant;
            public int Name;
        }

        public struct DoorChange
        {
            public int Sample;
            public int Door;        // index into DoorPaths
            public bool Open;
            public DoorDetail Detail;
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

        public bool HasEnemies => EnemyCount > 0;

        public bool HasWorld => EnemyCount > 0 || Doors.Length > 0 || HasEverything;

        // Something in World to decode (doors are always here).
        public bool HasWorldData => EnemyCount > 0 || HasEverything;

        public bool AliveAt(float t) => t >= 0f && t < Duration;

        // The eyes' offset from the body only changes smoothly (sliding down, standing up), but 1.0
        // recorded ULTRAKILL's screen shake into it: about 20 cm from one sample to the next in real runs.
        // A median over +-radius samples takes the shake out and keeps the slides; recordings from 1.1 on
        // (already steady) come through unchanged.
        private static void Steady(Vector3[] eye, int radius)
        {
            int n = eye.Length;
            if (n < 3)
            {
                return;
            }
            var window = new float[radius * 2 + 1];
            var steadied = new Vector3[n];
            for (int axis = 0; axis < 3; axis++)
            {
                for (int i = 0; i < n; i++)
                {
                    int from = Math.Max(0, i - radius), to = Math.Min(n - 1, i + radius), count = 0;
                    for (int k = from; k <= to; k++)
                    {
                        float v = eye[k][axis];
                        int j = count++;
                        for (; j > 0 && window[j - 1] > v; j--)
                        {
                            window[j] = window[j - 1];
                        }
                        window[j] = v;
                    }
                    Vector3 s = steadied[i];
                    s[axis] = window[count / 2];
                    steadied[i] = s;
                }
            }
            Array.Copy(steadied, eye, n);
        }

        // Off the main thread: only plain maths here (no Unity calls). Its world comes decoded (the loader lets
        // go of all but the newest few; the WorldCache reads them again from Source as they're wanted).
        public static Ghost FromRaw(RawAttempt raw, SessionHeader header)
        {
            int n = raw.Count;
            var g = new Ghost
            {
                Number = raw.Number,
                EndReason = raw.End,
                StartKind = raw.Start,
                StartedUtcTicks = raw.StartedUtcTicks,
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
            var snapped = new List<int>();
            for (int i = 0; i < n; i++)
            {
                g.Body[i] = new Vector3(raw.PosX[i] * 0.01f, raw.PosY[i] * 0.01f, raw.PosZ[i] * 0.01f);
                g.Eye[i] = new Vector3(raw.EyeX[i] * 0.01f, raw.EyeY[i] * 0.01f, raw.EyeZ[i] * 0.01f);
                float x = raw.RotX[i], y = raw.RotY[i], z = raw.RotZ[i], w = raw.RotW[i];
                float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                g.Look[i] = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f);
                if (i > 0 && (raw.Flags[i] & (byte)SampleFlags.Snap) != 0)
                {
                    snapped.Add(i);
                }
            }
            Array.Copy(raw.Flags, g.Flags, n);
            Array.Copy(raw.Hp, g.Hp, n);
            g.snaps = snapped.ToArray();
            Steady(g.Eye, Math.Max(1, g.Rate / 10));

            var weapons = new List<WeaponChange>();
            var upChanges = new List<UpChange>();
            var doors = new List<DoorChange>();
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
                else if (e.Kind == EventKind.Door && e.A >= 0)
                {
                    doors.Add(new DoorChange { Sample = e.Sample, Door = e.A, Open = e.B != 0, Detail = (DoorDetail)e.C });
                }
            }
            g.Weapons = weapons.ToArray();
            g.ups = upChanges.ToArray();
            g.WeaponNames = raw.WeaponNames.ToArray();
            g.Doors = InOrder(doors);
            g.DoorPaths = raw.DoorPaths != null ? raw.DoorPaths.ToArray() : Array.Empty<string>();
            g.Sounds = GhostSounds.FromRaw(raw, g.Rate);
            ArmsFromRaw(raw, g);
            if (raw.SourcePath != null)
            {
                g.Source = new WorldSource { Path = raw.SourcePath, Offset = raw.SourceOffset, Length = raw.SourceLength };
            }
            if ((raw.Enemies != null && raw.Enemies.Count > 0) || raw.HasThings)
            {
                GhostWorld world = GhostWorld.FromRaw(raw, g.Rate);
                Summarize(g, world);
                g.World = world;
            }
            return g;
        }

        // (stable: two changes to one door at the same sample keep their order)
        private static DoorChange[] InOrder(List<DoorChange> doors)
        {
            DoorChange[] sorted = doors.ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                DoorChange d = sorted[i];
                int j = i - 1;
                for (; j >= 0 && sorted[j].Sample > d.Sample; j--)
                {
                    sorted[j + 1] = sorted[j];
                }
                sorted[j + 1] = d;
            }
            return sorted;
        }

        // What's kept of its world while the world itself isn't: when its enemies died (the HUD's kills), how
        // many of each kind were about at once (the replay's copies), whether there's more than enemies.
        private static void Summarize(Ghost g, GhostWorld world)
        {
            var kills = new List<float>();
            foreach (EnemyPath e in world.Enemies)
            {
                if (e.Died)
                {
                    kills.Add(e.To);
                }
            }
            kills.Sort();
            g.kills = kills.ToArray();
            g.EnemyCount = world.Enemies.Length;
            g.ThingCount = world.Things.Length;
            g.HasEverything = world.Things.Length > 0 || world.Moments.Length > 0 || world.Breaks.Length > 0;
            g.MostAtOnce = world.Enemies.Length > 0 ? MostOfEachAtOnce(world.Enemies) : NoEnemies;
        }

        // For each type of enemy, the most of it around at the same time (how many copies a replay needs).
        private static Dictionary<int, int> MostOfEachAtOnce(EnemyPath[] enemies)
        {
            var edges = new List<(float time, int type, int step)>(enemies.Length * 2);
            foreach (EnemyPath e in enemies)
            {
                edges.Add((e.From, e.Type, 1));
                edges.Add((e.To, e.Type, -1));
            }
            // (at the same moment, arrivals first: one leaving and one arriving can both need a copy)
            edges.Sort((a, b) => a.time != b.time ? a.time.CompareTo(b.time) : b.step.CompareTo(a.step));
            var now = new Dictionary<int, int>();
            var most = new Dictionary<int, int>();
            foreach (var (_, type, step) in edges)
            {
                now.TryGetValue(type, out int count);
                count += step;
                now[type] = count;
                if (!most.TryGetValue(type, out int best) || count > best)
                {
                    most[type] = count;
                }
            }
            return most;
        }

        // Where it is t seconds in (clamped to its last sample). A smooth curve through the samples (never
        // across a teleport), so it doesn't move in straight lines that kink 60 times a second.
        public Pose PoseAt(float t)
        {
            float f = Mathf.Clamp(t * Rate, 0f, Count - 1);
            int i = (int)f;
            int j = Mathf.Min(i + 1, Count - 1);
            float a = f - i;
            Vector3 body, eye;
            Quaternion look;
            if (j == i || a <= 0f || Snapped(j))
            {
                // (on a sample, or teleported: stay put until the jump)
                a = 0f;
                body = Body[i];
                eye = Eye[i];
                look = Look[i];
            }
            else
            {
                int h = i > 0 && !Snapped(i) ? i - 1 : -1;
                int k = j + 1 < Count && !Snapped(j + 1) ? j + 1 : -1;
                body = Curve(Body, h, i, j, k, a);
                eye = Curve(Eye, h, i, j, k, a);
                look = Turn(h, i, j, k, a);
            }
            return new Pose
            {
                Body = body,
                Eye = body + eye,
                Look = look,
                Up = UpAt(i),
                Sample = i,
                Flags = (SampleFlags)Flags[a < 0.5f ? i : j],
                Hp = Hp[i],
            };
        }

        // PoseAt averaged over `radius` seconds either side, weighted to the middle: what the camera sees
        // through its eyes. It takes out the jitter without lagging behind (the whole attempt is known ahead),
        // and never reaches across a teleport.
        public Pose SmoothPoseAt(float t, float radius)
        {
            Pose p = PoseAt(t);
            if (radius < 0.002f || Count < 3)
            {
                return p;
            }
            Segment(p.Sample, out float from, out float to);
            const int Steps = 7;
            Vector3 body = Vector3.zero, eye = Vector3.zero;
            float qx = 0f, qy = 0f, qz = 0f, qw = 0f, total = 0f;
            for (int s = -Steps; s <= Steps; s++)
            {
                float x = s / (float)(Steps + 1);
                float w = 1f - x * x;
                Pose q = PoseAt(Mathf.Clamp(t + x * radius, from, to));
                Quaternion r = Near(q.Look, p.Look);
                body += q.Body * w;
                eye += q.Eye * w;
                qx += r.x * w;
                qy += r.y * w;
                qz += r.z * w;
                qw += r.w * w;
                total += w;
            }
            p.Body = body / total;
            p.Eye = eye / total;
            float len = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len > 1e-4f)
            {
                p.Look = new Quaternion(qx / len, qy / len, qz / len, qw / len);
            }
            return p;
        }

        public Pose EndPose => PoseAt(Duration);

        private bool Snapped(int sample) => (Flags[sample] & (byte)SampleFlags.Snap) != 0;

        // The stretch between teleports that a sample is in, in seconds.
        private void Segment(int sample, out float from, out float to)
        {
            int lo = 0, hi = snaps.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (snaps[mid] <= sample)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            from = lo > 0 ? snaps[lo - 1] / (float)Rate : 0f;
            to = lo < snaps.Length ? (snaps[lo] - 1) / (float)Rate : Duration;
        }

        // A smooth path through samples i and j (= i + 1), a of the way along, keeping to the speed they had.
        // h and k are the samples either side (-1: none, or across a teleport). Cubic Hermite, each sample's
        // slope from its neighbours, flat where an axis turns back and capped so it never overshoots (it
        // can't bounce through a wall it stopped at, or wobble past where a flick ended).
        internal static Vector3 Curve(Vector3[] p, int h, int i, int j, int k, float a)
        {
            Vector3 p1 = p[i], p2 = p[j];
            Vector3 d1 = p2 - p1;
            Vector3 d0 = h >= 0 ? p1 - p[h] : d1;
            Vector3 d2 = k >= 0 ? p[k] - p2 : d1;
            Basis(a, out float h00, out float h10, out float h01, out float h11);
            return new Vector3(
                h00 * p1.x + h10 * Slope(d0.x, d1.x) + h01 * p2.x + h11 * Slope(d1.x, d2.x),
                h00 * p1.y + h10 * Slope(d0.y, d1.y) + h01 * p2.y + h11 * Slope(d1.y, d2.y),
                h00 * p1.z + h10 * Slope(d0.z, d1.z) + h01 * p2.z + h11 * Slope(d1.z, d2.z));
        }

        // The same for where it looked: the quaternions, all turned to the same side, on that curve.
        private Quaternion Turn(int h, int i, int j, int k, float a)
        {
            Quaternion q1 = Look[i];
            Quaternion q2 = Near(Look[j], q1);
            if (q1.x * q2.x + q1.y * q2.y + q1.z * q2.z + q1.w * q2.w < 0.7f)
            {
                return Quaternion.Slerp(q1, q2, a);  // (a flick of over 90 degrees in one sample: just turn evenly)
            }
            Quaternion q0 = h >= 0 ? Near(Look[h], q1) : q1;
            Quaternion q3 = k >= 0 ? Near(Look[k], q2) : q2;
            Basis(a, out float h00, out float h10, out float h01, out float h11);
            float x = Mix(q0.x, q1.x, q2.x, q3.x, h >= 0, k >= 0, h00, h10, h01, h11);
            float y = Mix(q0.y, q1.y, q2.y, q3.y, h >= 0, k >= 0, h00, h10, h01, h11);
            float z = Mix(q0.z, q1.z, q2.z, q3.z, h >= 0, k >= 0, h00, h10, h01, h11);
            float w = Mix(q0.w, q1.w, q2.w, q3.w, h >= 0, k >= 0, h00, h10, h01, h11);
            float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 0.5f)
            {
                return Quaternion.Slerp(q1, q2, a);
            }
            return new Quaternion(x / len, y / len, z / len, w / len);
        }

        private static float Mix(float v0, float v1, float v2, float v3, bool before, bool after, float h00, float h10, float h01, float h11)
        {
            float d1 = v2 - v1;
            float d0 = before ? v1 - v0 : d1;
            float d2 = after ? v3 - v2 : d1;
            return h00 * v1 + h10 * Slope(d0, d1) + h01 * v2 + h11 * Slope(d1, d2);
        }

        private static void Basis(float a, out float h00, out float h10, out float h01, out float h11)
        {
            float a2 = a * a, a3 = a2 * a;
            h00 = 2f * a3 - 3f * a2 + 1f;
            h10 = a3 - 2f * a2 + a;
            h01 = 3f * a2 - 2f * a3;
            h11 = a3 - a2;
        }

        // A sample's slope from the steps either side of it (Fritsch-Carlson: monotone, no overshoot).
        private static float Slope(float before, float after)
        {
            if (before * after <= 0f)
            {
                return 0f;
            }
            float m = (before + after) * 0.5f;
            float cap = 3f * Math.Min(Math.Abs(before), Math.Abs(after));
            return m > cap ? cap : m < -cap ? -cap : m;
        }

        // q or -q (the same rotation), whichever is on the same side as `like`.
        internal static Quaternion Near(Quaternion q, Quaternion like)
        {
            return q.x * like.x + q.y * like.y + q.z * like.z + q.w * like.w < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
        }

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

        // How many of the enemies it met had died t seconds in.
        public int KillsAt(float t)
        {
            int lo = 0, hi = kills.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (kills[mid] <= t)
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

    // One enemy an attempt met, ready to play (from an EnemyTrack): metres, quaternions, its animation
    // state and normalized time, its health. Sample k is at From + k / (rate / stride) seconds.
    internal sealed class EnemyPath
    {
        private const float TeleportDistance = 10f;

        public int Type;            // (int)EnemyType, -1 if unknown
        public string Name = "";
        public float Scale = 1f;
        public bool Died;           // its last sample is its death
        public float From;          // seconds into the attempt it showed up
        public float To;            // ... and its last sample
        public int Count;
        public Vector3[] Pos;
        public Quaternion[] Rot;
        public int[] Anim;
        public float[] AnimTime;
        public byte[] Health;       // 255: as it was when it showed up
        private float perSecond;

        public struct State
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public int Anim;         // its animator's state (0: none)
            public float AnimTime;   // normalized time in it
            public byte Health;
        }

        // Off the main thread (no Unity calls). Null if there's nothing to it.
        public static EnemyPath FromTrack(EnemyTrack track, int stride, int rate)
        {
            if (track == null || track.PosX == null || track.PosY == null || track.PosZ == null || track.RotX == null || track.RotY == null
                || track.RotZ == null || track.RotW == null || track.Anim == null || track.AnimTime == null || track.Health == null)
            {
                return null;
            }
            int n = track.Count;
            foreach (int length in new[] { track.PosX.Length, track.PosY.Length, track.PosZ.Length, track.RotX.Length, track.RotY.Length,
                track.RotZ.Length, track.RotW.Length, track.Anim.Length, track.AnimTime.Length, track.Health.Length })
            {
                n = Math.Min(n, length);
            }
            if (n < 1)
            {
                return null;
            }
            var e = new EnemyPath
            {
                Type = track.Type,
                Name = track.Name ?? "",
                Scale = track.Scale > 0.001f && !float.IsInfinity(track.Scale) ? track.Scale : 1f,
                Died = track.Died,
                Count = n,
                Pos = new Vector3[n],
                Rot = new Quaternion[n],
                Anim = new int[n],
                AnimTime = new float[n],
                Health = new byte[n],
                perSecond = rate / (float)stride,
            };
            int start = Math.Max(0, track.Start);
            e.From = start / (float)rate;
            e.To = (start + (n - 1) * stride) / (float)rate;
            for (int k = 0; k < n; k++)
            {
                e.Pos[k] = new Vector3(track.PosX[k] * 0.01f, track.PosY[k] * 0.01f, track.PosZ[k] * 0.01f);
                float x = track.RotX[k], y = track.RotY[k], z = track.RotZ[k], w = track.RotW[k];
                float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                e.Rot[k] = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f);
                e.Anim[k] = track.Anim[k];
                e.AnimTime[k] = track.AnimTime[k] / 65535f;
                e.Health[k] = track.Health[k];
            }
            return e;
        }

        // Where it was and what it was doing t seconds into the attempt (clamped to its own samples).
        public State At(float t)
        {
            float f = Mathf.Clamp((t - From) * perSecond, 0f, Count - 1);
            int i = (int)f;
            int j = Math.Min(i + 1, Count - 1);
            float a = f - i;
            var s = new State { Health = Health[i] };
            if (j == i || a <= 0f || Jumped(i, j))
            {
                s.Pos = Pos[i];
                s.Rot = Rot[i];
                s.Anim = Anim[i];
                s.AnimTime = AnimTime[i];
                return s;
            }
            int h = i > 0 && !Jumped(i - 1, i) ? i - 1 : -1;
            int k = j + 1 < Count && !Jumped(j, j + 1) ? j + 1 : -1;
            s.Pos = Ghost.Curve(Pos, h, i, j, k, a);
            s.Rot = Quaternion.Slerp(Rot[i], Rot[j], a);
            if (Anim[i] == Anim[j])
            {
                float from = AnimTime[i], to = AnimTime[j];
                bool wrapped = to < from - 0.5f;  // (a looping state went round, or a one-shot started over)
                if (wrapped)
                {
                    to += 1f;
                }
                s.Anim = Anim[i];
                s.AnimTime = from + (to - from) * a;
                if (wrapped && s.AnimTime >= 1f)
                {
                    s.AnimTime -= 1f;
                }
            }
            else
            {
                s.Anim = a < 0.5f ? Anim[i] : Anim[j];
                s.AnimTime = a < 0.5f ? AnimTime[i] : AnimTime[j];
            }
            return s;
        }

        // Whether it lost health within `window` seconds up to t: how long ago, and how much of what it had.
        public bool HitAt(float t, float window, out float ago, out float amount)
        {
            ago = 0f;
            amount = 0f;
            if (t < From || Count < 2)
            {
                return false;
            }
            int i = Math.Min((int)((t - From) * perSecond), Count - 1);
            for (int k = i; k >= 1; k--)
            {
                float at = From + k / perSecond;
                if (t - at > window)
                {
                    break;
                }
                if (Health[k] < Health[k - 1])
                {
                    ago = t - at;
                    amount = (Health[k - 1] - Health[k]) / 255f;
                    return true;
                }
            }
            return false;
        }

        private bool Jumped(int i, int j) => (Pos[j] - Pos[i]).sqrMagnitude > TeleportDistance * TeleportDistance;
    }
}
