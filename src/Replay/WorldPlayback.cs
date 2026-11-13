using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraAttempts.Replay
{
    // The world as the followed attempt saw it: its enemies (copies of the real ones, where they were, in
    // the animation they were in, bleeding when it hit them and bursting when it killed them) and its
    // doors (open or shut as they were then). The level's own enemies, frozen as you left them, hide
    // meanwhile. Dispose puts back everything hidden, moved or made here.
    internal sealed class WorldPlayback
    {
        private const int CopiesPerFrame = 3;   // new enemy copies made in one frame (a stand-in meanwhile)
        private const int ReadyPerKind = 12;    // copies made up front, per kind of enemy...
        private const int ReadyTotal = 96;      // ...and in all
        private const float PopLife = 0.55f;
        private const float HitLife = 0.22f;
        private static readonly Color Blood = new Color(0.75f, 0.04f, 0.02f);
        private static readonly Color StandInColor = new Color(0.9f, 0.25f, 0.2f);
        private static readonly Vector3 DefaultCenter = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 DefaultSize = new Vector3(1.2f, 2f, 1.2f);

        private readonly Camera cam;
        private readonly Transform holder;
        private readonly int layer;
        private readonly Material material;
        private readonly Mesh sphere = Meshes.Sphere();
        private readonly Mesh cube = Meshes.Cube();
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");

        private bool anyEnemies;
        private bool levelHidden;
        private readonly List<Renderer> hidden = new List<Renderer>();

        private readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private Ghost shown;
        private Body[] bodies = Array.Empty<Body>();   // per enemy of the attempt shown
        private int madeThisFrame;
        private bool kindThisFrame;
        private int statesAsked, statesFound;          // (for the log: do the recorded animations match the copies?)

        private readonly Dictionary<string, DoorSpot> spots = new Dictionary<string, DoorSpot>();
        private readonly Dictionary<Door, DoorSpot> spotOf = new Dictionary<Door, DoorSpot>();  // (two paths, one door: one spot)
        private readonly List<DoorSpot> moved = new List<DoorSpot>();
        private Ghost doorsOf;
        private DoorSpot[] shownDoors = Array.Empty<DoorSpot>();   // per door of the attempt shown
        private bool[] doorKnown = Array.Empty<bool>();
        private bool[] doorOpen = Array.Empty<bool>();
        private float[] doorSince = Array.Empty<float>();

        // A kind of enemy: a stripped copy of it (kept inactive) to clone, and where the enemy's own
        // transform (the one recorded) sits in it.
        private sealed class Kind
        {
            public GameObject Template;                     // null: no model (a stand-in instead)
            public Vector3 Offset;                          // the enemy's transform in the root's space
            public Quaternion Turn = Quaternion.identity;   // from the enemy's rotation to the root's
            public Vector3 RootScale = Vector3.one;
            public float BaseScale = 1f;                    // the enemy's own scale in the copy
            public Vector3 Center = DefaultCenter;          // its middle and size, in the enemy's own space
            public Vector3 Size = DefaultSize;
            public readonly Dictionary<int, int> Owners = new Dictionary<int, int>();  // state -> which animator has it (-1: none)
            public readonly Stack<Body> Free = new Stack<Body>();
        }

        private sealed class Body
        {
            public GameObject Go;
            public Transform T;
            public Kind Kind;
            public Animator[] Animators;
            public int Playing = -1;    // the state it was put in last (0: left to play on its own)
        }

        private sealed class DoorSpot
        {
            public Door Door;
            public Transform T;
            public bool Moved;
            public Vector3 Home;        // where it was before the replay moved it
            public bool HomeInPos;
        }

        public WorldPlayback(Camera cam, Transform holder)
        {
            this.cam = cam;
            this.holder = holder;
            layer = (cam.cullingMask & 1) != 0 ? 0 : FirstLayer(cam.cullingMask);
            material = MakeMaterial();
        }

        // ---------------------------------------------------------------- starting and stopping

        // Once the attempts are loaded: a copy of every kind of enemy they met, made now (while the replay
        // is still loading) rather than in a hitch mid-replay, and their doors found in the level.
        public void Prepare(IList<Ghost> ghosts)
        {
            var most = new Dictionary<int, int>();
            var paths = new HashSet<string>();
            bool anyWorld = false;
            foreach (Ghost g in ghosts)
            {
                anyWorld |= g.HasWorld;
                anyEnemies |= g.HasEnemies;
                foreach (KeyValuePair<int, int> kv in g.MostAtOnce)
                {
                    most[kv.Key] = Math.Max(most.TryGetValue(kv.Key, out int m) ? m : 0, kv.Value);
                }
                foreach (string path in g.DoorPaths)
                {
                    paths.Add(path ?? "");
                }
            }
            if (!Settings.ShowWorld.Value)
            {
                return;  // (made as they're needed if it's turned on)
            }
            if (!anyWorld)
            {
                Plugin.Log.LogInfo("these attempts were recorded without their world (before 1.1, or with RecordWorld off): no enemies or doors to show");
                return;
            }
            float started = Time.realtimeSinceStartup;
            int copies = 0, models = 0;
            foreach (KeyValuePair<int, int> kv in most)
            {
                kindThisFrame = false;
                Kind kind = KindOf(kv.Key);
                if (kind?.Template == null)
                {
                    continue;
                }
                models++;
                for (int i = 0; i < Math.Min(kv.Value, ReadyPerKind) && copies < ReadyTotal; i++)
                {
                    kind.Free.Push(MakeBody(kind));
                    copies++;
                }
            }
            int movable = 0;
            foreach (string path in paths)
            {
                if (Spot(path) != null)
                {
                    movable++;
                }
            }
            SetLevelHidden(anyEnemies);
            Plugin.Log.LogInfo($"replay world: {models} of {most.Count} kinds of enemy modelled ({copies} copies ready), {movable} of {paths.Count} doors movable, {hidden.Count} of the level's enemy renderers hidden, {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        public void Dispose()
        {
            Safely(() =>
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    Release(i);
                }
                bodies = Array.Empty<Body>();
                shown = null;
            }, "putting away the enemies");
            Safely(() =>
            {
                PutDoorsBack();
                doorsOf = null;
            }, "putting the doors back");
            Safely(() => SetLevelHidden(false), "showing the level's enemies again");
            if (statesAsked > 0)
            {
                Plugin.Log.LogInfo($"replay world: {statesFound} of {statesAsked} recorded enemy animation states found in their copies");
            }
            foreach (Kind kind in kinds.Values)
            {
                foreach (Body body in kind.Free)
                {
                    if (body.Go != null)
                    {
                        UnityEngine.Object.Destroy(body.Go);
                    }
                }
                if (kind.Template != null)
                {
                    UnityEngine.Object.Destroy(kind.Template);
                }
            }
            kinds.Clear();
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(sphere);
            UnityEngine.Object.Destroy(cube);
        }

        private static void Safely(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(what + " after a replay: " + e);
            }
        }

        // ---------------------------------------------------------------- every frame

        // The world as g saw it t seconds into the replay (null: nobody's, the level as it is now).
        public void Update(Ghost g, float t)
        {
            madeThisFrame = 0;
            kindThisFrame = false;
            bool on = Settings.ShowWorld.Value;
            SetLevelHidden(on && anyEnemies);
            if (!on)
            {
                g = null;
            }
            if (g != shown)
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    Release(i);
                }
                shown = g;
                bodies = g != null && g.Enemies.Length > 0 ? new Body[g.Enemies.Length] : Array.Empty<Body>();
            }
            ShowDoors(g, t);
            if (g == null)
            {
                return;
            }

            float at = Mathf.Min(t, g.Duration);
            EnemyPath[] enemies = g.Enemies;
            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyPath e = enemies[i];
                bool there = at >= e.From && (e.Died ? at < e.To : at <= e.To);
                if (!there)
                {
                    Release(i);
                    if (e.Died && t >= e.To && t - e.To < PopLife)
                    {
                        DrawPop(e, i, t - e.To);
                    }
                    continue;
                }
                EnemyPath.State s = e.At(at);
                float punch = 1f;
                if (e.HitAt(at, HitLife, out float ago, out float amount))
                {
                    DrawHit(e, s, i, at - ago, ago, amount);
                    if (amount >= 0.04f)
                    {
                        punch = 1f + 0.06f * Mathf.Clamp01(1f - ago / 0.12f);  // (a real hit jolts it; burning doesn't)
                    }
                }
                Body body = bodies[i] ?? (bodies[i] = Acquire(e.Type));
                if (body == null || body.Go == null)
                {
                    bodies[i] = null;
                    DrawStandIn(e, s);
                    continue;
                }
                Place(body, e, s, punch);
                Animate(body, s);
            }
        }

        // ---------------------------------------------------------------- enemies

        // A kind of enemy's copy to clone, made the first time it's needed (one new kind a frame: null
        // meanwhile). Its Template is null if there's no model for it.
        private Kind KindOf(int type)
        {
            if (kinds.TryGetValue(type, out Kind kind))
            {
                return kind;
            }
            if (kindThisFrame)
            {
                return null;
            }
            kindThisFrame = true;
            kind = new Kind();
            kinds[type] = kind;
            try
            {
                Build(kind, type);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"couldn't copy {TypeName(type)} for the replay ({e.GetBaseException().Message}): a stand-in instead");
                if (kind.Template != null)
                {
                    UnityEngine.Object.Destroy(kind.Template);
                }
                kind.Template = null;
            }
            return kind;
        }

        // The sandbox spawner's prefab of it, else a living one in the level (bosses aren't in the
        // spawner, but are often in their level), stripped down to what it looks like.
        private void Build(Kind kind, int type)
        {
            GameObject prefab = type >= 0 ? Puppet.EnemyPrefab((EnemyType)type) : null;
            if (prefab != null && TryCopy(kind, type, prefab, live: false))
            {
                return;
            }
            GameObject living = LiveEnemy(type);
            if (living != null && TryCopy(kind, type, living, live: true))
            {
                return;
            }
            Plugin.Log.LogWarning($"no model for {TypeName(type)} in the replay: a stand-in instead");
        }

        // A stripped copy of source as the kind's template. False (and no copy left) if there's nothing in
        // it to see (some spawner entries only make the enemy when placed).
        private bool TryCopy(Kind kind, int type, GameObject source, bool live)
        {
            Transform sourceEnemy = EnemyIn(source.transform);
            int[] path = PathTo(source.transform, sourceEnemy);
            bool unhide = live && levelHidden;
            if (unhide)
            {
                SetLevelHidden(false);  // (else the copy would come out hidden too)
            }
            GameObject copy;
            try
            {
                copy = Puppet.Make(source, holder, layer, "UltraAttempts " + TypeName(type));
            }
            finally
            {
                if (unhide)
                {
                    SetLevelHidden(true);
                }
            }
            if (copy == null)
            {
                return false;
            }
            try
            {
                CopyLayers(source, copy);
                Transform root = copy.transform;
                Transform enemy = Walk(root, path) ?? root;
                bool any = false;
                var bounds = new Bounds();
                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled)
                    {
                        continue;
                    }
                    if (!any)
                    {
                        bounds = r.bounds;
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(r.bounds);
                    }
                }
                copy.SetActive(false);
                if (!any)
                {
                    UnityEngine.Object.Destroy(copy);
                    return false;
                }
                kind.RootScale = root.localScale;
                kind.Offset = root.InverseTransformPoint(enemy.position);
                kind.Turn = Quaternion.Inverse(Quaternion.Inverse(root.rotation) * enemy.rotation);
                float own = Mathf.Abs(enemy.lossyScale.y);
                kind.BaseScale = own > 1e-4f ? own : 1f;
                kind.Center = enemy.InverseTransformPoint(bounds.center);
                kind.Size = bounds.size / kind.BaseScale;
                kind.Template = copy;
                return true;
            }
            catch
            {
                copy.SetActive(false);  // (not left standing at the world's origin)
                UnityEngine.Object.Destroy(copy);
                throw;
            }
        }

        private Body MakeBody(Kind kind)
        {
            GameObject go = UnityEngine.Object.Instantiate(kind.Template, holder);
            go.name = kind.Template.name;
            var body = new Body { Go = go, T = go.transform, Kind = kind, Animators = go.GetComponentsInChildren<Animator>(true) };
            foreach (Animator a in body.Animators)
            {
                a.fireEvents = false;   // (its animation events would call scripts that aren't there)
                a.logWarnings = false;
            }
            return body;
        }

        private Body Acquire(int type)
        {
            Kind kind = KindOf(type);
            if (kind?.Template == null)
            {
                return null;
            }
            Body body = null;
            while (body == null && kind.Free.Count > 0)
            {
                body = kind.Free.Pop();
                if (body.Go == null)
                {
                    body = null;
                }
            }
            if (body == null)
            {
                if (madeThisFrame >= CopiesPerFrame)
                {
                    return null;
                }
                madeThisFrame++;
                body = MakeBody(kind);
            }
            body.Go.SetActive(true);
            body.Playing = -1;
            return body;
        }

        private void Release(int index)
        {
            Body body = bodies[index];
            if (body == null)
            {
                return;
            }
            bodies[index] = null;
            if (body.Go != null)
            {
                body.Go.SetActive(false);
                body.Kind.Free.Push(body);
            }
        }

        // Puts the copy where the enemy was: its own transform (the one recorded) on the recorded place.
        private static void Place(Body body, EnemyPath e, in EnemyPath.State s, float punch)
        {
            Kind kind = body.Kind;
            Quaternion rot = s.Rot * kind.Turn;
            Vector3 scale = kind.RootScale * (e.Scale / kind.BaseScale * punch);
            body.T.SetPositionAndRotation(s.Pos - rot * Vector3.Scale(scale, kind.Offset), rot);
            body.T.localScale = scale;
        }

        // In the animation it was in, at the moment of it that it was at (its own clock stopped, so the
        // recording's is the only one).
        private void Animate(Body body, in EnemyPath.State s)
        {
            int owner = s.Anim != 0 ? OwnerOf(body, s.Anim) : -1;
            Animator animator = owner >= 0 ? body.Animators[owner] : null;
            if (animator == null || !animator.isActiveAndEnabled)
            {
                if (body.Playing != 0)
                {
                    // (a state its controller doesn't have, or none recorded: it plays on its own)
                    foreach (Animator a in body.Animators)
                    {
                        if (a != null)
                        {
                            a.speed = 1f;
                        }
                    }
                    body.Playing = 0;
                }
                return;
            }
            animator.speed = 0f;
            animator.Play(s.Anim, 0, s.AnimTime);
            animator.Update(0f);
            body.Playing = s.Anim;
        }

        // Which of a copy's animators has that state (asked once per kind of enemy and state).
        private int OwnerOf(Body body, int state)
        {
            Kind kind = body.Kind;
            if (kind.Owners.TryGetValue(state, out int owner))
            {
                return owner;
            }
            owner = -1;
            bool sure = true;
            for (int i = 0; i < body.Animators.Length; i++)
            {
                Animator a = body.Animators[i];
                if (a == null || a.runtimeAnimatorController == null || !a.isActiveAndEnabled)
                {
                    continue;
                }
                if (!a.isInitialized)
                {
                    sure = false;  // (just switched on: ask again next frame)
                    continue;
                }
                if (a.HasState(0, state))
                {
                    owner = i;
                    break;
                }
            }
            if (owner >= 0 || sure)
            {
                kind.Owners[state] = owner;
                statesAsked++;
                statesFound += owner >= 0 ? 1 : 0;
            }
            return owner;
        }

        // Its middle in the world, and about how big it is.
        private Vector3 Middle(EnemyPath e, in EnemyPath.State s, out float size)
        {
            Kind kind = kinds.TryGetValue(e.Type, out Kind k) && k?.Template != null ? k : null;
            Vector3 center = kind != null ? kind.Center : DefaultCenter;
            Vector3 extent = kind != null ? kind.Size : DefaultSize;
            size = Mathf.Clamp(extent.magnitude * e.Scale, 0.8f, 14f);
            return s.Pos + s.Rot * (center * e.Scale);
        }

        // Blood where it got hit: a quick red puff and a few drops (a bigger puff for a bigger hit).
        private void DrawHit(EnemyPath e, in EnemyPath.State s, int index, float when, float ago, float amount)
        {
            float k = ago / HitLife;
            Vector3 at = Middle(e, s, out float size);
            float puff = size * (0.16f + 0.2f * Mathf.Min(1f, amount * 4f)) * (0.6f + 0.4f * k);
            Draw(sphere, Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * puff), new Color(Blood.r, Blood.g, Blood.b, (1f - k) * 0.5f));
            uint seed = Seed(index, Mathf.RoundToInt(when * 60f));
            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = RandomDirection(ref seed);
                Draw(cube, Matrix4x4.TRS(at + dir * (size * 0.45f * k), Quaternion.LookRotation(dir), Vector3.one * (size * 0.05f * (1f - k))),
                    new Color(Blood.r, Blood.g, Blood.b, (1f - k) * 0.9f));
            }
        }

        // Where it died: a burst of red and pieces flying off. It's gone after.
        private void DrawPop(EnemyPath e, int index, float age)
        {
            float k = age / PopLife;
            float ease = 1f - (1f - k) * (1f - k);
            EnemyPath.State s = e.At(e.To);
            Vector3 at = Middle(e, s, out float size);
            Draw(sphere, Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * Mathf.Lerp(0.3f, 1.1f * size, ease)), new Color(Blood.r, Blood.g, Blood.b, (1f - k) * 0.6f));
            uint seed = Seed(index, 7);
            for (int i = 0; i < 9; i++)
            {
                Vector3 dir = RandomDirection(ref seed);
                Vector3 pos = at + dir * (ease * size * 1.1f) + Vector3.down * (k * k * size * 0.6f);
                Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(age * 600f, age * 450f, 0f);
                Draw(cube, Matrix4x4.TRS(pos, spin, Vector3.one * (size * 0.09f * (1f - k))), new Color(Blood.r, Blood.g, Blood.b, (1f - k) * 0.95f));
            }
        }

        // An enemy with no model (the spawner doesn't have it and it isn't in the level, or copying it
        // failed): a see-through block its size, so it's still there.
        private void DrawStandIn(EnemyPath e, in EnemyPath.State s)
        {
            Kind kind = kinds.TryGetValue(e.Type, out Kind k) && k?.Template != null ? k : null;
            Vector3 center = kind != null ? kind.Center : DefaultCenter;
            Vector3 size = (kind != null ? kind.Size : DefaultSize) * e.Scale;
            Draw(cube, Matrix4x4.TRS(s.Pos + s.Rot * (center * e.Scale), s.Rot, size), new Color(StandInColor.r, StandInColor.g, StandInColor.b, 0.5f));
        }

        // The level's own enemies (alive or dead, frozen as you left them) out of sight, or back.
        private void SetLevelHidden(bool hide)
        {
            if (hide == levelHidden)
            {
                return;
            }
            levelHidden = hide;
            if (!hide)
            {
                foreach (Renderer r in hidden)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
                hidden.Clear();
                return;
            }
            foreach (EnemyIdentifier eid in UnityEngine.Object.FindObjectsByType<EnemyIdentifier>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (eid == null)
                {
                    continue;
                }
                foreach (Renderer r in eid.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        hidden.Add(r);
                    }
                }
            }
        }

        private static GameObject LiveEnemy(int type)
        {
            if (type < 0)
            {
                return null;
            }
            foreach (EnemyIdentifier eid in UnityEngine.Object.FindObjectsByType<EnemyIdentifier>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (eid != null && (int)eid.enemyType == type && !eid.dead && !eid.puppet)
                {
                    return eid.gameObject;
                }
            }
            return null;
        }

        private static Transform EnemyIn(Transform root)
        {
            EnemyIdentifier eid = root.GetComponentInChildren<EnemyIdentifier>(true);
            return eid != null ? eid.transform : root;
        }

        // The child indices from root down to target, to find the same object in a copy.
        private static int[] PathTo(Transform root, Transform target)
        {
            var steps = new List<int>();
            for (Transform t = target; t != null && t != root; t = t.parent)
            {
                steps.Add(t.GetSiblingIndex());
            }
            steps.Reverse();
            return steps.ToArray();
        }

        private static Transform Walk(Transform root, int[] path)
        {
            Transform t = root;
            foreach (int i in path)
            {
                if (i >= t.childCount)
                {
                    return null;
                }
                t = t.GetChild(i);
            }
            return t;
        }

        // Each part on the layer it has in the game (so it's lit and drawn like the real one), where the
        // camera draws that layer.
        private void CopyLayers(GameObject source, GameObject copy)
        {
            Transform[] from = source.GetComponentsInChildren<Transform>(true);
            Transform[] to = copy.GetComponentsInChildren<Transform>(true);
            if (from.Length != to.Length)
            {
                return;
            }
            for (int i = 0; i < to.Length; i++)
            {
                int l = from[i].gameObject.layer;
                if ((cam.cullingMask & (1 << l)) != 0)
                {
                    to[i].gameObject.layer = l;
                }
            }
        }

        private static string TypeName(int type) =>
            Enum.IsDefined(typeof(EnemyType), type) ? ((EnemyType)type).ToString() : "enemy type " + type;

        // ---------------------------------------------------------------- doors

        // The doors as g had them t seconds in: each one's last change by then (sample 0 says how they all
        // started), sliding at the door's own speed after a change. Only plain sliding doors move.
        private void ShowDoors(Ghost g, float t)
        {
            if (g != doorsOf)
            {
                PutDoorsBack();
                doorsOf = g;
                int n = g != null ? g.DoorPaths.Length : 0;
                shownDoors = new DoorSpot[n];
                doorKnown = new bool[n];
                doorOpen = new bool[n];
                doorSince = new float[n];
                for (int i = 0; i < n; i++)
                {
                    shownDoors[i] = Spot(g.DoorPaths[i]);
                }
            }
            if (g == null || g.Doors.Length == 0)
            {
                return;
            }
            float at = Mathf.Min(t, g.Duration);
            int sample = Mathf.FloorToInt(at * g.Rate);
            Array.Clear(doorKnown, 0, doorKnown.Length);
            foreach (Ghost.DoorChange c in g.Doors)
            {
                if (c.Sample > sample)
                {
                    break;
                }
                if (c.Door >= doorKnown.Length || (doorKnown[c.Door] && doorOpen[c.Door] == c.Open))
                {
                    continue;
                }
                // (how it started: already in place; a change: it slides from then on)
                doorSince[c.Door] = c.Sample == 0 ? float.NegativeInfinity : c.Sample / (float)g.Rate;
                doorOpen[c.Door] = c.Open;
                doorKnown[c.Door] = true;
            }
            bool changed = false;
            for (int i = 0; i < shownDoors.Length; i++)
            {
                DoorSpot spot = shownDoors[i];
                if (spot == null)
                {
                    continue;
                }
                if (doorKnown[i])
                {
                    changed |= Slide(spot, doorOpen[i], at - doorSince[i]);
                }
                else if (spot.Moved)
                {
                    changed |= PutBack(spot);  // (seeked back to before the attempt knew it)
                }
            }
            if (changed)
            {
                Physics.SyncTransforms();  // (so shots and the camera hit the doors where they're shown)
            }
        }

        // Returns whether it moved.
        private bool Slide(DoorSpot spot, bool open, float since)
        {
            Door door = spot.Door;
            if (door == null || spot.T == null)
            {
                return false;
            }
            Vector3 to = open ? door.openPosRelative : door.closedPos;
            Vector3 from = open ? door.closedPos : door.openPosRelative;
            Vector3 at = to;
            float distance = Vector3.Distance(from, to);
            if (!float.IsInfinity(since) && door.speed > 0f && distance > 0.001f)
            {
                float k = Mathf.Clamp01(since * door.speed / distance);
                at = Vector3.LerpUnclamped(from, to, door.easeIn ? 1f - (1f - k) * (1f - k) : k);
            }
            if (!spot.Moved)
            {
                spot.Home = spot.T.localPosition;
                spot.HomeInPos = door.inPos;
                spot.Moved = true;
                moved.Add(spot);
            }
            door.inPos = true;  // (its own Update only moves it while it isn't in place: this keeps it out of the way)
            if (spot.T.localPosition == at)
            {
                return false;
            }
            spot.T.localPosition = at;
            return true;
        }

        private static bool PutBack(DoorSpot spot)
        {
            spot.Moved = false;
            if (spot.Door != null)
            {
                spot.Door.inPos = spot.HomeInPos;
            }
            if (spot.T == null)
            {
                return false;
            }
            spot.T.localPosition = spot.Home;
            return true;
        }

        private void PutDoorsBack()
        {
            bool any = false;
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                if (moved[i].Moved)
                {
                    any |= PutBack(moved[i]);
                }
            }
            moved.Clear();
            if (any)
            {
                Physics.SyncTransforms();
            }
        }

        // A door by its path (looked up once): only a plain sliding door that knows its open and shut places.
        private DoorSpot Spot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (spots.TryGetValue(path, out DoorSpot spot))
            {
                return spot;
            }
            Door door = null;
            try
            {
                Transform t = Game.FindByPath(path);
                door = t != null ? t.GetComponent<Door>() : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"finding the door {path}: {e.Message}");
            }
            spot = null;
            if (door != null && door.doorType == DoorType.Normal && door.gotPos && !spotOf.TryGetValue(door, out spot))
            {
                spot = new DoorSpot { Door = door, T = door.transform };
                spotOf[door] = spot;
            }
            spots[path] = spot;
            return spot;
        }

        // ---------------------------------------------------------------- drawing

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, matrix, material, layer, cam, 0, block, false, false, false);
        }

        // Unlit and see-through, like the ghosts.
        private static Material MakeMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)CullMode.Back);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            mat.renderQueue = 3000;
            return mat;
        }

        private static int FirstLayer(int mask)
        {
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    return i;
                }
            }
            return 0;
        }

        // A little xorshift, so the bursts scatter the same way every time without allocating.
        private static uint Seed(int a, int b)
        {
            unchecked
            {
                return ((uint)(a * 73856093) ^ (uint)(b * 19349663)) | 1u;
            }
        }

        private static float Next(ref uint seed)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed / 4294967296f;
        }

        private static Vector3 RandomDirection(ref uint seed)
        {
            var v = new Vector3(Next(ref seed) * 2f - 1f, Next(ref seed) * 1.6f - 0.4f, Next(ref seed) * 2f - 1f);
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.up;
        }
    }
}
