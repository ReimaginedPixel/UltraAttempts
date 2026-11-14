using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UltraAttempts.Replay
{
    // The replay's overlay (and the "ATTEMPT 37" banner while playing). Laid out for 1080p and scaled to
    // the screen; in ULTRAKILL's own VCR font when it can find it.
    internal sealed class ReplayHud
    {
        private const float Ref = 1080f;
        private static readonly Color Accent = new Color(1f, 0.27f, 0.2f);
        private static readonly Color Dim = new Color(0.72f, 0.72f, 0.76f);
        private static readonly Color Panel = new Color(0f, 0f, 0f, 0.55f);

        private static Font font;
        private static bool fontSearched;
        private static GUIStyle style;

        private readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        private GunControl iconsFrom;

        private static Font GameFont()
        {
            if (!fontSearched)
            {
                fontSearched = true;
                foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f != null && f.name.ToUpperInvariant().Contains("VCR"))
                    {
                        font = f;
                        break;
                    }
                }
            }
            return font;
        }

        private static GUIStyle Style
        {
            get
            {
                if (style == null)
                {
                    style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Overflow };
                    Font f = GameFont();
                    if (f != null)
                    {
                        style.font = f;
                    }
                }
                return style;
            }
        }

        private static float Begin(out Matrix4x4 old)
        {
            old = GUI.matrix;
            float s = Screen.height / Ref;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            return Screen.width / s;
        }

        private static void Text(Rect r, string text, int size, Color color, TextAnchor anchor = TextAnchor.UpperLeft, bool bold = false)
        {
            GUIStyle st = Style;
            st.fontSize = size;
            st.alignment = anchor;
            st.fontStyle = bold && st.font == null ? FontStyle.Bold : FontStyle.Normal;
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

        public static string Clock(float seconds, bool millis = false)
        {
            if (seconds < 0f)
            {
                seconds = 0f;
            }
            int m = (int)(seconds / 60f);
            float s = seconds - m * 60;
            return millis ? $"{m}:{s:00.000}" : $"{m}:{s:00.00}";
        }

        // "ATTEMPT 37" for a couple of seconds as an attempt starts (Geometry Dash's counter).
        public static void DrawBanner(int number, float shownAt)
        {
            float age = Time.unscaledTime - shownAt;
            if (age < 0f || age > 2.2f || number <= 0)
            {
                return;
            }
            float alpha = age < 0.15f ? age / 0.15f : age > 1.6f ? 1f - (age - 1.6f) / 0.6f : 1f;
            float w = Begin(out Matrix4x4 old);
            Text(new Rect(0f, Ref * 0.2f, w, 80f), "ATTEMPT " + number, 56, new Color(1f, 1f, 1f, alpha), TextAnchor.MiddleCenter, bold: true);
            GUI.matrix = old;
        }

        public void DrawResultsPrompt(int attempts, Key key, bool playsFirst)
        {
            float w = Begin(out Matrix4x4 old);
            string words = attempts == 1 ? "WATCH YOUR 1 ATTEMPT" : $"WATCH ALL {attempts} ATTEMPTS";
            string line = key != Key.None ? $"[{key.ToString().ToUpperInvariant()}]  {words}" : words;
            Text(new Rect(0f, Ref - 120f, w, 30f), line, 24, Color.white, TextAnchor.MiddleCenter);
            if (playsFirst)
            {
                Text(new Rect(0f, Ref - 90f, w, 24f), "(or just continue: they play before the next level)", 16, Dim, TextAnchor.MiddleCenter);
            }
            GUI.matrix = old;
        }

        public void Draw(ReplayDirector d, bool loading)
        {
            float w = Begin(out Matrix4x4 old);
            try
            {
                if (d.Flash > 0f)
                {
                    Box(new Rect(0f, 0f, w, Ref), new Color(1f, 0.05f, 0.05f, d.Flash));
                    d.Flash = Mathf.Max(0f, d.Flash - Time.unscaledDeltaTime * 1.8f);
                }
                if (loading)
                {
                    Text(new Rect(0f, Ref * 0.45f, w, 60f), "LOADING ATTEMPTS...", 34, Color.white, TextAnchor.MiddleCenter);
                    return;
                }
                if (!d.HudVisible)
                {
                    Text(new Rect(20f, Ref - 34f, 400f, 24f), "H: SHOW HUD", 14, new Color(1f, 1f, 1f, 0.35f));
                    return;
                }
                // (the game's own HUD is at the bottom through an attempt's eyes: the timeline and keys go up top,
                // between the two panels)
                bool top = d.GameHudShown;
                Labels(d, w);
                Stats(d);
                Followed(d, w);
                Timeline(d, w, top);
                Help(d, top);
                float noticed = Time.unscaledTime - d.NoticeAt;
                if (noticed >= 0f && noticed < 1.6f)
                {
                    Text(new Rect(0f, Ref * (top ? 0.22f : 0.12f), w, 30f), d.Notice, 22, new Color(1f, 1f, 1f, Mathf.Clamp01((1.6f - noticed) / 0.4f)), TextAnchor.MiddleCenter);
                }
                if (d.Summary)
                {
                    SummaryCard(d, w);
                }
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        private void Stats(ReplayDirector d)
        {
            int count = d.Ghosts.Count;
            Box(new Rect(20f, 20f, 400f, d.OverlaysShown > 0 ? 224f : 196f), Panel);
            Text(new Rect(36f, 30f, 380f, 40f), "ATTEMPT REPLAY", 32, Accent, bold: true);
            Text(new Rect(36f, 72f, 380f, 24f), d.LevelName.ToUpperInvariant(), 18, Color.white);
            string days = d.RunDays > 1 ? $"  |  {d.RunDays} DAYS" : "";
            Text(new Rect(36f, 98f, 380f, 22f), $"{d.SourceLabel}  |  {count} ATTEMPT{(count == 1 ? "" : "S")}{days}", 15, Dim);
            Row(132f, "ALIVE", d.Alive.ToString(), Color.white);
            Row(156f, "DEATHS", d.DeadSoFar.ToString(), new Color(1f, 0.45f, 0.4f));
            Row(180f, "FINISHED", d.FinishedSoFar.ToString(), GhostRenderer.Gold);
            if (d.OverlaysShown > 0)
            {
                // (the other attempts' worlds layered in, see-through)
                Text(new Rect(36f, 220f, 380f, 20f), $"+ {d.OverlaysShown} OTHER ATTEMPT{(d.OverlaysShown == 1 ? "'S" : "S'")} WORLD{(d.OverlaysShown == 1 ? "" : "S")}  (O)", 13, Dim);
            }
        }

        private static void Row(float y, string label, string value, Color color)
        {
            Text(new Rect(36f, y, 200f, 24f), label, 18, Dim);
            Text(new Rect(200f, y, 204f, 24f), value, 20, color, TextAnchor.UpperRight);
        }

        private static string ModeName(ReplayDirector d)
        {
            string view = d.FirstPerson ? "EYES" : "BEHIND";
            switch (d.Mode)
            {
                case ReplayCamera.NextToDie: return "NEXT TO DIE | " + view;
                case ReplayCamera.BestRun: return "BEST RUN | " + view;
                case ReplayCamera.Overview: return d.Following ? "PICKED | " + view : "OVERVIEW";
                default: return "FREE CAM";
            }
        }

        private void Followed(ReplayDirector d, float w)
        {
            float x = w - 440f;
            Ghost g = d.Following ? d.Follow : null;
            float height = g == null ? 52f : 214f;
            Box(new Rect(x, 20f, 420f, height), Panel);
            Text(new Rect(x + 16f, 28f, 390f, 24f), "CAMERA  " + ModeName(d), 17, Dim);
            if (g == null)
            {
                return;
            }
            Text(new Rect(x + 16f, 56f, 390f, 36f), (g.IsBest && g.Finished ? "WINNING ATTEMPT #" : "ATTEMPT #") + g.Number, 28, g.Color, bold: true);
            float left = g.Duration - d.T;
            string status;
            Color statusColor = Color.white;
            switch (g.EndReason)
            {
                case AttemptEnd.Finish:
                    status = left > 0f ? $"FINISHES IN {left:0.0}s" : "FINISHED  " + Clock(g.LevelTime, millis: true);
                    statusColor = GhostRenderer.Gold;
                    break;
                case AttemptEnd.InProgress:
                    status = left > 0f ? "YOUR ATTEMPT RIGHT NOW" : "...AND HERE YOU ARE";
                    break;
                case AttemptEnd.Death:
                    status = left > 0f ? $"DIES IN {left:0.0}s" : "DIED";
                    statusColor = new Color(1f, 0.45f, 0.4f);
                    break;
                case AttemptEnd.Restart:
                    status = left > 0f ? $"RESTARTS IN {left:0.0}s" : "RESTARTED";
                    break;
                default:
                    status = left > 0f ? $"GIVES UP IN {left:0.0}s" : "LEFT THE LEVEL";
                    break;
            }
            Text(new Rect(x + 16f, 94f, 390f, 26f), status, 20, statusColor);
            // When it was played, and which day of the run that was (when the run took more than one).
            string when = g.When;
            if (g.Day > 0 && g.RunDays > 1)
            {
                when += (when.Length > 0 ? "  |  " : "") + "DAY " + g.Day;
            }
            Text(new Rect(x + 16f, 122f, 390f, 22f), when, 15, Dim);
            if (g.StartKind == AttemptStart.Checkpoint)
            {
                Text(new Rect(x + 16f, 122f, 390f, 22f), "FROM A CHECKPOINT", 14, Dim, TextAnchor.UpperRight);
            }

            float now = Mathf.Min(d.T, g.Duration);
            Ghost.Pose p = g.PoseAt(now);
            float hp = p.Hp / 100f;
            Box(new Rect(x + 16f, 152f, 200f, 10f), new Color(1f, 1f, 1f, 0.15f));
            Box(new Rect(x + 16f, 152f, 200f * Mathf.Clamp01(hp), 10f), new Color(1f, 0.2f, 0.2f, 0.9f));
            if (hp > 1f)
            {
                Box(new Rect(x + 16f, 152f, 200f * Mathf.Clamp01(hp - 1f), 10f), new Color(0.3f, 0.6f, 1f, 0.9f));
            }
            Text(new Rect(x + 224f, 145f, 80f, 22f), p.Hp + " HP", 14, Dim);
            if (g.HasEnemies)
            {
                Text(new Rect(x + 16f, 143f, 390f, 24f), "KILLS " + g.KillsAt(now), 16, Color.white, TextAnchor.UpperRight);
            }

            if (Settings.ShowWeapons.Value)
            {
                Ghost.WeaponChange weapon = g.WeaponAt(p.Sample);
                string name = g.WeaponName(weapon);
                if (name != null)
                {
                    Color c = GhostRenderer.VariantColor(weapon.Variant);
                    float tx = x + 16f;
                    Sprite icon = Icon(name);
                    if (icon != null && icon.texture != null)
                    {
                        Rect tr = icon.textureRect;
                        var uv = new Rect(tr.x / icon.texture.width, tr.y / icon.texture.height, tr.width / icon.texture.width, tr.height / icon.texture.height);
                        float h = 30f, iw = h * tr.width / Mathf.Max(1f, tr.height);
                        Color before = GUI.color;
                        GUI.color = c;
                        GUI.DrawTextureWithTexCoords(new Rect(tx, 174f, iw, h), icon.texture, uv);
                        GUI.color = before;
                        tx += iw + 10f;
                    }
                    Text(new Rect(tx, 178f, 390f, 24f), name.ToUpperInvariant(), 16, c);
                }
            }
        }

        // The weapon's icon, from the weapons V1 has in this level (by name).
        private Sprite Icon(string weaponName)
        {
            var guns = Game.Get<GunControl>();
            if (guns != iconsFrom)
            {
                icons.Clear();
                iconsFrom = guns;
                if (guns != null)
                {
                    foreach (GameObject weapon in guns.allWeapons)
                    {
                        WeaponIcon wi = weapon != null ? weapon.GetComponent<WeaponIcon>() : null;
                        WeaponDescriptor desc = wi != null ? wi.weaponDescriptor : null;
                        if (desc != null && !string.IsNullOrEmpty(desc.weaponName) && desc.icon != null)
                        {
                            icons[desc.weaponName] = desc.icon;
                        }
                    }
                }
            }
            return icons.TryGetValue(weaponName, out Sprite sprite) ? sprite : null;
        }

        private void Timeline(ReplayDirector d, float w, bool top)
        {
            float x = 28f, width = w - 56f, y = Ref - 52f;
            if (top)
            {
                x = 460f;
                width = Mathf.Max(300f, w - 920f);
                y = 64f;
            }
            Box(new Rect(x - 8f, y - 44f, width + 16f, 82f), Panel);
            float[] bins = d.Histogram;
            if (bins.Length > 0)
            {
                float bw = width / bins.Length;
                for (int i = 0; i < bins.Length; i++)
                {
                    if (bins[i] > 0f)
                    {
                        float h = 4f + bins[i] * 22f;
                        Box(new Rect(x + i * bw, y - h - 2f, Mathf.Max(1f, bw - 1f), h), new Color(1f, 0.3f, 0.25f, 0.75f));
                    }
                }
            }
            Box(new Rect(x, y, width, 8f), new Color(1f, 1f, 1f, 0.15f));
            Box(new Rect(x, y, width * d.Time01, 8f), new Color(1f, 1f, 1f, 0.75f));
            if (d.Best != null && d.Best.Finished && d.Total > 0f)
            {
                Box(new Rect(x + width * Mathf.Clamp01(d.Best.Duration / d.Total) - 1f, y - 6f, 3f, 20f), GhostRenderer.Gold);
            }
            Box(new Rect(x + width * d.Time01 - 1.5f, y - 8f, 3f, 24f), Accent);
            Text(new Rect(x, y + 12f, 400f, 22f), Clock(d.T) + " / " + Clock(d.Total), 16, Color.white);
            string state = d.UserPaused ? "PAUSED" : d.Summary ? "DONE" : "PLAYING";
            Text(new Rect(x + width - 400f, y + 12f, 400f, 22f), $"x{d.Speed:0.##}  {state}", 16, Color.white, TextAnchor.UpperRight);
        }

        private static readonly string[] HelpLines = new string[4];
        private static Key helpExitKey = (Key)(-1);

        private static void Help(ReplayDirector d, bool top)
        {
            // (made again only when the exit key changes, not every frame)
            if (helpExitKey != Settings.ExitKey.Value)
            {
                helpExitKey = Settings.ExitKey.Value;
                HelpLines[0] = "TAB / 1-4  CAMERA        V  EYES / BEHIND        G  GAME HUD";
                HelpLines[2] = "<- ->  SEEK   UP/DOWN  SPEED   SPACE  PAUSE   R  RESTART";
                HelpLines[3] = "M  MARKERS   O  OTHER ATTEMPTS' WORLDS   H  HIDE HUD   ESC / " + helpExitKey.ToString().ToUpperInvariant() + "  LEAVE";
            }
            HelpLines[1] = d.Mode == ReplayCamera.FreeCam ? "WASD Q/E  FLY  (SHIFT FAST)" : "Q / E  PICK AN ATTEMPT      MOUSE  LOOK AROUND";
            float x = top ? 460f : 28f, y = top ? 110f : Ref - 210f;
            foreach (string line in HelpLines)
            {
                Text(new Rect(x, y, 900f, 20f), line, 13, new Color(1f, 1f, 1f, 0.5f));
                y += 19f;
            }
        }

        private static void SummaryCard(ReplayDirector d, float w)
        {
            int count = d.Ghosts.Count;
            Ghost best = d.Best;
            bool won = best != null && best.Finished;
            float top = Ref * 0.36f;
            float height = d.RunDays > 1 ? 198f : 170f;
            Box(new Rect(w * 0.5f - 380f, top, 760f, height), new Color(0f, 0f, 0f, 0.7f));
            string head = won ? $"ATTEMPT {best.Number}: LEVEL CLEAR" : $"{count} ATTEMPT{(count == 1 ? "" : "S")}";
            Text(new Rect(0f, top + 22f, w, 54f), head, 44, won ? GhostRenderer.Gold : Color.white, TextAnchor.MiddleCenter, bold: true);
            string line = $"{count} ATTEMPTS  |  {d.DeadSoFar} DEATHS";
            if (won)
            {
                line += "  |  TIME " + Clock(best.LevelTime, millis: true);
            }
            Text(new Rect(0f, top + 86f, w, 30f), line, 22, Color.white, TextAnchor.MiddleCenter);
            if (d.RunDays > 1)
            {
                // (runs go on across launches: this one took days)
                Text(new Rect(0f, top + 120f, w, 26f), won ? $"IT TOOK {d.RunDays} DAYS" : $"{d.RunDays} DAYS SO FAR", 19, won ? GhostRenderer.Gold : Color.white, TextAnchor.MiddleCenter);
            }
            Text(new Rect(0f, top + height - 46f, w, 24f), "SPACE  WATCH AGAIN", 15, Dim, TextAnchor.MiddleCenter);
        }

        // "#512" over the followed attempt (when you're not inside its head) and the winner.
        private static void Labels(ReplayDirector d, float w)
        {
            Camera cam = d.Camera;
            if (cam == null)
            {
                return;
            }
            Label(d, cam, w, d.Best);
            if (d.Follow != d.Best && d.Following)
            {
                Label(d, cam, w, d.Follow);
            }
        }

        private static void Label(ReplayDirector d, Camera cam, float w, Ghost g)
        {
            if (g == null || !g.AliveAt(d.T) || (g == d.Follow && d.Following && d.FirstPerson))
            {
                return;
            }
            Ghost.Pose p = g.PoseAt(d.T);
            Vector3 v = cam.WorldToViewportPoint(p.Eye + p.Up * 0.9f);
            if (v.z <= 0f)
            {
                return;
            }
            float x = v.x * w, y = (1f - v.y) * Ref;
            string text = "#" + g.Number + (g.Finished && g.IsBest ? "  WIN" : "");
            Text(new Rect(x - 100f, y - 24f, 200f, 24f), text, 16, g.Color, TextAnchor.MiddleCenter);
        }
    }
}
