using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // Through an attempt's eyes: ULTRAKILL's own HUD, showing that attempt's health and weapon. It's a copy of
    // the HUD on your screen (whichever layout you play with), its canvas and images only: the health bars and
    // the weapon icon are set here from the recording, as HealthBar and WeaponHUD set them in play (the bar
    // rising quickly, the after-image falling slowly, the number red at 30). Whatever else on it shows the live
    // game (the style meter, stamina, charges, messages) is left out of the copy, since the recording doesn't
    // have them. Your own HUD is hidden meanwhile, never changed. If no game HUD is on screen (or it can't be
    // copied), a lookalike is drawn instead.
    internal sealed class FirstPersonHud
    {
        // The game's scripts that show the live game on the HUD (none of it recorded): their parts are left out of
        // the copy. Anything else on it (backgrounds, colours, the crosshair) comes along as it looks now.
        private static readonly HashSet<string> Live = new HashSet<string>
        {
            "StyleHUD", "StaminaMeter", "RailcannonMeter", "Speedometer", "PowerUpMeter", "HudMessageReceiver", "HudMessage",
            "BossBarManager", "BossHealthBarTemplate", "BossHealthSliderTemplate", "Countdown", "CrateCounter", "HeatResistance",
            "LevelStats", "WeaponWheel", "FishingHUD", "SandboxHud", "ScanningStuff", "ChallengeManager", "FinalRank",
            "FinalCyberRank", "LevelEndLeaderboard", "TramControl", "PuzzleController", "HellMap", "SubtitleController",
        };

        private sealed class Bar
        {
            public Slider[] Hp;
            public Slider[] After;
            public Slider Anti;
            public Image AntiFill;
            public TMP_Text Text;
            public bool AntiText;
            public bool ChangeColor;
            public bool Yellow;
            public Color Normal;
            public string LastText;
        }

        private readonly List<GameObject> copies = new List<GameObject>();
        private readonly List<Canvas> sources = new List<Canvas>();
        private readonly List<Bar> bars = new List<Bar>();
        private readonly Dictionary<string, WeaponDescriptor> descriptors = new Dictionary<string, WeaponDescriptor>();
        private Image weaponIcon;
        private Image weaponGlow;
        private string iconFor;
        private int difficulty;
        private Canvas replayHid;

        // The health shown (HealthBar's smoothing), its after-image, and the hard damage (never recorded: none).
        private float shownHp;
        private float afterHp;
        private float antiHp;
        private int sample;

        public bool Copied => copies.Count > 0 && bars.Count > 0;

        // The live canvases the copies were made from (hidden while the copies show).
        public IEnumerable<Canvas> Sources => sources;

        // replayHid: a canvas the replay itself switched off (the results screen's), which counts as on here.
        public static FirstPersonHud Build(CameraMirror mirror, Canvas replayHid, out string report)
        {
            var hud = new FirstPersonHud { replayHid = replayHid };
            GunControl guns = Game.Get<GunControl>();
            if (guns != null)
            {
                foreach (GameObject weapon in guns.allWeapons)
                {
                    WeaponIcon wi = weapon != null ? weapon.GetComponent<WeaponIcon>() : null;
                    WeaponDescriptor desc = wi != null ? wi.weaponDescriptor : null;
                    if (desc == null)
                    {
                        continue;
                    }
                    foreach (string name in new[] { desc.weaponName, Game.CleanName(weapon.name) })
                    {
                        if (!string.IsNullOrEmpty(name) && !hud.descriptors.ContainsKey(name))
                        {
                            hud.descriptors[name] = desc;
                        }
                    }
                }
            }
            var prefs = Game.Get<PrefsManager>();
            hud.difficulty = prefs != null ? prefs.GetInt("difficulty") : 2;

            List<HealthBar> found = Object.FindObjectsOfType<HealthBar>().Where(hud.Visible).ToList();
            WeaponHUD weaponHud = Game.Get<WeaponHUD>();
            if (weaponHud != null && !hud.Visible(weaponHud))
            {
                weaponHud = null;
            }
            if (found.Count == 0)
            {
                report = "a lookalike (no game health bar on screen to copy)";
                return hud;
            }

            // What gets set, by the canvas it's on.
            var targets = new Dictionary<Canvas, List<Transform>>();
            void Target(Component c)
            {
                Canvas canvas = c != null ? c.GetComponentInParent<Canvas>() : null;
                if (canvas == null)
                {
                    return;
                }
                Canvas root = canvas.rootCanvas;
                if (!targets.TryGetValue(root, out List<Transform> list))
                {
                    targets[root] = list = new List<Transform>();
                }
                if (!list.Contains(c.transform))
                {
                    list.Add(c.transform);
                }
            }
            foreach (HealthBar b in found)
            {
                foreach (Slider s in (b.hpSliders ?? Array.Empty<Slider>()).Concat(b.afterImageSliders ?? Array.Empty<Slider>()))
                {
                    Target(s);
                }
                Target(b.antiHpSlider);
                Target(b.antiHpSliderFill);
                Target(b.hpText);
            }
            Target(weaponHud);

            var copied = new Dictionary<Transform, Transform>();
            var left = new SortedSet<string>();
            var skipped = new List<string>();
            foreach (KeyValuePair<Canvas, List<Transform>> group in targets)
            {
                GameObject copy = hud.CopyCanvas(mirror, group.Key, group.Value, copied, left);
                if (copy == null)
                {
                    skipped.Add(Game.CleanName(group.Key.name));
                }
            }
            foreach (HealthBar b in found)
            {
                var bar = new Bar
                {
                    Hp = Sliders(b.hpSliders, copied),
                    After = Sliders(b.afterImageSliders, copied),
                    Anti = In<Slider>(b.antiHpSlider, copied),
                    AntiFill = In<Image>(b.antiHpSliderFill, copied),
                    Text = In<TMP_Text>(b.hpText, copied),
                    AntiText = b.antiHpText,
                    ChangeColor = b.changeTextColor,
                    Yellow = b.yellowColor,
                    Normal = b.normalTextColor,
                };
                if (bar.Hp.Length > 0 || bar.After.Length > 0 || bar.Text != null)
                {
                    hud.bars.Add(bar);
                }
            }
            if (weaponHud != null && copied.TryGetValue(weaponHud.transform, out Transform icon) && icon != null)
            {
                hud.weaponIcon = icon.GetComponent<Image>();
                hud.weaponGlow = icon.childCount > 0 ? icon.GetChild(0).GetComponent<Image>() : null;
            }
            if (!hud.Copied)
            {
                hud.Dispose();
                report = "a lookalike (the game's couldn't be copied" + (skipped.Count > 0 ? ": " + string.Join(", ", skipped) : "") + ")";
                return hud;
            }
            report = $"the game's own ({hud.bars.Count} health bar{(hud.bars.Count == 1 ? "" : "s")}"
                + (hud.weaponIcon != null ? ", the weapon icon" : ", no weapon icon")
                + $", from {string.Join(", ", hud.sources.Select(s => $"{Game.CleanName(s.name)} ({s.renderMode})"))}"
                + (left.Count > 0 ? "; left out (they'd show your game now): " + string.Join(", ", left) : "")
                + (skipped.Count > 0 ? "; couldn't copy " + string.Join(", ", skipped) : "") + ")";
            return hud;
        }

        // Shown on screen: an active object under canvases that are all on.
        private bool Visible(Behaviour b)
        {
            if (b == null || !b.isActiveAndEnabled)
            {
                return false;
            }
            foreach (Canvas c in b.GetComponentsInParent<Canvas>(true))
            {
                if (!c.enabled && c != replayHid)
                {
                    return false;
                }
            }
            return true;
        }

        private static Slider[] Sliders(Slider[] from, Dictionary<Transform, Transform> copied) =>
            (from ?? Array.Empty<Slider>()).Select(s => In<Slider>(s, copied)).Where(s => s != null).ToArray();

        private static T In<T>(Component c, Dictionary<Transform, Transform> copied) where T : Component =>
            c != null && copied.TryGetValue(c.transform, out Transform t) && t != null ? t.GetComponent<T>() : null;

        // One of the HUD's canvases, copied where it is, without the parts that show the live game.
        private GameObject CopyCanvas(CameraMirror mirror, Canvas root, List<Transform> targets, Dictionary<Transform, Transform> copied, SortedSet<string> left)
        {
            Transform parent;
            if (mirror.Under(root.transform))
            {
                parent = mirror.Of(root.transform.parent);
            }
            else
            {
                // (a screen-space canvas can sit anywhere; one in the world, away from the camera, can't come along)
                parent = root.renderMode != RenderMode.WorldSpace ? mirror.Root : null;
            }
            if (parent == null)
            {
                return null;
            }
            var path = new HashSet<Transform>();
            foreach (Transform target in targets)
            {
                for (Transform p = target.parent; p != null; p = p.parent)
                {
                    path.Add(p);
                    if (p == root.transform)
                    {
                        break;
                    }
                }
            }
            var targetSet = new HashSet<Transform>(targets);
            GameObject copy = VisualCopy.Make(root.gameObject, parent, KeepUi, c =>
            {
                // (found before anything's taken out of the copy: the paths are child indices)
                foreach (Transform target in targets)
                {
                    copied[target] = VisualCopy.InCopy(root.transform, c.transform, target);
                }
                Prune(root.transform, c.transform, path, targetSet, false, left);
            });
            if (root.renderMode == RenderMode.WorldSpace)
            {
                copy.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation);
            }
            Canvas canvas = copy.GetComponent<Canvas>();
            if (canvas != null && root == replayHid)
            {
                canvas.enabled = true;  // (the replay switched the original off, not the game)
            }
            copies.Add(copy);
            sources.Add(root);
            return copy;
        }

        private static bool KeepUi(Component c)
        {
            if (c is RectTransform || c is Canvas || c is CanvasGroup || c is CanvasRenderer || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer)
            {
                return true;
            }
            string ns = c.GetType().Namespace ?? "";
            if (ns != "UnityEngine.UI" && !ns.StartsWith("UnityEngine.UI.") && ns != "TMPro")
            {
                return false;
            }
            return c is Graphic || c is BaseMeshEffect || c is Mask || c is RectMask2D || c is LayoutGroup || c is LayoutElement
                || c is ContentSizeFitter || c is AspectRatioFitter || c is CanvasScaler || c is Slider;
        }

        // Takes out of the copy what wasn't showing (closed menus and the like) and what shows the live game,
        // keeping the way to everything set here.
        private static void Prune(Transform source, Transform copy, HashSet<Transform> path, HashSet<Transform> targets, bool within, SortedSet<string> left)
        {
            int n = source.childCount;
            if (copy.childCount != n)
            {
                return;
            }
            var pairs = new (Transform s, Transform c)[n];
            for (int i = 0; i < n; i++)
            {
                pairs[i] = (source.GetChild(i), copy.GetChild(i));
            }
            foreach ((Transform s, Transform c) in pairs)
            {
                bool inside = within || targets.Contains(s);
                if (!inside && !path.Contains(s))
                {
                    if (!s.gameObject.activeSelf)
                    {
                        Object.DestroyImmediate(c.gameObject);
                        continue;
                    }
                    string live = LiveScript(s.gameObject);
                    if (live != null)
                    {
                        left.Add(live);
                        Object.DestroyImmediate(c.gameObject);
                        continue;
                    }
                }
                Prune(s, c, path, targets, inside, left);
            }
        }

        // The first script on it that shows the live game, if any.
        private static string LiveScript(GameObject go)
        {
            foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb != null && Live.Contains(mb.GetType().Name))
                {
                    return mb.GetType().Name;
                }
            }
            return null;
        }

        public void Show(bool on)
        {
            foreach (GameObject copy in copies)
            {
                if (copy != null && copy.activeSelf != on)
                {
                    copy.SetActive(on);
                }
            }
        }

        // ---------------------------------------------------------------- every frame

        public void Frame(Ghost g, float t, float dt, bool fresh)
        {
            sample = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(t, g.Duration) * g.Rate), 0, g.Count - 1);
            float hp = g.Hp[sample];
            // HealthBar: up quickly, down at once; the after-image follows down slowly.
            if (fresh || shownHp > hp)
            {
                shownHp = hp;
            }
            else if (shownHp < hp)
            {
                shownHp = Mathf.MoveTowards(shownHp, hp, dt * ((hp - shownHp) * 5f + 5f));
            }
            if (fresh || afterHp < shownHp)
            {
                afterHp = shownHp;
            }
            else if (afterHp > shownHp)
            {
                afterHp = Mathf.MoveTowards(afterHp, shownHp, dt * ((afterHp - shownHp) * 5f + 5f));
            }
            antiHp = fresh ? 0f : Mathf.MoveTowards(antiHp, 0f, dt * (antiHp * 5f + 5f));
            if (!Copied)
            {
                return;
            }
            Show(true);
            foreach (Bar bar in bars)
            {
                foreach (Slider s in bar.Hp)
                {
                    if (s != null && s.value != shownHp)
                    {
                        s.value = shownHp;
                    }
                }
                foreach (Slider s in bar.After)
                {
                    if (s != null && s.value != afterHp)
                    {
                        s.value = afterHp;
                    }
                }
                if (bar.Anti != null)
                {
                    bar.Anti.value = antiHp;
                }
                if (bar.AntiFill != null)
                {
                    bar.AntiFill.enabled = antiHp > 0f;
                }
                SetText(bar);
            }
            SetIcon(g);
        }

        private void SetText(Bar bar)
        {
            if (bar.Text == null)
            {
                return;
            }
            string text = bar.AntiText ? (difficulty == 0 ? "/200" : "/" + (100f - antiHp).ToString("F0")) : shownHp.ToString("F0");
            if (text != bar.LastText)
            {
                bar.Text.text = text;
                bar.LastText = text;
            }
            if (bar.AntiText)
            {
                return;
            }
            if (bar.ChangeColor)
            {
                bar.Text.color = shownHp <= 30f ? Color.red : shownHp <= 50f && bar.Yellow ? Color.yellow : bar.Normal;
            }
            else if (bar.Normal == Color.white)
            {
                ColorBlindSettings colors = Game.Get<ColorBlindSettings>();
                bar.Text.color = shownHp <= 30f ? Color.red : colors != null ? colors.GetHudColor(HudColorType.healthText) : Color.white;
            }
        }

        // The icon of the weapon it held, in its variant's colour (WeaponHUD.UpdateImage).
        private void SetIcon(Ghost g)
        {
            if (weaponIcon == null)
            {
                return;
            }
            Ghost.WeaponChange w = g.WeaponAt(sample);
            string name = g.WeaponName(w);
            string key = name + "|" + w.Variant;
            if (key == iconFor)
            {
                return;
            }
            iconFor = key;
            WeaponDescriptor desc = name != null && descriptors.TryGetValue(name, out WeaponDescriptor d) ? d : null;
            bool on = desc != null && desc.icon != null;
            weaponIcon.enabled = on;
            if (weaponGlow != null)
            {
                weaponGlow.enabled = on && desc.glowIcon != null;
            }
            if (!on)
            {
                return;
            }
            Color color = VariantColor(w.Variant);
            weaponIcon.sprite = desc.icon;
            weaponIcon.color = color;
            if (weaponGlow != null && desc.glowIcon != null)
            {
                weaponGlow.sprite = desc.glowIcon;
                weaponGlow.color = color;
            }
        }

        private static Color VariantColor(int variant)
        {
            ColorBlindSettings colors = Game.Get<ColorBlindSettings>();
            if (colors != null && colors.variationColors != null && variant >= 0 && variant < colors.variationColors.Length)
            {
                return colors.variationColors[variant];
            }
            return GhostRenderer.VariantColor(variant);
        }

        public void Dispose()
        {
            foreach (GameObject copy in copies)
            {
                if (copy != null)
                {
                    Object.Destroy(copy);
                }
            }
            copies.Clear();
            sources.Clear();
            bars.Clear();
            weaponIcon = null;
            weaponGlow = null;
        }

        // ---------------------------------------------------------------- the lookalike

        private const float Ref = 1080f;
        private static Font font;
        private static bool fontSearched;
        private static GUIStyle style;

        // Bottom left, as the game's: the weapon's icon in its colour, the health as a number and a red bar
        // (the after-image behind it, overheal in blue).
        public void DrawFallback(Ghost g, float t)
        {
            Matrix4x4 old = GUI.matrix;
            float s = Screen.height / Ref;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            try
            {
                float x = 28f, y = Ref - 196f;
                Box(new Rect(x, y, 470f, 172f), new Color(0f, 0f, 0f, 0.55f));
                Ghost.WeaponChange w = g.WeaponAt(sample);
                string name = g.WeaponName(w);
                if (name != null)
                {
                    Color c = VariantColor(w.Variant);
                    float tx = x + 18f;
                    WeaponDescriptor desc = descriptors.TryGetValue(name, out WeaponDescriptor d) ? d : null;
                    Sprite icon = desc != null ? desc.icon : null;
                    if (icon != null && icon.texture != null)
                    {
                        Rect tr = icon.textureRect;
                        var uv = new Rect(tr.x / icon.texture.width, tr.y / icon.texture.height, tr.width / icon.texture.width, tr.height / icon.texture.height);
                        float h = 66f, iw = Mathf.Min(220f, h * tr.width / Mathf.Max(1f, tr.height));
                        Color before = GUI.color;
                        GUI.color = c;
                        GUI.DrawTextureWithTexCoords(new Rect(tx, y + 12f, iw, h), icon.texture, uv);
                        GUI.color = before;
                        tx += iw + 14f;
                    }
                    Text(new Rect(tx, y + 32f, 460f - (tx - x), 30f), name.ToUpperInvariant(), 20, c);
                }
                float hp = shownHp;
                Text(new Rect(x + 16f, y + 92f, 120f, 64f), Mathf.RoundToInt(hp).ToString(), 50, hp <= 30f ? Color.red : Color.white);
                var bar = new Rect(x + 140f, y + 112f, 310f, 30f);
                Box(bar, new Color(0.25f, 0.02f, 0.02f, 0.9f));
                Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(afterHp / 100f), bar.height), new Color(1f, 0.55f, 0.45f, 0.8f));
                Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(hp / 100f), bar.height), new Color(0.9f, 0.08f, 0.08f, 1f));
                if (hp > 100f)
                {
                    Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01((hp - 100f) / 100f), bar.height), new Color(0.3f, 0.6f, 1f, 0.95f));
                }
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        private static GUIStyle Style
        {
            get
            {
                if (style == null)
                {
                    style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Overflow };
                    if (!fontSearched)
                    {
                        fontSearched = true;
                        font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f != null && f.name.ToUpperInvariant().Contains("VCR"));
                    }
                    if (font != null)
                    {
                        style.font = font;
                    }
                }
                return style;
            }
        }

        private static void Text(Rect r, string text, int size, Color color)
        {
            GUIStyle st = Style;
            st.fontSize = size;
            st.alignment = TextAnchor.MiddleLeft;
            st.fontStyle = st.font == null ? FontStyle.Bold : FontStyle.Normal;
            Color before = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, color.a * 0.85f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, st);
            GUI.color = color;
            GUI.Label(r, text, st);
            GUI.color = before;
        }

        private static void Box(Rect r, Color color)
        {
            Color before = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = before;
        }
    }
}
