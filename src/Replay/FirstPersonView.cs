using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // Through an attempt's eyes, the screen as it looked to you then: its weapon as ULTRAKILL's own animated
    // viewmodel (Viewmodel) and the game's HUD with its health and weapon (FirstPersonHud). Both are copies
    // of yours, drawn by the game's HUD camera (the one that draws your gun and HUD over the world, which the
    // replay switches off). While they show, that camera is on and your own gun, arm and HUD are hidden;
    // everything goes back exactly as it was when the view leaves the eyes, the menus open, or the replay ends.
    internal sealed class FirstPersonView
    {
        private readonly CameraController cc;
        private readonly Camera hudCam;
        private CameraMirror mirror;
        private Viewmodel guns;
        private FirstPersonHud hud;
        private bool broken;
        private string gunsReport = "";
        private string hudReport = "";
        private bool reported;

        // What's hidden while the copies show, to put back.
        private bool liveHidden;
        private bool hudCamWasEnabled;
        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private static readonly HashSet<Renderer> hiddenNow = new HashSet<Renderer>();
        private readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        private readonly List<Canvas> hiddenHudSources = new List<Canvas>();

        private Ghost last;
        private float lastT;
        private bool wasActive;
        private bool weaponsShown;

        // One of your own renderers this switched off while it shows (a copy made of it now is off too: it isn't really).
        public static bool HidByReplay(Renderer r) => r != null && hiddenNow.Contains(r);

        // The game-style HUD is on screen (the replay's own overlay makes room for it).
        public bool HudShown { get; private set; }

        // replayHid: the canvas the replay itself switched off (the results screen's, at the level's end), if any.
        public FirstPersonView(CameraController cc, Camera hudCam, Canvas replayHid)
        {
            this.cc = cc;
            this.hudCam = hudCam;
            this.replayHid = replayHid;
        }

        private readonly Canvas replayHid;

        // Once the attempts are loaded: the copies are made now (while it says LOADING), not on the first cut.
        public void Prepare(List<Ghost> ghosts)
        {
            if (cc == null)
            {
                broken = true;
                return;
            }
            try
            {
                mirror = new CameraMirror(cc.transform);
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log.LogError("first person: couldn't get ready (the still weapon and the replay's own HUD stand in): " + e);
                return;
            }
            var needed = new HashSet<string>();
            var neededArms = new HashSet<Recording.ArmKind>();
            foreach (Ghost g in ghosts)
            {
                foreach (Ghost.WeaponChange w in g.Weapons)
                {
                    string name = g.WeaponName(w);
                    if (name != null)
                    {
                        needed.Add(name);
                    }
                }
                foreach (Recording.ArmSwitch s in g.ArmSwitches)
                {
                    neededArms.Add(s.Arm);
                }
            }
            if (hudCam == null)
            {
                gunsReport = "no viewmodels (the game's HUD camera wasn't found)";
            }
            else
            {
                try
                {
                    guns = Viewmodel.Build(mirror, cc.cam, needed, neededArms, out gunsReport);
                }
                catch (Exception e)
                {
                    guns = null;
                    gunsReport = "no viewmodels (" + e.GetBaseException().Message + ")";
                    Plugin.Log.LogDebug(e);
                }
            }
            try
            {
                hud = FirstPersonHud.Build(mirror, replayHid, out hudReport);
            }
            catch (Exception e)
            {
                hud = null;
                hudReport = "none (" + e.GetBaseException().Message + ")";
                Plugin.Log.LogDebug(e);
            }
        }

        // Whether the animated viewmodel draws this attempt's weapon at that sample (then the still one isn't).
        public bool Covers(Ghost g, int sample)
        {
            if (!weaponsShown || g != last || guns == null)
            {
                return false;
            }
            Ghost.WeaponChange w = g.WeaponAt(sample);
            return w.Slot <= 0 || guns.Has(g.WeaponName(w));
        }

        // Every frame of the replay. follow: the attempt seen through (null: none); eyes: following it in first
        // person; inside: the camera's in its head (a cut's blend is done).
        public void Frame(Ghost follow, float t, bool eyes, bool inside)
        {
            bool wantGuns = Settings.FirstPersonWeapons.Value && Settings.ShowWeapons.Value && guns != null;
            bool wantHud = Settings.FirstPersonHud.Value && hud != null;
            bool active = !broken && follow != null && eyes && !MenusOpen() && (wantGuns || wantHud);
            if (!active)
            {
                SafeDeactivate();
                return;
            }
            try
            {
                bool fresh = !wasActive || follow != last || t < lastT - 1e-4f || t - lastT > 0.9f;
                float dt = fresh ? 0f : t - lastT;
                bool showGuns = wantGuns && inside;
                bool copiedHud = wantHud && hud.Copied;
                // (through a cut's blend too: the copies only hide meanwhile, so the HUD camera doesn't flicker)
                SetLiveHidden(wantGuns || copiedHud);
                SetHudSourcesHidden(copiedHud);
                if (guns != null)
                {
                    if (wantGuns)
                    {
                        guns.Frame(follow, t, dt, fresh, showGuns);
                    }
                    else
                    {
                        guns.Hide();
                    }
                }
                if (wantHud)
                {
                    hud.Frame(follow, t, dt, fresh);
                }
                else
                {
                    hud?.Show(false);
                }
                weaponsShown = showGuns;
                HudShown = wantHud;
                last = follow;
                lastT = t;
                wasActive = true;
                Report();
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log.LogError("first person went wrong (off for the rest of this replay; the still weapon stands in): " + e);
                SafeDeactivate();
            }
        }

        private void SafeDeactivate()
        {
            try
            {
                Deactivate();
            }
            catch (Exception e)
            {
                if (!broken)
                {
                    broken = true;
                    Plugin.Log.LogError("first person: putting your gun and HUD back went wrong (off for the rest of this replay): " + e);
                }
            }
        }

        // The HUD drawn the replay's way, when the game's own couldn't be copied.
        public void OnGUI()
        {
            if (HudShown && hud != null && !hud.Copied && last != null)
            {
                try
                {
                    hud.DrawFallback(last, lastT);
                }
                catch (Exception e)
                {
                    hud = null;
                    HudShown = false;
                    Plugin.Log.LogError("drawing the first person HUD (off for the rest of this replay): " + e);
                }
            }
        }

        public void Dispose()
        {
            if (mirror != null)
            {
                Report();  // (once per replay, even if it never looked through an attempt's eyes)
            }
            try
            {
                Deactivate();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting your gun and HUD back after a replay: " + e);
            }
            try
            {
                guns?.Dispose();
                hud?.Dispose();
                mirror?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("clearing up the first person copies after a replay: " + e);
            }
            guns = null;
            hud = null;
            mirror = null;
        }

        // Yours back first (whatever happens to the copies after).
        private void Deactivate()
        {
            bool was = wasActive;
            wasActive = false;
            weaponsShown = false;
            HudShown = false;
            SetHudSourcesHidden(false);
            SetLiveHidden(false);
            if (was)
            {
                guns?.Hide();
                hud?.Show(false);
            }
        }

        // (the pause menu or the cheat menu: the game as the replay left it, so they show as they always do)
        private static bool MenusOpen()
        {
            if (Game.Paused)
            {
                return true;
            }
            var cheats = Game.Get<CheatsManager>();
            return cheats != null && cheats.IsMenuOpen();
        }

        // ---------------------------------------------------------------- your own gun, arm and HUD

        // The layers only the HUD camera draws (your gun and arm, the HUD): what turning it on brings back.
        private int HudOnlyLayers => hudCam != null ? hudCam.cullingMask & ~(cc.cam != null ? cc.cam.cullingMask : 0) : 0;

        private void SetLiveHidden(bool hide)
        {
            if (hide == liveHidden || (hide && hudCam == null))
            {
                return;
            }
            if (hide)
            {
                liveHidden = true;
                int layers = HudOnlyLayers;
                var roots = new List<Transform> { cc.transform };
                AddRoot(roots, Game.Get<GunControl>());
                AddRoot(roots, Game.Get<FistControl>());
                AddRoot(roots, Game.Get<HookArm>());
                AddRoot(roots, Game.Player);
                var seen = new HashSet<Renderer>();
                foreach (Transform root in roots)
                {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && r.enabled && ((1 << r.gameObject.layer) & layers) != 0 && !Ours(r.transform) && seen.Add(r))
                        {
                            r.enabled = false;
                            hiddenRenderers.Add(r);
                            hiddenNow.Add(r);
                        }
                    }
                }
                Transform player = Game.Player != null ? Game.Player.transform : null;
                foreach (Canvas c in Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.enabled || !c.isRootCanvas || Ours(c.transform))
                    {
                        continue;
                    }
                    bool drawnByHudCam = c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == hudCam;
                    bool onTheGun = c.renderMode == RenderMode.WorldSpace && ((1 << c.gameObject.layer) & layers) != 0
                        && (c.transform.IsChildOf(cc.transform) || (player != null && c.transform.IsChildOf(player)));
                    if (drawnByHudCam || onTheGun)
                    {
                        c.enabled = false;
                        hiddenCanvases.Add(c);
                    }
                }
                hudCamWasEnabled = hudCam.enabled;
                hudCam.enabled = true;
                return;
            }
            liveHidden = false;
            foreach (Renderer r in hiddenRenderers)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            hiddenRenderers.Clear();
            hiddenNow.Clear();
            foreach (Canvas c in hiddenCanvases)
            {
                if (c != null)
                {
                    c.enabled = true;
                }
            }
            hiddenCanvases.Clear();
            if (hudCam != null)
            {
                hudCam.enabled = hudCamWasEnabled;
            }
        }

        // The HUD canvases the copy was made from, when the HUD camera doesn't draw them (they'd show through).
        private void SetHudSourcesHidden(bool hide)
        {
            if (hide)
            {
                if (hiddenHudSources.Count > 0 || hud == null)
                {
                    return;
                }
                foreach (Canvas c in hud.Sources)
                {
                    if (c != null && c.enabled && !hiddenCanvases.Contains(c))
                    {
                        c.enabled = false;
                        hiddenHudSources.Add(c);
                    }
                }
                return;
            }
            foreach (Canvas c in hiddenHudSources)
            {
                if (c != null)
                {
                    c.enabled = true;
                }
            }
            hiddenHudSources.Clear();
        }

        private static void AddRoot(List<Transform> roots, Component c)
        {
            if (c != null && !roots.Contains(c.transform))
            {
                roots.Add(c.transform);
            }
        }

        private bool Ours(Transform t) => mirror != null && mirror.Root != null && t.IsChildOf(mirror.Root);

        // The one line for the log: the first time it shows (or at the end, if it never did).
        private void Report()
        {
            if (reported)
            {
                return;
            }
            reported = true;
            string shown = !wasActive ? "never seen through an attempt's eyes this replay"
                : liveHidden ? $"{hiddenRenderers.Count} of your own gun and arm renderers and {hiddenCanvases.Count + hiddenHudSources.Count} HUD canvases hidden while it shows"
                : "the HUD camera stays off";
            Plugin.Log.LogInfo($"first person: {gunsReport}; HUD: {hudReport}; {shown}");
        }
    }

    // The camera's own hierarchy, copied as empty transforms under one of ours: a copy of something under the
    // camera sits exactly where the real one does (its parents' places, the left-handed flip and all).
    internal sealed class CameraMirror
    {
        private readonly Transform camera;
        private readonly Dictionary<Transform, Transform> made = new Dictionary<Transform, Transform>();

        public Transform Root { get; }

        public CameraMirror(Transform camera)
        {
            this.camera = camera;
            var go = new GameObject("UltraAttempts first person");
            go.transform.SetParent(camera, false);
            Root = go.transform;
            made[camera] = Root;
        }

        // The mirror of real (the camera or something under it), made as it's needed; null if it's elsewhere.
        public Transform Of(Transform real)
        {
            if (real == null)
            {
                return null;
            }
            if (made.TryGetValue(real, out Transform found) && found != null)
            {
                return found;
            }
            if (!real.IsChildOf(camera))
            {
                return null;
            }
            Transform parent = Of(real.parent);
            if (parent == null)
            {
                return null;
            }
            var go = new GameObject(Game.CleanName(real.name) + " (replay)") { layer = real.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = real.localPosition;
            go.transform.localRotation = real.localRotation;
            go.transform.localScale = real.localScale;
            made[real] = go.transform;
            return go.transform;
        }

        public bool Under(Transform real) => real != null && real.IsChildOf(camera);

        public void Dispose()
        {
            if (Root != null)
            {
                Root.gameObject.SetActive(false);  // (gone this frame, not at its end: your own gun is back already)
                Object.Destroy(Root.gameObject);
            }
            made.Clear();
        }
    }

    // Visual-only copies of live objects (your gun, your HUD), like Puppet's: everything keep doesn't allow
    // is removed before the copy ever wakes up, so none of the game's scripts on it run.
    internal static class VisualCopy
    {
        // An inactive copy of source, at its local place under parent. ready: called on the copy (still asleep,
        // scripts and all) before they're removed.
        public static GameObject Make(GameObject source, Transform parent, Func<Component, bool> keep, Action<GameObject> ready = null)
        {
            var holder = new GameObject("UltraAttempts copy holder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject copy;
            try
            {
                copy = Object.Instantiate(source, holder.transform, false);
                copy.name = Game.CleanName(source.name) + " (replay)";
                ready?.Invoke(copy);
                Strip(copy, keep);
                copy.SetActive(false);
                copy.transform.SetParent(parent, false);
            }
            finally
            {
                Object.Destroy(holder);
            }
            return copy;
        }

        // Scripts first (so nothing they require is still needed), then renderers and joints before the particle
        // systems and bodies they need, then the rest; a few passes for anything else that depends on another.
        private static void Strip(GameObject root, Func<Component, bool> keep)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                bool removedAny = false;
                Component[] all = root.GetComponentsInChildren<Component>(true);
                foreach (Component c in all.OrderBy(c => c is MonoBehaviour ? 0 : c is Renderer ? 1 : c is Joint ? 2 : c is Rigidbody ? 4 : 3))
                {
                    if (c == null || c is Transform || keep(c))
                    {
                        continue;
                    }
                    try
                    {
                        Object.DestroyImmediate(c);
                        removedAny = true;
                    }
                    catch
                    {
                        // (still needed by another component: next pass)
                    }
                }
                if (!removedAny)
                {
                    break;
                }
            }
        }

        // Where t is under root, as child indices (the same in an exact copy).
        public static int[] PathOf(Transform root, Transform t)
        {
            var steps = new List<int>();
            for (; t != null && t != root; t = t.parent)
            {
                steps.Add(t.GetSiblingIndex());
            }
            if (t != root)
            {
                return null;
            }
            steps.Reverse();
            return steps.ToArray();
        }

        public static Transform Follow(Transform root, int[] path)
        {
            if (path == null)
            {
                return null;
            }
            Transform t = root;
            foreach (int i in path)
            {
                if (t == null || i < 0 || i >= t.childCount)
                {
                    return null;
                }
                t = t.GetChild(i);
            }
            return t;
        }

        // The same thing in a copy (null if it isn't under sourceRoot).
        public static Transform InCopy(Transform sourceRoot, Transform copyRoot, Transform t) => Follow(copyRoot, PathOf(sourceRoot, t));
    }
}
