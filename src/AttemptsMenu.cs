using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UltraAttempts.Recording;
using UltraAttempts.Replay;
using UnityEngine;

namespace UltraAttempts
{
    // UltraAttempts' own menu, on the pause screen and the main menu (not a cheat): every level's attempt
    // count (reset it to start again at 1), export and import of the recordings, and the settings people
    // change most. Laid out for 1080p and scaled, beside the game's own menu.
    internal sealed class AttemptsMenu
    {
        private const float Ref = 1080f;

        private bool open;
        private Vector2 scroll;
        private Task<List<LevelSummary>> loading;
        private List<LevelSummary> levels = new List<LevelSummary>();
        private int waitingImports;
        private Task<string> working;
        private string status = "";
        private string confirm;          // the folder whose RESET was clicked once
        private float confirmUntil;

        private GUIStyle text;
        private GUIStyle title;
        private GUIStyle button;
        private GUIStyle panel;

        private static bool Visible
        {
            get
            {
                if (!Game.Paused || (ReplayDirector.Instance != null && ReplayDirector.Instance.Presenting))
                {
                    return false;
                }
                var cheats = Game.Get<CheatsManager>();
                return cheats == null || !cheats.IsMenuOpen();
            }
        }

        public void Update()
        {
            if (loading != null && loading.IsCompleted)
            {
                levels = loading.Status == TaskStatus.RanToCompletion ? loading.Result : new List<LevelSummary>();
                loading = null;
            }
            if (working != null && working.IsCompleted)
            {
                status = working.Status == TaskStatus.RanToCompletion ? working.Result : "FAILED: " + working.Exception?.GetBaseException().Message;
                working = null;
                Refresh();
            }
        }

        private void Refresh()
        {
            if (loading != null)
            {
                return;
            }
            SessionStore store = Plugin.Store;
            loading = Task.Run(() =>
            {
                string import = Path.Combine(store.Root, SessionStore.ImportFolder);
                waitingImports = Directory.Exists(import) ? Directory.GetFiles(import, "*.zip").Length : 0;
                return store.Levels();
            });
        }

        private void Run(Func<string> job)
        {
            if (working == null)
            {
                status = "WORKING...";
                working = Task.Run(job);
            }
        }

