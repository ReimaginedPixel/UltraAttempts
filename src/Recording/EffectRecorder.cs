using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraAttempts.Recording
{
    // WorldDetail = Everything: the game's own effects as it made them around V1 (muzzle flashes, slide and wall
    // sparks, dodges, landing dust, punches landing, hits, explosions' fire and smoke and light, blood), so a replay
    // can copy the same prefabs: caught as the game makes them (its Instantiate, hooked in Patches), plus particles
    // that come on without being made (pooled blood, a gun's own particles), found by a scan. What the game makes
    // with a rigidbody is a thing: ThingRecorder tracks it from the frame it was made (a pellet is gone before its
    // scan would have seen it), and which prefab it was is kept so a replay can copy that too. Like the rest of the
    // recorder it only reads. Instantiate runs hundreds of times a second in a fight, so a spawn is only noted
    // there; it's looked at once the frame's done (and its spawner has put it where it goes).
    internal sealed class EffectRecorder
    {
        private const float Range = 150f;
        private const float CameraReach = 2f;           // made this near the camera, in front: a muzzle flash, kept on the camera
        private const int MaxPending = 1024;            // spawns noted in one frame
        private const int MaxLive = 256;                // effects followed at once
        private const int MaxEffects = 40_000;          // an attempt
        private const int MaxKeys = 400_000;
        private const int MaxKnownPrefabs = 4096;
        private const int MaxSystems = 20_000;
        private const int MaxParts = 300;               // more objects than this isn't an effect (a room a checkpoint brought back)
        private const float MaxSize = 60f;
        private const int ScansPerSecond = 15;          // looking for particles that came on by themselves
        private const int StopChecks = 3;               // world samples between looks at whether its particles still come
        private const float MovedDistance = 0.015f;     // a new key once it's moved this far...
        private const float MovedDot = 0.99996f;        // ...or turned about 1 degree
        private const int HoldAfter = 6;                // world samples without a key that count as having stopped

        private const int MaxMeshes = 24;

        // What the game makes that isn't an effect: enemies, your weapons and arms (a new loadout), items, the level's
        // own workings.
        private static readonly Type[] NotEffects =
        {
            typeof(EnemyIdentifier), typeof(WeaponIcon), typeof(Punch), typeof(GunControl), typeof(FistControl), typeof(HookArm),
            typeof(ItemIdentifier), typeof(NewMovement), typeof(CheckPoint), typeof(Door),
        };

        private static readonly Type[] ProjectileTypes =
        {
            typeof(Projectile), typeof(Nail), typeof(Grenade), typeof(Cannonball), typeof(Coin), typeof(Harpoon), typeof(Chainsaw),
            typeof(ThrownSword), typeof(MassSpear), typeof(GasolineProjectile), typeof(BlackHoleProjectile), typeof(Landmine),
        };

        private enum Kind : byte
        {
            Ignore,
            Thing,
            Effect,
        }

        // What a prefab the game makes copies of is (worked out the first time).
        private sealed class PrefabInfo
        {
            public Kind Kind;
            public string Name;
            public string Path;
            public int Parts;
            public bool Gibs;             // has EnviroGibModifiers: the game colours its particles like the floor
            public Color OwnColor;        // ...the colour they are in the prefab
            public int Attempt = -1;      // the attempt Index is in
            public int Index;             // its place in that attempt's EffectPrefabs
        }

        private struct Pending
        {
            public GameObject Go;
            public Object Original;
            public int Sample;
        }

        private sealed class Live
        {
            public Transform T;
            public int Key;
            public EffectTrack Track;
            public ParticleSystem[] Systems;
            public bool Emitted;
            public bool Keyed;            // (one left near the camera stays where it was put: no keys)
            public Vector3 Pos;           // its last key
            public Quaternion Rot;
            public float Scale;
            public int KeyedSample;
            public float BaseScale;
            public int[] Found;           // the particle systems the scan found it by (looked for again once it's gone)
        }

        private readonly ThingRecorder things;
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<Live> live = new List<Live>();
        private readonly Dictionary<int, Live> liveByKey = new Dictionary<int, Live>();
        private readonly Dictionary<int, PrefabInfo> prefabs = new Dictionary<int, PrefabInfo>();
        private readonly Dictionary<string, int> beamPrefabs = new Dictionary<string, int>();
        private readonly HashSet<int> linkedLooks = new HashSet<int>();
        private readonly HashSet<int> levelSystems = new HashSet<int>();     // the level's own particles (here when the scene was first scanned)
        private readonly HashSet<int> seenSystems = new HashSet<int>();      // particles already looked at this attempt
        private readonly List<Renderer> renderers = new List<Renderer>();
        private readonly List<ParticleSystem> systems = new List<ParticleSystem>();

        private RawAttempt attempt;
        private int serial;
        private int rate = 60;
        private int stride = 2;
        private int keys;
        private int ticks;
        private int scanEvery = 2;
        private bool scannedScene;
        private bool failed;
        private Object lastClone;
        private Transform player;
        private Transform camera;
        private Vector3 center;
        private int caughtProjectiles, caughtOther, scanned, dropped;
        private static bool warnedFull, warnedEffects, warnedKeys, warnedPending, warnedFailure;

        public EffectRecorder(ThingRecorder things)
        {
            this.things = things;
        }

        public bool On => attempt != null && !failed;

        public void EnterScene()
        {
            End();
            levelSystems.Clear();
            seenSystems.Clear();
            scannedScene = false;
            prefabs.Clear();
        }

        public void Begin(RawAttempt a, bool record)
        {
            End();
            attempt = record ? a : null;
            failed = false;
            serial++;
            keys = 0;
            ticks = 0;
            caughtProjectiles = caughtOther = scanned = dropped = 0;
            beamPrefabs.Clear();
            linkedLooks.Clear();
            seenSystems.Clear();
            if (attempt == null)
            {
                return;
            }
            rate = Math.Max(1, a.SampleRate);
            stride = Math.Max(1, a.EnemyStride);
            scanEvery = Math.Max(1, Mathf.RoundToInt(rate / (float)stride / ScansPerSecond));
            if (prefabs.Count > MaxKnownPrefabs)
            {
                prefabs.Clear();
            }
        }

        // The attempt's over: whatever's still showing was still there at its end.
        public void End()
        {
            if (attempt != null && attempt.Count > 0)
            {
                EffectsBlock.Trim(attempt, attempt.Count - 1);
            }
            foreach (Live l in live)
            {
                Forget(l);
            }
            live.Clear();
            liveByKey.Clear();
            pending.Clear();
            lastClone = null;
            attempt = null;
        }

        // The game just made clone (a copy of original) at `sample` (Instantiate's postfix): only noted here.
        public void OnSpawn(Object original, Object clone, int sample)
        {
            if (attempt == null || failed || ReferenceEquals(clone, lastClone))
            {
                return;  // (an Instantiate with no parent calls another one: the same clone twice)
            }
            lastClone = clone;
            try
            {
                GameObject go = clone as GameObject;
                if (ReferenceEquals(go, null))
                {
                    if (!(clone is Component c) || c == null)
                    {
                        return;  // (a material, a mesh...)
                    }
                    go = c.gameObject;
                }
                if (pending.Count >= MaxPending)
                {
                    if (!warnedPending)
                    {
                        warnedPending = true;
                        Plugin.Log.LogInfo($"the game made more than {MaxPending} things in one frame: the replay leaves the rest of that frame's effects out");
                    }
                    return;
                }
                pending.Add(new Pending { Go = go, Original = original, Sample = sample });
            }
            catch (Exception e)
            {
                Fail(e);  // (never into the game's own Instantiate)
            }
        }

        // Once a frame, before its samples: what the game made this frame.
        public void Frame()
        {
            if (!On)
            {
                pending.Clear();
                return;
            }
            if (pending.Count == 0)
            {
                return;
            }
            try
            {
                if (Where())
                {
                    for (int i = 0; i < pending.Count; i++)
                    {
                        Resolve(pending[i]);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                pending.Clear();
                lastClone = null;
            }
        }

        // A world sample: the effects followed (moved, stopped, gone), and now and then a look for particles that
        // came on by themselves.
        public void Tick(int sample)
        {
            if (!On)
            {
                return;
            }
            try
            {
                if (!Where())
                {
                    return;
                }
                Follow(sample, ticks % StopChecks == 0);
                if (ticks++ % scanEvery == 0)
                {
                    Scan(sample);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private bool Where()
        {
            NewMovement nm = Game.Player;
            if (nm == null)
            {
                return false;
            }
            player = nm.transform;
            camera = nm.cc != null ? nm.cc.transform : null;
            center = player.position;
            return true;
        }

        // ---------------------------------------------------------------- what the game made

        private void Resolve(Pending p)
        {
            GameObject go = p.Go;
            if (go == null)
            {
                return;  // (gone already: a sound that destroyed itself)
            }
            PrefabInfo info = InfoOf(p.Original, go);
            if (info.Kind == Kind.Thing)
            {
                int look = things.Spawned(go.transform, Math.Max(0, Math.Min(p.Sample, attempt.Count)), out ThingKind kind);
                if (look >= -1)
                {
                    if (kind == ThingKind.Projectile)
                    {
                        caughtProjectiles++;
                    }
                    else
                    {
                        caughtOther++;
                    }
                    if (look >= 0 && linkedLooks.Add(look))
                    {
                        attempt.LookPrefabs.Add(new EffectLink { Index = look, Prefab = IndexOf(info) });
                    }
                }
            }
            else if (info.Kind == Kind.Effect)
            {
                Start(go.transform, info, p.Sample, null);
            }
        }

        // What a prefab is: a thing (it has a rigidbody, or it's a projectile), an effect (something to see), or
        // nothing for a replay (a sound, a menu, an enemy, a beam: beams are their own events).
        private PrefabInfo InfoOf(Object original, GameObject clone)
        {
            GameObject source = original as GameObject;
            if (ReferenceEquals(source, null) && original is Component c)
            {
                source = c.gameObject;
            }
            if (source == null)
            {
                source = clone;
            }
            int id = source.GetInstanceID();
            if (prefabs.TryGetValue(id, out PrefabInfo info))
            {
                return info;
            }
            if (prefabs.Count > MaxKnownPrefabs)
            {
                prefabs.Clear();  // (copies of copies: a beam's ricochets are copies of the beam)
            }
            info = new PrefabInfo { Kind = Judge(clone) };
            if (info.Kind != Kind.Ignore)
            {
                string name = source.name;
                info.Name = Game.CleanName(name);
                // (a copy of something in the level, not of an asset or of a copy of one: found again by where it is)
                info.Path = source.scene.IsValid() && !name.Contains("(Clone)") ? Game.PathOf(source.transform) : "";
                info.Parts = source.GetComponentsInChildren<Transform>(true).Length;
                EnviroGibModifier gib = source.GetComponentInChildren<EnviroGibModifier>(true);
                info.Gibs = gib != null && gib.particleSystem != null;
                if (info.Gibs)
                {
                    info.OwnColor = gib.particleSystem.main.startColor.color;
                }
            }
            prefabs[id] = info;
            return info;
        }

        private Kind Judge(GameObject go)
        {
            if (go.GetComponent<RectTransform>() != null || go.GetComponent<RevolverBeam>() != null || go.GetComponent<ContinuousBeam>() != null)
            {
                return Kind.Ignore;
            }
            foreach (Type type in NotEffects)
            {
                if (go.GetComponentInChildren(type, true) != null)
                {
                    return Kind.Ignore;
                }
            }
            bool explosion = go.GetComponentInChildren<Explosion>(true) != null;
            if (!explosion && (go.GetComponent<Rigidbody>() != null || ProjectileTypes.Any(t => go.GetComponent(t) != null)))
            {
                return Kind.Thing;
            }
            renderers.Clear();
            go.GetComponentsInChildren(true, renderers);
            bool visible = renderers.Count > 0 || go.GetComponentInChildren<Light>(true) != null;
            int meshes = 0;
            foreach (Renderer r in renderers)
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                {
                    if (r.bounds.size.sqrMagnitude > MaxSize * MaxSize || ++meshes > MaxMeshes)
                    {
                        return Kind.Ignore;  // (the size of a room, or a piece of the level)
                    }
                }
            }
            if (!visible || go.GetComponentsInChildren<Transform>(true).Length > MaxParts)
            {
                return Kind.Ignore;
            }
            return Kind.Effect;
        }

        private int IndexOf(PrefabInfo info)
        {
            if (info.Attempt != serial)
            {
                info.Attempt = serial;
                info.Index = attempt.AddEffectPrefab(info.Name, info.Path, info.Parts);
            }
            return info.Index;
        }

        // A hitscan beam just recorded as WorldEvents[index]: which prefab it was (its LineRenderer, its light, its
        // muzzle flash), by its name: ricochets are copies of the beam, so its name says which asset it is.
        public void LinkBeam(int index, GameObject beam)
        {
            if (!On || beam == null)
            {
                return;
            }
            try
            {
                string name = Game.CleanName(beam.name);
                if (!beamPrefabs.TryGetValue(name, out int prefab))
                {
                    prefab = attempt.AddEffectPrefab(name, "", beam.GetComponentsInChildren<Transform>(true).Length);
                    beamPrefabs[name] = prefab;
                }
                attempt.BeamPrefabs.Add(new EffectLink { Index = index, Prefab = prefab });
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // ---------------------------------------------------------------- following effects

        // An effect starts: in its frame (on the camera, on V1's body, or in the world), with its first key.
        private void Start(Transform t, PrefabInfo info, int sample, int[] found)
        {
            int key = t.GetInstanceID();
            if (liveByKey.ContainsKey(key) || !t.gameObject.activeInHierarchy)
            {
                return;
            }
            Vector3 position = t.position;
            if ((position - center).sqrMagnitude > Range * Range)
            {
                return;
            }
            if (live.Count >= MaxLive || attempt.Effects.Count >= MaxEffects)
            {
                dropped++;
                bool crowded = live.Count >= MaxLive;
                if (crowded ? !warnedFull : !warnedEffects)
                {
                    warnedFull |= crowded;
                    warnedEffects |= !crowded;
                    Plugin.Log.LogInfo(crowded
                        ? $"more than {MaxLive} effects showing at once: the replay leaves out the ones after (WorldDetail = Everything's limit)"
                        : $"this attempt has recorded {MaxEffects} effects: no more for it (WorldDetail = Everything's limit)");
                }
                return;
            }
            EffectFrame frame = EffectFrame.World;
            bool keyed = true;
            if (camera != null && t.IsChildOf(camera))
            {
                frame = EffectFrame.Camera;
            }
            else if (t.IsChildOf(player))
            {
                frame = EffectFrame.Body;
            }
            else if (camera != null && NearCamera(position))
            {
                frame = EffectFrame.Near;
                keyed = false;  // (the game leaves it in the world where it made it)
            }
            Vector3 lossy = t.lossyScale;
            var track = new EffectTrack
            {
                Prefab = IndexOf(info),
                Frame = frame,
                Start = Math.Max(0, sample),
                ScaleX = Mathf.Max(Mathf.Abs(lossy.x), 1e-3f),
                ScaleY = Mathf.Max(Mathf.Abs(lossy.y), 1e-3f),
                ScaleZ = Mathf.Max(Mathf.Abs(lossy.z), 1e-3f),
                Tint = info.Gibs ? TintOf(t, info) : 0u,
            };
            systems.Clear();
            t.GetComponentsInChildren(false, systems);
            var l = new Live
            {
                T = t,
                Key = key,
                Track = track,
                Systems = systems.ToArray(),
                Keyed = keyed,
                BaseScale = track.ScaleX,
                Found = found,
            };
            foreach (ParticleSystem ps in l.Systems)
            {
                l.Emitted |= ps != null && ps.isEmitting;
            }
            Pose(l, out l.Pos, out l.Rot, out l.Scale);
            Add(l, track.Start, l.Pos, l.Rot, l.Scale);
            live.Add(l);
            liveByKey[key] = l;
            attempt.Effects.Add(track);
        }

        private bool NearCamera(Vector3 position)
        {
            Vector3 local = camera.InverseTransformPoint(position);
            return local.sqrMagnitude < CameraReach * CameraReach && local.z > 0.1f && local.y > -1.2f;
        }

        // The colour the game gave its particles (SceneHelper.SetParticlesColors: dust the colour of what it's on),
        // if it isn't the prefab's own.
        private static uint TintOf(Transform t, PrefabInfo info)
        {
            EnviroGibModifier gib = t.GetComponentInChildren<EnviroGibModifier>(true);
            if (gib == null || gib.particleSystem == null)
            {
                return 0u;
            }
            Color now = gib.particleSystem.main.startColor.color;
            Color own = info.OwnColor;
            if (Mathf.Abs(now.r - own.r) < 0.004f && Mathf.Abs(now.g - own.g) < 0.004f && Mathf.Abs(now.b - own.b) < 0.004f)
            {
                return 0u;
            }
            now.a = 1f;
            return ThingRecorder.Rgba(now);
        }

        private void Follow(int sample, bool checkStops)
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Live l = live[i];
                Transform t = l.T;
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    l.Track.End = Math.Max(sample, l.Track.Start);
                    Stop(i);
                    continue;
                }
                if (checkStops && l.Track.Stop < 0 && l.Systems.Length > 0)
                {
                    bool emitting = false;
                    foreach (ParticleSystem ps in l.Systems)
                    {
                        emitting |= ps != null && ps.isEmitting;
                    }
                    if (emitting)
                    {
                        l.Emitted = true;
                    }
                    else if (l.Emitted)
                    {
                        l.Track.Stop = Math.Max(sample, l.Track.Start);  // (one that hasn't started yet hasn't stopped)
                    }
                }
                if (!l.Keyed)
                {
                    continue;
                }
                Pose(l, out Vector3 pos, out Quaternion rot, out float scale);
                if (Moved(l, pos, rot, scale))
                {
                    if (l.Track.Count > 0 && sample - l.KeyedSample > stride * HoldAfter)
                    {
                        Add(l, sample - stride, l.Pos, l.Rot, l.Scale);  // (it waited where it was)
                    }
                    Add(l, sample, pos, rot, scale);
                }
            }
        }

        private void Stop(int index)
        {
            Live l = live[index];
            live.RemoveAt(index);
            liveByKey.Remove(l.Key);
            Forget(l);
        }

        // (a pooled one the scan found can come on again later: it's looked for again)
        private void Forget(Live l)
        {
            if (l.Found != null)
            {
                foreach (int id in l.Found)
                {
                    seenSystems.Remove(id);
                }
            }
            l.T = null;
            l.Track = null;
            l.Systems = null;
        }

        // Where it is in its frame: the world; the camera's own space; or V1's body (where it stands, turned the way
        // it looks, upright on its up; a replay has the same from the ghost).
        private void Pose(Live l, out Vector3 pos, out Quaternion rot, out float scale)
        {
            l.T.GetPositionAndRotation(out pos, out rot);
            scale = l.BaseScale > 1e-4f ? Mathf.Abs(l.T.lossyScale.x) / l.BaseScale : 1f;
            switch (l.Track.Frame)
            {
                case EffectFrame.Camera when camera != null:
                case EffectFrame.Near when camera != null:
                    Quaternion inverse = Quaternion.Inverse(camera.rotation);
                    pos = inverse * (pos - camera.position);
                    rot = inverse * rot;
                    break;
                case EffectFrame.Body:
                    Quaternion body = Quaternion.Inverse(BodyRotation(player, camera));
                    pos = body * (pos - player.position);
                    rot = body * rot;
                    break;
            }
        }

        public static Quaternion BodyRotation(Transform player, Transform camera)
        {
            Vector3 up = player.up;
            Vector3 look = camera != null ? camera.forward : player.forward;
            Vector3 forward = Vector3.ProjectOnPlane(look, up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(camera != null ? camera.up : player.forward, up);
            }
            return forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, up) : Quaternion.identity;
        }

        private bool Moved(Live l, Vector3 pos, Quaternion rot, float scale)
        {
            float distance = l.Track.Frame == EffectFrame.World ? MovedDistance : MovedDistance * 0.2f;
            if ((pos - l.Pos).sqrMagnitude > distance * distance || Mathf.Abs(scale - l.Scale) > 0.002f)
            {
                return true;
            }
            float dot = rot.x * l.Rot.x + rot.y * l.Rot.y + rot.z * l.Rot.z + rot.w * l.Rot.w;
            return Mathf.Abs(dot) < MovedDot;
        }

        // A key (in the world: centimetres; on V1: millimetres). None at or before its last one (a spawn this frame
        // is at the frame's last sample; the frame's earlier world samples come after it).
        private void Add(Live l, int sample, Vector3 pos, Quaternion rot, float scale)
        {
            EffectTrack track = l.Track;
            if (track.Count > 0 && sample <= track.Sample[track.Count - 1])
            {
                return;
            }
            if (keys >= MaxKeys)
            {
                if (!warnedKeys)
                {
                    warnedKeys = true;
                    Plugin.Log.LogInfo($"this attempt has recorded {MaxKeys} keys of effects moving: no more for it (WorldDetail = Everything's limit)");
                }
                return;
            }
            float units = track.Frame == EffectFrame.World ? 100f : 1000f;
            Recorder.Quantize(rot, out short rx, out short ry, out short rz, out short rw);
            track.Add(sample, Units(pos.x, units), Units(pos.y, units), Units(pos.z, units), rx, ry, rz, rw,
                (ushort)Mathf.Clamp(Mathf.RoundToInt(scale * 1000f), 0, 65535));
            keys++;
            l.Pos = pos;
            l.Rot = rot;
            l.Scale = scale;
            l.KeyedSample = sample;
        }

        private static int Units(float metres, float units) => Mathf.RoundToInt(Mathf.Clamp(metres, -1e6f, 1e6f) * units);

        // ---------------------------------------------------------------- particles that came on by themselves

        // Particles playing that weren't before, near V1: a pooled one switched back on (blood), or one that was
        // there all along and played (a gun's own). The level's own (here when the scene was first scanned, or in no
        // copy the game made) are left alone, and so is whatever's already followed (an effect's, a thing's).
        private void Scan(int sample)
        {
            ParticleSystem[] all = Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            bool first = !scannedScene;
            scannedScene = true;
            if (seenSystems.Count > MaxSystems)
            {
                seenSystems.Clear();
            }
            if (levelSystems.Count > MaxSystems * 2)
            {
                levelSystems.Clear();
            }
            float range = Range * Range;
            foreach (ParticleSystem ps in all)
            {
                if (ps == null)
                {
                    continue;
                }
                int id = ps.GetInstanceID();
                if (first)
                {
                    levelSystems.Add(id);
                    continue;
                }
                if (levelSystems.Contains(id) || seenSystems.Contains(id) || !ps.isPlaying)
                {
                    continue;
                }
                seenSystems.Add(id);
                Transform t = ps.transform;
                if ((t.position - center).sqrMagnitude > range)
                {
                    continue;
                }
                Transform root = EffectRoot(t);
                if (root == null || (OnV1(root) && !OnlyParticles(root)))
                {
                    levelSystems.Add(id);  // (the level's, or part of a gun: never looked at again)
                    continue;
                }
                int key = root.GetInstanceID();
                if (liveByKey.ContainsKey(key) || things.Tracks(key))
                {
                    continue;
                }
                PrefabInfo info = InfoOf(root.gameObject, root.gameObject);
                if (info.Kind != Kind.Effect)
                {
                    continue;
                }
                systems.Clear();
                root.GetComponentsInChildren(false, systems);
                int[] found = new int[systems.Count];
                for (int i = 0; i < found.Length; i++)
                {
                    found[i] = systems[i].GetInstanceID();
                    seenSystems.Add(found[i]);
                }
                // (it came on up to a scan ago: as long ago as it's been playing)
                int ago = Mathf.RoundToInt(Mathf.Clamp(ps.time, 0f, 0.5f) * rate);
                int before = attempt.Effects.Count;
                Start(root, info, Math.Max(0, sample - ago), found);
                scanned += attempt.Effects.Count - before;
            }
        }

        // What particles belong to: on V1 (its camera or body), the particles' own topmost system there (a gun's
        // muzzle particles, not the gun); elsewhere the topmost copy the game made above them (a pooled splatter). In
        // no copy the game made: the level's own.
        private Transform EffectRoot(Transform t)
        {
            Transform top = null;
            for (Transform x = t; x != null; x = x.parent)
            {
                if (x == camera || x == player)
                {
                    Transform system = t;
                    for (Transform y = t.parent; y != null && y != camera && y != player; y = y.parent)
                    {
                        if (y.GetComponent<ParticleSystem>() != null)
                        {
                            system = y;
                        }
                    }
                    return system;
                }
                if (x.name.Contains("(Clone)"))
                {
                    top = x;
                }
            }
            return top;
        }

        private bool OnV1(Transform t) => (camera != null && t.IsChildOf(camera)) || t.IsChildOf(player);

        // (particles and the like only: a gun's muzzle particles, not a piece of the gun)
        private bool OnlyParticles(Transform root)
        {
            renderers.Clear();
            root.GetComponentsInChildren(true, renderers);
            foreach (Renderer r in renderers)
            {
                if (!(r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SpriteRenderer))
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- the log

        public string Summary(RawAttempt a)
        {
            var kinds = new Dictionary<int, int>();
            foreach (EffectTrack e in a.Effects)
            {
                kinds.TryGetValue(e.Prefab, out int n);
                kinds[e.Prefab] = n + 1;
            }
            string most = string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(8)
                .Select(k => $"{(k.Key < a.EffectPrefabs.Count ? a.EffectPrefabs[k.Key].Name : "?")} {k.Value}"));
            int onCamera = a.Effects.Count(e => e.Frame == EffectFrame.Camera || e.Frame == EffectFrame.Near), onBody = a.Effects.Count(e => e.Frame == EffectFrame.Body);
            return $"{a.Effects.Count} effects of {kinds.Count} kinds ({(most.Length > 0 ? most : "none")}; {onCamera} on the camera, {onBody} on V1's body, "
                + $"{scanned} found by their particles), {caughtProjectiles} projectiles caught as they were spawned (and {caughtOther} other things), "
                + $"{a.LookPrefabs.Count} looks and {a.BeamPrefabs.Count} beams with their prefab"
                + (dropped > 0 ? $"; {dropped} left out over the limits" : "");
        }

        // Something in the game didn't look as expected: the rest of this attempt goes without its effects
        // (everything else is still recorded).
        private void Fail(Exception e)
        {
            failed = true;
            pending.Clear();
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording the game's effects failed, so this attempt has none from here on (it's still recorded): " + e);
            }
        }
    }
}
