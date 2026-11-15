using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // Through an attempt's eyes: its weapon as ULTRAKILL's own first-person viewmodel. Copies of the weapons
    // in your loadout (and the arms, and the whiplash), renderers and Animator only, sitting under the camera where
    // yours do, on their layer. Their animators play what the weapon scripts would have told them to, from what the
    // attempt recorded: drawing a weapon when it switched (the animator's first state), firing on its shots (the
    // triggers Revolver, Shotgun, ShotgunHammer, Nailgun, Railcannon and RocketLauncher set), the alt fires it held;
    // and WalkingBob's sway as it walked, the nailgun's barrels spinning. The arm it had out (copied from the game's
    // own arms, whichever it was) and the whiplash play exactly the states their animators went through when it was
    // recorded (every punch, parry, blast and coin flip; the whiplash thrown, its rope out to where it went and its
    // hook there, pulled, caught again); recordings from before that punch your arm on its punches. They run at the
    // replay's pace (stopped while it's paused).
    internal sealed class Viewmodel
    {
        private static readonly FieldInfo BobOrigin = typeof(WalkingBob).GetField("originalPos", BindingFlags.NonPublic | BindingFlags.Instance);

        private enum Kind
        {
            Other,
            Revolver,
            Shotgun,
            Hammer,
            Nailgun,
            Railcannon,
            Rockets,
        }

        private sealed class Spinner
        {
            public Transform T;
            public Vector3 Axis;
            public bool World;
            public float Speed;      // its own constant speed (degrees a second)
            public bool Barrel;      // a nailgun barrel: spins up as it fires
        }

        // One weapon (or the arm) ready to show.
        private sealed class Rig
        {
            public string Name;
            public GameObject Go;
            public Animator Anim;
            public Renderer[] Renderers;
            public Kind Kind;
            public int Variation;
            public bool AltVersion;
            public float Interval = 0.5f;   // between shots while fire's held
            public readonly List<Spinner> Spinners = new List<Spinner>();
            public readonly HashSet<string> Params = new HashSet<string>();
            public int Triggers;
            public int RestHash;            // the state it idles in (learned as it plays)

            // This attempt's state (replay seconds into the attempt).
            public float ShownAt;
            public float LastFire;
            public float LastAlt;
            public float LastCharged;
            public float AltSince;
            public bool Swinging;
            public float SwingFrom;
            public bool Sawing;
            public bool Spinning;
            public readonly Dictionary<string, float> Pending = new Dictionary<string, float>();

            public void Reset(float now)
            {
                ShownAt = now;
                LastFire = LastAlt = LastCharged = float.NegativeInfinity;
                AltSince = now;
                Swinging = false;
                Sawing = false;
                Spinning = false;
                Pending.Clear();
            }

            public void Trigger(string name, float now)
            {
                if (Anim == null)
                {
                    return;
                }
                Anim.SetTrigger(name);
                Pending[name] = now;
            }

            public void Bool(string name, bool value)
            {
                if (Anim != null && Params.Contains(name))
                {
                    Anim.SetBool(name, value);
                }
            }

            public void Float(string name, float value)
            {
                if (Anim != null && Params.Contains(name))
                {
                    Anim.SetFloat(name, value);
                }
            }

            // A state by name, from its start, as the scripts that play states rather than trigger them do (the
            // hammer, the thrown saw). (A name it hasn't got does nothing: its warnings are off.)
            public void Play(string state)
            {
                if (Anim != null)
                {
                    Anim.Play(state, -1, 0f);
                }
            }

            // Triggers the animator didn't take within a moment (it wasn't in a state that takes them) don't
            // fire late.
            public void ClearStale(float now)
            {
                if (Pending.Count == 0 || Anim == null)
                {
                    return;
                }
                List<string> stale = null;
                foreach (KeyValuePair<string, float> p in Pending)
                {
                    if (now - p.Value > 0.2f || now < p.Value)
                    {
                        (stale ?? (stale = new List<string>())).Add(p.Key);
                    }
                }
                if (stale != null)
                {
                    foreach (string name in stale)
                    {
                        Anim.ResetTrigger(name);
                        Pending.Remove(name);
                    }
                }
            }

            // (shown again: each renderer as it was on yours, some of which are off)
            public void SetVisible(bool visible)
            {
                for (int i = 0; i < Renderers.Length; i++)
                {
                    if (Renderers[i] != null)
                    {
                        Renderers[i].enabled = visible && On[i];
                    }
                }
            }

            public bool[] On;
        }

        private struct Bob
        {
            public Transform T;
            public Vector3 Origin;
        }

        // The whiplash: a copy of the HookArm (its animator, its arm's model), its rope (its own line, drawn from its
        // hand to where its end went) and its hook's head, which goes out there as the game puts yours.
        private sealed class Whip
        {
            public Rig Rig;
            public GameObject Model;        // its arm, out while it's used
            public Transform Hand;          // where the rope starts
            public Transform Hook;          // its head's bone
            public GameObject HookModel;    // its head (in the world while it's out, the HUD's in the hand)
            public int HandLayer;
            public LineRenderer Rope;
            public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
            public Vector3 ThrowHand, PullHand;

            // This frame.
            public bool Live;
            public bool Out;
            public Vector3 Point;
            public float Warp;
        }

        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");

        private readonly Dictionary<string, Rig> byName = new Dictionary<string, Rig>();
        private readonly List<Rig> rigs = new List<Rig>();
        private readonly List<Bob> bobs = new List<Bob>();
        private readonly Dictionary<Ghost, HashSet<int>> railShots = new Dictionary<Ghost, HashSet<int>>();
        private readonly Dictionary<ArmKind, Rig> arms = new Dictionary<ArmKind, Rig>();
        private ArmKind yours;              // the arm you have now (older recordings punch with it)
        private Rig arm;                    // the arm out now
        private int armSince = -1;          // (the switch it's been out since)
        private int armNext, whipNext;      // the next of the attempt's recorded moves to play
        private Whip whip;
        private Camera worldCam;
        private Rig shown;
        private bool visible = true;
        private Ghost ghost;
        private int lastSample;
        private float lastPunch;

        // WalkingBob, and the nailgun spinning up.
        private Vector3 bobAt;
        private Vector3 bobTarget = new Vector3(0.08f, -0.025f, 0f);
        private bool bobBack;
        private float nailHeat;
        private float nailSpin;

        public bool Has(string name) => name != null && byName.ContainsKey(name);

        // ---------------------------------------------------------------- making the copies

        // Your loadout's weapons, the arms and the whiplash, copied under the camera's mirror. needed: the weapons
        // the replay's attempts held (for the log: which of them aren't in your loadout); neededArms: the arms they had.
        public static Viewmodel Build(CameraMirror mirror, Camera worldCam, HashSet<string> needed, HashSet<ArmKind> neededArms, out string report)
        {
            var vm = new Viewmodel { worldCam = worldCam };
            var skipped = new List<string>();
            GunControl guns = Game.Get<GunControl>();
            var bobbers = new List<WalkingBob>();
            if (guns != null)
            {
                foreach (GameObject weapon in guns.allWeapons.Where(w => w != null).Distinct())
                {
                    Rig rig = null;
                    try
                    {
                        rig = vm.Make(mirror, weapon, bobbers);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogDebug($"viewmodel of {weapon.name}: {e}");
                    }
                    if (rig == null)
                    {
                        skipped.Add(Game.CleanName(weapon.name));
                        continue;
                    }
                    vm.rigs.Add(rig);
                    foreach (string key in Names(weapon))
                    {
                        if (!vm.byName.ContainsKey(key))
                        {
                            vm.byName[key] = rig;
                        }
                    }
                }
            }
            // (either going wrong leaves it out, not the guns)
            string armsReport, whipReport;
            try
            {
                armsReport = vm.BuildArms(mirror, neededArms, bobbers);
            }
            catch (Exception e)
            {
                armsReport = "no arms (" + e.GetBaseException().Message + ")";
                Plugin.Log.LogDebug(e);
            }
            try
            {
                whipReport = vm.BuildWhiplash(mirror);
            }
            catch (Exception e)
            {
                Camera.onPreCull -= vm.PreCull;
                vm.whip = null;
                whipReport = "no whiplash (" + e.GetBaseException().Message + ")";
                Plugin.Log.LogDebug(e);
            }
            vm.FindBobs(mirror, bobbers);

            List<string> missing = needed.Where(n => !vm.byName.ContainsKey(n)).OrderBy(n => n).ToList();
            int triggers = vm.rigs.Sum(r => r.Triggers) + vm.arms.Values.Sum(r => r.Triggers);
            int animated = vm.rigs.Count(r => r.Anim != null);
            report = $"{vm.rigs.Count} weapons ready as viewmodels ({animated} animated)"
                + (missing.Count > 0 ? $", {missing.Count} the replay needs aren't in your loadout ({string.Join(", ", missing)}: their still model stands in)" : ", every weapon the replay needs")
                + (skipped.Count > 0 ? $", couldn't copy {string.Join(", ", skipped)}" : "")
                + $", {whipReport}, {armsReport}"
                + $", {triggers} animator triggers found"
                + (vm.bobs.Count > 0 ? $", walking bob on {string.Join(", ", vm.bobs.Select(b => Game.CleanName(b.T.name).Replace(" (replay)", "")))}" : ", no walking bob found");
            return vm;
        }

        // The arms the attempts had (and yours), each from the game's own (FistControl's prefabs, the ones it makes
        // yours from), under the mirror of where yours go; yours as it is if its own can't be had.
        private string BuildArms(CameraMirror mirror, HashSet<ArmKind> needed, List<WalkingBob> bobbers)
        {
            FistControl fists = Game.Get<FistControl>();
            Transform parent = fists != null ? mirror.Of(fists.transform) : null;
            if (parent == null)
            {
                return "no arms (yours isn't under the camera)";
            }
            // (at rest: a punch that lands turns yours toward what it hit for a moment)
            parent.localRotation = Quaternion.identity;
            Punch current = fists.currentPunch;
            yours = current != null ? KindOf(current) : ArmKind.None;
            var wanted = new HashSet<ArmKind>(needed ?? new HashSet<ArmKind>()) { yours };
            wanted.Remove(ArmKind.None);
            // (FistControl's blue, red and gold arms; each loaded only if an attempt had it)
            var references = new[] { (fists.blueArm, ArmKind.Feedbacker), (fists.redArm, ArmKind.Knuckleblaster), (fists.goldArm, ArmKind.Spear) };
            foreach ((AssetReference reference, ArmKind expected) in references)
            {
                GameObject prefab = null;
                try
                {
                    if (wanted.Contains(expected) && !arms.ContainsKey(expected) && reference != null && reference.RuntimeKeyIsValid())
                    {
                        prefab = reference.ToAsset();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug("loading an arm for the viewmodel: " + e.Message);
                }
                Punch punch = prefab != null ? prefab.GetComponent<Punch>() : null;
                ArmKind kind = punch != null ? KindOf(punch) : ArmKind.None;
                if (kind == ArmKind.None || !wanted.Contains(kind) || arms.ContainsKey(kind))
                {
                    continue;
                }
                AddArm(mirror, kind, prefab, bobbers, parent);
            }
            if (yours != ArmKind.None && !arms.ContainsKey(yours))
            {
                AddArm(mirror, yours, current.gameObject, bobbers, null);
            }
            List<string> missing = wanted.Where(k => !arms.ContainsKey(k)).Select(k => k.ToString()).ToList();
            return arms.Count == 0 ? "no arms"
                : $"arms: {string.Join(", ", arms.Keys.OrderBy(k => k).Select(k => k.ToString()))} (yours: {yours})"
                    + (missing.Count > 0 ? $", couldn't copy {string.Join(", ", missing)} (yours stands in)" : "");
        }

        private void AddArm(CameraMirror mirror, ArmKind kind, GameObject source, List<WalkingBob> bobbers, Transform parent)
        {
            try
            {
                Rig rig = Make(mirror, source, bobbers, parent);
                if (rig != null)
                {
                    arms[kind] = rig;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"viewmodel of the {kind}: {e}");
            }
        }

        private static ArmKind KindOf(Punch punch) => (ArmKind)Mathf.Clamp((int)punch.type + 1, 1, 255);

        private static bool KeepWithLines(Component c) => Keep(c) || c is LineRenderer;

        // The whiplash, copied from yours (it's always there, equipped or not), with what it needs found in the copy.
        private string BuildWhiplash(CameraMirror mirror)
        {
            HookArm hook = Game.Get<HookArm>();
            if (hook == null)
            {
                return "no whiplash (there's none here)";
            }
            Rig rig;
            try
            {
                rig = Make(mirror, hook.gameObject, new List<WalkingBob>(), null, KeepWithLines);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("viewmodel of the whiplash: " + e);
                return "no whiplash (" + e.GetBaseException().Message + ")";
            }
            if (rig == null)
            {
                return "no whiplash (yours isn't under the camera)";
            }
            Transform source = hook.transform, copy = rig.Go.transform;
            GameObject model = ArmRecorder.ModelOf(hook);
            var w = new Whip
            {
                Rig = rig,
                Model = model != null ? VisualCopy.InCopy(source, copy, model.transform)?.gameObject : null,
                Hand = hook.hand != null ? VisualCopy.InCopy(source, copy, hook.hand) : null,
                Hook = hook.hook != null ? VisualCopy.InCopy(source, copy, hook.hook) : null,
                HookModel = hook.hookModel != null ? VisualCopy.InCopy(source, copy, hook.hookModel.transform)?.gameObject : null,
                Rope = rig.Go.GetComponent<LineRenderer>(),
            };
            // (its lines are drawn here: the rope from its hand, not the coiled wire it shows off when it's inspected)
            foreach (LineRenderer line in rig.Go.GetComponentsInChildren<LineRenderer>(true))
            {
                line.enabled = false;
            }
            rig.Renderers = rig.Renderers.Where(r => !(r is LineRenderer)).ToArray();
            rig.On = rig.Renderers.Select(r => r.enabled).ToArray();
            if (w.Rope != null)
            {
                w.Rope.useWorldSpace = true;
                w.Rope.shadowCastingMode = ShadowCastingMode.Off;
            }
            w.HandLayer = w.Model != null ? w.Model.layer : (w.HookModel != null ? w.HookModel.layer : 13);
            if (w.Model != null)
            {
                w.Model.SetActive(false);
            }
            // Where the rope leaves its hand, thrown and pulling (HookArm's, for your field of view).
            CameraController cc = Game.Get<CameraController>();
            float fov = cc != null ? cc.defaultFov : 105f;
            w.ThrowHand = new Vector3(0.09f, -0.051f, 0.045f);
            w.PullHand = new Vector3(-0.015f, 0.071f, 0.04f);
            if (fov > 105f)
            {
                w.ThrowHand += new Vector3(0.225f * ((fov - 105f) / 55f), -0.25f * ((fov - 105f) / 55f), 0.05f * ((fov - 105f) / 55f));
                w.PullHand += new Vector3(0.25f * ((fov - 105f) / 55f), 0f, 0.05f * ((fov - 105f) / 60f));
            }
            else if (fov < 105f)
            {
                w.PullHand -= new Vector3(0.05f * ((105f - fov) / 60f), 0.075f * ((105f - fov) / 60f), 0.125f * ((105f - fov) / 60f));
            }
            whip = w;
            var late = mirror.Root.gameObject.AddComponent<GhostFrameHook>();
            late.Late = Late;
            Camera.onPreCull += PreCull;
            var parts = new List<string>
            {
                w.Model != null ? "its arm" : "no arm",
                w.Rope != null ? "rope" : "no rope",
                w.Hook != null && w.HookModel != null ? "hook" : "no hook",
            };
            return $"the whiplash ready ({string.Join(", ", parts)}{(rig.Anim != null ? ", animated" : "")})";
        }

        // The names a recording may know a weapon by: its descriptor's, else its object's (as Recorder keeps them).
        private static IEnumerable<string> Names(GameObject weapon)
        {
            WeaponIcon icon = weapon.GetComponent<WeaponIcon>();
            string named = icon != null && icon.weaponDescriptor != null ? icon.weaponDescriptor.weaponName : null;
            if (!string.IsNullOrEmpty(named))
            {
                yield return named;
            }
            yield return Game.CleanName(weapon.name);
        }

        private static bool Keep(Component c) =>
            c is Animator || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer;

        // A copy of source (a weapon, an arm, the whiplash) under the mirror of its parent (or under parent: an arm
        // copied from the game's prefab, which has none).
        private Rig Make(CameraMirror mirror, GameObject source, List<WalkingBob> bobbers, Transform parentOverride = null, Func<Component, bool> keep = null)
        {
            Transform parent = parentOverride != null ? parentOverride : mirror.Of(source.transform.parent);
            if (parent == null)
            {
                return null;  // (not under the camera)
            }
            Renderer[] fromRenderers = source.GetComponentsInChildren<Renderer>(true);
            var blocks = new MaterialPropertyBlock[fromRenderers.Length];
            for (int i = 0; i < fromRenderers.Length; i++)
            {
                // (the variant's colour and the like: copies don't get them)
                if (fromRenderers[i] != null && fromRenderers[i].HasPropertyBlock())
                {
                    blocks[i] = new MaterialPropertyBlock();
                    fromRenderers[i].GetPropertyBlock(blocks[i]);
                }
            }
            var spinners = new List<(int[] path, Spin spin)>();
            foreach (Spin spin in source.GetComponentsInChildren<Spin>(true))
            {
                spinners.Add((VisualCopy.PathOf(source.transform, spin.transform), spin));
            }
            foreach (WalkingBob bob in source.GetComponentsInParent<WalkingBob>(true).Concat(source.GetComponentsInChildren<WalkingBob>(true)))
            {
                if (!bobbers.Contains(bob))
                {
                    bobbers.Add(bob);
                }
            }

            GameObject copy = VisualCopy.Make(source, parent, keep ?? Keep, c =>
            {
                Renderer[] to = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < to.Length && i < blocks.Length; i++)
                {
                    if (blocks[i] != null)
                    {
                        to[i].SetPropertyBlock(blocks[i]);
                    }
                }
            });
            var rig = new Rig
            {
                Name = Game.CleanName(source.name),
                Go = copy,
                Anim = copy.GetComponentInChildren<Animator>(true),
                Renderers = copy.GetComponentsInChildren<Renderer>(true),
            };
            rig.On = rig.Renderers.Select(r => r.enabled).ToArray();
            // (where the weapon rests: its own scripts shake it about while charging)
            WeaponPos pos = source.GetComponent<WeaponPos>();
            if (pos != null && pos.currentDefault != Vector3.zero)
            {
                copy.transform.localPosition = pos.currentDefault;
            }
            foreach (Renderer r in rig.Renderers)
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                }
            }
            Classify(rig, source);
            foreach ((int[] path, Spin spin) in spinners)
            {
                Transform t = VisualCopy.Follow(copy.transform, path);
                if (t != null && spin != null)
                {
                    rig.Spinners.Add(new Spinner
                    {
                        T = t,
                        Axis = spin.spinDirection,
                        World = spin.notRelative,
                        Speed = spin.off ? 0f : spin.speed,
                        Barrel = rig.Kind == Kind.Nailgun,
                    });
                }
            }
            if (rig.Anim != null)
            {
                rig.Anim.applyRootMotion = false;
                rig.Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                rig.Anim.updateMode = AnimatorUpdateMode.UnscaledTime;  // (the level is frozen during a replay)
                rig.Anim.fireEvents = false;   // (the scripts its animation events call were stripped)
                rig.Anim.logWarnings = false;
                rig.Anim.keepAnimatorStateOnDisable = false;  // (shown again: it draws the weapon again)
                // Its parameters, read while it's awake for a moment (the same frame: nothing's drawn).
                copy.SetActive(true);
                try
                {
                    foreach (AnimatorControllerParameter p in rig.Anim.parameters)
                    {
                        rig.Params.Add(p.name);
                        if (p.type == AnimatorControllerParameterType.Trigger)
                        {
                            rig.Triggers++;
                        }
                    }
                }
                finally
                {
                    copy.SetActive(false);
                }
            }
            sources[rig] = source;
            return rig;
        }

        // Which weapon script it had (it's gone from the copy), and its variant.
        private static void Classify(Rig rig, GameObject source)
        {
            if (Find<Revolver>(source) is Revolver revolver)
            {
                rig.Kind = Kind.Revolver;
                rig.Variation = revolver.gunVariation;
                rig.AltVersion = revolver.altVersion;
            }
            else if (Find<ShotgunHammer>(source) is ShotgunHammer hammer)
            {
                rig.Kind = Kind.Hammer;
                rig.Variation = hammer.variation;
            }
            else if (Find<Shotgun>(source) is Shotgun shotgun)
            {
                rig.Kind = Kind.Shotgun;
                rig.Variation = shotgun.variation;
            }
            else if (Find<Nailgun>(source) is Nailgun nailgun)
            {
                rig.Kind = Kind.Nailgun;
                rig.Variation = nailgun.variation;
                rig.AltVersion = nailgun.altVersion;
                // (a nail every fireRate hundredths of a second while it's held)
                rig.Interval = nailgun.fireRate > 0f ? Mathf.Clamp(nailgun.fireRate / 100f, 0.02f, 0.25f) : 0.04f;
            }
            else if (Find<Railcannon>(source) is Railcannon rail)
            {
                rig.Kind = Kind.Railcannon;
                rig.Variation = rail.variation;
            }
            else if (Find<RocketLauncher>(source) is RocketLauncher rockets)
            {
                rig.Kind = Kind.Rockets;
                rig.Variation = rockets.variation;
                rig.Interval = rockets.rateOfFire > 0f ? Mathf.Clamp(rockets.rateOfFire, 0.1f, 3f) : 1f;
            }
        }

        private static T Find<T>(GameObject go) where T : Component
        {
            T found = go.GetComponent<T>();
            return found != null ? found : go.GetComponentInChildren<T>(true);
        }

        // WalkingBob's places: a parent of the weapons (mirrored), or inside a weapon (in each copy).
        private void FindBobs(CameraMirror mirror, List<WalkingBob> bobbers)
        {
            foreach (WalkingBob bob in bobbers)
            {
                if (bob == null)
                {
                    continue;
                }
                Vector3 origin = BobOrigin != null && BobOrigin.GetValue(bob) is Vector3 o && o != Vector3.zero ? o : bob.transform.localPosition;
                bool inside = false;
                foreach (Rig rig in rigs.Concat(arms.Values))
                {
                    // (inside a weapon: the copy's own part, at the same place in it)
                    Transform source = Source(rig, bob.transform, out Transform copied);
                    if (source != null && copied != null)
                    {
                        bobs.Add(new Bob { T = copied, Origin = origin });
                        inside = true;
                    }
                }
                if (!inside)
                {
                    Transform t = mirror.Of(bob.transform);
                    if (t != null && t != mirror.Root)  // (never the camera itself)
                    {
                        bobs.Add(new Bob { T = t, Origin = origin });
                    }
                }
            }
        }

        private readonly Dictionary<Rig, GameObject> sources = new Dictionary<Rig, GameObject>();

        private Transform Source(Rig rig, Transform part, out Transform copied)
        {
            copied = null;
            if (!sources.TryGetValue(rig, out GameObject source) || source == null || !part.IsChildOf(source.transform))
            {
                return null;
            }
            copied = VisualCopy.InCopy(source.transform, rig.Go.transform, part);
            return part;
        }

        // ---------------------------------------------------------------- every frame

        // Seen through g's eyes, t seconds in. dt: replay seconds since the last frame; fresh: a cut, a seek or
        // just shown (it's put as it was at that moment, not animated there). show: draw it (else only keep up).
        public void Frame(Ghost g, float t, float dt, bool fresh, bool show)
        {
            int now = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(t, g.Duration) * g.Rate), 0, g.Count - 1);
            float tNow = now / (float)g.Rate;
            if (fresh || g != ghost)
            {
                Settle(g, now);
            }
            else
            {
                // (before this frame's: a trigger always gets a frame of the animator to be taken)
                shown?.ClearStale(tNow);
                arm?.ClearStale(tNow);
                for (int k = lastSample + 1; k <= now; k++)
                {
                    Step(g, k);
                }
            }
            ghost = g;
            lastSample = now;

            // The animators run on real time; this keeps them at the replay's pace (stopped while paused).
            float pace = fresh ? 1f : Mathf.Clamp(dt / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0f, 8f);
            Tick(shown, g, now, tNow, pace);
            Tick(arm, g, now, tNow, pace);
            Tick(whip?.Rig, g, now, tNow, pace);
            if (visible != show)
            {
                visible = show;
                shown?.SetVisible(show);
                arm?.SetVisible(show);
                whip?.Rig.SetVisible(show);
            }
            Sway(g, now, dt);
            Spin(g, now, dt);
            WhipFrame(g, t, show);
        }

        // Where the whiplash's end is this frame (its rope and head are put there after the animators: Late, PreCull).
        private void WhipFrame(Ghost g, float t, bool show)
        {
            if (whip == null)
            {
                return;
            }
            whip.Live = whip.Rig.Go.activeSelf;
            HookPhase phase = HookPhase.InHand;
            float ago = 0f;
            whip.Out = whip.Live && g.HookAt(Mathf.Min(t, g.Duration), out whip.Point, out phase, out ago);
            if (!whip.Out)
            {
                phase = HookPhase.InHand;
            }
            // (thrown, the rope snakes for a moment: HookArm's throwWarp)
            whip.Warp = phase == HookPhase.Throwing || phase == HookPhase.Caught ? Mathf.Max(0f, 1f - 6.5f * ago) : 0f;
            if (whip.Hand != null && whip.Out)
            {
                whip.Hand.localPosition = phase == HookPhase.Throwing || phase == HookPhase.Caught ? whip.ThrowHand : whip.PullHand;
            }
            if (whip.Rope != null)
            {
                whip.Rope.enabled = show && whip.Out;
            }
        }

        // After the animators: its head out where its end is, in the world (the game puts it on a layer the main
        // camera draws, behind walls and all); back in its hand on the HUD's.
        private void Late()
        {
            Whip w = whip;
            if (w == null || !w.Live || w.Rig.Go == null)
            {
                return;
            }
            try
            {
                if (w.HookModel != null)
                {
                    w.HookModel.layer = w.Out ? 2 : w.HandLayer;
                }
                if (w.Out && w.Hook != null)
                {
                    w.Hook.position = w.Point;
                    Vector3 away = w.Point - (worldCam != null ? worldCam.transform.position : w.Rig.Go.transform.position);
                    if (away.sqrMagnitude > 1e-4f)
                    {
                        w.Hook.up = away.normalized;
                    }
                }
            }
            catch (Exception e)
            {
                WhipFailed(e);
            }
        }

        // (never into the game's own frame: the whiplash goes for the rest of this replay)
        private void WhipFailed(Exception e)
        {
            Camera.onPreCull -= PreCull;
            if (whip != null && whip.Rig.Go != null)
            {
                whip.Rig.Go.SetActive(false);
            }
            whip = null;
            Plugin.Log.LogError("first person: the whiplash went wrong (off for the rest of this replay): " + e);
        }

        // Its rope, from its hand to its end, as HookArm draws it, once the camera's where it's drawn from this frame.
        private void PreCull(Camera cam)
        {
            Whip w = whip;
            if (worldCam == null)
            {
                Camera.onPreCull -= PreCull;  // (the scene's gone, and the replay with it)
                return;
            }
            if (cam != worldCam || w == null || w.Rope == null || !w.Rope.enabled || w.Hand == null)
            {
                return;
            }
            try
            {
                LineRenderer lr = w.Rope;
                Vector3 from = w.Hand.position, to = w.Point;
                int n = Mathf.Max(2, lr.positionCount);
                lr.positionCount = n;
                float scroll = Vector3.Distance(from, to) / 300f;
                lr.textureScale = new Vector2(scroll + 1f, 1f);
                w.Block.SetVector(MainTexST, new Vector4(1f, 1f, -scroll * 10f, 1f));
                lr.SetPropertyBlock(w.Block);
                Vector3 up = w.Rig.Go.transform.up;
                lr.SetPosition(0, from);
                for (int i = 1; i < n - 1; i++)
                {
                    lr.SetPosition(i, Vector3.Lerp(from, to, i / (float)n) + 1f / i * (i % 2 == 0 ? -3f : 3f) * w.Warp * up);
                }
                lr.SetPosition(n - 1, to);
            }
            catch (Exception e)
            {
                WhipFailed(e);
            }
        }

        private static void Tick(Rig rig, Ghost g, int now, float tNow, float pace)
        {
            if (rig?.Anim == null || !rig.Go.activeSelf)
            {
                return;
            }
            rig.Anim.speed = pace;
            Learn(rig, g, now, tNow);
        }

        // Off screen (out of its eyes, the menus): nothing shown, nothing running.
        public void Hide()
        {
            // (the scene may have gone, and the copies with it)
            if (shown != null && shown.Go != null)
            {
                shown.Go.SetActive(false);
            }
            shown = null;
            if (arm != null && arm.Go != null && arm.Go.activeSelf)
            {
                arm.Go.SetActive(false);
            }
            arm = null;
            if (whip != null)
            {
                whip.Live = whip.Out = false;
                if (whip.Rig.Go != null && whip.Rig.Go.activeSelf)
                {
                    whip.Rig.Go.SetActive(false);
                }
            }
            ghost = null;
        }

        public void Dispose()
        {
            // (the copies are under the camera's mirror, which goes with them)
            Camera.onPreCull -= PreCull;
            rigs.Clear();
            byName.Clear();
            sources.Clear();
            railShots.Clear();
            arms.Clear();
            arm = null;
            whip = null;
            shown = null;
        }

        private Rig RigAt(Ghost g, int sample, out Ghost.WeaponChange w)
        {
            w = g.WeaponAt(sample);
            string name = g.WeaponName(w);
            return name != null && byName.TryGetValue(name, out Rig rig) ? rig : null;
        }

        private void Show(Rig rig, float now)
        {
            if (shown == rig)
            {
                return;
            }
            if (shown != null)
            {
                shown.Go.SetActive(false);
            }
            shown = rig;
            if (rig == null)
            {
                return;
            }
            rig.Go.SetActive(true);  // (its animator starts over: the weapon being drawn)
            rig.Reset(now);
            rig.SetVisible(visible);
        }

        // As it was at sample now, without playing up to it: the held weapon drawn as long ago as it was
        // switched to, the arm at rest.
        private void Settle(Ghost g, int now)
        {
            float tNow = now / (float)g.Rate;
            Rig rig = RigAt(g, now, out Ghost.WeaponChange w);
            if (shown != null && shown != rig)
            {
                shown.Go.SetActive(false);
                shown = null;
            }
            if (rig != null)
            {
                if (!rig.Go.activeSelf)
                {
                    rig.Go.SetActive(true);
                }
                shown = rig;
                rig.Reset(tNow);
                rig.ShownAt = Mathf.Min(tNow, w.Sample / (float)g.Rate);
                rig.SetVisible(visible);
                if (rig.Anim != null)
                {
                    rig.Anim.Rebind();
                    rig.Anim.speed = 1f;
                    FastForward(rig.Anim, Mathf.Min((now - w.Sample) / (float)g.Rate, 3f));
                }
                // (an alt fire held through the cut)
                rig.AltSince = HeldSince(g, now, SampleFlags.AltFireHeld);
                bool alt = ((SampleFlags)g.Flags[now] & SampleFlags.AltFireHeld) != 0;
                Hold(rig, alt, tNow - rig.AltSince);
            }
            SettleArm(g, now, tNow);
            SettleWhip(g, now);
            lastPunch = float.NegativeInfinity;
            bobAt = Vector3.zero;
            bobBack = false;
            nailHeat = 0f;
            nailSpin = 0f;
        }

        private static void FastForward(Animator anim, float seconds)
        {
            for (float left = seconds; left > 1e-4f; left -= 0.1f)
            {
                anim.Update(Mathf.Min(0.1f, left));
            }
        }

        // When the flag (held at now) was first held, in seconds into the attempt.
        private static float HeldSince(Ghost g, int now, SampleFlags flag)
        {
            int k = now;
            int floor = Mathf.Max(0, now - g.Rate * 10);
            while (k > floor && ((SampleFlags)g.Flags[k - 1] & flag) != 0)
            {
                k--;
            }
            return k / (float)g.Rate;
        }

        // One recorded sample, played forward: a switch draws the new weapon; shots, alt fires and punches
        // set what the game's scripts would have.
        private void Step(Ghost g, int k)
        {
            float tk = k / (float)g.Rate;
            Rig rig = RigAt(g, k, out _);
            if (rig != shown)
            {
                Show(rig, tk);
            }
            var f = (SampleFlags)g.Flags[k];
            var prev = (SampleFlags)g.Flags[k - 1];
            StepArm(g, k, tk, f);
            StepWhip(g, k);
            if (rig != null)
            {
                Act(rig, g, k, f, prev, tk);
            }
        }

        // ---------------------------------------------------------------- the arms

        // The arm it had out at sample k (since: the switch to it): its own, else yours (recorded before arms were,
        // or one that couldn't be copied).
        private Rig ArmFor(Ghost g, int k, out int since)
        {
            since = 0;
            Rig mine = arms.TryGetValue(yours, out Rig r) ? r : null;
            if (!g.ArmsRecorded)
            {
                return mine;
            }
            ArmKind kind = g.ArmAt(k, out since);
            if (kind == ArmKind.None)
            {
                return null;
            }
            return arms.TryGetValue(kind, out Rig its) ? its : mine;
        }

        private void ShowArm(Rig rig, float now)
        {
            if (arm == rig)
            {
                return;
            }
            if (arm != null && arm.Go != null)
            {
                arm.Go.SetActive(false);
            }
            arm = rig;
            if (rig == null)
            {
                return;
            }
            rig.Go.SetActive(true);  // (its animator starts over, as the game's arm does when it's switched to)
            rig.Reset(now);
            rig.SetVisible(visible);
        }

        // The arm as it was at sample now: in the state its animator was last recorded going into, as far on as it
        // was by now (older recordings: at rest).
        private void SettleArm(Ghost g, int now, float tNow)
        {
            Rig rig = ArmFor(g, now, out int since);
            if (arm != null && arm != rig && arm.Go != null)
            {
                arm.Go.SetActive(false);
            }
            arm = rig;
            armSince = since;
            armNext = Ghost.FirstMoveAfter(g.ArmMoves, now);
            if (rig == null)
            {
                return;
            }
            if (!rig.Go.activeSelf)
            {
                rig.Go.SetActive(true);
            }
            rig.Reset(tNow);
            rig.SetVisible(visible);
            if (rig.Anim == null)
            {
                return;
            }
            rig.Anim.Rebind();
            rig.Anim.speed = 1f;
            int last = armNext - 1;
            if (g.ArmsRecorded && last >= 0 && g.ArmMoves[last].Sample >= since)
            {
                Apply(rig, g.ArmMoves[last], (now - g.ArmMoves[last].Sample) / (float)g.Rate);
            }
            else
            {
                FastForward(rig.Anim, 2f);
            }
        }

        private void StepArm(Ghost g, int k, float tk, SampleFlags f)
        {
            Rig rig = ArmFor(g, k, out int since);
            if (rig != arm)
            {
                ShowArm(rig, tk);
            }
            armSince = since;
            if (g.ArmsRecorded)
            {
                // (its own animations, as recorded)
                for (; armNext < g.ArmMoves.Length && g.ArmMoves[armNext].Sample <= k; armNext++)
                {
                    if (g.ArmMoves[armNext].Sample >= armSince && arm != null)
                    {
                        Apply(arm, g.ArmMoves[armNext], 0f);
                    }
                }
                return;
            }
            if ((f & SampleFlags.Punch) != 0 && arm != null && tk - lastPunch >= 0.25f)
            {
                lastPunch = tk;
                arm.Float("PunchRandomizer", UnityEngine.Random.value);
                arm.Trigger("Punch", tk);
            }
        }

        // The whiplash as it was at sample now: its arm out or not, its animator in the state it was last recorded in.
        private void SettleWhip(Ghost g, int now)
        {
            whipNext = Ghost.FirstMoveAfter(g.WhipMoves, now);
            if (whip == null)
            {
                return;
            }
            Rig rig = whip.Rig;
            if (!rig.Go.activeSelf)
            {
                rig.Go.SetActive(true);
            }
            rig.SetVisible(visible);
            int last = whipNext - 1;
            bool shownNow = g.ArmsRecorded && last >= 0 && (g.WhipMoves[last].Flags & ArmMoveFlags.Shown) != 0;
            if (whip.Model != null && whip.Model.activeSelf != shownNow)
            {
                whip.Model.SetActive(shownNow);
            }
            if (rig.Anim == null)
            {
                return;
            }
            rig.Anim.Rebind();
            rig.Anim.speed = 1f;
            if (g.ArmsRecorded && last >= 0)
            {
                Apply(rig, g.WhipMoves[last], (now - g.WhipMoves[last].Sample) / (float)g.Rate);
            }
        }

        private void StepWhip(Ghost g, int k)
        {
            if (whip == null)
            {
                return;
            }
            for (; whipNext < g.WhipMoves.Length && g.WhipMoves[whipNext].Sample <= k; whipNext++)
            {
                ArmMove m = g.WhipMoves[whipNext];
                bool shownNow = (m.Flags & ArmMoveFlags.Shown) != 0;
                if (whip.Model != null && whip.Model.activeSelf != shownNow)
                {
                    whip.Model.SetActive(shownNow);
                }
                Apply(whip.Rig, m, 0f);
            }
        }

        // A recorded move played on a copy: into the state the game's animator went into, where it was in it (ahead:
        // seconds on from then, a settle). Not if the copy's own animator has gone there already, the way the game's
        // did (back to rest after a punch), unless it's started a state over (a second punch, a jab as it hits).
        private static void Apply(Rig rig, in ArmMove move, float ahead)
        {
            Animator a = rig?.Anim;
            if (a == null || !rig.Go.activeInHierarchy)
            {
                return;
            }
            rig.Bool("Holding", (move.Flags & ArmMoveFlags.Holding) != 0);
            rig.Bool("SemiHolding", (move.Flags & ArmMoveFlags.SemiHolding) != 0);
            float time = move.Time / (float)ushort.MaxValue;
            if (ahead <= 0f)
            {
                AnimatorStateInfo at = a.IsInTransition(0) ? a.GetNextAnimatorStateInfo(0) : a.GetCurrentAnimatorStateInfo(0);
                if (at.fullPathHash == move.State && (at.loop || at.normalizedTime <= time + 0.1f))
                {
                    return;
                }
            }
            if (!a.HasState(0, move.State))
            {
                return;  // (another arm's state: one that couldn't be copied, played by yours)
            }
            if (ahead <= 0f && (move.Flags & ArmMoveFlags.Blend) != 0)
            {
                a.CrossFade(move.State, 0.15f, 0, time);  // (the game's was blending into it too)
                return;
            }
            a.Play(move.State, 0, time);
            if (ahead > 0f)
            {
                FastForward(a, Mathf.Min(ahead, 3f));
            }
        }

        private void Act(Rig r, Ghost g, int k, SampleFlags f, SampleFlags prev, float tk)
        {
            bool shot = (f & SampleFlags.Shot) != 0;
            bool fire = shot || (f & SampleFlags.FireHeld) != 0;
            bool press = shot || ((f & SampleFlags.FireHeld) != 0 && (prev & SampleFlags.FireHeld) == 0);
            bool alt = (f & SampleFlags.AltFireHeld) != 0;
            bool altBefore = (prev & SampleFlags.AltFireHeld) != 0;
            bool altPress = alt && !altBefore;
            bool altRelease = !alt && altBefore;
            if (altPress)
            {
                r.AltSince = tk;
            }
            float altHeld = alt || altRelease ? tk - r.AltSince : 0f;
            Hold(r, alt, altHeld);

            switch (r.Kind)
            {
                case Kind.Revolver:
                    if (r.Variation == 1)
                    {
                        // Marksman: the other hand tosses a coin (recorded with its arms: as its arm did).
                        if (altPress && tk - r.LastAlt >= 0.3f && arm != null && !g.ArmsRecorded)
                        {
                            r.LastAlt = tk;
                            arm.Trigger("CoinFlip", tk);
                        }
                        if (fire && tk - r.LastFire >= 0.5f)
                        {
                            Shoot(r, "Shoot", tk);
                        }
                        break;
                    }
                    // Piercer (charged by holding alt fire) and Sharpshooter (spun): let go, or fire, to shoot it.
                    float needs = r.Variation == 0 ? 0.57f : 25f / (r.AltVersion ? 750f : 75f);
                    bool charged = altHeld >= needs && (r.Variation != 0 || tk - r.LastCharged >= 2.5f);
                    if (charged && (altRelease || press))
                    {
                        r.LastCharged = tk;
                        r.AltSince = tk;
                        Shoot(r, "ChargeShoot", tk);
                    }
                    else if (fire && !alt && tk - r.LastFire >= 0.5f)
                    {
                        Shoot(r, "Shoot", tk);
                    }
                    break;

                case Kind.Shotgun:
                    if (r.Variation == 0 && altRelease && altHeld > 0.05f && Ready(r, tk, 0.3f))
                    {
                        r.LastFire = tk;
                        r.Trigger("Secondary Fire", tk);  // (the core, thrown)
                    }
                    else if (r.Variation == 1 && alt && Ready(r, tk, 0.3f))
                    {
                        r.LastFire = tk;
                        r.Trigger("Pump", tk);
                    }
                    else if (r.Variation == 2 && altRelease && altHeld > 0.1f)
                    {
                        r.LastFire = tk;
                        r.Play("FireNoReload");  // (the chainsaw, thrown)
                    }
                    else if (fire && !(r.Variation == 2 && alt) && Ready(r, tk, 0.3f))
                    {
                        r.LastFire = tk;
                        r.Trigger(r.Variation == 1 ? "PumpFire" : "Fire", tk);
                    }
                    break;

                case Kind.Hammer:
                    // The swing winds up while fire's held (half a second at least) and lands as it's let go.
                    if (press && !r.Swinging && tk - r.LastFire >= 0.3f)
                    {
                        r.Swinging = true;
                        r.SwingFrom = tk;
                    }
                    if (r.Swinging && (f & SampleFlags.FireHeld) == 0 && tk - r.SwingFrom >= 0.5f)
                    {
                        r.Swinging = false;
                        r.LastFire = tk;
                        r.Play("Fire");
                    }
                    if (r.Variation == 2)
                    {
                        if (altRelease && altHeld > 0.1f)
                        {
                            r.LastFire = tk;
                            r.Play("SawingShot");
                        }
                    }
                    else if (altPress && Ready(r, tk, 0.4f))
                    {
                        r.LastFire = tk;
                        r.Play(r.Variation == 0 ? "NadeSpawn" : "Pump");
                    }
                    break;

                case Kind.Nailgun:
                    if (fire && tk - r.LastFire >= r.Interval)
                    {
                        r.LastFire = tk;
                        r.Trigger("Shoot", tk);
                    }
                    if (altPress && tk - r.LastAlt >= 0.3f)
                    {
                        r.LastAlt = tk;
                        if (r.Variation != 0)
                        {
                            r.Trigger("Shoot", tk);  // (the magnet, the zapper)
                        }
                        else if (r.AltVersion)
                        {
                            r.Trigger("SuperShoot", tk);
                        }
                    }
                    break;

                case Kind.Railcannon:
                    // (only when charged: 16 seconds after the last shot, as WeaponCharges recharges it)
                    if (shot && RailShots(g).Contains(k))
                    {
                        r.LastFire = tk;
                        r.Trigger("Shoot", tk);
                    }
                    break;

                case Kind.Rockets:
                    if (fire && !(r.Variation == 2 && alt) && tk - r.LastFire >= r.Interval)
                    {
                        r.LastFire = tk;
                        r.Trigger("Fire", tk);
                    }
                    if (r.Variation == 1 && altRelease && altHeld >= 0.1f)
                    {
                        r.LastFire = tk;
                        r.Trigger("Fire", tk);  // (the cannonball, let go)
                    }
                    if (r.Variation == 2 && alt && tk - r.LastAlt >= 0.12f)
                    {
                        r.LastAlt = tk;
                        r.Trigger("Spray", tk);  // (the napalm)
                    }
                    break;

                default:
                    if (fire && tk - r.LastFire >= 0.5f)
                    {
                        r.LastFire = tk;
                        r.Trigger("Shoot", tk);
                        r.Trigger("Fire", tk);
                    }
                    break;
            }
        }

        private static void Shoot(Rig r, string trigger, float tk)
        {
            r.LastFire = tk;
            r.Float("RandomChance", UnityEngine.Random.value);
            r.Trigger(trigger, tk);
        }

        // What holding alt fire keeps set: the Sharpshooter spinning, a saw winding up.
        private static void Hold(Rig r, bool alt, float held)
        {
            if (r.Kind == Kind.Revolver && r.Variation == 2)
            {
                if (alt != r.Spinning)
                {
                    r.Spinning = alt;
                    r.Bool("Spinning", alt);
                }
                if (alt)
                {
                    float level = Mathf.Min(3f, Mathf.Floor(held * (r.AltVersion ? 750f : 75f) / 25f)) + 1f;
                    r.Float("TwirlSpeed", level / 3f);
                }
            }
            else if ((r.Kind == Kind.Shotgun || r.Kind == Kind.Hammer) && r.Variation == 2 && alt != r.Sawing)
            {
                r.Sawing = alt;
                r.Bool("Sawing", alt);
            }
        }

        // Whether a gun whose script waits for its animation (the shotgun's pump, the hammer) can go again: back
        // in the state it idles in. Until that state's known, a short while after the last.
        private static bool Ready(Rig r, float tk, float atLeast)
        {
            float since = tk - r.LastFire;
            if (since < atLeast || tk - r.ShownAt < 0.35f)
            {
                return false;
            }
            if (since > 2.5f || r.Anim == null)
            {
                return true;
            }
            if (r.Anim.IsInTransition(0))
            {
                return false;
            }
            return r.RestHash != 0 ? r.Anim.GetCurrentAnimatorStateInfo(0).fullPathHash == r.RestHash : since > 0.9f;
        }

        // The state it idles in: where it sits once it's been drawn and left alone a while.
        private static void Learn(Rig r, Ghost g, int now, float tNow)
        {
            if (r.Anim == null || !r.Go.activeInHierarchy || r.Anim.IsInTransition(0))
            {
                return;
            }
            var f = (SampleFlags)g.Flags[now];
            if ((f & (SampleFlags.FireHeld | SampleFlags.AltFireHeld)) != 0)
            {
                return;
            }
            if (tNow - r.ShownAt > 1.5f && tNow - r.LastFire > 1.2f && tNow - r.LastAlt > 1.2f && r.Pending.Count == 0)
            {
                r.RestHash = r.Anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            }
        }

        // The samples where the railcannon really fired: it starts charged, and takes 16 seconds to charge
        // again (WeaponCharges: 0.25 a second up to 4, then full).
        private HashSet<int> RailShots(Ghost g)
        {
            if (railShots.TryGetValue(g, out HashSet<int> shots))
            {
                return shots;
            }
            shots = new HashSet<int>();
            float readyAt = 0f;
            for (int k = 1; k < g.Count; k++)
            {
                float tk = k / (float)g.Rate;
                if (((SampleFlags)g.Flags[k] & SampleFlags.Shot) == 0 || tk < readyAt)
                {
                    continue;
                }
                Rig rig = RigAt(g, k, out _);
                if (rig != null && rig.Kind == Kind.Railcannon)
                {
                    shots.Add(k);
                    readyAt = tk + 16f;
                }
            }
            railShots[g] = shots;
            return shots;
        }

        // WalkingBob, as the game runs it: while walking on the ground, the guns sway out to one side and back,
        // then the other, faster the faster it went; still, they settle back.
        private void Sway(Ghost g, int now, float dt)
        {
            if (bobs.Count == 0)
            {
                return;
            }
            var f = (SampleFlags)g.Flags[now];
            int back = Mathf.Max(0, now - Mathf.Max(1, g.Rate / 15));
            Vector3 up = g.UpAt(now);
            Vector3 moved = g.Body[now] - g.Body[back];
            moved -= up * Vector3.Dot(moved, up);
            float speed = now > back ? moved.magnitude * g.Rate / (now - back) : 0f;
            bool walking = (f & SampleFlags.Grounded) != 0 && (f & SampleFlags.Sliding) == 0 && speed > 0.5f && speed < 200f;
            if (dt > 0f)
            {
                if (walking)
                {
                    float step = dt * (2f - bobAt.magnitude * 3f) * (Mathf.Min(speed, 15f) / 15f) * 0.25f;
                    bobAt = Vector3.MoveTowards(bobAt, bobBack ? Vector3.zero : bobTarget, Mathf.Max(0f, step));
                    if (bobAt == Vector3.zero)
                    {
                        bobBack = false;
                    }
                    else if (bobAt == bobTarget)
                    {
                        bobBack = true;
                        bobTarget = new Vector3(-bobTarget.x, bobTarget.y, bobTarget.z);
                    }
                }
                else
                {
                    bobAt = Vector3.MoveTowards(bobAt, Vector3.zero, dt);
                }
            }
            foreach (Bob b in bobs)
            {
                if (b.T != null)
                {
                    b.T.localPosition = b.Origin + bobAt;
                }
            }
        }

        // The nailgun's barrels spin up while it fires (Nailgun: 250 degrees a second, up to 2500 as it heats)
        // and wind down after; anything else that spins, at its own speed.
        private void Spin(Ghost g, int now, float dt)
        {
            if (shown == null || shown.Spinners.Count == 0 || dt <= 0f)
            {
                return;
            }
            if (shown.Kind == Kind.Nailgun)
            {
                bool firing = ((SampleFlags)g.Flags[now] & SampleFlags.FireHeld) != 0;
                if (firing)
                {
                    nailHeat = shown.Variation == 1 ? 1f : shown.Variation == 2 ? 0.33f : shown.AltVersion ? nailHeat : Mathf.MoveTowards(nailHeat, 1f, dt * 0.55f);
                    nailSpin = 250f + nailHeat * 2250f;
                }
                else
                {
                    nailHeat = Mathf.MoveTowards(nailHeat, 0f, dt * 0.3f);
                    nailSpin = Mathf.MoveTowards(nailSpin, 0f, dt * 1000f);
                }
            }
            foreach (Spinner s in shown.Spinners)
            {
                float degrees = (s.Barrel ? nailSpin : s.Speed) * dt;
                if (s.T != null && degrees != 0f)
                {
                    s.T.Rotate(s.Axis, degrees, s.World ? Space.World : Space.Self);
                }
            }
        }
    }
}
