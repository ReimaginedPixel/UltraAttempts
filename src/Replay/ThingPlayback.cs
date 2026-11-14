using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // Everything else an attempt saw (WorldDetail = Everything). The followed one's: its projectiles, debris
    // and props in their own meshes and materials (found again among what the game has loaded), the level's
    // own movers and props moved for real (and put back), its explosions, beams and breakables breaking. The
    // other attempts' (OverlayWorlds): the same, see-through in each attempt's colour. Dispose puts back
    // everything moved or hidden.
    internal sealed class ThingPlayback
    {
        private const int MaxRealDraws = 3000;
        private const int LookupsPerFrame = 12;
        private const float ExplosionFade = 0.5f;
        private const float BreakLife = 0.6f;
        private const float WhiteFlash = 0.1f;

        private readonly Camera cam;
        private readonly int layer;
        private readonly Material glow;      // see-through, unlit (overlays, explosions, beams)
        private readonly Material solid;     // a part whose material can't be found, a box for a thing whose mesh can't
        private readonly Mesh sphere = Meshes.Sphere();
        private readonly Mesh cube = Meshes.Cube();
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");
        private readonly LookLibrary looks = new LookLibrary();

        private readonly Dictionary<string, Spot> spots = new Dictionary<string, Spot>();
        private readonly Dictionary<Transform, Spot> spotOf = new Dictionary<Transform, Spot>();
        private readonly List<Spot> touched = new List<Spot>();
        private GhostWorld movedFor;
        private int frame;
        private int lookupFrame = -1, lookups;
        private int realDraws, realCapped, overlayCapped;
        private int levelMoved, levelDrawn;
        private bool loggedReal, loggedOverlay;

        // A level object found again by its path: where it was before the replay moved (or hid) it.
        private sealed class Spot
        {
            public Transform T;
            public Vector3 HomePos;
            public Quaternion HomeRot;
            public bool Moved;
            public Renderer[] Renderers;
            public bool[] WasOn;
            public bool Hidden;
            public bool Touched;        // the replay has its home
            public int Placed;          // the frame a track last put it somewhere
        }

        public ThingPlayback(Camera cam)
        {
            this.cam = cam;
            layer = (cam.cullingMask & 1) != 0 ? 0 : FirstLayer(cam.cullingMask);
            glow = MakeMaterial(zWrite: false);
            solid = MakeMaterial(zWrite: true);
        }

        // Once the attempts are loaded: the game's meshes and materials, by name (once, while the replay loads).
        public void Prepare(IList<Ghost> ghosts)
        {
            bool any = false;
            foreach (Ghost g in ghosts)
            {
                any |= g.HasEverything;
            }
            if (any)
            {
                looks.Index();
            }
        }

        public void Dispose()
        {
            try
            {
                PutBack();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting the level's objects back after a replay: " + e);
            }
            if (looks.Asked > 0)
            {
                Plugin.Log.LogInfo($"replay things: {looks.Summary}; {levelMoved} level objects moved for real, {levelDrawn} drawn instead (not found)"
                    + (realCapped > 0 ? $"; the followed attempt's things were cut at {MaxRealDraws} draws on {realCapped} frames" : "")
                    + (overlayCapped > 0 ? $"; the other attempts' worlds were cut short on {overlayCapped} frames" : ""));
            }
            Object.Destroy(glow);
            Object.Destroy(solid);
            Object.Destroy(sphere);
            Object.Destroy(cube);
        }

        // ---------------------------------------------------------------- the followed attempt

        // The things g saw, t seconds in, as they were (null: nobody's).
        public void Update(Ghost g, GhostWorld w, float t)
        {
            realDraws = 0;
            if (w != movedFor)
            {
                PutBack();
                movedFor = w;
            }
            if (g == null || w == null || !Settings.ShowWorld.Value)
            {
                if (movedFor != null)
                {
                    PutBack();
                    movedFor = null;
                }
                return;
            }
            float end = g.Duration;
            float at = Mathf.Min(t, end);
            bool moved = false;
            frame++;
            ThingPath[] things = w.Things;
            for (int i = 0; i < things.Length; i++)
            {
                ThingPath p = things[i];
                if (!p.Level && p.From > at)
                {
                    continue;
                }
                bool there = p.At(at, end, out Vector3 pos, out Quaternion rot, out float scale);
                Spot spot = null;
                if (p.Level && !Find(w.Paths[p.Path], out spot))
                {
                    continue;  // (not looked for yet: next frame)
                }
                if (spot != null)
                {
                    // (one object tracked twice, moving again after a rest: an earlier track that has it now wins
                    // over a later one's waiting place; they're in order of when they start)
                    if (!(at < p.From && spot.Placed == frame))
                    {
                        moved |= Place(spot, there, pos, rot);
                    }
                    continue;
                }
                if (there && realDraws < MaxRealDraws)
                {
                    realDraws += DrawLook(w, p.Look, Matrix4x4.TRS(pos, rot, p.BaseScale * scale), true, default, MaxRealDraws - realDraws);
                }
            }
            Breaks(w, t, at, null);
            Moments(w, t, null);
            if (moved)
            {
                Physics.SyncTransforms();  // (so the camera and the tracers meet them where they're shown)
            }
            if (realDraws >= MaxRealDraws)
            {
                realCapped++;
                if (!loggedReal)
                {
                    loggedReal = true;
                    Plugin.Log.LogInfo($"the followed attempt has more things than {MaxRealDraws} draws a frame: the rest are left out while it does");
                }
            }
        }

        // A level object where the attempt had it (moved: true if it moved this frame); hidden when it was gone.
        private bool Place(Spot spot, bool there, Vector3 pos, Quaternion rot)
        {
            if (spot.T == null)
            {
                return false;
            }
            Touch(spot);
            Hide(spot, !there);
            if (!there)
            {
                return false;
            }
            spot.Placed = frame;
            spot.T.GetPositionAndRotation(out Vector3 now, out Quaternion turned);
            if (now == pos && turned == rot)
            {
                return false;
            }
            if (!spot.Moved)
            {
                spot.Moved = true;
                levelMoved++;
            }
            spot.T.SetPositionAndRotation(pos, rot);
            return true;
        }

        // Breakables: whole until the attempt broke them (the real one, else drawn from its look), a burst of
        // pieces then, gone after. tint: an overlay's (only the burst shows).
        private void Breaks(GhostWorld w, float t, float at, Color? tint)
        {
            foreach (WorldMoment b in w.Breaks)
            {
                if (tint == null)
                {
                    Spot spot = null;
                    if (b.Path >= 0 && !Find(w.Paths[b.Path], out spot))
                    {
                        continue;
                    }
                    if (spot != null && spot.T != null)
                    {
                        Touch(spot);
                        Hide(spot, at >= b.Time);
                    }
                    else if (at < b.Time && b.Look >= 0 && realDraws < MaxRealDraws)
                    {
                        realDraws += DrawLook(w, b.Look, Matrix4x4.TRS(b.From, b.Rot, Vector3.one), true, default, MaxRealDraws - realDraws);
                    }
                }
                float age = t - b.Time;
                if (age >= 0f && age < BreakLife)
                {
                    Color c = tint ?? new Color(b.Color.r, b.Color.g, b.Color.b, 1f);
                    Burst(b.From, Mathf.Clamp(b.Size, 0.5f, 12f), age / BreakLife, c, b.Time);
                }
            }
        }

        // Explosions (white for a moment, then their colour, growing to their size and fading) and beams
        // (fading thinner), from a little before t up to t.
        private void Moments(GhostWorld w, float t, Color? tint)
        {
            WorldMoment[] moments = w.Moments;
            for (int i = w.FirstMomentFrom(t - GhostWorld.LongestMoment); i < moments.Length && moments[i].Time <= t; i++)
            {
                WorldMoment m = moments[i];
                float age = t - m.Time;
                if (m.Kind == WorldEventKind.Explosion)
                {
                    if (age > m.Life + ExplosionFade)
                    {
                        continue;
                    }
                    float k = Mathf.Clamp01(age / m.Life);
                    float radius = Mathf.Max(0.2f, m.Size * (1f - (1f - k) * (1f - k)));
                    float fade = age <= m.Life ? 1f : 1f - (age - m.Life) / ExplosionFade;
                    Color c = tint ?? (age < WhiteFlash ? Color.white : new Color(m.Color.r, m.Color.g, m.Color.b, 1f));
                    c.a = (tint.HasValue ? tint.Value.a * 1.5f : 0.75f) * fade;
                    Draw(sphere, Matrix4x4.TRS(m.From, Quaternion.identity, Vector3.one * (radius * 2f)), c, glow);
                }
                else if (m.Kind == WorldEventKind.Beam && age <= m.Life)
                {
                    Vector3 d = m.To - m.From;
                    float length = d.magnitude;
                    if (length < 0.01f)
                    {
                        continue;
                    }
                    float k = age / m.Life;
                    float width = Mathf.Max(0.03f, m.Size * (1f - k));
                    Color c = tint ?? Color.Lerp(new Color(m.Color.r, m.Color.g, m.Color.b, 1f), Color.white, 0.25f);
                    c.a = (tint.HasValue ? Mathf.Min(1f, tint.Value.a * 2.5f) : 0.9f) * (1f - k);
                    Draw(cube, Matrix4x4.TRS(m.From + d * 0.5f, Quaternion.LookRotation(d / length), new Vector3(width, width, length)), c, glow);
                }
            }
        }

        // ---------------------------------------------------------------- the other attempts

        // Another attempt's things, explosions and beams, see-through in its colour. budget: draws left this
        // frame (shared with the overlay's enemies); returns false if it ran out.
        public bool DrawOverlay(Ghost g, GhostWorld w, Color tint, float t, ref int budget)
        {
            if (w == null || !Settings.ShowWorld.Value)
            {
                return true;
            }
            float end = g.Duration;
            float at = Mathf.Min(t, end);
            ThingPath[] things = w.Things;
            for (int i = 0; i < things.Length; i++)
            {
                if (budget <= 0)
                {
                    return false;
                }
                ThingPath p = things[i];
                if (p.From > at || !p.At(at, end, out Vector3 pos, out Quaternion rot, out float scale))
                {
                    continue;
                }
                budget -= DrawLook(w, p.Look, Matrix4x4.TRS(pos, rot, p.BaseScale * scale), false, tint, budget);
            }
            Breaks(w, t, at, tint);
            Moments(w, t, tint);
            return budget > 0;
        }

        public void OverlayCut()
        {
            overlayCapped++;
            if (!loggedOverlay)
            {
                loggedOverlay = true;
                Plugin.Log.LogInfo("the other attempts' worlds are more than a frame's worth of draws: the nearest attempts' come first, the rest are left out while it lasts");
            }
        }

        // ---------------------------------------------------------------- drawing

        // A thing's look at m: its parts (real: in their own materials; else see-through in tint), or the box of
        // its bounds if none of its meshes are loaded. Returns the draws it took.
        private int DrawLook(GhostWorld w, int look, Matrix4x4 m, bool real, Color tint, int budget)
        {
            ResolvedLook r = look >= 0 ? looks.Get(w, look) : null;
            if (r == null || r.Meshes.Length == 0)
            {
                Matrix4x4 box = r != null ? m * r.Box : m * Matrix4x4.Scale(Vector3.one * 0.3f);
                Color c = real ? (r != null ? r.Color : Color.gray) : tint;
                if (real)
                {
                    c.a = 1f;
                }
                Draw(cube, box, c, real ? solid : glow);
                return 1;
            }
            int used = 0;
            for (int i = 0; i < r.Meshes.Length; i++)
            {
                Mesh mesh = r.Meshes[i];
                if (mesh == null)
                {
                    continue;
                }
                Matrix4x4 at = m * r.Local[i];
                Material[] materials = r.Materials[i];
                int subs = Math.Max(1, mesh.subMeshCount);
                for (int s = 0; s < subs && used < budget; s++, used++)
                {
                    if (!real)
                    {
                        block.SetColor(colorId, tint);
                        Graphics.DrawMesh(mesh, at, glow, layer, cam, s, block, false, false, false);
                        continue;
                    }
                    Material mat = materials.Length > 0 ? materials[Math.Min(s, materials.Length - 1)] : null;
                    if (mat != null)
                    {
                        Graphics.DrawMesh(mesh, at, mat, layer, cam, s, null, false, false, false);
                    }
                    else
                    {
                        block.SetColor(colorId, new Color(r.Color.r, r.Color.g, r.Color.b, 1f));
                        Graphics.DrawMesh(mesh, at, solid, layer, cam, s, block, false, false, false);
                    }
                }
            }
            return Math.Max(1, used);
        }

        // Pieces flying out of where something broke, falling and shrinking.
        private void Burst(Vector3 at, float size, float k, Color c, float seedTime)
        {
            uint seed = Seed(Mathf.RoundToInt(at.x * 10f) ^ Mathf.RoundToInt(at.z * 10f), Mathf.RoundToInt(seedTime * 60f));
            float ease = 1f - (1f - k) * (1f - k);
            for (int i = 0; i < 8; i++)
            {
                var dir = new Vector3(Next(ref seed) * 2f - 1f, Next(ref seed) * 1.4f - 0.2f, Next(ref seed) * 2f - 1f);
                dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up;
                Vector3 pos = at + dir * (ease * size * 0.8f) + Vector3.down * (k * k * size * 0.8f);
                Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(k * 500f, k * 350f, 0f);
                Draw(cube, Matrix4x4.TRS(pos, spin, Vector3.one * (size * 0.12f * (1f - k))), new Color(c.r, c.g, c.b, c.a * (1f - k * k)), glow);
            }
        }

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color, Material material)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, matrix, material, layer, cam, 0, block, false, false, false);
        }

        // ---------------------------------------------------------------- level objects

        // A level object by its path (spot: null if it isn't in the level now). False if it hasn't been looked
        // for yet and this frame's lookups are used up (each walks the scene: a few a frame, so a cut to an
        // attempt that moved hundreds of things doesn't stall).
        private bool Find(string path, out Spot spot)
        {
            spot = null;
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }
            if (spots.TryGetValue(path, out spot))
            {
                return true;
            }
            if (lookupFrame != Time.frameCount)
            {
                lookupFrame = Time.frameCount;
                lookups = 0;
            }
            if (lookups++ >= LookupsPerFrame)
            {
                return false;
            }
            Transform t = null;
            try
            {
                t = Game.FindByPath(path, exact: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"finding {path}: {e.Message}");
            }
            spot = null;
            if (t != null && !spotOf.TryGetValue(t, out spot))
            {
                spot = new Spot { T = t };
                spotOf[t] = spot;
            }
            levelDrawn += spot == null ? 1 : 0;
            spots[path] = spot;
            return true;
        }

        // (the first time the replay touches it: where it is, and which of its renderers are on)
        private void Touch(Spot spot)
        {
            if (spot.Touched)
            {
                return;
            }
            spot.Touched = true;
            spot.T.GetLocalPositionAndRotation(out spot.HomePos, out spot.HomeRot);
            spot.Renderers = spot.T.GetComponentsInChildren<Renderer>(true);
            spot.WasOn = new bool[spot.Renderers.Length];
            for (int i = 0; i < spot.Renderers.Length; i++)
            {
                spot.WasOn[i] = spot.Renderers[i] != null && spot.Renderers[i].enabled;
            }
            touched.Add(spot);
        }

        private static void Hide(Spot spot, bool hide)
        {
            if (spot.Hidden == hide || spot.Renderers == null)
            {
                return;
            }
            spot.Hidden = hide;
            for (int i = 0; i < spot.Renderers.Length; i++)
            {
                if (spot.Renderers[i] != null && spot.WasOn[i])
                {
                    spot.Renderers[i].enabled = !hide;
                }
            }
        }

        private void PutBack()
        {
            bool any = false;
            foreach (Spot spot in touched)
            {
                spot.Touched = false;
                if (spot.T == null)
                {
                    continue;
                }
                Hide(spot, false);
                if (spot.Moved)
                {
                    spot.T.SetLocalPositionAndRotation(spot.HomePos, spot.HomeRot);
                    spot.Moved = false;
                    any = true;
                }
            }
            touched.Clear();
            if (any)
            {
                Physics.SyncTransforms();
            }
        }

        // ---------------------------------------------------------------- bits

        private static Material MakeMaterial(bool zWrite)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)(zWrite ? CullMode.Back : CullMode.Off));
            mat.SetInt("_ZWrite", zWrite ? 1 : 0);
            mat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            mat.renderQueue = zWrite ? 2450 : 3000;
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
    }

    // A recorded look, found again: each part's mesh (by name and vertex count) and materials (by name)
    // among what the game has loaded; the box of its bounds and its colour for when no mesh is found.
    internal sealed class ResolvedLook
    {
        public Mesh[] Meshes = Array.Empty<Mesh>();
        public Material[][] Materials = Array.Empty<Material[]>();
        public Matrix4x4[] Local = Array.Empty<Matrix4x4>();
        public Matrix4x4 Box = Matrix4x4.Scale(Vector3.one * 0.3f);
        public Color Color = Color.gray;
    }

    internal sealed class LookLibrary
    {
        private Dictionary<string, List<Mesh>> meshes;
        private Dictionary<string, Material> materials;
        private readonly Dictionary<string, ResolvedLook> resolved = new Dictionary<string, ResolvedLook>();
        private int parts, found, materialsAsked, materialsFound;

        public int Asked => resolved.Count;

        public string Summary => $"{resolved.Count} looks, {found} of {parts} meshes and {materialsFound} of {materialsAsked} materials found among {meshes?.Count ?? 0} mesh names and {materials?.Count ?? 0} materials loaded";

        // Every mesh and material the game has loaded, by name (once).
        public void Index()
        {
            if (meshes != null)
            {
                return;
            }
            float started = Time.realtimeSinceStartup;
            meshes = new Dictionary<string, List<Mesh>>();
            materials = new Dictionary<string, Material>();
            try
            {
                foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
                {
                    if (m == null || m.name.StartsWith("UltraAttempts", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!meshes.TryGetValue(m.name, out List<Mesh> list))
                    {
                        meshes[m.name] = list = new List<Mesh>(1);
                    }
                    list.Add(m);
                }
                foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null)
                    {
                        continue;
                    }
                    string name = ThingRecorder.CleanMaterial(m.name);
                    // (the asset itself over a copy made at runtime)
                    if (!materials.TryGetValue(name, out Material known) || (known.name != name && m.name == name))
                    {
                        materials[name] = m;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("listing the game's meshes and materials (the replay's things are boxes): " + e.Message);
            }
            Plugin.Log.LogInfo($"replay things: {meshes.Count} mesh names and {materials.Count} materials loaded to draw them with, {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        public ResolvedLook Get(GhostWorld w, int look)
        {
            if (look < 0 || look >= w.Looks.Length)
            {
                return null;
            }
            if (w.Resolved == null || w.Resolved.Length != w.Looks.Length)
            {
                w.Resolved = new ResolvedLook[w.Looks.Length];
            }
            ResolvedLook r = w.Resolved[look];
            if (r != null)
            {
                return r;
            }
            // (the same look in another attempt is the same meshes: found once)
            string key = w.LookKeys[look] ?? "";
            if (!resolved.TryGetValue(key, out r))
            {
                r = Resolve(w.Looks[look]);
                resolved[key] = r;
            }
            w.Resolved[look] = r;
            return r;
        }

        private ResolvedLook Resolve(ThingLook look)
        {
            Index();
            var r = new ResolvedLook();
            if (look == null)
            {
                return r;
            }
            uint c = look.Color;
            r.Color = new Color((c >> 24) / 255f, ((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, 1f);
            var center = new Vector3(look.CenterX, look.CenterY, look.CenterZ);
            var size = new Vector3(look.SizeX, look.SizeY, look.SizeZ);
            if (Finite(center) && Finite(size) && size.sqrMagnitude > 1e-6f && size.sqrMagnitude < 1e6f)
            {
                r.Box = Matrix4x4.TRS(center, Quaternion.identity, Vector3.Max(size, Vector3.one * 0.05f));
            }
            var foundMeshes = new List<Mesh>();
            var foundMaterials = new List<Material[]>();
            var local = new List<Matrix4x4>();
            foreach (LookPart p in look.Parts)
            {
                parts++;
                var position = new Vector3(p.PX, p.PY, p.PZ);
                var rotation = new Quaternion(p.RX, p.RY, p.RZ, p.RW);
                var scale = new Vector3(p.SX, p.SY, p.SZ);
                float q = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
                if (!Finite(position) || !Finite(scale) || float.IsNaN(q) || q < 1e-6f || float.IsInfinity(q))
                {
                    continue;
                }
                Mesh mesh = MeshNamed(p.Mesh, p.Vertices);
                if (mesh == null)
                {
                    continue;
                }
                found++;
                float n = Mathf.Sqrt(q);
                var mats = new Material[p.Materials?.Length ?? 0];
                for (int i = 0; i < mats.Length; i++)
                {
                    materialsAsked++;
                    if (materials.TryGetValue(p.Materials[i] ?? "", out Material m) && m != null)
                    {
                        mats[i] = m;
                        materialsFound++;
                    }
                }
                foundMeshes.Add(mesh);
                foundMaterials.Add(mats);
                local.Add(Matrix4x4.TRS(position, new Quaternion(rotation.x / n, rotation.y / n, rotation.z / n, rotation.w / n), scale));
            }
            r.Meshes = foundMeshes.ToArray();
            r.Materials = foundMaterials.ToArray();
            r.Local = local.ToArray();
            return r;
        }

        private Mesh MeshNamed(string name, int vertices)
        {
            if (string.IsNullOrEmpty(name) || !meshes.TryGetValue(name, out List<Mesh> list))
            {
                return null;
            }
            Mesh any = null;
            foreach (Mesh m in list)
            {
                if (m == null)
                {
                    continue;
                }
                if (m.vertexCount == vertices)
                {
                    return m;
                }
                any = any ?? m;
            }
            return any;
        }

        private static bool Finite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
