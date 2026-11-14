using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraAttempts.Recording
{
    // WorldDetail = Everything: everything else that moves or happens around V1 while an attempt is recorded.
    // Every moving thing with a rigidbody (anyone's projectiles, coins, rockets, nails, cannonballs, harpoons,
    // gibs, debris, props knocked about) and the level's own movers (platforms, elevators, pistons, trams, big
    // doors), as keys while they move; explosions, beams and breakables breaking as events. Like the rest of the
    // recorder it only reads. Bounded: within Range of V1, MaxLive at once, MaxKeys an attempt.
    internal sealed class ThingRecorder
    {
        private const float Range = 150f;
        private const float LetGo = 175f;               // (a little further before a tracked thing is dropped)
        private const int MaxLive = 320;
        private const int MaxKeys = 1_500_000;          // about 40 MB while it's recorded, a few MB on disk
        private const int MaxEvents = 200_000;
        private const int MaxLooks = 4096;
        private const int MaxParts = 16;                // drawn parts per thing
        private const int ScansPerSecond = 15;          // looking for new things (rigidbodies)
        private const float MoverScanSeconds = 2f;      // looking for the level's movers
        private const float MovedDistance = 0.015f;     // a new key once it's moved this far...
        private const float MovedDot = 0.99996f;        // ...or turned about 1 degree
        private const int HoldAfter = 6;                // world samples without a key that count as having stopped
        private const float SnapDistance = 20f;
        private const float MaxSize = 60f;              // bigger than this is level geometry, not a thing
        private const int MaxKnown = 40000;

        private static readonly Type[] ProjectileTypes =
        {
            typeof(Projectile), typeof(Nail), typeof(Grenade), typeof(Cannonball), typeof(Coin), typeof(Harpoon), typeof(Chainsaw),
            typeof(ThrownSword), typeof(MassSpear), typeof(GasolineProjectile), typeof(BlackHoleProjectile), typeof(Landmine),
        };

        private static readonly Type[] MoverTypes =
        {
            typeof(MovingPlatform), typeof(Elevator), typeof(Piston), typeof(Rotator), typeof(BigDoor), typeof(SubDoor), typeof(Train.Tram),
        };

        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Scale;
        }

        // A rigidbody (or mover) seen in a scan: whether it's one to track, and where it was then.
        private sealed class Known
        {
            public bool Eligible;
            public bool Level;
            public ThingKind Kind;
            public bool Seen;
            public Pose Last;
            public int LastSample;
            public bool Tracked;        // tracked before in this attempt (and let go)
            public ThingTrack Track;    // ...as this
        }

        private sealed class Live
        {
            public int Key;
            public Transform T;
            public ThingTrack Track;
            public Pose Before;
            public Pose Now;
            public Pose Keyed;          // its last key
            public int KeyedSample;
            public Vector3 BaseScale;
            public int StillSince;      // the sample it last moved at
        }

        private readonly List<Live> live = new List<Live>();
        private readonly Dictionary<int, Live> byKey = new Dictionary<int, Live>();
        private readonly Dictionary<int, Known> known = new Dictionary<int, Known>();
        private readonly List<Transform> movers = new List<Transform>();
        private readonly HashSet<int> moverKeys = new HashSet<int>();
        private readonly List<ContinuousBeam> beams = new List<ContinuousBeam>();
        private readonly Dictionary<int, string> paths = new Dictionary<int, string>();
        private readonly Dictionary<string, int> lookIndex = new Dictionary<string, int>();
        private readonly List<Renderer> renderers = new List<Renderer>();
        private readonly StringBuilder signature = new StringBuilder();

        private RawAttempt attempt;
        private int stride = 1;
        private int rate = 60;
        private int keys;
        private int nextId;
        private int ticks;
        private int scanEvery = 2;
        private float nextMoverScan;
        private bool failed;
        private Transform player;
        private Transform camera;
        private Vector3 center;
        private static bool warnedFull, warnedKeys, warnedFailure, warnedEvents;

        public bool On => attempt != null && !failed;

        public void EnterScene()
        {
            End();
            known.Clear();
            paths.Clear();
            movers.Clear();
            moverKeys.Clear();
            beams.Clear();
        }

        public void Begin(RawAttempt a, bool record)
        {
            End();
            attempt = record ? a : null;
            failed = false;
            keys = 0;
            nextId = 0;
            ticks = 0;
            nextMoverScan = 0f;
            lookIndex.Clear();
            if (attempt == null)
            {
                return;
            }
            stride = Math.Max(1, a.EnemyStride);
            rate = Math.Max(1, a.SampleRate);
            scanEvery = Math.Max(1, Mathf.RoundToInt(rate / (float)stride / ScansPerSecond));
            if (known.Count > MaxKnown)
            {
                known.Clear();
            }
            foreach (Known k in known.Values)
            {
                // (what the last attempt saw doesn't carry over: only whether it's worth tracking)
                k.Seen = false;
                k.Tracked = false;
                k.Track = null;
            }
        }

        // The attempt's over: whatever's still tracked stops here (still there: Gone stays false).
        public void End()
        {
            foreach (Live l in live)
            {
                l.T = null;
                l.Track = null;
            }
            live.Clear();
            byKey.Clear();
            attempt = null;
        }

        // Once a frame, before its samples: how each tracked thing is now.
        public void Frame()
        {
            if (!On)
            {
                return;
            }
            try
            {
                for (int i = 0; i < live.Count; i++)
                {
                    Live l = live[i];
                    l.Before = l.Now;
                    if (l.T != null)
                    {
                        l.Now = Read(l.T, l.BaseScale);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A world sample (every EnemyStride-th attempt sample), `between` of the way from last frame to this.
        public void Tick(int sample, float between)
        {
            if (!On)
            {
                return;
            }
            try
            {
                NewMovement nm = Game.Player;
                if (nm == null)
                {
                    return;
                }
                player = nm.transform;
                camera = nm.cc != null ? nm.cc.transform : null;
                center = player.position;
                Follow(sample, between);
                if (ticks++ % scanEvery == 0)
                {
                    Scan(sample);
                    WatchMovers(sample);
                }
                Beams(sample);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // ---------------------------------------------------------------- things

        private void Follow(int sample, float between)
        {
            float letGo = LetGo * LetGo;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Live l = live[i];
                Transform t = l.T;
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    Stop(i, gone: true);
                    continue;
                }
                Pose p = Blend(l.Before, l.Now, between);
                if ((p.Position - center).sqrMagnitude > letGo)
                {
                    Stop(i, gone: true);
                    continue;
                }
                l.Track.End = sample;
                if (Moved(l.Keyed, p))
                {
                    Key(l, sample, p);
                    l.StillSince = sample;
                }
            }
        }

        // A key at `sample`: first one where it waited, if it's been still a while since its last key (so it
        // doesn't slide there all the while in a replay). A short gap is something moving slowly: no wait.
        private void Key(Live l, int sample, Pose p)
        {
            if (keys >= MaxKeys)
            {
                if (!warnedKeys)
                {
                    warnedKeys = true;
                    Plugin.Log.LogInfo($"this attempt has recorded {MaxKeys} keys of things moving: no more for it (WorldDetail = Everything's limit)");
                }
                return;
            }
            if (l.Track.Count > 0 && sample - l.KeyedSample > stride * HoldAfter)
            {
                Add(l.Track, sample - stride, l.Keyed, l.BaseScale);
            }
            Add(l.Track, sample, p, l.BaseScale);
            l.Keyed = p;
            l.KeyedSample = sample;
        }

        private void Add(ThingTrack track, int sample, Pose p, Vector3 baseScale)
        {
            Recorder.Quantize(p.Rotation, out short rx, out short ry, out short rz, out short rw);
            track.Add(sample, Recorder.Cm(p.Position.x), Recorder.Cm(p.Position.y), Recorder.Cm(p.Position.z),
                WorldRecorder.Coarse(rx), WorldRecorder.Coarse(ry), WorldRecorder.Coarse(rz), WorldRecorder.Coarse(rw),
                (ushort)Mathf.Clamp(Mathf.RoundToInt(p.Scale * 1000f), 0, 65535));
            keys++;
        }

        private void Stop(int index, bool gone)
        {
            Live l = live[index];
            l.Track.Gone = gone;
            live.RemoveAt(index);
            byKey.Remove(l.Key);
            l.T = null;
            l.Track = null;
        }

        private static bool Moved(Pose a, Pose b)
        {
            if ((a.Position - b.Position).sqrMagnitude > MovedDistance * MovedDistance || Mathf.Abs(a.Scale - b.Scale) > 0.002f)
            {
                return true;
            }
            float dot = a.Rotation.x * b.Rotation.x + a.Rotation.y * b.Rotation.y + a.Rotation.z * b.Rotation.z + a.Rotation.w * b.Rotation.w;
            return Mathf.Abs(dot) < MovedDot;
        }

        private static Pose Read(Transform t, Vector3 baseScale)
        {
            var p = new Pose();
            t.GetPositionAndRotation(out p.Position, out p.Rotation);
            float now = Mathf.Abs(t.lossyScale.x);
            p.Scale = baseScale.x > 1e-4f ? now / baseScale.x : 1f;
            return p;
        }

        private static Pose Blend(Pose before, Pose now, float t)
        {
            if (t >= 1f || (now.Position - before.Position).sqrMagnitude > SnapDistance * SnapDistance)
            {
                return now;
            }
            return new Pose
            {
                Position = Vector3.LerpUnclamped(before.Position, now.Position, t),
                Rotation = Quaternion.Slerp(before.Rotation, now.Rotation, t),
                Scale = Mathf.LerpUnclamped(before.Scale, now.Scale, t),
            };
        }

        // New rigidbodies about, and ones that started moving. (The scene's own list of them, not a search
        // through every object.)
        private void Scan(int sample)
        {
            Rigidbody[] bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            float range = Range * Range;
            foreach (Rigidbody rb in bodies)
            {
                if (rb == null)
                {
                    continue;
                }
                Transform t = rb.transform;
                int key = t.GetInstanceID();
                if (byKey.ContainsKey(key))
                {
                    continue;
                }
                if (!known.TryGetValue(key, out Known k))
                {
                    k = Judge(rb);
                    known[key] = k;
                }
                if (!k.Eligible)
                {
                    continue;
                }
                Vector3 position = t.position;
                if ((position - center).sqrMagnitude > range)
                {
                    k.Seen = false;
                    continue;
                }
                // (a projectile's tracked from when it's first seen; anything else, and a projectile let go at
                // rest, from when it moves)
                bool moving = (k.Kind == ThingKind.Projectile && !k.Tracked) || (!rb.isKinematic && !rb.IsSleeping() && rb.velocity.sqrMagnitude > 0.25f);
                Pose now = new Pose { Position = position, Rotation = t.rotation, Scale = 1f };
                if (!moving && k.Seen)
                {
                    moving = Moved(k.Last, now);
                }
                if (!moving)
                {
                    k.Seen = true;
                    k.Last = now;
                    k.LastSample = sample;
                    continue;
                }
                Track(t, key, k, sample);
            }
        }

        // The level's movers, looked for now and then; each one tracked from when it moves.
        private void WatchMovers(int sample)
        {
            if (Time.time >= nextMoverScan)
            {
                nextMoverScan = Time.time + MoverScanSeconds;
                movers.RemoveAll(m => m == null);
                foreach (Type type in MoverTypes)
                {
                    foreach (Object found in Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    {
                        if (found is Component c && c != null && moverKeys.Add(c.transform.GetInstanceID()))
                        {
                            movers.Add(c.transform);
                        }
                    }
                }
            }
            float range = Range * Range;
            foreach (Transform t in movers)
            {
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    continue;
                }
                int key = t.GetInstanceID();
                if (byKey.ContainsKey(key))
                {
                    continue;
                }
                if (!known.TryGetValue(key, out Known k))
                {
                    k = new Known { Eligible = HasLook(t), Level = true, Kind = ThingKind.Mover };
                    known[key] = k;
                }
                Vector3 position = t.position;
                if (!k.Eligible || (position - center).sqrMagnitude > range)
                {
                    k.Seen = false;
                    continue;
                }
                var now = new Pose { Position = position, Rotation = t.rotation, Scale = 1f };
                if (!k.Seen || !Moved(k.Last, now))
                {
                    k.Seen = true;
                    k.Last = now;
                    k.LastSample = sample;
                    continue;
                }
                Track(t, key, k, sample);
            }
        }

        // Whether a rigidbody is one to track: not V1's, not an enemy's, something to see, not the size of a room.
        private Known Judge(Rigidbody rb)
        {
            var k = new Known();
            Transform t = rb.transform;
            if ((player != null && t.IsChildOf(player)) || (camera != null && t.IsChildOf(camera)))
            {
                return k;
            }
            if (rb.GetComponentInParent<EnemyIdentifier>(true) != null || rb.GetComponentInParent<EnemyIdentifierIdentifier>(true) != null)
            {
                return k;
            }
            if (!HasLook(t))
            {
                return k;
            }
            bool projectile = false;
            foreach (Type type in ProjectileTypes)
            {
                if (rb.GetComponent(type) != null)
                {
                    projectile = true;
                    break;
                }
            }
            bool spawned = projectile || t.name.Contains("(Clone)") || (t.parent != null && t.parent.name.Contains("(Clone)"))
                || rb.GetComponentInParent<GoreZone>(true) != null;
            k.Eligible = true;
            k.Level = !spawned;
            k.Kind = projectile ? ThingKind.Projectile : spawned ? ThingKind.Debris : ThingKind.Prop;
            return k;
        }

        // Something visible on it, and not too big.
        private bool HasLook(Transform t)
        {
            renderers.Clear();
            t.GetComponentsInChildren(false, renderers);
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (r == null || !r.enabled)
                {
                    continue;
                }
                if (r.bounds.size.sqrMagnitude > MaxSize * MaxSize)
                {
                    return false;
                }
                any = true;
            }
            return any;
        }

        private void Track(Transform t, int key, Known k, int sample)
        {
            if (live.Count >= MaxLive && !MakeRoom())
            {
                if (!warnedFull)
                {
                    warnedFull = true;
                    Plugin.Log.LogInfo($"more than {MaxLive} things moving at once: the replay shows the first {MaxLive} (WorldDetail = Everything's limit)");
                }
                return;
            }
            Vector3 lossy = t.lossyScale;
            var baseScale = new Vector3(Mathf.Max(Mathf.Abs(lossy.x), 1e-3f), Mathf.Max(Mathf.Abs(lossy.y), 1e-3f), Mathf.Max(Mathf.Abs(lossy.z), 1e-3f));
            var l = new Live
            {
                Key = key,
                T = t,
                BaseScale = baseScale,
                StillSince = sample,
                Track = new ThingTrack
                {
                    Id = nextId++,
                    Kind = k.Kind,
                    Look = LookOf(t, baseScale),
                    Path = k.Level ? attempt.AddThingPath(PathOf(t)) : -1,
                    ScaleX = baseScale.x,
                    ScaleY = baseScale.y,
                    ScaleZ = baseScale.z,
                    End = sample,
                },
            };
            l.Now = Read(t, baseScale);
            l.Before = l.Now;
            if (k.Seen && k.LastSample < sample)
            {
                // (where it was before it moved: a replay starts it there)
                Pose before = k.Last;
                before.Scale = l.Now.Scale;
                Add(l.Track, k.LastSample, before, baseScale);
                l.Keyed = before;
                l.KeyedSample = k.LastSample;
            }
            Key(l, sample, l.Now);
            if (k.Track != null && k.Track != l.Track && !k.Track.Gone)
            {
                // (it was let go at rest and moves again: that rest is over, this track carries on from here)
                k.Track.Gone = true;
                k.Track.End = Math.Max(k.Track.End, sample - 1);
            }
            k.Track = l.Track;
            k.Tracked = true;
            k.Seen = false;
            live.Add(l);
            byKey[key] = l;
            attempt.Things.Add(l.Track);
        }

        // Full: whatever's been still the longest (a stuck nail, a gib on the floor; not the level's movers) makes
        // way, if it's been still a second or more. It stays in the replay where it came to rest.
        private bool MakeRoom()
        {
            int oldest = -1;
            for (int i = 0; i < live.Count; i++)
            {
                Live l = live[i];
                if (l.Track.Kind != ThingKind.Mover && (oldest < 0 || l.StillSince < live[oldest].StillSince))
                {
                    oldest = i;
                }
            }
            if (oldest < 0 || live[oldest].Track.End - live[oldest].StillSince < rate)
            {
                return false;
            }
            Stop(oldest, gone: false);
            return true;
        }

        private string PathOf(Transform t)
        {
            int key = t.GetInstanceID();
            if (!paths.TryGetValue(key, out string path))
            {
                path = Game.PathOf(t);
                paths[key] = path;
            }
            return path;
        }

        // ---------------------------------------------------------------- looks

        // What it looks like: each mesh it draws with (by name, and how many vertices), its materials (by
        // name), where each sits on it; its bounds and colour for when none can be found again.
        private int LookOf(Transform root, Vector3 baseScale)
        {
            if (attempt.Looks.Count >= MaxLooks)
            {
                return -1;
            }
            Matrix4x4 toLocal = Matrix4x4.TRS(root.position, root.rotation, baseScale).inverse;
            var look = new ThingLook { Name = Game.CleanName(root.name) };
            renderers.Clear();
            root.GetComponentsInChildren(false, renderers);
            bool anyBounds = false;
            var bounds = new Bounds();
            Color color = Color.white;
            bool colored = false;
            foreach (Renderer r in renderers)
            {
                if (r == null || !r.enabled)
                {
                    continue;
                }
                Bounds b = r.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) != 0 ? 1f : -1f, (corner & 2) != 0 ? 1f : -1f, (corner & 4) != 0 ? 1f : -1f));
                    c = toLocal.MultiplyPoint3x4(c);
                    if (!anyBounds)
                    {
                        bounds = new Bounds(c, Vector3.zero);
                        anyBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(c);
                    }
                }
                if (!colored && ColorOf(r, out Color c2))
                {
                    color = c2;
                    colored = true;
                }
                Mesh mesh = r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : r is SkinnedMeshRenderer s ? s.sharedMesh : null;
                if (mesh == null || look.Parts.Count >= MaxParts)
                {
                    continue;
                }
                Matrix4x4 local = toLocal * r.transform.localToWorldMatrix;
                Quaternion rotation = local.rotation;
                Vector3 scale = local.lossyScale;
                Material[] materials = r.sharedMaterials;
                var names = new string[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    names[i] = materials[i] != null ? CleanMaterial(materials[i].name) : "";
                }
                look.Parts.Add(new LookPart
                {
                    Mesh = mesh.name,
                    Vertices = mesh.vertexCount,
                    Materials = names,
                    PX = local.m03, PY = local.m13, PZ = local.m23,
                    RX = rotation.x, RY = rotation.y, RZ = rotation.z, RW = rotation.w,
                    SX = scale.x, SY = scale.y, SZ = scale.z,
                });
            }
            if (anyBounds)
            {
                look.CenterX = bounds.center.x;
                look.CenterY = bounds.center.y;
                look.CenterZ = bounds.center.z;
                look.SizeX = bounds.size.x;
                look.SizeY = bounds.size.y;
                look.SizeZ = bounds.size.z;
            }
            look.Color = Rgba(color);

            // The same look as one already in this attempt (most things are copies of a few prefabs).
            signature.Clear();
            signature.Append(look.Name).Append('|').Append(look.Color).Append('|');
            signature.Append(Mathf.RoundToInt(look.SizeX * 100f)).Append(',').Append(Mathf.RoundToInt(look.SizeY * 100f)).Append(',').Append(Mathf.RoundToInt(look.SizeZ * 100f));
            foreach (LookPart p in look.Parts)
            {
                signature.Append('|').Append(p.Mesh).Append(':').Append(p.Vertices);
                foreach (string m in p.Materials)
                {
                    signature.Append(':').Append(m);
                }
                signature.Append('@').Append(Mathf.RoundToInt(p.PX * 100f)).Append(',').Append(Mathf.RoundToInt(p.PY * 100f)).Append(',').Append(Mathf.RoundToInt(p.PZ * 100f))
                    .Append(',').Append(Mathf.RoundToInt(p.RX * 100f)).Append(',').Append(Mathf.RoundToInt(p.RY * 100f)).Append(',').Append(Mathf.RoundToInt(p.RZ * 100f))
                    .Append(',').Append(Mathf.RoundToInt(p.SX * 100f)).Append(',').Append(Mathf.RoundToInt(p.SY * 100f)).Append(',').Append(Mathf.RoundToInt(p.SZ * 100f));
            }
            string key = signature.ToString();
            if (!lookIndex.TryGetValue(key, out int index))
            {
                index = attempt.Looks.Count;
                attempt.Looks.Add(look);
                lookIndex[key] = index;
            }
            return index;
        }

        public static string CleanMaterial(string name)
        {
            name = name ?? "";
            while (name.EndsWith(" (Instance)", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - " (Instance)".Length);
            }
            return name.Trim();
        }

        private static bool ColorOf(Renderer r, out Color color)
        {
            color = Color.white;
            if (r is SpriteRenderer sprite)
            {
                color = sprite.color;
                return true;
            }
            if (r is TrailRenderer trail)
            {
                color = trail.startColor;
                return true;
            }
            if (r is LineRenderer line)
            {
                color = line.startColor;
                return true;
            }
            Material m = r.sharedMaterial;
            if (m == null)
            {
                return false;
            }
            if (m.HasProperty(UKShaderProperties.EmissiveColor) && m.HasProperty(UKShaderProperties.EmissiveIntensity) && m.GetFloat(UKShaderProperties.EmissiveIntensity) > 0.01f)
            {
                color = m.GetColor(UKShaderProperties.EmissiveColor);
                return true;
            }
            if (m.HasProperty("_Color"))
            {
                color = m.color;
                return true;
            }
            if (m.HasProperty("_TintColor"))
            {
                color = m.GetColor("_TintColor");
                return true;
            }
            return false;
        }

        public static uint Rgba(Color c)
        {
            Color32 b = c;
            return ((uint)b.r << 24) | ((uint)b.g << 16) | ((uint)b.b << 8) | b.a;
        }

        // ---------------------------------------------------------------- events

        public void OnExplosion(Explosion e, Color color, int sample)
        {
            if (!On || e == null || !Near(e.transform.position))
            {
                return;
            }
            try
            {
                // How long it grows: ULTRAKILL adds 0.05 * speed to its scale every physics step until its
                // radius (scale * the collider's) reaches maxSize.
                Transform t = e.transform;
                SphereCollider sphere = e.GetComponent<SphereCollider>();
                float radius = sphere != null ? sphere.radius : 0.5f;
                float parent = t.parent != null ? Mathf.Abs(t.parent.lossyScale.x) : 1f;
                float perSecond = 0.05f * Mathf.Max(e.speed, 0.01f) * radius * parent / Mathf.Max(Time.fixedDeltaTime, 0.001f);
                float now = Mathf.Abs(t.lossyScale.x) * radius;
                float grow = Mathf.Clamp((e.maxSize - now) / Mathf.Max(perSecond, 0.01f), 0.04f, 3f);
                Vector3 p = t.position;
                Event(new WorldEvent
                {
                    Sample = sample,
                    Kind = WorldEventKind.Explosion,
                    X = Recorder.Cm(p.x), Y = Recorder.Cm(p.y), Z = Recorder.Cm(p.z),
                    ToX = Recorder.Cm(p.x), ToY = Recorder.Cm(p.y), ToZ = Recorder.Cm(p.z),
                    Size = Mathf.RoundToInt(Mathf.Clamp(e.maxSize, 0f, 1000f) * 1000f),
                    Life = Mathf.RoundToInt(grow * 1000f),
                    Color = Rgba(color),
                    Look = -1,
                    Path = -1,
                    RotW = 32767,
                });
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        // A hitscan beam (the revolver's, the railcannon's, a coin's ricochet, an enemy's).
        public void OnBeam(RevolverBeam beam, int sample)
        {
            if (!On || beam == null)
            {
                return;
            }
            try
            {
                LineRenderer lr = beam.GetComponent<LineRenderer>();
                if (lr != null)
                {
                    Line(lr, beam.transform, sample, Mathf.Clamp(lr.widthMultiplier / 1.5f, 0.12f, 1.5f));
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A beam that lasts (a Mindflayer's, a turret's...): drawn every world sample while it's on.
        public void OnContinuousBeam(ContinuousBeam beam)
        {
            if (beam != null && beams.Count < 512 && !beams.Contains(beam))
            {
                beams.Add(beam);
            }
        }

        private void Beams(int sample)
        {
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                ContinuousBeam beam = beams[i];
                if (beam == null)
                {
                    beams.RemoveAt(i);
                    continue;
                }
                if (!beam.isActiveAndEnabled)
                {
                    continue;
                }
                LineRenderer lr = beam.GetComponent<LineRenderer>();
                if (lr != null && lr.enabled && lr.widthMultiplier > 0.01f)
                {
                    Line(lr, beam.transform, sample, 1.6f * stride / rate);
                }
            }
        }

        private void Line(LineRenderer lr, Transform t, int sample, float life)
        {
            int n = lr.positionCount;
            if (n < 2)
            {
                return;
            }
            Vector3 from = lr.GetPosition(0), to = lr.GetPosition(n - 1);
            if (!lr.useWorldSpace)
            {
                from = t.TransformPoint(from);
                to = t.TransformPoint(to);
            }
            if (!Near(from) && !Near(to))
            {
                return;
            }
            float width = Mathf.Clamp(lr.widthMultiplier * Mathf.Max(lr.startWidth, lr.endWidth), 0.02f, 4f);
            Color color = lr.startColor;
            Material m = lr.sharedMaterial;
            if (m != null && m.HasProperty("_TintColor"))
            {
                color *= m.GetColor("_TintColor");
            }
            else if (m != null && m.HasProperty("_Color"))
            {
                color *= m.color;
            }
            Event(new WorldEvent
            {
                Sample = sample,
                Kind = WorldEventKind.Beam,
                X = Recorder.Cm(from.x), Y = Recorder.Cm(from.y), Z = Recorder.Cm(from.z),
                ToX = Recorder.Cm(to.x), ToY = Recorder.Cm(to.y), ToZ = Recorder.Cm(to.z),
                Size = Mathf.RoundToInt(width * 1000f),
                Life = Mathf.RoundToInt(life * 1000f),
                Color = Rgba(color),
                Look = -1,
                Path = -1,
                RotW = 32767,
            });
        }

        // A breakable about to break (a crate, a window, a pillar): what it looked like and where, so a replay
        // can show it there until then and gone after.
        public void OnBreak(Breakable b, int sample)
        {
            if (!On || b == null || !Near(b.transform.position))
            {
                return;
            }
            try
            {
                Transform t = b.transform;
                // (its look keeps its scale in: an event has no size of its own to put back)
                int look = HasLook(t) ? LookOf(t, Vector3.one) : -1;
                bool level = !t.name.Contains("(Clone)");
                float size = look >= 0 ? new Vector3(attempt.Looks[look].SizeX, attempt.Looks[look].SizeY, attempt.Looks[look].SizeZ).magnitude : 1f;
                Recorder.Quantize(t.rotation, out short rx, out short ry, out short rz, out short rw);
                Vector3 p = t.position;
                Color color = Color.gray;
                if (look >= 0)
                {
                    uint c = attempt.Looks[look].Color;
                    color = new Color32((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), 255);
                }
                Event(new WorldEvent
                {
                    Sample = sample,
                    Kind = WorldEventKind.Break,
                    X = Recorder.Cm(p.x), Y = Recorder.Cm(p.y), Z = Recorder.Cm(p.z),
                    ToX = Recorder.Cm(p.x), ToY = Recorder.Cm(p.y), ToZ = Recorder.Cm(p.z),
                    Size = Mathf.RoundToInt(Mathf.Clamp(size, 0f, 1000f) * 1000f),
                    Life = 600,
                    Color = Rgba(color),
                    Look = look,
                    Path = level ? attempt.AddThingPath(PathOf(t)) : -1,
                    RotX = rx, RotY = ry, RotZ = rz, RotW = rw,
                });
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private bool Near(Vector3 p) => (p - center).sqrMagnitude <= Range * Range || center == Vector3.zero;

        private void Event(WorldEvent e)
        {
            if (attempt.WorldEvents.Count >= MaxEvents)
            {
                if (!warnedEvents)
                {
                    warnedEvents = true;
                    Plugin.Log.LogInfo($"this attempt has recorded {MaxEvents} explosions and beams: no more for it (WorldDetail = Everything's limit)");
                }
                return;
            }
            attempt.WorldEvents.Add(e);
        }

        // Something in the game didn't look as expected: the rest of this attempt goes without its things (V1,
        // the enemies and the doors are still recorded).
        private void Fail(Exception e)
        {
            failed = true;
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording the projectiles, props and explosions failed, so this attempt has none from here on (it's still recorded): " + e);
            }
        }
    }
}