        public void OnGUI()
        {
            if (!Visible)
            {
                open = false;
                return;
            }
            Styles();
            Matrix4x4 old = GUI.matrix;
            float s = Screen.height / Ref;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            try
            {
                if (GUI.Button(new Rect(20f, 20f, 240f, 40f), open ? "ULTRAATTEMPTS  <" : "ULTRAATTEMPTS  >", button))
                {
                    open = !open;
                    status = "";
                    if (open)
                    {
                        Refresh();
                    }
                }
                if (open)
                {
                    Panel(new Rect(20f, 70f, 600f, Ref - 110f));
                }
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        private void Panel(Rect area)
        {
            GUI.Box(area, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(area.x + 16f, area.y + 12f, area.width - 32f, area.height - 24f));

            LevelInfo here = Recorder.Instance?.Level;
            if (here != null)
            {
                GUILayout.Label("THIS LEVEL", title);
                int count = Plugin.Store.AttemptCount(here.Folder);
                Plugin.Store.CurrentRunPath(here.Key, out int thisRun);
                GUILayout.Label($"{here.Name.ToUpperInvariant()}  ({Game.DifficultyName(here.Difficulty).ToUpperInvariant()})", text);
                GUILayout.Label($"ATTEMPT {count}  |  {thisRun} SINCE THE LAST WIN", text);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("WATCH", button, GUILayout.Width(150f)))
                {
                    Watch(here.Folder, here.Scene);
                }
                ResetButton(here.Folder, $"{here.Name}: back to attempt 1 (its recordings stay)");
                if (GUILayout.Button("EXPORT", button, GUILayout.Width(150f)))
                {
                    string folder = here.Folder;
                    Run(() => "EXPORTED TO " + Short(Plugin.Store.Export(folder)));
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(14f);
            }

            GUILayout.Label("SETTINGS", title);
            Cycle("AFTER BEATING A LEVEL", Settings.AtLevelEnd,
                v => v == LevelEndReplay.Auto ? "PLAY THE ATTEMPTS FIRST" : v == LevelEndReplay.Prompt ? "ONLY IF I PRESS " + Settings.WatchKey.Value.ToString().ToUpperInvariant() : "NEVER (JUST SAVE THEM)");
            Cycle("RECORD", Settings.World,
                v => v == WorldDetail.Everything ? "EVERYTHING THAT MOVES" : v == WorldDetail.Enemies ? "V1, ENEMIES AND DOORS" : "ONLY V1");
            Cycle("GHOSTS", Settings.Model, v => v == GhostModel.V1 ? "V1" : v == GhostModel.V2 ? "V2" : "CRYSTALS");
            Toggle("\"ATTEMPT 37\" WHEN AN ATTEMPT STARTS", Settings.ShowAttemptCounter);
            Toggle("RECORDING", Settings.Record);
            GUILayout.Space(14f);

            GUILayout.Label("EVERY LEVEL", title);
            if (loading != null && levels.Count == 0)
            {
                GUILayout.Label("READING...", text);
            }
            else if (levels.Count == 0)
            {
                GUILayout.Label("NOTHING RECORDED YET", text);
            }
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            foreach (LevelSummary level in levels)
            {
                string wins = level.Wins == 1 ? "1 WIN" : level.Wins + " WINS";
                GUILayout.Label($"{level.Level.ToUpperInvariant()} {level.Difficulty.ToUpperInvariant()}  |  ATTEMPT {level.Attempts}  |  {level.Runs} RUN{(level.Runs == 1 ? "" : "S")}, {wins}", text);
                GUILayout.BeginHorizontal();
                // (custom levels can't be loaded from here)
                if (!string.IsNullOrEmpty(level.Scene) && !level.Level.StartsWith("Custom") && GUILayout.Button("WATCH", button, GUILayout.Width(120f)))
                {
                    Watch(level.Folder, level.Scene);
                }
                ResetButton(level.Folder, $"{level.Level} {level.Difficulty}: back to attempt 1 (its recordings stay)");
                if (GUILayout.Button("EXPORT", button, GUILayout.Width(100f)))
                {
                    string folder = level.Folder;
                    Run(() => "EXPORTED TO " + Short(Plugin.Store.Export(folder)));
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("EXPORT EVERYTHING", button))
            {
                Run(() => "EXPORTED TO " + Short(Plugin.Store.Export(null)));
            }
            if (GUILayout.Button(waitingImports > 0 ? $"IMPORT ({waitingImports} WAITING)" : "IMPORT", button))
            {
                Run(() =>
                {
                    ImportResult r = Plugin.Store.Import();
                    if (r.Zips == 0 && r.Failed == 0)
                    {
                        return "PUT .ZIP FILES IN " + SessionStore.ImportFolder.ToUpperInvariant() + " FIRST (OPEN FOLDER)";
                    }
                    return $"IMPORTED {r.Runs} RUN{(r.Runs == 1 ? "" : "S")} FROM {r.Zips} ZIP{(r.Zips == 1 ? "" : "S")}" + (r.Skipped > 0 ? $", {r.Skipped} ALREADY HERE" : "") + (r.Failed > 0 ? $", {r.Failed} FAILED (SEE THE LOG)" : "");
                });
            }
            if (GUILayout.Button("OPEN FOLDER", button))
            {
                Directory.CreateDirectory(Path.Combine(Plugin.Store.Root, SessionStore.ImportFolder));
                Application.OpenURL("file:///" + Plugin.Store.Root.Replace('\\', '/'));
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Label(status, text);
            }
            GUILayout.EndArea();
        }

        // Every attempt at a level, played at once (no cheats needed). Here: once the pause menu closes.
        // Another level: it loads, then plays once V1 has landed.
        private void Watch(string folder, string scene)
        {
            ReplayDirector director = ReplayDirector.Instance;
            if (director == null || director.Busy)
            {
                return;
            }
            LevelInfo here = Recorder.Instance?.Level;
            if (here != null && scene == here.Scene)
            {
                NewMovement nm = Game.Player;
                if (nm != null && nm.dead)
                {
                    status = "RESPAWN FIRST, THEN WATCH";
                    return;
                }
                director.RequestFromMenu(folder == here.Folder ? null : folder);
                open = false;
                Game.Get<OptionsManager>()?.UnPause();
                return;
            }
            if (string.IsNullOrEmpty(scene))
            {
                status = "CAN'T FIND THAT LEVEL";
                return;
            }
            open = false;
            if (here != null)
            {
                Game.Get<OptionsManager>()?.UnPause();  // (leaving a level from its pause menu)
            }
            Plugin.Log.LogInfo($"loading {scene} to watch its attempts");
            ReplayDirector.WatchLevel(scene, folder);
        }

        // RESET, then SURE? for a few seconds: a second click resets.
        private void ResetButton(string folder, string done)
        {
            bool asking = confirm == folder && Time.unscaledTime < confirmUntil;
            if (GUILayout.Button(asking ? "SURE?" : "RESET", button, GUILayout.Width(100f)))
            {
                if (asking)
                {
                    confirm = null;
                    Plugin.Store.ResetCount(folder);
                    status = done.ToUpperInvariant();
                    Plugin.Log.LogInfo(done);
                    Refresh();
                }
                else
                {
                    confirm = folder;
                    confirmUntil = Time.unscaledTime + 3f;
                }
            }
        }

        private void Cycle<T>(string label, BepInEx.Configuration.ConfigEntry<T> entry, Func<T, string> name) where T : struct, Enum
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(250f));
            if (GUILayout.Button(name(entry.Value), button))
            {
                T[] values = (T[])Enum.GetValues(typeof(T));
                int at = Array.IndexOf(values, entry.Value);
                entry.Value = values[(at + 1) % values.Length];
            }
            GUILayout.EndHorizontal();
        }

        private void Toggle(string label, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(250f));
            if (GUILayout.Button(entry.Value ? "ON" : "OFF", button))
            {
                entry.Value = !entry.Value;
            }
            GUILayout.EndHorizontal();
        }

        private static string Short(string path) => path.StartsWith(Plugin.Store.Root) ? path.Substring(Plugin.Store.Root.Length).TrimStart('\\', '/') : path;

        private void Styles()
        {
            if (text != null)
            {
                return;
            }
            Font font = null;
            foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f != null && f.name.ToUpperInvariant().Contains("VCR"))
                {
                    font = f;
                    break;
                }
            }
            text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
            text.normal.textColor = new Color(0.85f, 0.85f, 0.9f);
            title = new GUIStyle(text) { fontSize = 22 };
            title.normal.textColor = new Color(1f, 0.27f, 0.2f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            button.normal.background = Solid(new Color(0.12f, 0.12f, 0.14f, 0.95f));
            button.hover.background = Solid(new Color(0.45f, 0.1f, 0.08f, 0.95f));
            button.active.background = Solid(new Color(0.8f, 0.2f, 0.15f, 1f));
            button.normal.textColor = button.hover.textColor = button.active.textColor = Color.white;
            button.padding = new RectOffset(10, 10, 6, 6);
            panel = new GUIStyle(GUI.skin.box);
            panel.normal.background = Solid(new Color(0f, 0f, 0f, 0.82f));
            if (font != null)
            {
                text.font = title.font = button.font = font;
            }
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
