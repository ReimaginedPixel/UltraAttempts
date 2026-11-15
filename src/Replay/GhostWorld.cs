using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // One attempt's world, decoded and ready to play: the enemies it met, the things it saw move, what they
    // looked like, the explosions, beams and breaks. Decoded off the main thread (only plain maths here), kept
    // by the WorldCache while it's needed, and read again from its run file when it's wanted after that.
    internal sealed partial class GhostWorld
    {
        public EnemyPath[] Enemies = Array.Empty<EnemyPath>();
        public ThingPath[] Things = Array.Empty<ThingPath>();       // by when they show up
        public WorldMoment[] Moments = Array.Empty<WorldMoment>();  // explosions and beams, in order
        public WorldMoment[] Breaks = Array.Empty<WorldMoment>();   // breakables breaking, in order
        public ThingLook[] Looks = Array.Empty<ThingLook>();
        public string[] LookKeys = Array.Empty<string>();          // each look's signature (the same look across attempts)
        public ResolvedLook[] Resolved;                            // each look found again (the main thread fills it in)
        public string[] Paths = Array.Empty<string>();
        public bool HasBeams;
        public long Weight;                                        // about how big it is (samples and keys)

        // The longest anything in Moments shows for (finding the ones showing now looks back this far).
        public const float LongestMoment = 3.6f;

        public static GhostWorld FromRaw(RawAttempt raw, int rate)
        {
            var w = new GhostWorld();
            rate = Math.Max(1, rate);
            if (raw.Enemies != null && raw.Enemies.Count > 0)
            {
                int stride = Math.Max(1, raw.EnemyStride);
                var enemies = new List<EnemyPath>(raw.Enemies.Count);
                foreach (EnemyTrack track in raw.Enemies)
                {
                    EnemyPath e = EnemyPath.FromTrack(track, stride, rate);
                    if (e != null)
                    {
                        enemies.Add(e);
                        w.Weight += e.Count;
                    }
                }
                w.Enemies = enemies.ToArray();
            }
            w.Looks = raw.Looks != null ? raw.Looks.ToArray() : Array.Empty<ThingLook>();
            w.LookKeys = new string[w.Looks.Length];
            for (int i = 0; i < w.Looks.Length; i++)
            {
                w.LookKeys[i] = Signature(w.Looks[i]);
            }
            w.Paths = raw.ThingPaths != null ? raw.ThingPaths.ToArray() : Array.Empty<string>();
            if (raw.Things != null && raw.Things.Count > 0)
            {
                var things = new List<ThingPath>(raw.Things.Count);
                foreach (ThingTrack track in raw.Things)
                {
                    ThingPath p = ThingPath.FromTrack(track, rate, w.Looks.Length, w.Paths.Length);
                    if (p != null)
                    {
                        things.Add(p);
                        w.Weight += p.Count;
                    }
                }
                things.Sort((a, b) => a.From.CompareTo(b.From));
                w.Things = things.ToArray();
            }
            if (raw.WorldEvents != null && raw.WorldEvents.Count > 0)
            {
                var moments = new List<WorldMoment>();
                var breaks = new List<WorldMoment>();
                foreach (WorldEvent e in raw.WorldEvents)
                {
                    WorldMoment m = WorldMoment.Of(e, rate, w.Looks.Length, w.Paths.Length);
                    if (m.Kind == WorldEventKind.Break)
                    {
                        breaks.Add(m);
                    }
                    else if (m.Kind == WorldEventKind.Explosion || m.Kind == WorldEventKind.Beam)
                    {
                        moments.Add(m);
                        w.HasBeams |= m.Kind == WorldEventKind.Beam;
                    }
                }
                w.Moments = InOrder(moments);
                w.Breaks = InOrder(breaks);
                w.Weight += moments.Count + breaks.Count;
            }
            return w;
        }

        // (stable, by time)
        private static WorldMoment[] InOrder(List<WorldMoment> list)
        {
            WorldMoment[] sorted = list.ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                WorldMoment m = sorted[i];
                int j = i - 1;
                for (; j >= 0 && sorted[j].Time > m.Time; j--)
                {
                    sorted[j + 1] = sorted[j];
                }
                sorted[j + 1] = m;
            }
            return sorted;
        }

        // The first moment at or after `time`.
        public int FirstMomentFrom(float time)
        {
            int lo = 0, hi = Moments.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Moments[mid].Time < time)
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

        private static string Signature(ThingLook look)
        {
            if (look == null)
            {
                return "";
            }
            var s = new System.Text.StringBuilder(look.Name ?? "");
            s.Append('|').Append(look.Color);
            foreach (LookPart p in look.Parts)
            {
                s.Append('|').Append(p.Mesh).Append(':').Append(p.Vertices);
                foreach (string m in p.Materials ?? Array.Empty<string>())
                {
                    s.Append(':').Append(m);
                }
                s.Append('@').Append(p.PX).Append(',').Append(p.PY).Append(',').Append(p.PZ).Append(',').Append(p.RX).Append(',').Append(p.RY).Append(',').Append(p.RZ)
                    .Append(',').Append(p.RW).Append(',').Append(p.SX).Append(',').Append(p.SY).Append(',').Append(p.SZ);
            }
            s.Append('#').Append(look.CenterX).Append(',').Append(look.CenterY).Append(',').Append(look.CenterZ).Append(',')
                .Append(look.SizeX).Append(',').Append(look.SizeY).Append(',').Append(look.SizeZ);
            return s.ToString();
        }
    }

    // One thing an attempt saw move, ready to play: its keys in seconds and metres. It goes straight from one key
    // to the next (a rest is a key repeated where it waited); it's there from its first key (a level object: all
    // along) until To (or the attempt's end, if it didn't go).
    internal sealed class ThingPath
    {
        private const float JumpDistance = 10f;

        public ThingKind Kind;
        public int Look;            // -1: none
        public int Path;            // -1: spawned
        public Vector3 BaseScale = Vector3.one;
        public bool Gone;
        public float From;
        public float To;
        public int Count;
        public float[] Time;
        public Vector3[] Pos;
        public Quaternion[] Rot;
        public float[] Scale;

        public bool Level => Path >= 0;

        public static ThingPath FromTrack(ThingTrack track, int rate, int looks, int paths)
        {
            if (track == null || track.Sample == null || track.PosX == null || track.PosY == null || track.PosZ == null || track.RotX == null
                || track.RotY == null || track.RotZ == null || track.RotW == null || track.Scale == null)
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
            var p = new ThingPath
            {
                Kind = track.Kind,
                Look = track.Look >= 0 && track.Look < looks ? track.Look : -1,
                Path = track.Path >= 0 && track.Path < paths ? track.Path : -1,
                BaseScale = new Vector3(Sane(track.ScaleX), Sane(track.ScaleY), Sane(track.ScaleZ)),
                Gone = track.Gone,
                Count = n,
                Time = new float[n],
                Pos = new Vector3[n],
                Rot = new Quaternion[n],
                Scale = new float[n],
            };
            float last = float.NegativeInfinity;
            for (int k = 0; k < n; k++)
            {
                last = Math.Max(last, track.Sample[k] / (float)rate);  // (in order, whatever the file says)
                p.Time[k] = last;
                p.Pos[k] = new Vector3(track.PosX[k] * 0.01f, track.PosY[k] * 0.01f, track.PosZ[k] * 0.01f);
                float x = track.RotX[k], y = track.RotY[k], z = track.RotZ[k], w = track.RotW[k];
                float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                p.Rot[k] = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f);
                p.Scale[k] = track.Scale[k] / 1000f;
            }
            p.From = p.Time[0];
            p.To = Math.Max(p.Time[n - 1], track.End / (float)rate);
            return p;
        }

        private static float Sane(float scale) => float.IsNaN(scale) || float.IsInfinity(scale) || Math.Abs(scale) < 1e-4f || Math.Abs(scale) > 1e4f ? 1f : scale;

        // Whether it's there t seconds into the attempt (end: the attempt's end), and how.
        public bool At(float t, float end, out Vector3 pos, out Quaternion rot, out float scale)
        {
            pos = default;
            rot = default;
            scale = 1f;
            if ((!Level && t < From) || t > (Gone ? To : Math.Max(To, end)))
            {
                return false;
            }
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
                return true;
            }
            float a = Math.Min(1f, (t - Time[i]) / span);
            pos = Vector3.LerpUnclamped(Pos[i], Pos[j], a);
            rot = Quaternion.Slerp(Rot[i], Rot[j], a);
            scale = Scale[i] + (Scale[j] - Scale[i]) * a;
            return true;
        }
    }

    // An explosion, a beam or a break, ready to play.
    internal struct WorldMoment
    {
        public float Time;
        public WorldEventKind Kind;
        public Vector3 From;
        public Vector3 To;
        public float Size;          // metres
        public float Life;          // seconds
        public Color Color;
        public int Look;
        public int Path;
        public Quaternion Rot;
        public float StartScale;    // an explosion's scale when it went off, and how much it grows a second (0: not recorded)
        public float Growth;

        public static WorldMoment Of(WorldEvent e, int rate, int looks, int paths)
        {
            float x = e.RotX, y = e.RotY, z = e.RotZ, w = e.RotW;
            float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            uint c = e.Color;
            return new WorldMoment
            {
                Time = Math.Max(0, e.Sample) / (float)Math.Max(1, rate),
                Kind = e.Kind,
                From = new Vector3(e.X * 0.01f, e.Y * 0.01f, e.Z * 0.01f),
                To = new Vector3(e.ToX * 0.01f, e.ToY * 0.01f, e.ToZ * 0.01f),
                Size = Mathf.Clamp(e.Size / 1000f, 0f, 500f),
                Life = Mathf.Clamp(e.Life / 1000f, 0.02f, 3f),
                Color = new Color((c >> 24) / 255f, ((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f),
                Look = e.Look >= 0 && e.Look < looks ? e.Look : -1,
                Path = e.Path >= 0 && e.Path < paths ? e.Path : -1,
                Rot = len > 1f ? new Quaternion(x / len, y / len, z / len, w / len) : new Quaternion(0f, 0f, 0f, 1f),
                StartScale = e.Kind == WorldEventKind.Explosion ? ExplosionGrowth.StartScale(e) : 0f,
                Growth = e.Kind == WorldEventKind.Explosion ? ExplosionGrowth.PerSecond(e) : 0f,
            };
        }
    }

    // Where an attempt's world can be read again from (its run file).
    internal sealed class WorldSource
    {
        public string Path;
        public long Offset;
        public int Length;
    }

    // The attempts' worlds, decoded as they're needed on worker threads and kept while they're used (at most
    // Keep of them besides the ones wanted this frame). A world asked for that isn't ready is null this frame.
    internal sealed class WorldCache
    {
        private const int Keep = 32;
        private const int Working = 2;

        private readonly Dictionary<Ghost, Task<GhostWorld>> pending = new Dictionary<Ghost, Task<GhostWorld>>();
        private readonly List<Ghost> queue = new List<Ghost>();
        private readonly List<Ghost> held = new List<Ghost>();
        private readonly List<Ghost> done = new List<Ghost>();
        private int frame;
        private int decoded, failed;
        private float decodeSeconds;
        private bool disposed;

        public int Held => held.Count;

        // Ghosts whose world was decoded while loading (kept until it's their turn to go).
        public void Adopt(Ghost g)
        {
            if (g.World != null && !held.Contains(g))
            {
                held.Add(g);
                g.WorldUsed = frame;
            }
        }

        // Its world if it's ready (and asks for it if not).
        public GhostWorld Get(Ghost g)
        {
            if (g == null || !g.HasWorldData)
            {
                return null;
            }
            g.WorldUsed = frame;
            if (g.World != null)
            {
                return g.World;
            }
            Want(g, urgent: true);
            return null;
        }

        // Asks for its world, to have it ready when it's needed (the next to die, the winner). urgent: it's
        // wanted on screen now, so it's decoded before anything only wanted ahead of time.
        public void Want(Ghost g, bool urgent = false)
        {
            if (disposed || g == null || !g.HasWorldData)
            {
                return;
            }
            g.WorldUsed = frame;
            if (g.World != null || g.WorldFailed || g.Source == null || pending.ContainsKey(g))
            {
                return;
            }
            int at = queue.IndexOf(g);
            if (at >= 0 && urgent)
            {
                queue.RemoveAt(at);
                at = -1;
            }
            if (at < 0)
            {
                queue.Insert(urgent ? queue.Count : 0, g);  // (taken from the end)
            }
        }

        // Once a frame: decoded worlds put in, new decodes started, old worlds let go.
        public void Update()
        {
            frame++;
            done.Clear();
            foreach (KeyValuePair<Ghost, Task<GhostWorld>> p in pending)
            {
                if (p.Value.IsCompleted)
                {
                    done.Add(p.Key);
                }
            }
            foreach (Ghost g in done)
            {
                Task<GhostWorld> task = pending[g];
                pending.Remove(g);
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                {
                    g.World = task.Result;
                    held.Add(g);
                    decoded++;
                }
                else
                {
                    g.WorldFailed = true;
                    if (failed++ == 0)
                    {
                        Plugin.Log.LogWarning($"couldn't read attempt {g.Number}'s world again ({task.Exception?.GetBaseException().Message ?? "its run file changed"}): it plays without it");
                    }
                }
            }
            // (the most recently wanted first; ones no longer wanted drop out)
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                if (frame - queue[i].WorldUsed > 2 || queue[i].World != null)
                {
                    queue.RemoveAt(i);
                }
            }
            while (pending.Count < Working && queue.Count > 0)
            {
                Ghost g = queue[queue.Count - 1];
                queue.RemoveAt(queue.Count - 1);
                WorldSource source = g.Source;
                int rate = g.Rate;
                pending[g] = Task.Run(() =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    RawAttempt raw = SessionFile.ReadAttemptAt(source.Path, source.Offset, source.Length);
                    GhostWorld w = raw != null ? GhostWorld.FromRaw(raw, rate) : null;
                    lock (pending)
                    {
                        decodeSeconds += (float)clock.Elapsed.TotalSeconds;
                    }
                    return w;
                });
            }
            if (held.Count > Keep)
            {
                // (the least recently used go first; never one used this frame or the last)
                held.Sort((a, b) => a.WorldUsed.CompareTo(b.WorldUsed));
                for (int i = 0; i < held.Count && held.Count > Keep; )
                {
                    Ghost g = held[i];
                    if (frame - g.WorldUsed <= 1 || g.Source == null)
                    {
                        i++;
                        continue;
                    }
                    g.World = null;
                    held.RemoveAt(i);
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (decoded > 0 || failed > 0)
            {
                Plugin.Log.LogInfo($"replay worlds: {decoded} read again from their run files as they were needed ({decodeSeconds * 1000f / Math.Max(1, decoded):0} ms each on a worker thread){(failed > 0 ? $", {failed} couldn't be" : "")}, {held.Count} kept at the end");
            }
            foreach (Ghost g in held)
            {
                g.World = null;
            }
            held.Clear();
            queue.Clear();
            pending.Clear();  // (any still running finish on their own; nothing picks them up)
        }
    }
}
