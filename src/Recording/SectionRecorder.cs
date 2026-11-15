using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UltraAttempts.Recording
{
    // Which parts of the level were switched on while an attempt played, so a replay can show the level as that attempt
    // had it. ULTRAKILL switches whole areas on as you reach them and off behind you (doors' rooms, its scripts' events),
    // and its checkpoints make rooms again from copies. Once a scene the parts are found, a few milliseconds a frame: what
    // the level's scripts switch on and off, and what's switched off when the level starts, as long as there's something
    // to see in it (not an enemy's, the player's, or a checkpoint's spare room). Then, ten times a second while an attempt
    // records, whether each is on: its state when the attempt started, then each change. Like the rest of the recorder it
    // only reads.
    internal sealed class SectionRecorder
    {
        private const int MostParts = 4096;
        private const int PollsPerSecond = 10;
        private const float ScanMsPerFrame = 3f;

        // The scene's parts (found once), and where each is now.
        private bool scanned;
        private IEnumerator<bool> scanning;
        private float frameStarted;
        private int scanFrames;
        private string[] paths = Array.Empty<string>();      // sorted (an attempt keeps its own copy)
        private GameObject[] objects = Array.Empty<GameObject>();
        private CheckPoint[] checkpoints = Array.Empty<CheckPoint>();
        private int rooms;               // the checkpoints' rooms as they were (when they change, they were made again)
        private int findAfter = -1;      // a frame: find the parts again once it's over (the old rooms are gone by then)
        private int fromScripts, offAtStart;
        private bool capped;
        private float scanMs;

        // The attempt being recorded: each part as last recorded (-1: not yet).
        private RawAttempt attempt;
        private sbyte[] last = Array.Empty<sbyte>();
        private bool first;
        private int nextPoll;
        private int pollEvery = 1;
        private int lookedFrame = -1;
        private bool failed;
        private static bool warnedFailure;

        private readonly List<Transform> chain = new List<Transform>();

        // A new scene: its parts are found again.
        public void EnterScene()
        {
            End();
            scanned = false;
            scanning = null;
            scanFrames = 0;
            paths = Array.Empty<string>();
            objects = Array.Empty<GameObject>();
            checkpoints = Array.Empty<CheckPoint>();
            findAfter = -1;
            fromScripts = offAtStart = 0;
            capped = false;
            scanMs = 0f;
        }

        // An attempt starts (record: its world is recorded).
        public void Begin(RawAttempt a, bool record)
        {
            End();
            failed = false;
            if (!record)
            {
                return;
            }
            attempt = a;
            first = true;
            nextPoll = 0;
            pollEvery = Math.Max(1, a.SampleRate / PollsPerSecond);
        }

        public void End()
        {
            attempt = null;
            last = Array.Empty<sbyte>();
        }

        // An attempt sample: until the scene's parts are found, a little more of that; then, every few, a look at each.
        public void Tick(int sample)
        {
            if (attempt == null || failed || Time.frameCount == lookedFrame)
            {
                return;
            }
            try
            {
                if (!scanned)
                {
                    lookedFrame = Time.frameCount;
                    Scanning();
                }
                else if (sample >= nextPoll)
                {
                    lookedFrame = Time.frameCount;
                    nextPoll = sample + pollEvery;
                    Poll(sample);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // For the log: what an attempt recorded of the level's parts.
        public string Summary(RawAttempt a)
        {
            int on = 0, switched = 0;
            var seen = new HashSet<int>();
            foreach (SectionChange c in a.SectionChanges)
            {
                if (seen.Add(c.Section))
                {
                    on += c.On ? 1 : 0;
                }
                else
                {
                    switched++;
                }
            }
            return $"{a.SectionPaths.Count} parts ({on} on when it started), {switched} switched on or off during it; {fromScripts} switched by the level's scripts, "
                + $"{offAtStart} off when the level started{(capped ? $" (the first {MostParts})" : "")}, found in {scanMs:0} ms over {scanFrames} frames";
        }

        // A few milliseconds more of finding the scene's parts (a failure leaves the scene without them).
        private void Scanning()
        {
            frameStarted = Time.realtimeSinceStartup;
            scanFrames++;
            bool more;
            try
            {
                scanning = scanning ?? Scan();
                more = scanning.MoveNext();
            }
            catch (Exception)
            {
                scanning = null;
                scanned = true;
                paths = Array.Empty<string>();
                objects = Array.Empty<GameObject>();
                throw;
            }
            finally
            {
                scanMs += (Time.realtimeSinceStartup - frameStarted) * 1000f;
            }
            if (!more)
            {
                scanning = null;
                scanned = true;
                nextPoll = 0;  // (the first look, at once: how the attempt started)
            }
        }

        // (this frame's few milliseconds are up)
        private bool Over() => (Time.realtimeSinceStartup - frameStarted) * 1000f > ScanMsPerFrame;

        private void Poll(int sample)
        {
            if (paths.Length == 0)
            {
                return;
            }
            // A checkpoint made its rooms again (or a part was destroyed): they're found again a frame on, once the
            // old copies are gone (Destroy waits for the frame's end).
            int now = Rooms();
            if (now != rooms)
            {
                rooms = now;
                findAfter = Time.frameCount;
                return;
            }
            if (findAfter >= 0)
            {
                if (Time.frameCount <= findAfter)
                {
                    return;
                }
                findAfter = -1;
                FindAgain();
            }
            if (first)
            {
                attempt.SectionPaths.AddRange(paths);
                last = new sbyte[paths.Length];
                for (int i = 0; i < last.Length; i++)
                {
                    last[i] = -1;
                }
            }
            int at = first ? 0 : sample;  // (the first look is how it started)
            first = false;
            for (int i = 0; i < objects.Length; i++)
            {
                GameObject go = objects[i];
                if (go == null)
                {
                    if (!ReferenceEquals(go, null))
                    {
                        objects[i] = null;
                        findAfter = Time.frameCount;
                    }
                    continue;
                }
                sbyte on = go.activeSelf ? (sbyte)1 : (sbyte)0;
                if (last[i] != on)
                {
                    last[i] = on;
                    attempt.SectionChanges.Add(new SectionChange { Sample = at, Section = i, On = on == 1 });
                }
            }
        }

        // The checkpoints' rooms as they are: a checkpoint makes its rooms again when it's first switched on, reached (taking
        // over the rooms before it) or respawned at.
        private int Rooms()
        {
            unchecked
            {
                int h = 17;
                foreach (CheckPoint cp in checkpoints)
                {
                    if (cp != null)
                    {
                        h = h * 31 + Ids(cp.newRooms);
                        h = h * 31 + Ids(cp.defaultRooms);
                    }
                }
                return h;
            }
        }

        private static int Ids(List<GameObject> list)
        {
            unchecked
            {
                if (list == null)
                {
                    return 0;
                }
                int h = list.Count;
                foreach (GameObject go in list)
                {
                    h = h * 31 + (ReferenceEquals(go, null) ? 0 : go.GetInstanceID());
                }
                return h;
            }
        }

        // Each part gone, or in a room a checkpoint has just kept as its spare, found again by its path (in the room made
        // in its place).
        private void FindAgain()
        {
            var level = new LevelPaths();
            checkpoints = level.Checkpoints;
            rooms = Rooms();
            for (int i = 0; i < objects.Length; i++)
            {
                GameObject go = objects[i];
                if (go != null && !level.InSpare(go.transform))
                {
                    continue;
                }
                Transform t = level.Find(paths[i]);
                objects[i] = t != null ? t.gameObject : null;
            }
        }

        // Every part of the scene, found once, a few milliseconds a frame (each yield: this frame's time is up).
        private IEnumerator<bool> Scan()
        {
            var level = new LevelPaths();
            checkpoints = level.Checkpoints;
            Scene scene = level.Scene;

            // Left out: enemies (the replay shows the attempt's own), the player and its camera, the checkpoints' spare
            // rooms, and the replay's own things.
            var outside = new HashSet<Transform>();
            foreach (EnemyIdentifier e in Object.FindObjectsByType<EnemyIdentifier>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (e != null)
                {
                    outside.Add(e.transform);
                }
            }
            if (Game.Player != null)
            {
                outside.Add(Game.Player.transform);
            }
            var cc = Game.Get<CameraController>();
            if (cc != null)
            {
                outside.Add(cc.transform);
            }
            foreach (GameObject spare in level.Spares)
            {
                outside.Add(spare.transform);
            }
            yield return true;

            // Everything with something to see in it (each renderer's every parent).
            var memo = new Dictionary<Transform, bool>();
            var holds = new HashSet<Transform>();
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                if ((i & 63) == 63 && Over())
                {
                    yield return true;
                }
                Renderer r = renderers[i];
                if (r == null || !r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer || r is SpriteRenderer))
                {
                    continue;
                }
                Transform t = r.transform;
                if (holds.Contains(t) || t.gameObject.scene != scene || Left(t, outside, memo))
                {
                    continue;
                }
                for (; t != null && holds.Add(t); t = t.parent)
                {
                }
            }
            renderers = null;
            yield return true;

            // What the level's scripts switch, then what's off now.
            var targets = new HashSet<GameObject>();
            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if ((i & 63) == 63 && Over())
                {
                    yield return true;
                }
                MonoBehaviour mb = behaviours[i];
                if (mb != null && mb.gameObject.scene == scene)
                {
                    Switches.Collect(mb, targets);
                }
            }
            behaviours = null;
            var picked = new List<Transform>();
            var seen = new HashSet<Transform>();
            foreach (GameObject go in targets)
            {
                if (go != null && holds.Contains(go.transform) && seen.Add(go.transform))
                {
                    picked.Add(go.transform);
                }
            }
            fromScripts = picked.Count;
            // (only the outermost of what's off: something off inside it that no script switches stays off with it, as
            // the copy of it a replay shows has it)
            var off = new List<Transform>();
            foreach (Transform t in holds)
            {
                if (t != null && !t.gameObject.activeSelf && !seen.Contains(t) && !InSomethingOff(t))
                {
                    off.Add(t);
                }
            }
            if (picked.Count + off.Count > MostParts)
            {
                // (the biggest first: the nearest the top)
                capped = true;
                var depth = new Dictionary<Transform, int>();
                foreach (Transform t in off)
                {
                    int d = 0;
                    for (Transform p = t.parent; p != null; p = p.parent)
                    {
                        d++;
                    }
                    depth[t] = d;
                }
                off.Sort((a, b) => depth[a].CompareTo(depth[b]));
                if (picked.Count > MostParts)
                {
                    picked.RemoveRange(MostParts, picked.Count - MostParts);
                    fromScripts = MostParts;
                }
            }
            foreach (Transform t in off)
            {
                if (picked.Count >= MostParts)
                {
                    break;
                }
                picked.Add(t);
                offAtStart++;
            }
            yield return true;

            // Sorted by path: parts come before what's in them, and neighbours share most of theirs (smaller files).
            var found = new List<(string path, GameObject go)>(picked.Count);
            var unique = new HashSet<string>();
            for (int i = 0; i < picked.Count; i++)
            {
                if ((i & 63) == 63 && Over())
                {
                    yield return true;
                }
                Transform t = picked[i];
                string path = t != null ? level.Of(t) : null;
                if (path != null && unique.Add(path))
                {
                    found.Add((path, t.gameObject));
                }
            }
            found.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            paths = new string[found.Count];
            objects = new GameObject[found.Count];
            for (int i = 0; i < found.Count; i++)
            {
                paths[i] = found[i].path;
                objects[i] = found[i].go;
            }
            rooms = Rooms();
        }

        private static bool InSomethingOff(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (!p.gameObject.activeSelf)
                {
                    return true;
                }
            }
            return false;
        }

        // Whether t is in something left out (memo: what's known of each transform on the way up).
        private bool Left(Transform t, HashSet<Transform> outside, Dictionary<Transform, bool> memo)
        {
            chain.Clear();
            bool left = false;
            for (Transform p = t; p != null; p = p.parent)
            {
                if (memo.TryGetValue(p, out bool known))
                {
                    left = known;
                    break;
                }
                chain.Add(p);
                if (outside.Contains(p) || (p.parent == null && p.name.StartsWith("UltraAttempts", StringComparison.Ordinal)))
                {
                    left = true;
                    break;
                }
            }
            foreach (Transform p in chain)
            {
                memo[p] = left;
            }
            return left;
        }


        // Something in the game didn't look as expected: this attempt goes without its level's parts (a replay shows the
        // rooms it brings back for recordings from before them), and the rest of it is still recorded.
        private void Fail(Exception e)
        {
            failed = true;
            if (attempt != null)
            {
                attempt.SectionPaths.Clear();
                attempt.SectionChanges.Clear();
            }
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording which parts of the level are switched on failed, so this attempt has none (it's still recorded): " + e);
            }
        }

        // What a level's script switches on and off, read from its fields: UltrakillEvents (toActivateObjects,
        // toDisActivateObjects, and their UnityEvents), any UnityEvent's SetActive calls, and GameObjects in fields named
        // for it (toActivate, deactivatedRooms, objectsToHide, a FakeRoomActivator's fake, a blocker, rubble...); also in
        // its own [Serializable] parts, one level down. (Whatever isn't something to see is left out after.)
        private static class Switches
        {
            private static readonly Dictionary<Type, FieldInfo[]> fieldsOf = new Dictionary<Type, FieldInfo[]>();
            private static readonly string[] Words = { "activ", "appear", "hide", "show", "enabl", "disabl", "room", "fake", "block", "rubble", "open", "graphic", "model" };

            public static void Collect(object owner, HashSet<GameObject> into, int depth = 0)
            {
                foreach (FieldInfo f in Fields(owner.GetType()))
                {
                    object value = f.GetValue(owner);
                    if (value != null)
                    {
                        Take(value, into, depth);
                    }
                }
            }

            private static void Take(object value, HashSet<GameObject> into, int depth)
            {
                switch (value)
                {
                    case UltrakillEvent e:
                        Add(e.toActivateObjects, into);
                        Add(e.toDisActivateObjects, into);
                        Calls(e.onActivate, into);
                        Calls(e.onDisActivate, into);
                        return;
                    case UnityEventBase e:
                        Calls(e, into);
                        return;
                    case GameObject go:
                        if (go != null)
                        {
                            into.Add(go);
                        }
                        return;
                    case Object _:
                        return;
                    case IList list:
                        foreach (object item in list)
                        {
                            if (item != null)
                            {
                                Take(item, into, depth);
                            }
                        }
                        return;
                    default:
                        if (depth == 0)
                        {
                            Collect(value, into, depth + 1);
                        }
                        return;
                }
            }

            private static void Add(GameObject[] objects, HashSet<GameObject> into)
            {
                if (objects == null)
                {
                    return;
                }
                foreach (GameObject go in objects)
                {
                    if (go != null)
                    {
                        into.Add(go);
                    }
                }
            }

            // A UnityEvent's GameObject.SetActive calls set in the level (the ones not switched off).
            private static void Calls(UnityEventBase e, HashSet<GameObject> into)
            {
                if (e == null)
                {
                    return;
                }
                int count = e.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                {
                    if (e.GetPersistentMethodName(i) == "SetActive" && e.GetPersistentTarget(i) is GameObject go && go != null
                        && e.GetPersistentListenerState(i) != UnityEventCallState.Off)
                    {
                        into.Add(go);
                    }
                }
            }

            // The fields worth reading on a type (serialized ones of the kinds above), worked out once per type.
            private static FieldInfo[] Fields(Type type)
            {
                if (fieldsOf.TryGetValue(type, out FieldInfo[] known))
                {
                    return known;
                }
                fieldsOf[type] = Array.Empty<FieldInfo>();  // (a type that holds itself stops here)
                var fields = new List<FieldInfo>();
                for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                {
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        bool serialized = (f.IsPublic && !f.IsNotSerialized) || f.IsDefined(typeof(SerializeField), false);
                        if (serialized && !f.IsInitOnly && Wanted(f))
                        {
                            fields.Add(f);
                        }
                    }
                }
                FieldInfo[] found = fields.ToArray();
                fieldsOf[type] = found;
                return found;
            }

            private static bool Wanted(FieldInfo f)
            {
                Type e = Element(f.FieldType);
                if (e == typeof(UltrakillEvent) || typeof(UnityEventBase).IsAssignableFrom(e))
                {
                    return true;
                }
                if (e == typeof(GameObject))
                {
                    string name = f.Name.ToLowerInvariant();
                    foreach (string word in Words)
                    {
                        if (name.Contains(word))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                // (a part of the level's own script: a [Serializable] class or struct of its own, one with fields like these)
                return !e.IsPrimitive && !e.IsEnum && e != typeof(string) && !typeof(Object).IsAssignableFrom(e) && e.Assembly == typeof(UltrakillEvent).Assembly
                    && e.IsDefined(typeof(SerializableAttribute), false) && Fields(e).Length > 0;
            }

            private static Type Element(Type t)
            {
                if (t.IsArray)
                {
                    return t.GetElementType();
                }
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return t.GetGenericArguments()[0];
                }
                return t;
            }
        }
    }

    // Where a part of the level is, kept the same however its checkpoints have swapped its rooms about. Like Game.PathOf
    // (each step its name without "(Clone)" and which of the same-named ones it is), but a checkpoint's spare rooms
    // (CheckPoint.defaultRooms: switched off and moved 10 km away, to make the room again from) don't count. So a room
    // and the copy a checkpoint made of it (and the copy made of that, on a respawn) have one path, and so does
    // everything in them, now and in a later visit. Made for one look at the level: it remembers what it's seen.
    internal sealed class LevelPaths
    {
        public readonly Scene Scene;
        public readonly CheckPoint[] Checkpoints;
        public readonly HashSet<GameObject> Spares = new HashSet<GameObject>();

        private readonly Dictionary<Transform, Dictionary<string, List<Transform>>> children = new Dictionary<Transform, Dictionary<string, List<Transform>>>();
        private Dictionary<string, List<Transform>> roots;
        private readonly Dictionary<Transform, string> known = new Dictionary<Transform, string>();

        public LevelPaths()
        {
            Scene = SceneManager.GetActiveScene();
            Checkpoints = Object.FindObjectsByType<CheckPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (CheckPoint cp in Checkpoints)
            {
                if (cp == null || cp.defaultRooms == null)
                {
                    continue;
                }
                foreach (GameObject room in cp.defaultRooms)
                {
                    if (room != null)
                    {
                        Spares.Add(room);
                    }
                }
            }
        }

        public bool InSpare(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (Spares.Contains(t.gameObject))
                {
                    return true;
                }
            }
            return false;
        }

        // Its path (null: a spare room's, or not in this scene).
        public string Of(Transform t)
        {
            if (t == null)
            {
                return null;
            }
            if (known.TryGetValue(t, out string path))
            {
                return path;
            }
            string name = Step(t.name);
            List<Transform> same = Named(t.parent, name);
            int ordinal = same != null ? same.IndexOf(t) : -1;
            if (ordinal < 0)
            {
                path = null;
            }
            else if (t.parent == null)
            {
                path = name + "#" + ordinal;
            }
            else
            {
                string above = Of(t.parent);
                path = above != null ? above + "/" + name + "#" + ordinal : null;
            }
            known[t] = path;
            return path;
        }

        // What's at a path now (null: nothing).
        public Transform Find(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            Transform at = null;
            bool top = true;
            foreach (string step in path.Split('/'))
            {
                int hash = step.LastIndexOf('#');
                if (hash < 0 || !int.TryParse(step.Substring(hash + 1), out int ordinal))
                {
                    return null;
                }
                List<Transform> same = Named(top ? null : at, step.Substring(0, hash));
                if (same == null || ordinal < 0 || ordinal >= same.Count)
                {
                    return null;
                }
                at = same[ordinal];
                top = false;
            }
            return at;
        }

        // (a name as a step: "/" would split it)
        private static string Step(string name) => Game.CleanName(name).Replace('/', '|');

        // parent's children (null: the scene's roots) called name, in order, but the spare rooms.
        private List<Transform> Named(Transform parent, string name)
        {
            Dictionary<string, List<Transform>> byName;
            if (parent == null)
            {
                if (roots == null)
                {
                    roots = new Dictionary<string, List<Transform>>();
                    foreach (GameObject go in Scene.GetRootGameObjects())
                    {
                        Put(roots, go.transform);
                    }
                }
                byName = roots;
            }
            else if (!children.TryGetValue(parent, out byName))
            {
                byName = new Dictionary<string, List<Transform>>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    Put(byName, parent.GetChild(i));
                }
                children[parent] = byName;
            }
            return byName.TryGetValue(name, out List<Transform> list) ? list : null;
        }

        private void Put(Dictionary<string, List<Transform>> byName, Transform t)
        {
            if (Spares.Contains(t.gameObject))
            {
                return;
            }
            string name = Step(t.name);
            if (!byName.TryGetValue(name, out List<Transform> list))
            {
                list = new List<Transform>();
                byName[name] = list;
            }
            list.Add(t);
        }
    }
}
