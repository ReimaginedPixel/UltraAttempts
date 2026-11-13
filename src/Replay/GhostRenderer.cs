using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // Draws the replay: every ghost, the burst where each one dies, red markers left behind, tracers for
    // shots, and trails, straight to ULTRAKILL's main camera (so it all picks up the game's pixelation and
    // palette). The ghosts are ULTRAKILL's own V1 (or V2): a few animated copies (the one followed and the
    // winner as the real thing, the nearest see-through), and the rest of the crowd drawn from meshes
    // baked once in a handful of poses, so a thousand of them stay cheap. If no model loads (or by
    // choice) they're crystals made in code.
    internal sealed class GhostRenderer
    {
        public static readonly Color Gold = new Color(1f, 0.8f, 0.2f);
        public static readonly Color Visor = new Color(1f, 0.85f, 0.3f);
        public static readonly Color MarkerRed = new Color(1f, 0.15f, 0.12f);

        public static Color VariantColor(int variant)
        {
            switch (variant)
            {
                case 0: return new Color(0.3f, 0.65f, 1f);
                case 1: return new Color(0.35f, 1f, 0.4f);
                case 2: return new Color(1f, 0.3f, 0.25f);
                case 3: return new Color(1f, 0.82f, 0.2f);
                default: return Color.white;
            }
        }

        // A model stands this much of the recorded capsule's height (V1 is a little shorter than its collider).
        private const float ModelHeight = 0.94f;
        // The model height weapons and outlines are sized for (the standard 3.5 capsule).
        private const float ReferenceHeight = 3.5f * ModelHeight;
        // Sliding shrinks V1's collider to 1.25 tall, its middle dropping with it: the bottom stays on the floor.
        private const float SlideHalfHeight = 0.625f;
        private const float OutlineWidth = 0.04f;
        private const int NewModelsPerFrame = 3;

        private readonly Camera cam;
        private readonly int layer;
        private readonly Material ghost;
        private readonly Material xray;
        private readonly Material markerMat;
        private readonly Material lineMat;
        private readonly Material outlineMat;
        private readonly Material hiddenMat;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");

        private readonly Mesh body = Meshes.Crystal();
        private readonly Mesh cube = Meshes.Cube();
        private readonly Mesh sphere = Meshes.Sphere();
        private Mesh markers;
        private int markersShown = -1;

        private readonly List<Tracer> tracers = new List<Tracer>();
        private readonly LineRenderer bestTrail;
        private readonly LineRenderer followTrail;
        private readonly Vector3[] trailPoints = new Vector3[512];

        private readonly WeaponModels guns;
        private readonly GhostModelKit kit;
        private readonly Transform modelRoot;
        private readonly GhostFrameHook hook;
        private bool broken;

        // This frame's ghosts (EndFrame decides who gets an animated model), and what the crowd remembers.
        private Entry[] entries = new Entry[64];
        private int entryCount;
        private float[] keys = new float[64];
        private int[] order = new int[64];
        private Track[] tracks = new Track[64];
        private int frame = 1;
        private float frameT;
        private bool haveFrame;
        private bool seeked;
        private float replayDt;
        private float animSpeed = 1f;
        private Vector3 camPos;
        private int drawnFrame = -1;

        private readonly List<LivePuppet> puppets = new List<LivePuppet>();
        private readonly Stack<LivePuppet> spare = new Stack<LivePuppet>();
        private readonly Dictionary<Ghost, LivePuppet> live = new Dictionary<Ghost, LivePuppet>();
        private readonly HashSet<Ghost> chosen = new HashSet<Ghost>();
        private readonly List<Ghost> releasing = new List<Ghost>();
        private readonly List<Vector3> inflateVerts = new List<Vector3>();
        private readonly List<Vector3> inflateNormals = new List<Vector3>();

        private struct Tracer
        {
            public Vector3 From;
            public Vector3 To;
            public Color Color;
            public float Born;
            public float Life;
            public float Width;
        }

        private struct Entry
        {
            public Ghost G;
            public Ghost.Pose P;
            public GhostMotion M;
            public float Alpha;
            public bool Featured;
            public bool Weapons;
            public bool Hurt;
            public Vector3 Bottom;
            public Vector3 Facing;
            public Quaternion Rot;
            public float Height;
            public float Phase;
            public float Distance;
        }

        // Kept per ghost (by Ghost.Order) from one frame to the next.
        private struct Track
        {
            public int Seen;
            public Vector3 Facing;
            public Vector3 LastBody;
            public float Phase;
            public float AirTime;
        }

        public GhostRenderer(Camera cam, Transform root)
        {
            this.cam = cam;
            layer = (cam.cullingMask & 1) != 0 ? 0 : FirstLayer(cam.cullingMask);
            ghost = MakeMaterial(CullMode.Back, CompareFunction.LessEqual);
            xray = MakeMaterial(CullMode.Back, CompareFunction.Always);
            markerMat = MakeMaterial(CullMode.Off, CompareFunction.LessEqual);
            lineMat = MakeMaterial(CullMode.Off, CompareFunction.LessEqual);
            outlineMat = MakeMaterial(CullMode.Front, CompareFunction.LessEqual);
            hiddenMat = MakeMaterial(CullMode.Back, CompareFunction.Greater);
            bestTrail = MakeTrail(root, "best trail");
            followTrail = MakeTrail(root, "follow trail");
            guns = new WeaponModels(root, layer);

            var models = new GameObject("UltraAttempts ghost models");
            models.transform.SetParent(root, false);
            modelRoot = models.transform;
            try
            {
                kit = GhostModelKit.Load(Settings.Model.Value, modelRoot, layer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("loading the ghost model: " + e);
                kit = null;
            }
            if (kit != null)
            {
                hook = models.AddComponent<GhostFrameHook>();
                hook.Late = LateFrame;
            }
        }

        public void Dispose()
        {
            if (hook != null)
            {
                hook.Late = null;
            }
            foreach (LivePuppet p in puppets)
            {
                p.Dispose();
            }
            puppets.Clear();
            spare.Clear();
            live.Clear();
            kit?.Dispose();
            guns.Dispose();
            if (modelRoot != null)
            {
                Object.Destroy(modelRoot.gameObject);
            }
            Object.Destroy(ghost);
            Object.Destroy(xray);
            Object.Destroy(markerMat);
            Object.Destroy(lineMat);
            Object.Destroy(outlineMat);
            Object.Destroy(hiddenMat);
            Object.Destroy(body);
            Object.Destroy(cube);
            Object.Destroy(sphere);
            if (markers != null)
            {
                Object.Destroy(markers);
            }
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

        // Unlit, see-through, vertex colours times _Color. Internal-Colored is in every Unity build.
        private static Material MakeMaterial(CullMode cull, CompareFunction zTest)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)cull);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)zTest);
            mat.renderQueue = 3000;
            return mat;
        }

        private LineRenderer MakeTrail(Transform root, string name)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(root, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMat;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.12f;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = 0;
            return line;
        }

        private bool Models => kit != null && !broken;

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color, Material material = null, int submesh = 0)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, matrix, material ?? ghost, layer, cam, submesh, block, false, false, false);
        }

        private void DrawAll(Mesh mesh, Matrix4x4 matrix, Color color, Material material)
        {
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Draw(mesh, matrix, color, material, s);
            }
        }

        // ---------------------------------------------------------------- every frame

        // Once a frame, around that frame's DrawGhost calls (t: the replay's time). Model ghosts hand out
        // and take back their animated copies here.
        public void BeginFrame(float t)
        {
            frame++;
            float dt = haveFrame ? t - frameT : 0f;
            seeked = !haveFrame || dt < -1e-4f || dt > 0.9f;  // (a seek, a restart: 8x speed moves at most 0.8 s a frame)
            replayDt = seeked ? 0f : dt;
            // The animators run on real time; this keeps them at the replay's pace (stopped while paused).
            animSpeed = seeked ? 1f : Mathf.Clamp(dt / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0f, 8f);
            frameT = t;
            haveFrame = true;
            entryCount = 0;
            camPos = cam != null ? cam.transform.position : Vector3.zero;
        }

        // One ghost. featured: the one the camera follows (or the winner): the real model, outlined, seen
        // through walls. pov: the camera is inside its head (only its weapon shows, as in first person).
        public void DrawGhost(Ghost g, in Ghost.Pose p, float alpha, bool featured, bool pov, bool weapons)
        {
            if (!Models)
            {
                DrawCrystal(g, p, alpha, featured, pov, weapons);
                return;
            }
            if (pov)
            {
                if (weapons)
                {
                    DrawViewmodel(g, p);
                }
                return;
            }
            try
            {
                Collect(g, p, alpha, featured, weapons);
            }
            catch (Exception e)
            {
                Fail(e);
                DrawCrystal(g, p, alpha, featured, false, weapons);
            }
        }

        public void EndFrame()
        {
            if (!Models)
            {
                return;
            }
            try
            {
                Assign();
                for (int i = 0; i < entryCount; i++)
                {
                    if (live.TryGetValue(entries[i].G, out LivePuppet pup))
                    {
                        Place(pup, ref entries[i]);
                    }
                    else
                    {
                        DrawCrowd(ref entries[i]);
                    }
                }
                drawnFrame = Time.frameCount;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Models went wrong mid-replay: crystals from here on (never an exception into the frame loop).
        private void Fail(Exception e)
        {
            if (!broken)
            {
                broken = true;
                Plugin.Log.LogError("drawing the ghost models failed, crystals for the rest of this replay: " + e);
            }
            foreach (LivePuppet p in puppets)
            {
                p.Release();
            }
            live.Clear();
        }

        private void Collect(Ghost g, in Ghost.Pose p, float alpha, bool featured, bool weapons)
        {
            float t = Mathf.Min(frameT, g.Duration);
            GhostMotion m = GhostMotion.Of(g, p, t);
            ref Track track = ref TrackOf(g);
            bool fresh = seeked || track.Seen != frame - 1;
            float step = Mathf.Abs(replayDt);
            // (the ground check flickers over bumps: a moment off the ground isn't a fall)
            track.AirTime = m.Grounded || m.Sliding ? 0f : fresh ? 1f : track.AirTime + step;
            m.Airborne = !m.Grounded && !m.Sliding && (track.AirTime > 0.12f || m.Rise > 3f);

            float height = g.CapsuleHeight * ModelHeight;
            Vector3 look = Flat(p.Look * Vector3.forward, p.Up);
            if (look == Vector3.zero)
            {
                look = Flat(p.Look * Vector3.up, p.Up);
            }
            if (look == Vector3.zero)
            {
                look = Flat(Vector3.forward, p.Up) != Vector3.zero ? Flat(Vector3.forward, p.Up) : Vector3.right;
            }
            // V1 turns to where it runs (its animations only run forward); V2's animated legs run any way,
            // so it faces where it aims, as V2 does.
            bool moving = m.Heading != Vector3.zero && (m.Running || m.Sliding || m.Dashing || (m.Airborne && m.Speed > 6f));
            bool aims = !kit.Driver.FacesHeading && live.ContainsKey(g);
            Vector3 want = moving && !aims ? m.Heading : look;
            Vector3 facing = Flat(track.Facing, p.Up);
            if (fresh || facing == Vector3.zero)
            {
                facing = want;
            }
            else
            {
                float turn = Mathf.Clamp(Vector3.SignedAngle(facing, want, p.Up), -720f * step, 720f * step);
                facing = Flat(Quaternion.AngleAxis(turn, p.Up) * facing, p.Up);
                if (facing == Vector3.zero)
                {
                    facing = want;
                }
            }
            track.Facing = facing;
            if (m.Running && !fresh)
            {
                Vector3 moved = p.Body - track.LastBody;
                float distance = (moved - p.Up * Vector3.Dot(moved, p.Up)).magnitude;
                if (distance < 20f)
                {
                    track.Phase = Mathf.Repeat(track.Phase + distance / (2f * height), 1f);  // (a run cycle: two strides, about twice its height)
                }
            }
            track.LastBody = p.Body;
            track.Seen = frame;

            if (entryCount == entries.Length)
            {
                Array.Resize(ref entries, entries.Length * 2);
            }
            entries[entryCount++] = new Entry
            {
                G = g,
                P = p,
                M = m,
                Alpha = alpha,
                Featured = featured,
                Weapons = weapons,
                Hurt = g.HurtAt(p.Sample),
                Bottom = Bottom(g, p, t),
                Facing = facing,
                Rot = Quaternion.LookRotation(facing, p.Up),
                Height = height,
                Phase = track.Phase,
                Distance = (p.Body - camPos).sqrMagnitude,
            };
        }

        private ref Track TrackOf(Ghost g)
        {
            int i = Mathf.Max(0, g.Order);
            if (i >= tracks.Length)
            {
                Array.Resize(ref tracks, Mathf.Max(i + 1, tracks.Length * 2));
            }
            return ref tracks[i];
        }

        private static Vector3 Flat(Vector3 v, Vector3 up)
        {
            v -= up * Vector3.Dot(v, up);
            float length = v.magnitude;
            return length > 1e-3f ? v / length : Vector3.zero;
        }

        // The bottom of its collider: where its feet go.
        private static Vector3 Bottom(Ghost g, in Ghost.Pose p, float t)
        {
            float f = Mathf.Clamp(t * g.Rate, 0f, g.Count - 1);
            int i = (int)f;
            int j = Mathf.Min(i + 1, g.Count - 1);
            float half = Mathf.Lerp(HalfHeight(g, i), HalfHeight(g, j), f - i);
            return p.Body + p.Up * (g.CapsuleCenterY - half);
        }

        private static float HalfHeight(Ghost g, int sample) =>
            (g.Flags[sample] & (byte)SampleFlags.Sliding) != 0 ? SlideHalfHeight : g.CapsuleHeight * 0.5f;

        private static Color Tint(Ghost g, bool hurt, bool dashing, float alpha)
        {
            Color c = g.Color;
            if (hurt)
            {
                c = Color.Lerp(c, Color.red, 0.7f);
            }
            if (dashing)
            {
                c = Color.Lerp(c, Color.white, 0.35f);
            }
            c.a = alpha;
            return c;
        }

        // ---------------------------------------------------------------- animated models

        // The followed ghost and the winner first, then the nearest, get the animated copies (a ghost keeps
        // its copy until it's clearly farther than the next one, so they don't flicker at the edge).
        private void Assign()
        {
            int want = Mathf.Clamp(Settings.AnimatedGhosts.Value, 0, 200);
            if (keys.Length < entryCount)
            {
                keys = new float[entries.Length];
                order = new int[entries.Length];
            }
            for (int i = 0; i < entryCount; i++)
            {
                Entry e = entries[i];
                float key = e.Distance * (live.ContainsKey(e.G) ? 0.7f : 1f);
                if (e.Featured)
                {
                    key = e.G.IsBest ? -1f : -2f;
                }
                keys[i] = key;
                order[i] = i;
            }
            Array.Sort(keys, order, 0, entryCount);
            chosen.Clear();
            for (int i = 0; i < entryCount && chosen.Count < want; i++)
            {
                chosen.Add(entries[order[i]].G);
            }
            releasing.Clear();
            foreach (KeyValuePair<Ghost, LivePuppet> pair in live)
            {
                if (!chosen.Contains(pair.Key))
                {
                    releasing.Add(pair.Key);
                }
            }
            foreach (Ghost g in releasing)
            {
                LivePuppet pup = live[g];
                live.Remove(g);
                pup.Release();
                spare.Push(pup);
            }
            int made = 0;
            for (int i = 0; i < entryCount && i < want; i++)
            {
                Ghost g = entries[order[i]].G;
                if (live.ContainsKey(g))
                {
                    continue;
                }
                LivePuppet pup = spare.Count > 0 ? spare.Pop() : null;
                if (pup == null && made < NewModelsPerFrame)
                {
                    pup = kit.NewPuppet(modelRoot, ghost);  // (a few a frame: no hitch when the replay starts)
                    puppets.Add(pup);
                    made++;
                }
                if (pup == null)
                {
                    break;
                }
                pup.Take(g);
                live[g] = pup;
            }
        }

        // An animated ghost: placed, its animator fed from the recording; drawn after the animators run.
        private void Place(LivePuppet pup, ref Entry e)
        {
            float scale = e.Height / kit.Height;
            Transform holder = pup.Holder.transform;
            holder.SetPositionAndRotation(e.Bottom - e.M.Up * (kit.FloorY * scale), e.Rot);
            holder.localScale = new Vector3(scale, scale, scale);
            pup.Facing = e.Facing;

            PoseKind kind = e.M.Kind;
            Animator a = pup.Animator;
            if (a != null && a.isActiveAndEnabled)
            {
                if (pup.Fresh || seeked)
                {
                    kit.Driver.Snap(a, pup, kind, e.Phase);
                }
                else
                {
                    kit.Driver.Drive(a, pup, e.M, Events(pup, e), Mathf.Min(Time.unscaledDeltaTime, 0.1f));
                }
                float pace = e.M.Running ? Mathf.Clamp(e.M.Speed / (2f * e.Height) / kit.Driver.RunCycles, 0.5f, 3f) : 1f;
                a.speed = animSpeed * pace;
            }
            pup.Fresh = false;
            pup.WasAirborne = e.M.Airborne;
            pup.WasDashing = e.M.Dashing;
            pup.LastRise = e.M.Rise;
            pup.LastSample = e.P.Sample;

            Color c = Tint(e.G, e.Hurt, e.M.Dashing, e.Alpha);
            pup.SetLook(e.Featured, kit.LiveBakes);
            if (!e.Featured && !kit.LiveBakes)
            {
                pup.Tint(c);
            }
            else if (e.Featured && kit.Wings)
            {
                pup.Glow(e.Hurt ? Color.red : e.G.Color);
            }
            pup.Shown = true;
            pup.Featured = e.Featured;
            pup.Hurt = e.Hurt;
            pup.Weapons = e.Weapons;
            pup.Color = c;
            pup.Alpha = e.Alpha;
            pup.Size = e.Height / ReferenceHeight;
            pup.Pose = e.P;
            pup.Motion = e.M;
            pup.Bottom = e.Bottom;
            pup.Rotation = e.Rot;
            pup.Height = e.Height;
            pup.PoseIndex = GhostModelKit.PoseIndex(kind, e.Phase);
        }

        private static GhostEvents Events(LivePuppet pup, in Entry e)
        {
            var events = new GhostEvents
            {
                Jumped = e.M.Airborne && e.M.Rise > 2f && (!pup.WasAirborne || (e.M.Rise > 8f && pup.LastRise < 4f)),
                Dashed = e.M.Dashing && !pup.WasDashing,
            };
            Ghost g = e.G;
            for (int k = Mathf.Max(pup.LastSample + 1, e.P.Sample - 30); k <= e.P.Sample && k < g.Count; k++)
            {
                var flags = (SampleFlags)g.Flags[k];
                events.Shot |= (flags & SampleFlags.Shot) != 0;
                events.Punched |= (flags & SampleFlags.Punch) != 0;
            }
            return events;
        }

        // After the animators: each animated ghost's body baked in this frame's pose (drawn see-through, or
        // as the outline and the through-walls silhouette of the real model), and its weapon in its hand.
        private void LateFrame()
        {
            if (!Models || drawnFrame != Time.frameCount)
            {
                return;
            }
            try
            {
                foreach (LivePuppet pup in puppets)
                {
                    if (pup.Shown && pup.Ghost != null)
                    {
                        pup.Shown = false;
                        DrawLive(pup);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void DrawLive(LivePuppet pup)
        {
            GhostMotion m = pup.Motion;
            if (kit.LiveBakes)
            {
                Color outline = pup.Hurt ? new Color(1f, 0.15f, 0.1f, 1f) : Color.Lerp(pup.Ghost.Color, Color.white, 0.25f);
                outline.a = pup.Hurt ? 1f : 0.9f;
                Color through = pup.Hurt ? new Color(1f, 0.15f, 0.1f, 0.4f) : new Color(pup.Ghost.Color.r, pup.Ghost.Color.g, pup.Ghost.Color.b, 0.25f);
                for (int i = 0; i < pup.Body.Length; i++)
                {
                    Mesh mesh = pup.Live[i];
                    Transform t = pup.Body[i].transform;
                    if (!ModelMeshes.TryBake(pup.Body[i], mesh))
                    {
                        continue;
                    }
                    Color32[] colors = pup.Featured ? kit.White(i) : kit.Shading(i);
                    if (colors != null && colors.Length == mesh.vertexCount)
                    {
                        mesh.SetColors(colors);
                    }
                    Matrix4x4 world = kit.BakedToWorld(i, t);
                    if (pup.Featured)
                    {
                        Inflate(mesh, OutlineWidth * pup.Size * (pup.Hurt ? 1.6f : 1f) / kit.BakedUnit(i, t));
                        DrawAll(mesh, world, outline, outlineMat);
                        DrawAll(mesh, world, through, hiddenMat);
                    }
                    else
                    {
                        DrawAll(mesh, world, pup.Color, ghost);
                    }
                    if (m.Dashing)
                    {
                        Color after = Color.Lerp(pup.Ghost.Color, Color.white, 0.5f);
                        for (int k = 1; k <= 2; k++)
                        {
                            after.a = (pup.Featured ? 0.5f : pup.Alpha) * 0.45f / k;
                            DrawAll(mesh, Matrix4x4.Translate(-m.Velocity * (0.04f * k)) * world, after, ghost);
                        }
                    }
                }
            }
            if (pup.Weapons)
            {
                Vector3 hand = pup.Hand != null ? pup.Hand.position : pup.Bottom + pup.Rotation * (kit.Hands[pup.PoseIndex] * pup.Height);
                DrawHeld(pup.Ghost, pup.Pose.Sample, hand, pup.Pose.Look, pup.Size, pup.Featured ? 1f : pup.Alpha, ghost, real: pup.Featured, parts: true);
            }
        }

        // Pushes a baked body out along its normals (an outline: drawn back faces only, behind the real one).
        private void Inflate(Mesh mesh, float by)
        {
            mesh.GetVertices(inflateVerts);
            mesh.GetNormals(inflateNormals);
            if (inflateNormals.Count != inflateVerts.Count)
            {
                return;
            }
            for (int i = 0; i < inflateVerts.Count; i++)
            {
                inflateVerts[i] += inflateNormals[i] * by;
            }
            mesh.SetVertices(inflateVerts);
        }

        // ---------------------------------------------------------------- the crowd

        // A ghost without an animated copy: the pre-posed mesh for what it's doing (run frames by how far
        // it's run), tinted and see-through.
        private void DrawCrowd(ref Entry e)
        {
            int pose = GhostModelKit.PoseIndex(e.M.Kind, e.Phase);
            Mesh mesh = kit.Poses[pose];
            Matrix4x4 m = Matrix4x4.TRS(e.Bottom, e.Rot, new Vector3(e.Height, e.Height, e.Height));
            Draw(mesh, m, Tint(e.G, e.Hurt, e.M.Dashing, e.Alpha));
            if (e.M.Dashing)
            {
                Color after = Color.Lerp(e.G.Color, Color.white, 0.5f);
                for (int k = 1; k <= 2; k++)
                {
                    after.a = e.Alpha * 0.45f / k;
                    Draw(mesh, Matrix4x4.Translate(-e.M.Velocity * (0.04f * k)) * m, after);
                }
            }
            if (e.Weapons)
            {
                DrawHeld(e.G, e.P.Sample, m.MultiplyPoint3x4(kit.Hands[pose]), e.P.Look, e.Height / ReferenceHeight, e.Alpha, ghost, real: false, parts: false);
            }
        }

        // ---------------------------------------------------------------- weapons

        // The weapon it held at that sample, its grip at hand, pointing where it looked. real: in its own
        // materials (the featured ghosts); else see-through in its variant's colour. parts: allowed to draw
        // a gun part by part (a few ghosts only) when it has no single see-through mesh.
        private void DrawHeld(Ghost g, int sample, Vector3 hand, Quaternion look, float size, float alpha, Material material, bool real, bool parts)
        {
            Ghost.WeaponChange w = g.WeaponAt(sample);
            if (w.Slot <= 0)
            {
                return;
            }
            Color gun = VariantColor(w.Variant);
            gun.a = Mathf.Min(1f, alpha * 1.8f);
            WeaponModel model = guns.Get(g.WeaponName(w), w.Variant);
            if (model != null)
            {
                Matrix4x4 m = Matrix4x4.TRS(hand, look, Vector3.one * (size * model.HoldScale)) * model.Hold;
                if (real)
                {
                    model.DrawReal(m, layer, cam, firstPerson: false);
                    return;
                }
                if (model.Tinted != null)
                {
                    Draw(model.Tinted, m, gun, material);
                    return;
                }
                if (parts)
                {
                    block.SetColor(colorId, gun);
                    model.DrawParts(m, material, block, layer, cam);
                    return;
                }
            }
            Draw(cube, Matrix4x4.TRS(hand + look * new Vector3(0f, 0f, 0.3f * size), look, new Vector3(0.13f, 0.13f, 0.8f) * size), gun, material);
        }

        // Through a ghost's eyes: its weapon where the game shows yours, bottom right.
        private void DrawViewmodel(Ghost g, in Ghost.Pose p)
        {
            Ghost.WeaponChange w = g.WeaponAt(p.Sample);
            if (w.Slot <= 0)
            {
                return;
            }
            WeaponModel model = guns.Get(g.WeaponName(w), w.Variant);
            if (model != null)
            {
                model.DrawReal(Matrix4x4.TRS(p.Eye, p.Look, Vector3.one) * model.CameraFromView, layer, cam, firstPerson: true);
                return;
            }
            Color gun = VariantColor(w.Variant);
            Draw(cube, Matrix4x4.TRS(p.Eye + p.Look * new Vector3(0.35f, -0.32f, 0.75f), p.Look, new Vector3(0.13f, 0.13f, 0.8f)), new Color(gun.r, gun.g, gun.b, 0.6f));
        }

        // ---------------------------------------------------------------- crystals

        // The crystal ghost (GhostModel = Crystal, or no model could be loaded): a body, a head with a
        // visor, its weapon.
        private void DrawCrystal(Ghost g, in Ghost.Pose p, float alpha, bool featured, bool pov, bool weapons)
        {
            Color color = g.Color;
            if (g.HurtAt(p.Sample))
            {
                color = Color.Lerp(color, Color.red, 0.7f);
            }
            Material mat = featured && !pov ? xray : ghost;

            if (!pov)
            {
                Vector3 up = p.Up;
                Vector3 forward = Vector3.ProjectOnPlane(p.Look * Vector3.forward, up);
                if (forward.sqrMagnitude < 1e-4f)
                {
                    forward = Vector3.ProjectOnPlane(p.Look * Vector3.up, up);
                }
                Quaternion yaw = Quaternion.LookRotation(forward.normalized, up);

                // Body: from the capsule's bottom up to just under the eyes.
                Vector3 bottom = Bottom(g, p, Mathf.Min(frameT, g.Duration));
                float length = Mathf.Max(0.6f, Vector3.Dot(p.Eye - bottom, up) - 0.4f);
                float width = Mathf.Max(0.5f, g.CapsuleRadius * 1.8f);
                Vector3 scale = new Vector3(width, length, width);
                if ((p.Flags & SampleFlags.Sliding) != 0)
                {
                    scale = new Vector3(width, length * 0.45f, width * 1.4f);
                }
                if ((p.Flags & SampleFlags.Dashing) != 0)
                {
                    scale.z *= 1.8f;
                    color = Color.Lerp(color, Color.white, 0.35f);
                }
                Vector3 mid = bottom + up * (scale.y * 0.5f);
                Draw(body, Matrix4x4.TRS(mid, yaw, scale), new Color(color.r, color.g, color.b, alpha), mat);

                // Head and visor, looking where it looked.
                Draw(cube, Matrix4x4.TRS(p.Eye, p.Look, new Vector3(0.6f, 0.45f, 0.6f)), new Color(color.r * 0.8f, color.g * 0.8f, color.b * 0.8f, alpha), mat);
                Draw(cube, Matrix4x4.TRS(p.Eye + p.Look * new Vector3(0f, 0.03f, 0.31f), p.Look, new Vector3(0.48f, 0.12f, 0.03f)), new Color(Visor.r, Visor.g, Visor.b, Mathf.Min(1f, alpha * 2f)), mat);
            }

            if (weapons)
            {
                if (pov)
                {
                    DrawViewmodel(g, p);
                }
                else
                {
                    DrawHeld(g, p.Sample, p.Eye + p.Look * new Vector3(0.42f, -0.3f, 0.3f), p.Look, 1f, alpha, mat, real: false, parts: featured);
                }
            }
        }

        // ---------------------------------------------------------------- bursts, markers, tracers, trails

        // The pop where an attempt ends: a sphere bursting out, and shards flying off. age: seconds since.
        public void DrawBurst(Ghost g, Vector3 at, float age)
        {
            const float life = 0.55f;
            if (age < 0f || age > life)
            {
                return;
            }
            float k = age / life;
            float ease = 1f - (1f - k) * (1f - k);
            bool finish = g.Finished;
            Color c = finish ? Gold : g.Color;
            float size = g.EndReason == AttemptEnd.Death || finish ? 1f : 0.5f;
            Draw(sphere, Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * Mathf.Lerp(0.3f, 3.4f * size, ease)), new Color(c.r, c.g, c.b, (1f - k) * 0.55f), ghost);
            if (g.EndReason != AttemptEnd.Death && !finish)
            {
                return;
            }
            var random = new System.Random(g.Number * 7919 + g.Order);
            for (int i = 0; i < 7; i++)
            {
                var dir = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 1.6f - 0.4f, (float)random.NextDouble() * 2f - 1f).normalized;
                Vector3 pos = at + dir * (ease * (finish ? 7f : 4.5f));
                Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(age * 720f, age * 540f, 0f);
                Draw(cube, Matrix4x4.TRS(pos, spin, Vector3.one * (0.3f * (1f - k))), new Color(c.r, c.g, c.b, (1f - k) * 0.9f), ghost);
            }
        }

        // Every death's marker, built once; only the first `shown` (deaths so far, in order) are drawn.
        public void BuildMarkers(IList<Ghost> deathsInOrder)
        {
            if (markers != null)
            {
                Object.Destroy(markers);
            }
            markers = Meshes.Markers(deathsInOrder, 0.32f, MarkerRed);
            markersShown = -1;
        }

        public void DrawMarkers(int shown)
        {
            if (markers == null || shown <= 0)
            {
                return;
            }
            if (shown != markersShown)
            {
                markersShown = shown;
                markers.SetSubMesh(0, new SubMeshDescriptor(0, shown * Meshes.MarkerIndices), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
            }
            Draw(markers, Matrix4x4.identity, new Color(1f, 1f, 1f, 0.75f), markerMat);
        }

        public void AddTracer(Vector3 from, Vector3 to, Color color, float now, float life, float width)
        {
            if (tracers.Count >= 256)
            {
                tracers.RemoveAt(0);
            }
            tracers.Add(new Tracer { From = from, To = to, Color = color, Born = now, Life = life, Width = width });
        }

        public void ClearTracers() => tracers.Clear();

        public void DrawTracers(float now)
        {
            for (int i = tracers.Count - 1; i >= 0; i--)
            {
                Tracer tr = tracers[i];
                float k = (now - tr.Born) / tr.Life;
                if (k >= 1f || k < 0f)
                {
                    tracers.RemoveAt(i);
                    continue;
                }
                Vector3 d = tr.To - tr.From;
                float length = d.magnitude;
                if (length < 0.01f)
                {
                    continue;
                }
                var m = Matrix4x4.TRS(tr.From + d * 0.5f, Quaternion.LookRotation(d / length), new Vector3(tr.Width, tr.Width, length));
                Draw(cube, m, new Color(tr.Color.r, tr.Color.g, tr.Color.b, (1f - k) * 0.85f), ghost);
            }
        }

        // A ghost's path from `from` seconds to `to` seconds in.
        public void SetTrail(bool best, Ghost g, float from, float to, Color color)
        {
            LineRenderer line = best ? bestTrail : followTrail;
            if (g == null || to <= from)
            {
                line.positionCount = 0;
                return;
            }
            int first = Mathf.Clamp(Mathf.FloorToInt(from * g.Rate), 0, g.Count - 1);
            int last = Mathf.Clamp(Mathf.FloorToInt(to * g.Rate), 0, g.Count - 1);
            int span = last - first + 1;
            int step = Mathf.Max(1, Mathf.CeilToInt(span / (float)(trailPoints.Length - 1)));
            int n = 0;
            for (int i = first; i <= last && n < trailPoints.Length - 1; i += step)
            {
                trailPoints[n++] = g.Body[i];
            }
            trailPoints[n++] = g.PoseAt(to).Body;
            line.positionCount = n;
            line.SetPositions(trailPoints);
            line.startColor = new Color(color.r, color.g, color.b, 0.05f);
            line.endColor = new Color(color.r, color.g, color.b, 0.7f);
        }
    }

    // The meshes, made in code: grey shading baked into the vertex colours (the material is unlit),
    // tinted per ghost through _Color.
    internal static class Meshes
    {
        public const int MarkerIndices = 24;

        private static readonly Vector3 Light = new Vector3(0.35f, 0.85f, 0.4f).normalized;

        public static Color Shade(Vector3 normal)
        {
            float b = 0.55f + 0.45f * Mathf.Max(0f, Vector3.Dot(normal, Light));
            return new Color(b, b, b, 1f);
        }

        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> I = new List<int>();

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Color? tint = null)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                Color col = Shade(n);
                if (tint.HasValue)
                {
                    col = new Color(col.r * tint.Value.r, col.g * tint.Value.g, col.b * tint.Value.b, tint.Value.a);
                }
                int start = V.Count;
                V.Add(a);
                V.Add(b);
                V.Add(c);
                C.Add(col);
                C.Add(col);
                C.Add(col);
                I.Add(start);
                I.Add(start + 1);
                I.Add(start + 2);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Tri(a, b, c);
                Tri(a, c, d);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
                if (V.Count > 65000)
                {
                    mesh.indexFormat = IndexFormat.UInt32;
                }
                mesh.SetVertices(V);
                mesh.SetColors(C);
                mesh.SetTriangles(I, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // A six-sided crystal, 1 tall and 1 wide, centred: the ghost's body.
        public static Mesh Crystal()
        {
            var b = new Builder();
            const int sides = 6;
            var top = new Vector3(0f, 0.5f, 0f);
            var bottom = new Vector3(0f, -0.5f, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides;
                float a1 = (i + 1) * Mathf.PI * 2f / sides;
                var p0 = new Vector3(Mathf.Sin(a0) * 0.5f, 0f, Mathf.Cos(a0) * 0.5f);
                var p1 = new Vector3(Mathf.Sin(a1) * 0.5f, 0f, Mathf.Cos(a1) * 0.5f);
                Vector3 hi0 = p0 + Vector3.up * 0.22f, hi1 = p1 + Vector3.up * 0.22f;
                Vector3 lo0 = p0 * 0.8f - Vector3.up * 0.3f, lo1 = p1 * 0.8f - Vector3.up * 0.3f;
                // (clockwise seen from outside: Unity's front faces)
                b.Tri(top, hi0, hi1);
                b.Quad(hi0, lo0, lo1, hi1);
                b.Tri(bottom, lo1, lo0);
            }
            return b.Build("UltraAttempts crystal");
        }

        public static Mesh Cube()
        {
            var b = new Builder();
            Vector3 h = Vector3.one * 0.5f;
            Vector3[] c =
            {
                new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
                new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z),
            };
            b.Quad(c[0], c[3], c[2], c[1]);  // back
            b.Quad(c[4], c[5], c[6], c[7]);  // front
            b.Quad(c[0], c[4], c[7], c[3]);  // left
            b.Quad(c[1], c[2], c[6], c[5]);  // right
            b.Quad(c[3], c[7], c[6], c[2]);  // top
            b.Quad(c[0], c[1], c[5], c[4]);  // bottom
            return b.Build("UltraAttempts cube");
        }

        // An octahedron split once and puffed out: a low-poly ball, radius 0.5.
        public static Mesh Sphere()
        {
            var b = new Builder();
            Vector3[] o = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[,] faces = { { 0, 4, 3 }, { 0, 3, 5 }, { 0, 5, 2 }, { 0, 2, 4 }, { 1, 3, 4 }, { 1, 5, 3 }, { 1, 2, 5 }, { 1, 4, 2 } };
            for (int f = 0; f < 8; f++)
            {
                Vector3 a = o[faces[f, 0]], c = o[faces[f, 1]], d = o[faces[f, 2]];
                Vector3 ac = (a + c).normalized, cd = (c + d).normalized, da = (d + a).normalized;
                b.Tri(a * 0.5f, ac * 0.5f, da * 0.5f);
                b.Tri(ac * 0.5f, c * 0.5f, cd * 0.5f);
                b.Tri(da * 0.5f, cd * 0.5f, d * 0.5f);
                b.Tri(ac * 0.5f, cd * 0.5f, da * 0.5f);
            }
            return b.Build("UltraAttempts sphere");
        }

        // A small diamond where each attempt died, in the order they died (MarkerIndices each).
        public static Mesh Markers(IList<Ghost> deaths, float size, Color color)
        {
            var b = new Builder();
            Vector3[] o = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[,] faces = { { 0, 4, 3 }, { 0, 3, 5 }, { 0, 5, 2 }, { 0, 2, 4 }, { 1, 3, 4 }, { 1, 5, 3 }, { 1, 2, 5 }, { 1, 4, 2 } };
            foreach (Ghost g in deaths)
            {
                Ghost.Pose end = g.EndPose;
                Vector3 at = end.Body + end.Up * 0.3f;
                for (int f = 0; f < 8; f++)
                {
                    Vector3 s = new Vector3(size, size * 1.6f, size);
                    b.Tri(at + Vector3.Scale(o[faces[f, 0]], s), at + Vector3.Scale(o[faces[f, 1]], s), at + Vector3.Scale(o[faces[f, 2]], s), color);
                }
            }
            if (b.V.Count == 0)
            {
                return null;
            }
            Mesh mesh = b.Build("UltraAttempts markers");
            mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 10f);
            return mesh;
        }
    }
}
