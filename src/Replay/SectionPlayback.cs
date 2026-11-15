using System;
using System.Collections.Generic;
using System.Reflection;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // The level as the attempt shown had it (SectionRecorder recorded which of its parts were switched on, and when).
    // Nothing of the game's is switched on or off, so none of its scripts, triggers, enemies, arenas, music or sounds wake:
    // - A part it had on that's off now shows as a copy that only draws. Each is made while the replay loads, from the
    //   switched-off original inside a switched-off holder, and stripped of everything but what it looks like (meshes,
    //   materials, lights, LODs) before it could ever wake; the parts inside it switch on and off in the copy as they did.
    //   Copies aren't in the level's baked occlusion data, so they're only culled by what the camera can see.
    // - A part it had off that's on now has its renderers hidden (the camera's occlusion culling is off meanwhile: a
    //   hidden wall still hides what's behind it in the baked data).
    // Attempts recorded before parts were (or without their world) show the rooms the level's doors switch (RoomReveal)
    // as copies, as they were brought back before. Dispose puts back every renderer hidden and drops the copies.
    internal sealed class SectionPlayback
    {
        private const float LoadingMs = 25f;    // copies made in a frame while the replay loads (at most about this long)
        private const int MostAhead = 256;      // copies made ahead; any more, when first wanted

        // (a copy of a statically batched renderer draws its part of the combined mesh, if the batch came with it)
        private static readonly MethodInfo setStaticBatch = typeof(Renderer).GetMethod("SetStaticBatchInfo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo staticBatchRoot = typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool warnedCopy;

        private readonly Camera cam;
        private readonly Transform holder;      // the copies, each switched on while it shows
        private readonly Transform workshop;    // switched off: copies are made in here, so nothing in them ever wakes
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private Section[] all = Array.Empty<Section>();
        private int[] order = Array.Empty<int>();             // the parts in order of how deep: each one's parent first
        private sbyte[] state = Array.Empty<sbyte>();         // the shown attempt's: -1 not known (as it is now), 0 off, 1 on
        private bool[] wanted = Array.Empty<bool>();          // ...and so whether each is to be seen
        private readonly Dictionary<string[], int[]> maps = new Dictionary<string[], int[]>();   // an attempt's parts in all (-1: not found)
        private readonly List<int> copyRoots = new List<int>();
        private readonly List<int> hideRoots = new List<int>();
        private readonly List<Section> showing = new List<Section>();
        private readonly HashSet<Renderer> hidden = new HashSet<Renderer>();

        private bool prepared, disposed;
        private int ahead, hideAhead;
        private bool started;
        private Ghost lastGhost;
        private int lastSample = -1;
        private int[] map;
        private int cursor;
        private bool dirty;
        private bool occlusionOff, occlusionWas;

        // (for the log)
        private float madeMs;
        private int recorded, notFound, oldAttempts, fallbackRooms, made, failed;
        private int everShown, shownRenderers, everHidden, hiddenRenderers;
        private int batchedKept, batchedSet, batchedDrawn;

        // A part of the level found now, how it is now, and what the replay does with it.
        private sealed class Section
        {
            public GameObject Go;
            public Transform T;
            public int Parent = -1;         // the nearest part it's in
            public int Depth;
            public bool NowSelf;            // activeSelf now
            public bool NowVis;             // activeInHierarchy now
            public bool GapOn;              // everything between it and its parent part is on now
            public bool EverOn, EverOff;    // any attempt had it so
            public bool Fallback;           // a room brought back for attempts recorded before parts were
            public bool CopyRoot;           // off now, in something on now: it shows as a copy
            public int InCopy = -1;         // inside that copy root
            public List<int> Nested;        // (a copy root) the parts inside it
            public Transform Twin;          // (inside a copy) its place in the copy
            public Copy Copy;
            public bool CopyFailed, Shown, EverShown;
            public Renderer[] Renderers;    // (one it might hide) its renderers, but the enemies'
            public bool Hidden, EverHidden;
        }

        private sealed class Copy
        {
            public GameObject Go;
            public int Renderers;
            public readonly List<(Transform from, Transform to)> Followers = new List<(Transform, Transform)>();   // doors and movers: moved as the original is
            public readonly List<Drawn> Drawn = new List<Drawn>();
        }

        // A statically batched part whose copy couldn't draw it: drawn by hand, its submesh of the combined mesh.
        private struct Drawn
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public Matrix4x4 Matrix;
            public int Layer;
            public Renderer Owner;          // its renderer in the copy (switched off): drawn while that's in something on
            public MaterialPropertyBlock Block;
            public bool Shadows, Receive;
        }

        public SectionPlayback(Camera cam, Transform root)
        {
            this.cam = cam;
            holder = new GameObject("UltraAttempts sections").transform;
            holder.SetParent(root, false);
            var shop = new GameObject("UltraAttempts section workshop");
            shop.SetActive(false);
            shop.transform.SetParent(root, false);
            workshop = shop.transform;
        }

        // ---------------------------------------------------------------- starting and stopping

        // While the replay loads, a frame at a time: the parts the attempts recorded found in the level, then the
        // copies made. True once it's ready.
        public bool MakeAhead(IList<Ghost> ghosts)
        {
            float from = Time.realtimeSinceStartup;
            try
            {
                if (!prepared)
                {
                    prepared = true;
                    Prepare(ghosts);
                }
                while (hideAhead < hideRoots.Count && Ms(from) < LoadingMs)
                {
                    RenderersOf(all[hideRoots[hideAhead++]]);
                }
                while (ahead < copyRoots.Count && ahead < MostAhead && Ms(from) < LoadingMs)
                {
                    Section s = all[copyRoots[ahead++]];
                    if (s.Copy == null && !s.CopyFailed)
                    {
                        Make(s);
                    }
                }
            }
            finally
            {
                madeMs += Ms(from);
            }
            return hideAhead >= hideRoots.Count && ahead >= Math.Min(copyRoots.Count, MostAhead);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (Renderer r in hidden)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            hidden.Clear();
            if (occlusionOff && cam != null)
            {
                cam.useOcclusionCulling = occlusionWas;
            }
            occlusionOff = false;
            showing.Clear();
            foreach (Section s in all)
            {
                if (s.Copy != null && s.Copy.Go != null)
                {
                    Object.Destroy(s.Copy.Go);
                }
            }
            if (holder != null)
            {
                Object.Destroy(holder.gameObject);
            }
            if (workshop != null)
            {
                Object.Destroy(workshop.gameObject);
            }
            if (prepared)
            {
                Plugin.Log.LogInfo(Summary());
            }
            all = Array.Empty<Section>();
        }

        private string Summary()
        {
            string line = $"replay sections: {recorded} recorded for this run{(notFound > 0 ? $" ({notFound} not in the level now)" : "")}, "
                + $"{everShown} shown as copies ({shownRenderers} renderers), {everHidden} hidden ({hiddenRenderers} renderers), made in {madeMs:0} ms "
                + $"({made} copies of {copyRoots.Count} that might show{(failed > 0 ? $", {failed} couldn't be" : "")})";
            if (batchedKept + batchedSet + batchedDrawn > 0)
            {
                line += $"; statically batched renderers in them: {batchedKept} drew as they were, {batchedSet} set up again, {batchedDrawn} drawn by hand";
            }
            if (oldAttempts > 0)
            {
                line += $"; {oldAttempts} attempts recorded before sections, shown with the {fallbackRooms} rooms the level's doors switch";
            }
            return line;
        }

        // The parts every attempt recorded, found in the level as it is now, and what each might need: a copy (off now,
        // and some attempt had it on), or hiding (on now, and some attempt had it off).
        private void Prepare(IList<Ghost> ghosts)
        {
            var level = new LevelPaths();
            var byPath = new Dictionary<string, int>();
            var byObject = new Dictionary<GameObject, int>();
            var list = new List<Section>();
            foreach (Ghost g in ghosts)
            {
                GhostSections s = g.Sections;
                if (s == null)
                {
                    oldAttempts++;
                    continue;
                }
                if (maps.ContainsKey(s.Paths))
                {
                    continue;
                }
                var m = new int[s.Paths.Length];
                for (int i = 0; i < m.Length; i++)
                {
                    string path = s.Paths[i] ?? "";
                    if (!byPath.TryGetValue(path, out int id))
                    {
                        Transform t = level.Find(path);
                        id = t != null ? Add(t.gameObject, list, byObject) : -1;
                        byPath[path] = id;
                        notFound += id < 0 ? 1 : 0;
                    }
                    m[i] = id;
                }
                maps[s.Paths] = m;
            }
            recorded = byPath.Count;
            if (oldAttempts > 0)
            {
                foreach (GameObject room in RoomReveal.Rooms(level))
                {
                    Section s = list[Add(room, list, byObject)];
                    if (!s.Fallback)
                    {
                        s.Fallback = true;
                        s.EverOn = true;
                        fallbackRooms++;
                    }
                }
            }
            all = list.ToArray();

            // How they're nested, and how they are now (the level's frozen: it stays so).
            var byTransform = new Dictionary<Transform, int>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                byTransform[all[i].T] = i;
            }
            foreach (Section s in all)
            {
                bool gap = true;
                for (Transform p = s.T.parent; p != null; p = p.parent)
                {
                    if (byTransform.TryGetValue(p, out int id))
                    {
                        s.Parent = id;
                        break;
                    }
                    gap &= p.gameObject.activeSelf;
                }
                s.GapOn = gap;
            }
            foreach (Section s in all)
            {
                for (int p = s.Parent; p >= 0 && s.Depth <= all.Length; p = all[p].Parent)
                {
                    s.Depth++;
                }
            }
            order = new int[all.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            Array.Sort(order, (a, b) => all[a].Depth != all[b].Depth ? all[a].Depth.CompareTo(all[b].Depth) : a.CompareTo(b));

            // What the attempts had on and off.
            foreach (Ghost g in ghosts)
            {
                GhostSections s = g.Sections;
                if (s == null || !maps.TryGetValue(s.Paths, out int[] m))
                {
                    continue;
                }
                for (int i = 0; i < m.Length; i++)
                {
                    if (m[i] >= 0 && s.KnownAtStart(i))
                    {
                        Ever(all[m[i]], s.OnAtStart(i));
                    }
                }
                for (int k = 0; k < s.What.Length; k++)
                {
                    int i = s.What[k] >> 1;
                    if (i < m.Length && m[i] >= 0)
                    {
                        Ever(all[m[i]], (s.What[k] & 1) != 0);
                    }
                }
            }

            // Which show as copies (off now, in something on now), which are inside one, and which might be hidden.
            foreach (int i in order)
            {
                Section s = all[i];
                Section p = s.Parent >= 0 ? all[s.Parent] : null;
                s.CopyRoot = s.EverOn && !s.NowVis && s.GapOn && (p == null || p.NowVis);
                if (s.CopyRoot)
                {
                    copyRoots.Add(i);
                }
                else if (p != null && (p.CopyRoot || p.InCopy >= 0))
                {
                    s.InCopy = p.CopyRoot ? s.Parent : p.InCopy;
                    Section root = all[s.InCopy];
                    (root.Nested ?? (root.Nested = new List<int>())).Add(i);
                }
                if (s.NowVis && s.EverOff)
                {
                    hideRoots.Add(i);
                }
            }
            state = new sbyte[all.Length];
            wanted = new bool[all.Length];
            for (int i = 0; i < state.Length; i++)
            {
                state[i] = -1;
            }
        }

        private static int Add(GameObject go, List<Section> list, Dictionary<GameObject, int> byObject)
        {
            if (byObject.TryGetValue(go, out int id))
            {
                return id;
            }
            id = list.Count;
            list.Add(new Section { Go = go, T = go.transform, NowSelf = go.activeSelf, NowVis = go.activeInHierarchy });
            byObject[go] = id;
            return id;
        }

        private static void Ever(Section s, bool on)
        {
            if (on)
            {
                s.EverOn = true;
            }
            else
            {
                s.EverOff = true;
            }
        }

        // ---------------------------------------------------------------- every frame

        // The level as shown had it t seconds in (null: as it is now).
        public void Update(Ghost shown, float t)
        {
            if (!prepared || disposed)
            {
                return;
            }
            GhostSections s = shown?.Sections;
            int sample = shown != null ? Mathf.Clamp(Mathf.FloorToInt(t * shown.Rate), 0, Mathf.Max(0, shown.Count - 1)) : 0;
            if (!started || shown != lastGhost || sample < lastSample)
            {
                Reset(shown, s);
            }
            lastSample = sample;
            if (s != null && map != null)
            {
                while (cursor < s.At.Length && s.At[cursor] <= sample)
                {
                    int what = s.What[cursor++];
                    int i = what >> 1;
                    int id = i < map.Length ? map[i] : -1;
                    sbyte on = (sbyte)(what & 1);
                    if (id >= 0 && state[id] != on)
                    {
                        state[id] = on;
                        dirty = true;
                    }
                }
            }
            if (dirty)
            {
                dirty = false;
                Apply();
            }
            Frame();
        }

        // Another attempt, or back in time: each part as it started.
        private void Reset(Ghost shown, GhostSections s)
        {
            started = true;
            lastGhost = shown;
            cursor = 0;
            dirty = true;
            map = null;
            for (int i = 0; i < state.Length; i++)
            {
                state[i] = -1;
            }
            if (s != null)
            {
                if (maps.TryGetValue(s.Paths, out map))
                {
                    for (int i = 0; i < map.Length; i++)
                    {
                        if (map[i] >= 0 && s.KnownAtStart(i))
                        {
                            state[map[i]] = s.OnAtStart(i) ? (sbyte)1 : (sbyte)0;
                        }
                    }
                }
            }
            else if (shown != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Fallback)
                    {
                        state[i] = 1;
                    }
                }
            }
        }

        private void Apply()
        {
            foreach (int i in order)
            {
                Section s = all[i];
                bool on = state[i] < 0 ? s.NowSelf : state[i] > 0;
                wanted[i] = on && s.GapOn && (s.Parent < 0 || wanted[s.Parent]);
            }
            // Hidden: what's on now that it had off (the outermost: what's in it goes with it). The ones coming back first,
            // so a part hidden inside one coming back stays hidden.
            foreach (int i in hideRoots)
            {
                if (all[i].Hidden && !Hides(i))
                {
                    SetHidden(all[i], false);
                }
            }
            foreach (int i in hideRoots)
            {
                if (!all[i].Hidden && Hides(i))
                {
                    SetHidden(all[i], true);
                }
            }
            // Copies: what's off now that it had on, with the parts inside as it had them.
            foreach (int i in copyRoots)
            {
                Section s = all[i];
                if (wanted[i] != s.Shown)
                {
                    Show(s, wanted[i]);
                }
                if (!s.Shown || s.Nested == null)
                {
                    continue;
                }
                foreach (int n in s.Nested)
                {
                    Section inside = all[n];
                    if (inside.Twin == null)
                    {
                        continue;
                    }
                    bool on = state[n] < 0 ? inside.NowSelf : state[n] > 0;
                    if (inside.Twin.gameObject.activeSelf != on)
                    {
                        inside.Twin.gameObject.SetActive(on);
                    }
                }
            }
            Occlusion();
        }

        private bool Hides(int i) => !wanted[i] && (all[i].Parent < 0 || wanted[all[i].Parent]);

        // The copies showing: their doors and movers where the replay has put the originals', their hand-drawn parts.
        private void Frame()
        {
            foreach (Section s in showing)
            {
                Copy c = s.Copy;
                foreach ((Transform from, Transform to) in c.Followers)
                {
                    if (from == null || to == null)
                    {
                        continue;
                    }
                    from.GetLocalPositionAndRotation(out Vector3 pos, out Quaternion rot);
                    to.GetLocalPositionAndRotation(out Vector3 was, out Quaternion wasRot);
                    if (pos != was || rot != wasRot)
                    {
                        to.SetLocalPositionAndRotation(pos, rot);
                    }
                }
                foreach (Drawn d in c.Drawn)
                {
                    if (d.Owner != null && d.Owner.gameObject.activeInHierarchy)
                    {
                        Graphics.DrawMesh(d.Mesh, d.Matrix, d.Material, d.Layer, cam, d.Submesh, d.Block, d.Shadows, d.Receive, true);
                    }
                }
            }
        }

        private void Show(Section s, bool show)
        {
            if (show && s.Copy == null)
            {
                if (s.CopyFailed)
                {
                    return;
                }
                float from = Time.realtimeSinceStartup;
                Make(s);  // (not made ahead: one more than MostAhead)
                madeMs += Ms(from);
                if (s.Copy == null)
                {
                    return;
                }
            }
            if (s.Copy == null)
            {
                return;
            }
            s.Shown = show;
            s.Copy.Go.SetActive(show);
            if (!show)
            {
                showing.Remove(s);
                return;
            }
            showing.Add(s);
            if (!s.EverShown)
            {
                s.EverShown = true;
                everShown++;
                shownRenderers += s.Copy.Renderers;
            }
        }

        private void SetHidden(Section s, bool hide)
        {
            Renderer[] renderers = RenderersOf(s);
            s.Hidden = hide;
            if (!hide)
            {
                foreach (Renderer r in renderers)
                {
                    if (r != null && hidden.Remove(r))
                    {
                        r.enabled = true;
                    }
                }
                return;
            }
            int n = 0;
            foreach (Renderer r in renderers)
            {
                // (only what's on: what the game, or the rest of the replay, has off stays theirs to put back)
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    hidden.Add(r);
                    n++;
                }
            }
            if (!s.EverHidden)
            {
                s.EverHidden = true;
                everHidden++;
                hiddenRenderers += n;
            }
        }

        // A part's renderers, but its enemies' (the replay shows the attempt's own enemies, and hides the level's itself)
        // and V1's own (should the player be in it).
        private static Renderer[] RenderersOf(Section s)
        {
            if (s.Renderers != null)
            {
                return s.Renderers;
            }
            Renderer[] renderers = s.Go.GetComponentsInChildren<Renderer>(true);
            var theirs = new HashSet<Renderer>();
            foreach (EnemyIdentifier e in s.Go.GetComponentsInChildren<EnemyIdentifier>(true))
            {
                if (e != null)
                {
                    theirs.UnionWith(e.GetComponentsInChildren<Renderer>(true));
                }
            }
            NewMovement player = Game.Player;
            if (player != null && player.transform.IsChildOf(s.T))
            {
                theirs.UnionWith(player.GetComponentsInChildren<Renderer>(true));
            }
            if (theirs.Count > 0)
            {
                renderers = Array.FindAll(renderers, r => !theirs.Contains(r));
            }
            s.Renderers = renderers;
            return renderers;
        }

        // While something's hidden, the camera doesn't use the level's baked occlusion culling (in it, a wall hidden
        // still hides whatever's behind it).
        private void Occlusion()
        {
            bool off = hidden.Count > 0;
            if (off == occlusionOff || cam == null)
            {
                return;
            }
            if (off)
            {
                occlusionWas = cam.useOcclusionCulling;
                cam.useOcclusionCulling = false;
            }
            else
            {
                cam.useOcclusionCulling = occlusionWas;
            }
            occlusionOff = off;
        }

        // ---------------------------------------------------------------- copies

        // A copy of a part that only draws, at the original's place, switched off until it's wanted. It's made from the
        // original as it is (switched off) inside the switched-off workshop, so nothing in it ever wakes; everything but
        // what it looks like is stripped out before it's moved to where it can switch on.
        private void Make(Section s)
        {
            GameObject original = s.Go;
            GameObject copy = null;
            try
            {
                if (original == null)
                {
                    throw new InvalidOperationException("it's gone from the level");
                }
                copy = Object.Instantiate(original, workshop, false);
                copy.name = "UltraAttempts copy of " + Game.CleanName(original.name);
                copy.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
                copy.transform.localScale = original.transform.lossyScale;
                Transform[] from = original.GetComponentsInChildren<Transform>(true);
                Transform[] to = copy.GetComponentsInChildren<Transform>(true);
                Renderer[] fromR = original.GetComponentsInChildren<Renderer>(true);
                Renderer[] toR = copy.GetComponentsInChildren<Renderer>(true);
                if (from.Length != to.Length || fromR.Length != toR.Length)
                {
                    throw new InvalidOperationException("its copy came out different");
                }
                var c = new Copy { Go = copy };
                var twin = new Dictionary<Transform, Transform>(from.Length);
                for (int i = 0; i < from.Length; i++)
                {
                    twin[from[i]] = to[i];
                }
                var keep = new HashSet<Transform>();
                if (s.Nested != null)
                {
                    foreach (int n in s.Nested)
                    {
                        if (twin.TryGetValue(all[n].T, out Transform t))
                        {
                            all[n].Twin = t;
                            Keep(keep, t, copy.transform);
                        }
                    }
                }
                foreach (Transform moving in Movers(original))
                {
                    if (moving != original.transform && twin.TryGetValue(moving, out Transform t))
                    {
                        c.Followers.Add((moving, t));
                        Keep(keep, t, copy.transform);
                    }
                }
                for (int i = 0; i < fromR.Length; i++)
                {
                    Look(c, fromR[i], toR[i]);
                }
                Strip(copy, keep);
                c.Renderers = copy.GetComponentsInChildren<Renderer>(true).Length;
                copy.SetActive(false);
                copy.transform.SetParent(holder, true);
                s.Copy = c;
                made++;
            }
            catch (Exception e)
            {
                failed++;
                s.CopyFailed = true;
                if (copy != null)
                {
                    Object.DestroyImmediate(copy);
                }
                if (s.Nested != null)
                {
                    foreach (int n in s.Nested)
                    {
                        all[n].Twin = null;
                    }
                }
                if (!warnedCopy)
                {
                    warnedCopy = true;
                    Plugin.Log.LogWarning($"couldn't copy {(original != null ? Game.CleanName(original.name) : "a part of the level")} to show it in a replay (it's left out): {e.Message}");
                }
            }
        }

        // What in a part moves during a replay (DoorPlayback's door parts, ThingPlayback's movers): the copy follows it.
        private static IEnumerable<Transform> Movers(GameObject original)
        {
            var found = new HashSet<Transform>();
            foreach (Door d in original.GetComponentsInChildren<Door>(true))
            {
                found.Add(d.transform);
            }
            foreach (BigDoor d in original.GetComponentsInChildren<BigDoor>(true))
            {
                found.Add(d.transform);
            }
            foreach (SubDoor d in original.GetComponentsInChildren<SubDoor>(true))
            {
                found.Add(d.transform);
            }
            foreach (Type type in MoverTypes)
            {
                foreach (Component m in original.GetComponentsInChildren(type, true))
                {
                    found.Add(m.transform);
                }
            }
            return found;
        }

        private static readonly Type[] MoverTypes = { typeof(MovingPlatform), typeof(Elevator), typeof(Piston), typeof(Rotator), typeof(Train.Tram) };

        // A renderer's copy: drawn as the original (its property block), only frustum-culled, and its static batch checked.
        private void Look(Copy c, Renderer o, Renderer r)
        {
            if (!Looks(r))
            {
                return;  // (stripped)
            }
            r.allowOcclusionWhenDynamic = false;
            if (o.HasPropertyBlock())
            {
                o.GetPropertyBlock(block);
                r.SetPropertyBlock(block);
            }
            if (!(o is MeshRenderer om) || !om.isPartOfStaticBatch || !(r is MeshRenderer rm))
            {
                return;
            }
            // Static batching put the original's mesh into a combined one (Unity's "Combined Mesh (root: scene)", or the
            // one ULTRAKILL's StaticSceneOptimizer bakes and points each renderer at), where it's already in place; a
            // renderer draws its submeshes of it. A copy keeps them (Instantiate copies the batch info), else it's set up
            // again, else drawn by hand. Its bounds are the original's: worked out from its own transform they'd be the
            // whole combined mesh's, moved (the optimizer sets the originals' the same way).
            if (BatchBounds(om, out Bounds bounds))
            {
                rm.bounds = bounds;
            }
            if (SameBatch(om, rm))
            {
                batchedKept++;
            }
            else if (SetBatch(om, rm))
            {
                batchedSet++;
            }
            else
            {
                rm.enabled = false;
                if (om.enabled)
                {
                    ByHand(c, om, rm);
                }
                batchedDrawn++;
            }
        }

        private static bool SameBatch(MeshRenderer o, MeshRenderer r) => r.isPartOfStaticBatch && r.subMeshStartIndex == o.subMeshStartIndex;

        private static bool SetBatch(MeshRenderer o, MeshRenderer r)
        {
            if (setStaticBatch == null)
            {
                return false;
            }
            try
            {
                MeshFilter filter = o.GetComponent<MeshFilter>();
                setStaticBatch.Invoke(r, new object[] { o.subMeshStartIndex, BatchCount(o, filter != null ? filter.sharedMesh : null) });
                staticBatchRoot?.SetValue(r, staticBatchRoot.GetValue(o));
                return SameBatch(o, r);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A batched renderer's place in the world: its own bounds (the optimizer's), else (switched off, it may say
        // nothing) its submeshes' in the combined mesh, which are in place already (or in its batch root's space).
        private static bool BatchBounds(MeshRenderer o, out Bounds bounds)
        {
            bounds = o.bounds;
            if (bounds.size.sqrMagnitude > 0f)
            {
                return true;
            }
            MeshFilter filter = o.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                return false;
            }
            int start = o.subMeshStartIndex, count = BatchCount(o, mesh);
            bool any = false;
            for (int k = 0; k < count && start + k < mesh.subMeshCount; k++)
            {
                Bounds b = mesh.GetSubMesh(start + k).bounds;
                if (any)
                {
                    bounds.Encapsulate(b);
                }
                else
                {
                    bounds = b;
                    any = true;
                }
            }
            Transform root = null;
            try
            {
                root = staticBatchRoot?.GetValue(o) as Transform;
            }
            catch (Exception)
            {
                // (the scene's own batch)
            }
            if (any && root != null)
            {
                Matrix4x4 m = root.localToWorldMatrix;
                Vector3 c = bounds.center, e = bounds.extents;
                var world = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
                for (int corner = 0; corner < 8; corner++)
                {
                    world.Encapsulate(m.MultiplyPoint3x4(c + new Vector3((corner & 1) != 0 ? e.x : -e.x, (corner & 2) != 0 ? e.y : -e.y, (corner & 4) != 0 ? e.z : -e.z)));
                }
                bounds = world;
            }
            return any && bounds.size.sqrMagnitude > 0f;
        }

        private static int BatchCount(MeshRenderer o, Mesh combined)
        {
            int materials = Math.Max(1, o.sharedMaterials.Length);
            return combined != null ? Mathf.Clamp(materials, 1, Math.Max(1, combined.subMeshCount - o.subMeshStartIndex)) : materials;
        }

        private void ByHand(Copy c, MeshRenderer o, MeshRenderer r)
        {
            MeshFilter filter = o.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                return;
            }
            Transform root = null;
            try
            {
                root = staticBatchRoot?.GetValue(o) as Transform;
            }
            catch (Exception)
            {
                // (the scene's own batch: in place already)
            }
            MaterialPropertyBlock props = null;
            if (o.HasPropertyBlock())
            {
                props = new MaterialPropertyBlock();
                o.GetPropertyBlock(props);
            }
            Material[] materials = o.sharedMaterials;
            int start = o.subMeshStartIndex, count = BatchCount(o, mesh);
            for (int k = 0; k < count && start + k < mesh.subMeshCount; k++)
            {
                Material m = materials.Length > 0 ? materials[Math.Min(k, materials.Length - 1)] : null;
                if (m == null)
                {
                    continue;
                }
                c.Drawn.Add(new Drawn
                {
                    Mesh = mesh,
                    Submesh = start + k,
                    Material = m,
                    Matrix = root != null ? root.localToWorldMatrix : Matrix4x4.identity,
                    Layer = o.gameObject.layer,
                    Owner = r,
                    Block = props,
                    Shadows = o.shadowCastingMode != ShadowCastingMode.Off,
                    Receive = o.receiveShadows,
                });
            }
        }

        // What a copy keeps: what it looks like.
        private static bool Looks(Component c) =>
            c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer || c is SpriteRenderer || c is LineRenderer || c is LODGroup || c is Light;

        private static void Keep(HashSet<Transform> keep, Transform t, Transform root)
        {
            for (; t != null && keep.Add(t); t = t.parent)
            {
                if (t == root)
                {
                    break;
                }
            }
        }

        // Everything in a copy but what it looks like. Whole parts first: its enemies (the replay shows the attempt's own)
        // and UI, then whatever has nothing to see in it; then, on what's left, every other component: scripts first,
        // what other components need last.
        private static void Strip(GameObject copy, HashSet<Transform> keep)
        {
            Transform root = copy.transform;
            foreach (EnemyIdentifier e in copy.GetComponentsInChildren<EnemyIdentifier>(true))
            {
                if (e == null)
                {
                    continue;
                }
                if (e.transform == root)
                {
                    throw new InvalidOperationException("it's an enemy");
                }
                Object.DestroyImmediate(e.gameObject);
            }
            foreach (Canvas canvas in copy.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas == null)
                {
                    continue;
                }
                if (canvas.transform == root)
                {
                    throw new InvalidOperationException("it's UI");
                }
                Object.DestroyImmediate(canvas.gameObject);
            }
            keep.Add(root);
            foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (!Looks(r))
                {
                    continue;
                }
                Keep(keep, r.transform, root);
                if (r is SkinnedMeshRenderer skinned)
                {
                    Keep(keep, skinned.rootBone, root);
                    foreach (Transform bone in skinned.bones)
                    {
                        Keep(keep, bone, root);
                    }
                }
            }
            foreach (Light light in copy.GetComponentsInChildren<Light>(true))
            {
                Keep(keep, light.transform, root);
            }
            foreach (LODGroup lod in copy.GetComponentsInChildren<LODGroup>(true))
            {
                Keep(keep, lod.transform, root);
            }
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && !keep.Contains(t))
                {
                    Object.DestroyImmediate(t.gameObject);
                }
            }
            var components = new List<Component>();
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    t.GetComponents(components);
                    components.RemoveAll(c => c == null || c is Transform || Looks(c));
                    if (components.Count == 0)
                    {
                        break;
                    }
                    components.Sort((a, b) => Order(a).CompareTo(Order(b)));
                    foreach (Component c in components)
                    {
                        if (c != null)
                        {
                            Object.DestroyImmediate(c);
                        }
                    }
                }
            }
        }

        // Which goes first (a component can't go while another needs it): scripts that need another, the other scripts,
        // the rest that need another (joints, audio filters, particle systems), the rest, then what others need.
        private static int Order(Component c)
        {
            Type type = c.GetType();
            if (orderOf.TryGetValue(type, out int known))
            {
                return known;
            }
            bool needs = type.IsDefined(typeof(RequireComponent), true) || type.Name == "ParticleSystem";
            int order = c is MonoBehaviour ? (needs ? 0 : 1)
                : c is Rigidbody || c is AudioSource || type.Name == "ParticleSystemRenderer" ? 4
                : needs ? 2 : 3;
            orderOf[type] = order;
            return order;
        }

        private static readonly Dictionary<Type, int> orderOf = new Dictionary<Type, int>();

        private static float Ms(float since) => (Time.realtimeSinceStartup - since) * 1000f;
    }
}
