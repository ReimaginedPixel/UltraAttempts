using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // The shown attempt's effects as the game's own (Replay.Effects): copies of its own prefabs, visual only
    // (Puppet.MakeEffect), where and when the attempt had them. Their particles run on the replay's time, simulated
    // by hand (the level's clock is stopped): at its speed, still when it's paused, started over at the right age
    // after a seek. What scripts did to them in the game is done here from how old they are: explosions growing and
    // their lights (Explosion), sprites and lights scaling and fading (ScaleNFade), spinning (Spin), facing the camera
    // (AlwaysLookAtCamera), trails trailing behind (a TrailRenderer stands still with the clock: a line along the
    // recorded path instead), beams thinning (RevolverBeam). Projectiles and the livelier bits of debris are drawn
    // as copies of their prefabs too, trails and smoke included, and beams as the game's own line between their
    // recorded ends. Copies are kept and used again. Other attempts' worlds stay as ThingPlayback draws them.
    internal sealed class EffectPlayback
    {
        private const int MaxCopies = 192;          // effects shown at once
        private const int MaxThingCopies = 160;     // projectiles and debris shown as the game's own at once
        private const int MaxBeamCopies = 48;
        private const int MaxLive = 2000;           // effects alive at a moment (after a seek: the newest)
        private const int MakesPerFrame = 24;       // copies made in one frame (the rest wait a frame)
        private const int PooledPerPrefab = 48;
        private const float CatchUp = 4f;           // at most this much of an effect's past is run when it's shown late
        private const float Linger = 4f;            // its particles play out at most this long once it's done
        private const int MaxTrailPoints = 32;
        private const float GunReach = 1.3f;        // nearer its camera than this, an effect was at the gun (or the fist)

        private readonly Camera cam;
        private readonly GhostRenderer renderer;
        private readonly int layer;                 // for what the camera wouldn't show on its own layer, and muzzle flashes seen from outside
        private readonly Transform root;
        private readonly PrefabLibrary library = new PrefabLibrary();
        private readonly Dictionary<GameObject, Stack<Copy>> pool = new Dictionary<GameObject, Stack<Copy>>();
        private readonly Dictionary<GameObject, Facts> facts = new Dictionary<GameObject, Facts>();
        private readonly List<Copy> made = new List<Copy>();
        private readonly List<int> live = new List<int>();                 // the shown attempt's effects alive now
        private readonly Dictionary<int, Copy> shown = new Dictionary<int, Copy>();
        private readonly Dictionary<int, Copy> things = new Dictionary<int, Copy>();
        private readonly Dictionary<int, Copy> beams = new Dictionary<int, Copy>();
        private readonly List<int> gone = new List<int>();
        private readonly HashSet<GameObject> thingKinds = new HashSet<GameObject>();
        private readonly HashSet<GameObject> broken = new HashSet<GameObject>();     // prefabs that wouldn't copy
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private GhostWorld world;
        private Ghost ghost;
        private float t;
        private float lastT = float.NaN;
        private int frame;
        private int cursor;
        private int makes;
        private bool eyes;
        private Ghost.Pose pose;
        private Vector3 bodyPos;
        private Quaternion bodyRot = Quaternion.identity;
        private Vector3 muzzle;
        private int copiesMade, reused, effectsShown, thingsShown, beamsShown, mostAtOnce, notMade;

        // What's known of a prefab once one copy of it is made.
        private sealed class Facts
        {
            public bool OnlyParticles;  // nothing to show once its particles are gone
            public bool Lively;         // particles or a trail (a bit of debris worth copying)
            public bool Beam;           // a RevolverBeam (its line between a beam's ends)
        }

        // One copy, and what in it moved by itself in the game (worked out when it's made).
        private sealed class Copy
        {
            public GameObject Source;
            public GameObject Go;
            public Transform T;
            public Facts Facts;
            public Vector3 Scale0;
            public ParticleSystem[] Systems = Array.Empty<ParticleSystem>();
            public ParticleSystem[] Roots = Array.Empty<ParticleSystem>();   // (no system above them: Simulate takes their children along)
            public bool[] Emits = Array.Empty<bool>();
            public ParticleSystem.MinMaxGradient[] Colors = Array.Empty<ParticleSystem.MinMaxGradient>();
            public readonly List<ParticleSystem> Tintable = new List<ParticleSystem>();
            public readonly List<SpriteRenderer> TintableSprites = new List<SpriteRenderer>();
            public readonly List<Color> SpriteColors = new List<Color>();
            public Light[] Lights = Array.Empty<Light>();                    // lights nothing else fades
            public float[] Intensity = Array.Empty<float>();
            public Animator[] Animators = Array.Empty<Animator>();
            public readonly List<Boom> Booms = new List<Boom>();
            public readonly List<Fade> Fades = new List<Fade>();
            public readonly List<Spinner> Spins = new List<Spinner>();
            public readonly List<Facer> Facers = new List<Facer>();
            public readonly List<Trail> Trails = new List<Trail>();
            public Transform[] Layered = Array.Empty<Transform>();
            public int[] Layers = Array.Empty<int>();
            public Renderer[] Renderers = Array.Empty<Renderer>();
            public bool[] RenderersOn = Array.Empty<bool>();
            public LineRenderer Beam;
            public float BeamWidth;
            public Light BeamLight;
            public float BeamIntensity;
            public Transform BeamFlash;

            // in use
            public float Simulated;     // how far into its life its particles have been run (< 0: not yet)
            public bool Emitting;
            public int Mode = -1;       // 0: in the world, 1: on the camera (through its eyes), 2: in the world on the replay's layer,
                                        // 3: in the world on its own layers (any camera's: one left next to the camera)
            public int Seen;            // the frame it was last shown
            public bool Anchored;       // (made next to its camera: where it was left in the world)
            public int AnchorFrame = int.MaxValue;
            public Vector3 AnchorPos;
            public Quaternion AnchorRot;
            public ThingPath Path;      // (a thing's)
            public float GoneAt = -1f;  // (a thing's: when it stopped being there)
            public float LastAt;
        }

        // An Explosion: it grows by 0.05 * speed a physics step until its radius passes maxSize, then a quarter as
        // fast while it fades at 2 a second; its light spreads and dims (Explosion.FixedUpdate, Update). Its sphere
        // is ThingPlayback's to draw (see-through, ExplosionOpacity).
        private sealed class Boom
        {
            public Transform T;
            public bool Root;           // the copy itself: its size is in the recorded keys
            public bool On;             // switched on in the prefab
            public Vector3 Scale0;
            public float Speed, MaxSize, Radius, Alpha0;
            public Light Light;
            public float Range0, Intensity0;
        }

        // A ScaleNFade.
        private sealed class Fade
        {
            public Transform T;
            public bool Root;
            public bool On;
            public Vector3 Scale0;
            public bool Scales, Fades, Stays, Clamp, Intensity, ToBlack;
            public float ScaleSpeed, FadeSpeed, Min, Max;
            public FadeType Kind;
            public SpriteRenderer Sprite;
            public Color Color0;
            public Light Light;
            public float Light0;
            public LineRenderer Line;
            public Color End0;
            public Renderer Renderer;
            public int Property;        // _OpacScale, _Tint or _Color
            public bool Opacity;
            public float Value0;
        }

        private sealed class Spinner
        {
            public Transform T;
            public Quaternion Rot0;
            public Vector3 Axis;
            public float Speed;
        }

        private sealed class Facer
        {
            public Transform T;
            public bool Screen, X, Y, Z;
            public Vector3 Offset;
        }

        private sealed class Trail
        {
            public LineRenderer Line;
            public Vector3 Offset;      // where it is on the copy (in the copy's own space)
            public float Time;
            public float Width;
            public bool On;
            public Vector3[] Points;
        }

        public EffectPlayback(Camera cam, GhostRenderer renderer, int layer)
        {
            this.cam = cam;
            this.renderer = renderer;
            this.layer = layer;
            root = new GameObject("UltraAttempts effects").transform;
        }

        // Once the attempts are loaded: the game's prefabs, by name (once, while the replay loads).
        public void Prepare(IList<Ghost> ghosts)
        {
            foreach (Ghost g in ghosts)
            {
                if (g.HasEverything)
                {
                    library.Index();
                    return;
                }
            }
        }

        public string Summary =>
            $"{library.Kinds} kinds found as the game's own ({library.Missing} not found{(library.MissingNames.Count > 0 ? ": " + string.Join(", ", library.MissingNames) : "")}); "
            + $"{effectsShown} effects shown, {thingsShown} projectiles and bits of debris as the game's own ({thingKinds.Count} kinds), {beamsShown} beams with their own line; "
            + $"{copiesMade} copies made ({reused} times one was used again), {mostAtOnce} at most at once"
            + (notMade > 0 ? $", {notMade} times one waited for room" : "");

        public bool Asked => library.Asked;

        // Nothing to show (nobody followed, the world's off): every copy put away.
        public void Clear()
        {
            ReleaseAll();
            world = null;
            ghost = null;
            lastT = float.NaN;
        }

        public void Dispose()
        {
            foreach (Copy c in made)
            {
                if (c.Go != null)
                {
                    Object.Destroy(c.Go);
                }
            }
            made.Clear();
            pool.Clear();
            shown.Clear();
            things.Clear();
            beams.Clear();
            live.Clear();
            if (root != null)
            {
                Object.Destroy(root.gameObject);
            }
        }

        // ---------------------------------------------------------------- each frame

        // Before ThingPlayback draws g's things t seconds in. eyes: seen through its eyes (what was on its camera goes
        // on the replay's camera, on the viewmodel); else from outside (at the gun in the ghost's hand).
        public void Begin(Ghost g, GhostWorld w, float time, bool inEyes)
        {
            frame++;
            makes = MakesPerFrame;
            if (w.FoundPrefabs == null || w.FoundPrefabs.Length != w.EffectPrefabs.Length)
            {
                w.FoundPrefabs = new GameObject[w.EffectPrefabs.Length];
                w.PrefabsLooked = new bool[w.EffectPrefabs.Length];
            }
            if (w.FoundForLooks == null || w.FoundForLooks.Length != w.Looks.Length)
            {
                w.FoundForLooks = new GameObject[w.Looks.Length];
                w.LooksLooked = new bool[w.Looks.Length];
            }
            bool jumped = w != world || g != ghost || float.IsNaN(lastT) || time < lastT - 1e-4f || time - lastT > 1f;
            if (jumped)
            {
                // (a seek, a cut to another attempt, the start: everything shown again from where it is now)
                ReleaseAll();
                world = w;
                ghost = g;
                Rebuild(time);
            }
            lastT = time;
            t = time;
            eyes = inEyes;
            pose = g.PoseAt(Mathf.Min(t, g.Duration));
            bodyPos = pose.Body;
            Vector3 forward = Vector3.ProjectOnPlane(pose.Look * Vector3.forward, pose.Up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(pose.Look * Vector3.up, pose.Up);
            }
            bodyRot = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, pose.Up) : Quaternion.identity;
            muzzle = renderer != null ? renderer.Muzzle(g, pose.Sample, pose.Eye, pose.Look) : pose.Eye + pose.Look * new Vector3(0.42f, -0.3f, 0.9f);
        }

        // A thing of the shown attempt's, there this frame (at: seconds into the attempt): drawn as a copy of its prefab
        // if it has one (true), else ThingPlayback draws its meshes.
        public bool Thing(int index, ThingPath p, Vector3 pos, Quaternion rot, Vector3 scale, float at)
        {
            if (p.Kind != ThingKind.Projectile && p.Kind != ThingKind.Debris)
            {
                return false;
            }
            if (!things.TryGetValue(index, out Copy c))
            {
                if (things.Count >= MaxThingCopies)
                {
                    return false;
                }
                GameObject source = ForThing(p);
                c = source != null ? Take(source) : null;
                if (c == null)
                {
                    return false;
                }
                Assign(c, null);
                c.Path = p;
                things[index] = c;
                thingsShown++;
                thingKinds.Add(source);
            }
            c.Seen = frame;
            c.GoneAt = -1f;
            c.LastAt = at;
            Place(c, 0);
            c.T.SetPositionAndRotation(pos, rot);
            c.T.localScale = scale;
            Run(c, t - p.From, ghost.Duration - p.From);
            Emulate(c, t - p.From, null);
            ThingTrails(c, p, at, 0f);
            return true;
        }

        // A beam of the shown attempt's, age seconds old: the game's own line between its ends if its prefab's
        // known (true), else ThingPlayback draws one.
        public bool Beam(int index, in WorldMoment m, float age)
        {
            int prefab = index < world.MomentPrefab.Length ? world.MomentPrefab[index] : -1;
            if (prefab < 0)
            {
                return false;
            }
            if (!beams.TryGetValue(index, out Copy c))
            {
                GameObject source = beams.Count < MaxBeamCopies ? Found(prefab) : null;
                if (source == null || (facts.TryGetValue(source, out Facts known) && !known.Beam))
                {
                    return false;
                }
                c = Take(source);
                if (c == null)
                {
                    return false;
                }
                if (c.Beam == null)
                {
                    Release(c);
                    return false;
                }
                Assign(c, null);
                beams[index] = c;
                beamsShown++;
            }
            c.Seen = frame;
            Vector3 d = m.To - m.From;
            Quaternion rot = d.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(d) : Quaternion.identity;
            Place(c, 0);
            c.T.SetPositionAndRotation(m.From, rot);
            c.T.localScale = c.Scale0;
            // (RevolverBeam: its line thins by 1.5 a second and its light dims by 100, from its gun to what it hit)
            LineRenderer lr = c.Beam;
            lr.positionCount = 2;
            lr.SetPosition(0, lr.useWorldSpace ? m.From : c.T.InverseTransformPoint(m.From));
            lr.SetPosition(1, lr.useWorldSpace ? m.To : c.T.InverseTransformPoint(m.To));
            lr.widthMultiplier = Mathf.Max(0f, c.BeamWidth - 1.5f * age);
            if (c.BeamLight != null)
            {
                c.BeamLight.intensity = Mathf.Max(0f, c.BeamIntensity - 100f * age);
            }
            if (c.BeamFlash != null)
            {
                c.BeamFlash.SetPositionAndRotation(m.From, rot);
            }
            Run(c, age, float.PositiveInfinity);
            Emulate(c, age, null);
            return true;
        }

        // After ThingPlayback has drawn: the effects, and what of this frame's things and beams wasn't drawn.
        public void End()
        {
            if (world == null)
            {
                return;
            }
            EffectPath[] effects = world.Effects;
            while (cursor < effects.Length && effects[cursor].From <= t)
            {
                live.Add(cursor++);
            }
            // (the newest first: they get the room when there's too little)
            for (int i = live.Count - 1; i >= 0; i--)
            {
                int index = live[i];
                EffectPath e = effects[index];
                shown.TryGetValue(index, out Copy c);
                if (Over(e, c))
                {
                    if (c != null)
                    {
                        Release(c);
                        shown.Remove(index);
                    }
                    live.RemoveAt(i);
                    continue;
                }
                if (c == null)
                {
                    GameObject source = shown.Count < MaxCopies ? Found(e.Prefab) : null;
                    c = source != null ? Take(source) : null;
                    if (c == null)
                    {
                        continue;
                    }
                    Assign(c, e);
                    shown[index] = c;
                    effectsShown++;
                }
                Show(c, e);
            }
            Leftovers();
            mostAtOnce = Math.Max(mostAtOnce, shown.Count + things.Count + beams.Count);
        }

        // ---------------------------------------------------------------- the attempt's effects

        // Everything alive at `time` (from the attempt's start: one can last).
        private void Rebuild(float time)
        {
            live.Clear();
            EffectPath[] effects = world.Effects;
            int lo = 0, hi = effects.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (effects[mid].From <= time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            cursor = lo;
            t = time;
            for (int i = 0; i < cursor; i++)
            {
                if (!Over(effects[i], null))
                {
                    live.Add(i);
                }
            }
            if (live.Count > MaxLive)
            {
                live.RemoveRange(0, live.Count - MaxLive);
            }
        }

        // Whether an effect is done with: gone in the game, or done playing out (its particles gone, or long enough
        // since the attempt ended).
        private bool Over(EffectPath e, Copy c)
        {
            if (t < e.From || (!e.Lasts && t >= e.To) || (e.Lasts && t >= ghost.Duration + Linger))
            {
                return true;
            }
            float quiet = Quiet(e);
            if (t < quiet)
            {
                return false;
            }
            Facts f = c?.Facts ?? FactsOf(e.Prefab);
            if (f == null || !f.OnlyParticles)
            {
                return false;
            }
            if (t >= quiet + Linger)
            {
                return true;
            }
            return c != null && c.Simulated > quiet - e.From + 0.1f && Particles(c) == 0;
        }

        // When it stopped making particles: when they stopped coming, it went, or the attempt ended.
        private float Quiet(EffectPath e) => Mathf.Min(e.Stop, e.Lasts ? ghost.Duration : e.To);

        private Facts FactsOf(int prefab)
        {
            GameObject source = prefab >= 0 && prefab < world.FoundPrefabs.Length ? world.FoundPrefabs[prefab] : null;
            return source != null && facts.TryGetValue(source, out Facts f) ? f : null;
        }

        // Where it is (its frame: the world; the replay's camera, through its eyes; else the ghost's gun; the ghost's
        // body), then its particles and what its scripts did, from how old it is.
        private void Show(Copy c, EffectPath e)
        {
            float age = t - e.From;
            e.At(Mathf.Min(t, e.Lasts ? ghost.Duration : e.To), out Vector3 pos, out Quaternion rot, out float scale);
            c.T.localScale = e.BaseScale * scale;
            switch (e.Frame)
            {
                case EffectFrame.Camera when eyes:
                    Place(c, 1);
                    c.T.SetLocalPositionAndRotation(pos, rot);
                    break;
                case EffectFrame.Camera:
                    Place(c, 2);
                    Outside(pose, pos, rot, out Vector3 there, out Quaternion turned);
                    c.T.SetPositionAndRotation(there, turned);
                    break;
                case EffectFrame.Near when !c.Anchored && eyes:
                    // (put on the camera this frame; once the camera's been put where it is now, left there in the world)
                    if (c.Mode == 1 && frame > c.AnchorFrame)
                    {
                        c.T.GetPositionAndRotation(out c.AnchorPos, out c.AnchorRot);
                        c.Anchored = true;
                        Place(c, 3);
                        c.T.SetPositionAndRotation(c.AnchorPos, c.AnchorRot);
                        break;
                    }
                    Place(c, 1);
                    c.T.SetLocalPositionAndRotation(pos, rot);
                    c.AnchorFrame = Math.Min(c.AnchorFrame, frame);
                    break;
                case EffectFrame.Near:
                    if (!c.Anchored)
                    {
                        Outside(ghost.PoseAt(Mathf.Min(e.From, ghost.Duration)), pos, rot, out c.AnchorPos, out c.AnchorRot);
                        c.Anchored = true;
                        Place(c, 2);
                    }
                    c.T.SetPositionAndRotation(c.AnchorPos, c.AnchorRot);
                    break;
                case EffectFrame.Body:
                    Place(c, 0);
                    c.T.SetPositionAndRotation(bodyPos + bodyRot * pos, bodyRot * rot);
                    break;
                default:
                    Place(c, 0);
                    c.T.SetPositionAndRotation(pos, rot);
                    break;
            }
            Run(c, age, Quiet(e) - e.From);
            Emulate(c, age, e);
            EffectTrails(c, e);
        }

        // Something on (or next to) its camera, seen from outside: at the ghost's gun if it was at the gun (a muzzle
        // flash, a punch's hit at the fist), else where it was in front of its eyes (a punch's dust on the wall).
        private void Outside(in Ghost.Pose p, Vector3 local, Quaternion rot, out Vector3 at, out Quaternion turned)
        {
            turned = p.Look * rot;
            at = local.sqrMagnitude < GunReach * GunReach ? muzzle : p.Eye + p.Look * local;
        }

        // Things not drawn this frame: gone (their particles play out, their trails drain), or past.
        private void Leftovers()
        {
            gone.Clear();
            foreach (KeyValuePair<int, Copy> pair in things)
            {
                Copy c = pair.Value;
                if (c.Seen == frame)
                {
                    continue;
                }
                if (c.GoneAt < 0f)
                {
                    c.GoneAt = t;
                }
                float since = t - c.GoneAt;
                float age = t - c.Path.From;
                Run(c, age, c.GoneAt - c.Path.From);
                Emulate(c, age, null);
                bool drained = ThingTrails(c, c.Path, c.LastAt, since);
                if (since > Linger || (since > 0.1f && drained && Particles(c) == 0))
                {
                    gone.Add(pair.Key);
                }
            }
            foreach (int index in gone)
            {
                Release(things[index]);
                things.Remove(index);
            }
            gone.Clear();
            foreach (KeyValuePair<int, Copy> pair in beams)
            {
                if (pair.Value.Seen != frame)
                {
                    gone.Add(pair.Key);
                }
            }
            foreach (int index in gone)
            {
                Release(beams[index]);
                beams.Remove(index);
            }
        }

        // ---------------------------------------------------------------- running a copy

        // Its particles and animators up to `age` seconds into its life (quiet: when its particles stop coming).
        private void Run(Copy c, float age, float quiet)
        {
            if (c.Simulated < 0f)
            {
                Restart(c, age, quiet);
                return;
            }
            float dt = age - c.Simulated;
            if (dt <= 0f)
            {
                return;  // (paused)
            }
            if (c.Emitting && age >= quiet)
            {
                float before = Mathf.Clamp(quiet - c.Simulated, 0f, dt);
                Step(c, before);
                Emission(c, false);
                Step(c, dt - before);
            }
            else
            {
                Step(c, dt);
            }
            c.Simulated = age;
            foreach (Animator a in c.Animators)
            {
                if (a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null)
                {
                    a.Update(dt);
                }
            }
        }

        // From the start of its life to `age` (as much of it as CatchUp), as after a seek.
        private void Restart(Copy c, float age, float quiet)
        {
            Emission(c, true);
            age = Mathf.Max(0f, age);
            float first = Mathf.Min(age, quiet);
            foreach (ParticleSystem ps in c.Roots)
            {
                if (ps != null)
                {
                    ps.Simulate(Mathf.Clamp(first, 0f, CatchUp), true, true, false);
                }
            }
            if (age >= quiet)
            {
                Emission(c, false);
                Step(c, Mathf.Min(age - Mathf.Max(0f, first), CatchUp));
            }
            c.Simulated = age;
            foreach (Animator a in c.Animators)
            {
                if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null)
                {
                    continue;
                }
                a.Rebind();
                for (float left = Mathf.Min(age, 2f); left > 0f; left -= 0.1f)
                {
                    a.Update(Mathf.Min(0.1f, left));
                }
            }
        }

        private static void Step(Copy c, float dt)
        {
            if (dt <= 0f)
            {
                return;
            }
            foreach (ParticleSystem ps in c.Roots)
            {
                if (ps != null)
                {
                    ps.Simulate(dt, true, false, false);
                }
            }
        }

        // (its emission off is the game's Stop: what's out plays out, nothing new comes)
        private static void Emission(Copy c, bool on)
        {
            c.Emitting = on;
            for (int i = 0; i < c.Systems.Length; i++)
            {
                if (c.Systems[i] != null)
                {
                    ParticleSystem.EmissionModule emission = c.Systems[i].emission;
                    emission.enabled = on && c.Emits[i];
                }
            }
        }

        private static int Particles(Copy c)
        {
            int n = 0;
            foreach (ParticleSystem ps in c.Systems)
            {
                if (ps != null)
                {
                    n += ps.particleCount;
                }
            }
            return n;
        }

        // What its scripts did, from how old it is (e: the effect, for its lights; null for a thing or a beam).
        private void Emulate(Copy c, float age, EffectPath e)
        {
            foreach (Boom b in c.Booms)
            {
                Grow(b, age);
            }
            foreach (Fade f in c.Fades)
            {
                Fading(f, age);
            }
            foreach (Spinner s in c.Spins)
            {
                if (s.T != null)
                {
                    s.T.localRotation = s.Rot0 * Quaternion.AngleAxis(s.Speed * age, s.Axis);
                }
            }
            if (c.Facers.Count > 0)
            {
                Transform camera = cam.transform;
                foreach (Facer f in c.Facers)
                {
                    Face(f, camera);
                }
            }
            float light = LightFade(e, age);
            for (int i = 0; i < c.Lights.Length; i++)
            {
                if (c.Lights[i] != null)
                {
                    c.Lights[i].intensity = c.Intensity[i] * light;
                }
            }
        }

        // A light nothing fades by itself: over a short life (a muzzle flash's), or quickly once its particles stop.
        private static float LightFade(EffectPath e, float age)
        {
            if (e == null)
            {
                return 1f;
            }
            if (!e.Lasts && e.To - e.From < 1.5f)
            {
                return Mathf.Clamp01(1f - age / Mathf.Max(e.To - e.From, 0.02f));
            }
            float stopped = e.Stop - e.From;
            return age > stopped ? Mathf.Clamp01(1f - (age - stopped) / 0.3f) : 1f;
        }

        private static void Grow(Boom b, float age)
        {
            if (b.T == null)
            {
                return;
            }
            float perSecond = 0.05f * b.Speed / Mathf.Max(Time.fixedDeltaTime, 0.001f);
            float parent = b.T.parent != null ? Mathf.Abs(b.T.parent.lossyScale.x) : 1f;
            float fades = perSecond > 1e-4f ? Mathf.Max(0f, (b.MaxSize / Mathf.Max(b.Radius * parent, 1e-4f) - b.Scale0.x) / perSecond) : float.PositiveInfinity;
            float growing = Mathf.Min(age, fades), fading = Mathf.Max(0f, age - fades);
            float alpha = b.Alpha0 - 2f * fading;
            bool there = alpha > 0f && b.On;
            if (b.T.gameObject.activeSelf != there && !b.Root)
            {
                b.T.gameObject.SetActive(there);  // (it destroys itself, and its light with it)
            }
            if (!there)
            {
                if (b.Light != null)
                {
                    b.Light.enabled = false;
                }
                return;
            }
            if (!b.Root)
            {
                b.T.localScale = b.Scale0 + Vector3.one * (perSecond * growing + perSecond * 0.25f * fading);
            }
            if (b.Light != null)
            {
                b.Light.enabled = true;
                b.Light.range = b.Range0 + 5f * b.Speed * growing + 5f * b.Speed * 0.25f * fading;
                b.Light.intensity = Mathf.Max(0f, b.Intensity0 - 65f * fading);
            }
        }

        private void Fading(Fade f, float age)
        {
            if (f.T == null)
            {
                return;
            }
            if (f.Scales && !f.Root)
            {
                f.T.localScale = f.Scale0 + Vector3.one * (f.ScaleSpeed * age);
            }
            if (!f.Fades)
            {
                return;
            }
            float value = 1f;
            switch (f.Kind)
            {
                case FadeType.Sprite when f.Sprite != null:
                    value = Faded(f, f.Color0.a, age);
                    f.Sprite.color = new Color(f.Color0.r, f.Color0.g, f.Color0.b, value);
                    break;
                case FadeType.Light when f.Light != null:
                    value = Faded(f, f.Light0, age);
                    if (f.Intensity)
                    {
                        f.Light.intensity = value;
                    }
                    else
                    {
                        f.Light.range = value;
                    }
                    break;
                case FadeType.Line when f.Line != null:
                    float start = Faded(f, f.Color0.a, age), end = Faded(f, f.End0.a, age);
                    f.Line.startColor = new Color(f.Color0.r, f.Color0.g, f.Color0.b, start);
                    f.Line.endColor = new Color(f.End0.r, f.End0.g, f.End0.b, end);
                    value = Mathf.Max(start, end);
                    break;
                case FadeType.Renderer when f.Renderer != null:
                    f.Renderer.GetPropertyBlock(block);
                    if (f.Opacity)
                    {
                        value = Mathf.Max(0f, Faded(f, f.Value0, age));
                        block.SetFloat(f.Property, value);
                    }
                    else if (f.ToBlack)
                    {
                        block.SetColor(f.Property, Color.Lerp(Color.black, f.Color0, Mathf.Exp(-f.FadeSpeed * age)));
                    }
                    else
                    {
                        value = Mathf.Max(0f, Faded(f, f.Color0.a, age));
                        block.SetColor(f.Property, new Color(f.Color0.r, f.Color0.g, f.Color0.b, value));
                    }
                    f.Renderer.SetPropertyBlock(block);
                    break;
            }
            bool there = f.On && !(value <= 0f && f.FadeSpeed > 0f && !f.Stays);
            if (f.T.gameObject.activeSelf != there && !f.Root)
            {
                f.T.gameObject.SetActive(there);  // (it destroys itself once it's faded out)
            }
        }

        private static float Faded(Fade f, float from, float age)
        {
            float v = from - f.FadeSpeed * age;
            return f.Clamp ? Mathf.Clamp(v, f.Min, f.Max) : v;
        }

        private static void Face(Facer f, Transform camera)
        {
            if (f.T == null)
            {
                return;
            }
            if (f.Screen)
            {
                f.T.rotation = Quaternion.AngleAxis(180f, Vector3.up) * Quaternion.LookRotation(camera.forward, camera.up);
            }
            else
            {
                Vector3 at = f.T.position, target = camera.position;
                target = new Vector3(f.X ? target.x : at.x, f.Y ? target.y : at.y, f.Z ? target.z : at.z);
                if ((target - at).sqrMagnitude > 1e-6f)
                {
                    f.T.rotation = Quaternion.LookRotation(target - at);
                }
            }
            if (f.Offset != Vector3.zero)
            {
                f.T.localRotation = Quaternion.Euler(f.T.localRotation.eulerAngles + f.Offset);
            }
        }

        // A thing's trails: lines along where it went over each trail's time (to `head`, `since` seconds after it
        // went: they drain). True once there's nothing left of them.
        private bool ThingTrails(Copy c, ThingPath p, float head, float since)
        {
            bool drained = true;
            foreach (Trail tr in c.Trails)
            {
                if (!tr.On)
                {
                    continue;
                }
                float fade = Mathf.Clamp01(since / tr.Time);
                float tail = Mathf.Max(p.From, Mathf.Min(t, head + since) - tr.Time);
                if (fade >= 1f || head - tail < 1e-3f)
                {
                    tr.Line.positionCount = 0;
                    continue;
                }
                drained = false;
                int n = tr.Points.Length;
                float end = ghost.Duration;
                for (int k = 0; k < n; k++)
                {
                    float at = head - (head - tail) * k / (n - 1);
                    if (!p.At(at, end, out Vector3 pos, out Quaternion rot, out float scale))
                    {
                        p.At(Mathf.Clamp(at, p.From, p.To), end, out pos, out rot, out scale);
                    }
                    tr.Points[k] = Matrix4x4.TRS(pos, rot, p.BaseScale * scale).MultiplyPoint3x4(tr.Offset);
                }
                tr.Line.positionCount = n;
                tr.Line.SetPositions(tr.Points);
                tr.Line.widthMultiplier = tr.Width * (1f - fade);
            }
            return drained;
        }

        // An effect's trails, along its keys in the world (on V1's camera or body they'd only follow V1: none).
        private void EffectTrails(Copy c, EffectPath e)
        {
            foreach (Trail tr in c.Trails)
            {
                if (!tr.On)
                {
                    continue;
                }
                float head = Mathf.Min(t, e.Lasts ? ghost.Duration : e.To);
                float tail = Mathf.Max(e.From, t - tr.Time);
                if (e.Frame != EffectFrame.World || e.Count < 2 || head - tail < 1e-3f)
                {
                    tr.Line.positionCount = 0;
                    continue;
                }
                int n = tr.Points.Length;
                for (int k = 0; k < n; k++)
                {
                    e.At(head - (head - tail) * k / (n - 1), out Vector3 pos, out Quaternion rot, out float scale);
                    tr.Points[k] = Matrix4x4.TRS(pos, rot, e.BaseScale * scale).MultiplyPoint3x4(tr.Offset);
                }
                tr.Line.positionCount = n;
                tr.Line.SetPositions(tr.Points);
                tr.Line.widthMultiplier = tr.Width * Mathf.Clamp01(1f - (t - head) / tr.Time);
            }
        }

        // Where a copy hangs: in the world (on its own layers, if the camera shows them), on the replay's camera, or
        // in the world on the replay's layer (a muzzle flash seen from outside: walls hide it, as they hide the ghost).
        // On the camera, its own layers also if another camera draws them: the HUD camera draws the gun over the
        // world, and through the eyes it's on for the viewmodel (FirstPersonView), so a flash on its layers is over
        // the gun as it was in the game.
        private void Place(Copy c, int mode)
        {
            if (c.Mode == mode)
            {
                return;
            }
            c.Mode = mode;
            c.T.SetParent(mode == 1 ? cam.transform : root, false);
            int mask = cam.cullingMask;
            if (mode == 1 || mode == 3)
            {
                foreach (Camera other in Camera.allCameras)
                {
                    mask |= other != null ? other.cullingMask : 0;
                }
            }
            for (int i = 0; i < c.Layered.Length; i++)
            {
                if (c.Layered[i] != null)
                {
                    int own = c.Layers[i];
                    c.Layered[i].gameObject.layer = mode == 2 || (mask & (1 << own)) == 0 ? layer : own;
                }
            }
        }

        // ---------------------------------------------------------------- prefabs and copies

        private GameObject Found(int prefab)
        {
            if (prefab < 0 || prefab >= world.FoundPrefabs.Length)
            {
                return null;
            }
            if (!world.PrefabsLooked[prefab] || (world.FoundPrefabs[prefab] == null && !ReferenceEquals(world.FoundPrefabs[prefab], null)))
            {
                world.PrefabsLooked[prefab] = true;
                world.FoundPrefabs[prefab] = library.Get(world.EffectPrefabs[prefab]);
            }
            return world.FoundPrefabs[prefab];
        }

        // A thing's prefab: the one it was caught being made from, else (a projectile only) one of its name with its
        // mesh. A bit of debris only if it has particles or a trail (the rest are fine as their meshes).
        private GameObject ForThing(ThingPath p)
        {
            int look = p.Look;
            if (look < 0 || look >= world.Looks.Length)
            {
                return null;
            }
            if (!world.LooksLooked[look])
            {
                world.LooksLooked[look] = true;
                int prefab = look < world.LookPrefab.Length ? world.LookPrefab[look] : -1;
                GameObject source = prefab >= 0 ? Found(prefab) : p.Kind == ThingKind.Projectile ? library.ForLook(world.Looks[look], world.LookKeys[look]) : null;
                if (source != null && p.Kind == ThingKind.Debris && !Lively(source))
                {
                    source = null;
                }
                world.FoundForLooks[look] = source;
            }
            return world.FoundForLooks[look];
        }

        private bool Lively(GameObject source)
        {
            if (facts.TryGetValue(source, out Facts f))
            {
                return f.Lively;
            }
            return source.GetComponentInChildren<ParticleSystem>(true) != null || source.GetComponentInChildren<TrailRenderer>(true) != null;
        }

        // A copy of source: one put away before, else a new one (if this frame has any makes left).
        private Copy Take(GameObject source)
        {
            if (pool.TryGetValue(source, out Stack<Copy> spare))
            {
                while (spare.Count > 0)
                {
                    Copy c = spare.Pop();
                    if (c.Go != null)
                    {
                        reused++;
                        return c;
                    }
                }
            }
            if (broken.Contains(source))
            {
                return null;
            }
            if (makes <= 0)
            {
                notMade++;
                return null;
            }
            makes--;
            return Make(source);
        }

        private void Release(Copy c)
        {
            if (c.Go == null)
            {
                return;
            }
            c.Go.SetActive(false);
            if (c.Mode != 0)
            {
                c.T.SetParent(root, false);
                c.Mode = -1;
            }
            c.Path = null;
            if (!pool.TryGetValue(c.Source, out Stack<Copy> spare))
            {
                pool[c.Source] = spare = new Stack<Copy>();
            }
            if (spare.Count >= PooledPerPrefab)
            {
                Object.Destroy(c.Go);
                made.Remove(c);
                return;
            }
            spare.Push(c);
        }

        private void ReleaseAll()
        {
            foreach (Copy c in shown.Values)
            {
                Release(c);
            }
            foreach (Copy c in things.Values)
            {
                Release(c);
            }
            foreach (Copy c in beams.Values)
            {
                Release(c);
            }
            shown.Clear();
            things.Clear();
            beams.Clear();
            live.Clear();
            cursor = 0;
        }

        // Ready to show from the start of its life (e: the effect it's for, for its tint).
        private void Assign(Copy c, EffectPath e)
        {
            if (!c.Go.activeSelf)
            {
                c.Go.SetActive(true);
            }
            foreach (Boom b in c.Booms)
            {
                if (b.T != null && !b.Root)
                {
                    b.T.gameObject.SetActive(b.On);
                    b.T.localScale = b.Scale0;
                }
            }
            foreach (Fade f in c.Fades)
            {
                if (f.T != null && !f.Root)
                {
                    f.T.gameObject.SetActive(f.On);
                    f.T.localScale = f.Scale0;
                }
            }
            for (int i = 0; i < c.Systems.Length; i++)
            {
                if (c.Systems[i] != null)
                {
                    ParticleSystem.MainModule main = c.Systems[i].main;
                    main.startColor = c.Colors[i];
                }
            }
            for (int i = 0; i < c.TintableSprites.Count; i++)
            {
                if (c.TintableSprites[i] != null)
                {
                    c.TintableSprites[i].color = c.SpriteColors[i];
                }
            }
            if (e != null && e.Tinted)
            {
                // (SceneHelper.SetParticlesColors: the floor's colour, each keeping its own alpha)
                foreach (ParticleSystem ps in c.Tintable)
                {
                    if (ps != null)
                    {
                        ParticleSystem.MainModule main = ps.main;
                        main.startColor = new Color(e.Tint.r, e.Tint.g, e.Tint.b, main.startColor.color.a);
                    }
                }
                foreach (SpriteRenderer s in c.TintableSprites)
                {
                    if (s != null)
                    {
                        s.color = new Color(e.Tint.r, e.Tint.g, e.Tint.b, s.color.a);
                    }
                }
            }
            foreach (Trail tr in c.Trails)
            {
                tr.Line.positionCount = 0;
            }
            // (on or off as it was made: through the eyes, the first person view hides what's on the gun's layers
            // under the camera as it comes on, a copy hanging there included)
            for (int i = 0; i < c.Renderers.Length; i++)
            {
                if (c.Renderers[i] != null && c.Renderers[i].enabled != c.RenderersOn[i])
                {
                    c.Renderers[i].enabled = c.RenderersOn[i];
                }
            }
            c.Simulated = -1f;
            c.Emitting = true;
            c.Seen = frame;
            c.GoneAt = -1f;
            c.Anchored = false;
            c.AnchorFrame = int.MaxValue;
        }

        private Copy Make(GameObject source)
        {
            var c = new Copy { Source = source };
            GameObject go;
            try
            {
                go = Puppet.MakeEffect(source, root, copy => Read(c, copy));
            }
            catch (Exception e)
            {
                // (one prefab that won't copy: shown without it, the rest still are)
                Plugin.Log.LogWarning($"couldn't copy {source.name} for the replay's effects: {e.Message}");
                broken.Add(source);
                return null;
            }
            if (go == null)
            {
                broken.Add(source);
                return null;
            }
            c.Go = go;
            c.T = go.transform;
            c.Scale0 = c.T.localScale;
            var faded = new HashSet<Light>();
            foreach (Boom b in c.Booms)
            {
                if (b.Light != null)
                {
                    faded.Add(b.Light);
                }
            }
            foreach (Fade f in c.Fades)
            {
                if (f.Light != null)
                {
                    faded.Add(f.Light);
                }
            }
            if (c.BeamLight != null)
            {
                faded.Add(c.BeamLight);
            }
            c.Systems = go.GetComponentsInChildren<ParticleSystem>(true);
            var roots = new List<ParticleSystem>();
            c.Emits = new bool[c.Systems.Length];
            c.Colors = new ParticleSystem.MinMaxGradient[c.Systems.Length];
            for (int i = 0; i < c.Systems.Length; i++)
            {
                ParticleSystem ps = c.Systems[i];
                c.Emits[i] = ps.emission.enabled;
                c.Colors[i] = ps.main.startColor;
                Transform above = ps.transform.parent;
                if (above == null || above.GetComponentInParent<ParticleSystem>(true) == null)
                {
                    roots.Add(ps);
                }
            }
            c.Roots = roots.ToArray();
            var lights = new List<Light>();
            foreach (Light l in go.GetComponentsInChildren<Light>(true))
            {
                if (!faded.Contains(l))
                {
                    lights.Add(l);
                }
            }
            c.Lights = lights.ToArray();
            c.Intensity = new float[c.Lights.Length];
            for (int i = 0; i < c.Lights.Length; i++)
            {
                c.Intensity[i] = c.Lights[i].intensity;
            }
            c.Animators = go.GetComponentsInChildren<Animator>(true);
            c.Layered = go.GetComponentsInChildren<Transform>(true);
            c.Layers = new int[c.Layered.Length];
            for (int i = 0; i < c.Layered.Length; i++)
            {
                c.Layers[i] = c.Layered[i].gameObject.layer;
            }
            c.Renderers = go.GetComponentsInChildren<Renderer>(true);
            c.RenderersOn = new bool[c.Renderers.Length];
            for (int i = 0; i < c.Renderers.Length; i++)
            {
                c.RenderersOn[i] = c.Renderers[i].enabled;
            }
            // (what's showing once it's made: an explosion's sphere is ThingPlayback's to draw)
            bool onlyParticles = c.Systems.Length > 0;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(false))
            {
                bool trail = false;
                foreach (Trail tr in c.Trails)
                {
                    trail |= tr.Line == r;
                }
                if (r.enabled && !(r is ParticleSystemRenderer) && !trail && !(c.Beam != null && r == c.Beam))
                {
                    onlyParticles = false;
                }
            }
            if (!facts.TryGetValue(source, out Facts known))
            {
                facts[source] = known = new Facts
                {
                    OnlyParticles = onlyParticles,
                    Lively = c.Systems.Length > 0 || c.Trails.Count > 0,
                    Beam = c.Beam != null,
                };
            }
            c.Facts = known;
            made.Add(c);
            copiesMade++;
            return c;
        }

        // The copy before it's stripped: what the game's scripts on it would have done, to do it from its age.
        private static void Read(Copy c, GameObject copy)
        {
            var prefs = Game.Get<PrefsManager>();
            bool simple = prefs != null && prefs.GetBoolLocal("simpleExplosions");
            foreach (ExplosionController ec in copy.GetComponentsInChildren<ExplosionController>(true))
            {
                // (its fancy parts come on as it starts, unless the game's set to simple explosions or fire)
                string pref = ec.playerPref == "SimFir" ? "simpleFire" : ec.playerPref == "SimExp" ? "simpleExplosions" : ec.playerPref;
                bool plain = ec.forceSimple || (prefs != null && !string.IsNullOrEmpty(pref) && prefs.GetBoolLocal(pref));
                if (!plain && ec.toActivate != null)
                {
                    foreach (GameObject part in ec.toActivate)
                    {
                        if (part != null)
                        {
                            part.SetActive(true);
                        }
                    }
                }
            }
            foreach (Explosion e in copy.GetComponentsInChildren<Explosion>(true))
            {
                var b = new Boom
                {
                    T = e.transform,
                    Root = e.transform == copy.transform,
                    On = e.gameObject.activeSelf,
                    Scale0 = e.transform.localScale,
                    Speed = (e.speed == 0f ? 1f : e.speed) * Explosion.globalSizeMulti,
                    MaxSize = e.maxSize * Explosion.globalSizeMulti,
                    Alpha0 = 1f,
                };
                SphereCollider sphere = e.GetComponent<SphereCollider>();
                b.Radius = sphere != null && sphere.radius > 1e-4f ? sphere.radius : 0.5f;
                MeshRenderer mr = e.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Material m = mr.sharedMaterial;
                    b.Alpha0 = m != null && m.HasProperty("_Color") ? m.color.a : 1f;
                    mr.enabled = false;  // (its sphere: ThingPlayback draws that, see-through)
                }
                if (!e.lowQuality && !simple)
                {
                    b.Light = e.GetComponentInChildren<Light>(true);
                    if (b.Light != null)
                    {
                        b.Range0 = b.Light.range;
                        b.Intensity0 = b.Light.intensity;
                    }
                }
                c.Booms.Add(b);
            }
            foreach (ScaleNFade s in copy.GetComponentsInChildren<ScaleNFade>(true))
            {
                var f = new Fade
                {
                    T = s.transform,
                    Root = s.transform == copy.transform,
                    On = s.gameObject.activeSelf,
                    Scale0 = s.transform.localScale,
                    Scales = s.scale,
                    ScaleSpeed = s.scaleSpeed,
                    Fades = s.fade,
                    FadeSpeed = s.fadeSpeed,
                    Kind = s.ft,
                    Stays = s.dontDestroyOnZero,
                    Clamp = s.clampFade,
                    Min = s.clampMinimum,
                    Max = s.clampMaximum,
                    Intensity = s.lightUseIntensityInsteadOfRange,
                    ToBlack = s.fadeToBlack,
                };
                switch (s.ft)
                {
                    case FadeType.Sprite:
                        f.Sprite = s.GetComponent<SpriteRenderer>();
                        f.Color0 = f.Sprite != null ? f.Sprite.color : Color.white;
                        break;
                    case FadeType.Light:
                        f.Light = s.GetComponent<Light>();
                        f.Light0 = f.Light == null ? 0f : f.Intensity ? f.Light.intensity : f.Light.range;
                        break;
                    case FadeType.Line:
                        f.Line = s.GetComponent<LineRenderer>();
                        f.Color0 = f.Line != null ? f.Line.startColor : Color.white;
                        f.End0 = f.Line != null ? f.Line.endColor : Color.white;
                        break;
                    case FadeType.Renderer:
                        f.Renderer = s.GetComponent<Renderer>();
                        if (f.Renderer == null)
                        {
                            f.Renderer = s.GetComponentInChildren<Renderer>(true);
                        }
                        Material m = f.Renderer != null ? f.Renderer.sharedMaterial : null;
                        if (m != null && m.HasProperty("_OpacScale"))
                        {
                            f.Opacity = true;
                            f.Property = Shader.PropertyToID("_OpacScale");
                            f.Value0 = m.GetFloat(f.Property);
                        }
                        else if (m != null && (m.HasProperty("_Tint") || m.HasProperty("_Color")))
                        {
                            f.Property = Shader.PropertyToID(m.HasProperty("_Tint") ? "_Tint" : "_Color");
                            f.Color0 = m.GetColor(f.Property);
                        }
                        else
                        {
                            f.Fades = false;
                        }
                        break;
                    default:
                        f.Fades = false;
                        break;
                }
                c.Fades.Add(f);
            }
            foreach (Spin s in copy.GetComponentsInChildren<Spin>(true))
            {
                // (the copy itself spinning: that's in its recorded turns)
                if (!s.off && s.speed != 0f && s.spinDirection != Vector3.zero && s.transform != copy.transform)
                {
                    c.Spins.Add(new Spinner { T = s.transform, Rot0 = s.transform.localRotation, Axis = s.spinDirection, Speed = s.speed });
                }
            }
            foreach (AlwaysLookAtCamera a in copy.GetComponentsInChildren<AlwaysLookAtCamera>(true))
            {
                c.Facers.Add(new Facer
                {
                    T = a.transform,
                    Screen = a.faceScreenInsteadOfCamera && a.useXAxis && a.useYAxis && a.useZAxis,
                    X = a.useXAxis,
                    Y = a.useYAxis,
                    Z = a.useZAxis,
                    Offset = a.rotationOffset,
                });
            }
            RevolverBeam beam = copy.GetComponent<RevolverBeam>();
            if (beam != null)
            {
                c.Beam = copy.GetComponent<LineRenderer>();
                c.BeamWidth = c.Beam != null ? c.Beam.widthMultiplier : 0f;
                c.BeamLight = copy.GetComponent<Light>();
                c.BeamIntensity = c.BeamLight != null ? c.BeamLight.intensity : 0f;
                Transform flash = copy.transform.childCount > 0 ? copy.transform.GetChild(0) : null;
                if (flash != null && beam.noMuzzleflash)
                {
                    flash.gameObject.SetActive(false);
                    flash = null;
                }
                c.BeamFlash = flash;
            }
            foreach (EnviroGibModifier gib in copy.GetComponentsInChildren<EnviroGibModifier>(true))
            {
                if (gib.particleSystem != null)
                {
                    c.Tintable.Add(gib.particleSystem);
                }
                if (gib.spriteRenderer != null)
                {
                    c.TintableSprites.Add(gib.spriteRenderer);
                    c.SpriteColors.Add(gib.spriteRenderer.color);
                }
            }
            Transform top = copy.transform;
            Matrix4x4 toCopy = top.worldToLocalMatrix;
            foreach (TrailRenderer tr in copy.GetComponentsInChildren<TrailRenderer>(true))
            {
                c.Trails.Add(TrailOf(tr, toCopy));
            }
        }

        // A trail stands still while the level's clock does (its points never get old): a line in its place, drawn
        // along the path the copy is known to have taken.
        private static Trail TrailOf(TrailRenderer tr, Matrix4x4 toCopy)
        {
            var go = new GameObject("trail (replay)") { layer = tr.gameObject.layer };
            go.transform.SetParent(tr.transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterials = tr.sharedMaterials;
            line.widthCurve = tr.widthCurve;
            line.widthMultiplier = tr.widthMultiplier;
            line.colorGradient = tr.colorGradient;
            line.numCornerVertices = tr.numCornerVertices;
            line.numCapVertices = tr.numCapVertices;
            line.alignment = tr.alignment;
            line.textureMode = tr.textureMode;
            line.sortingLayerID = tr.sortingLayerID;
            line.sortingOrder = tr.sortingOrder;
            line.useWorldSpace = true;
            line.positionCount = 0;
            bool on = tr.enabled && tr.emitting;
            line.enabled = on;
            tr.enabled = false;
            tr.emitting = false;
            float time = Mathf.Clamp(tr.time, 0.01f, 10f);
            return new Trail
            {
                Line = line,
                Offset = toCopy.MultiplyPoint3x4(tr.transform.position),
                Time = time,
                Width = tr.widthMultiplier,
                On = on,
                Points = new Vector3[Mathf.Clamp(Mathf.CeilToInt(time * 30f) + 1, 2, MaxTrailPoints)],
            };
        }
    }

    // The game's prefabs that are loaded now (assets, not copies in the level), by name, to copy for a replay's
    // effects and projectiles; and the level's own objects by path, for what was copied from the level. Same-named
    // prefabs are told apart by how many objects they have (or, for a thing, by its mesh).
    internal sealed class PrefabLibrary
    {
        private Dictionary<string, List<GameObject>> assets;
        private readonly Dictionary<string, GameObject> found = new Dictionary<string, GameObject>();
        private readonly Dictionary<GameObject, int> parts = new Dictionary<GameObject, int>();

        public int Kinds { get; private set; }
        public int Missing { get; private set; }
        public List<string> MissingNames { get; } = new List<string>();
        public bool Asked => assets != null || found.Count > 0;

        // Every prefab the game has loaded, by name (once).
        public void Index()
        {
            if (assets != null)
            {
                return;
            }
            float started = Time.realtimeSinceStartup;
            assets = new Dictionary<string, List<GameObject>>();
            int count = 0;
            try
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.transform.parent != null || go.scene.IsValid())
                    {
                        continue;
                    }
                    string name = go.name;
                    if (name.StartsWith("UltraAttempts", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!assets.TryGetValue(name, out List<GameObject> list))
                    {
                        assets[name] = list = new List<GameObject>(1);
                    }
                    list.Add(go);
                    count++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("listing the game's prefabs (the replay's effects won't be the game's own): " + e.Message);
            }
            Plugin.Log.LogInfo($"replay effects: {count} of the game's prefabs loaded to copy ({assets.Count} names), {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        // What an effect was a copy of: the level object at its path, if it was one; else the prefab of its name.
        public GameObject Get(EffectPrefab p)
        {
            string key = (p.Name ?? "") + "|" + (p.Path ?? "") + "|" + p.Parts;
            if (found.TryGetValue(key, out GameObject go) && (go != null || ReferenceEquals(go, null)))
            {
                return go;
            }
            Index();
            go = null;
            if (!string.IsNullOrEmpty(p.Path))
            {
                try
                {
                    Transform t = Game.FindByPath(p.Path);
                    go = t != null ? t.gameObject : null;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"finding {p.Path}: {e.Message}");
                }
            }
            if (go == null)
            {
                go = Named(p.Name, p.Parts, null);
            }
            Count(key, go, p.Name);
            return go;
        }

        // A projectile's prefab from its look (recorded before projectiles kept theirs): of its name, with a
        // rigidbody, and with the mesh it was drawn with.
        public GameObject ForLook(ThingLook look, string signature)
        {
            if (look == null || string.IsNullOrEmpty(look.Name))
            {
                return null;
            }
            string key = "look|" + signature;
            if (found.TryGetValue(key, out GameObject go) && (go != null || ReferenceEquals(go, null)))
            {
                return go;
            }
            Index();
            string mesh = look.Parts.Count > 0 ? look.Parts[0].Mesh : null;
            go = Named(look.Name, -1, candidate => candidate.GetComponent<Rigidbody>() != null && (string.IsNullOrEmpty(mesh) || HasMesh(candidate, mesh)));
            Count(key, go, look.Name);
            return go;
        }

        private void Count(string key, GameObject go, string name)
        {
            found[key] = go;
            if (go != null)
            {
                Kinds++;
                return;
            }
            Missing++;
            if (MissingNames.Count < 8 && !string.IsNullOrEmpty(name) && !MissingNames.Contains(name))
            {
                MissingNames.Add(name);
            }
        }

        private GameObject Named(string name, int wantParts, Func<GameObject, bool> fits)
        {
            if (assets == null || string.IsNullOrEmpty(name) || !assets.TryGetValue(name, out List<GameObject> list))
            {
                return null;
            }
            GameObject any = null;
            foreach (GameObject go in list)
            {
                if (go == null || (fits != null && !fits(go)))
                {
                    continue;
                }
                if (wantParts < 0 || list.Count == 1 || PartsOf(go) == wantParts)
                {
                    return go;
                }
                any = any ?? go;
            }
            return any;
        }

        private int PartsOf(GameObject go)
        {
            if (!parts.TryGetValue(go, out int n))
            {
                parts[go] = n = go.GetComponentsInChildren<Transform>(true).Length;
            }
            return n;
        }

        private static bool HasMesh(GameObject go, string mesh)
        {
            foreach (MeshFilter f in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh != null && f.sharedMesh.name == mesh)
                {
                    return true;
                }
            }
            foreach (SkinnedMeshRenderer s in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s.sharedMesh != null && s.sharedMesh.name == mesh)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
