using System.Collections.Generic;
using System.IO;
using System.Linq;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltraAttempts
{
    // ULTRAKILL's singletons, looked up without ULTRAKILL creating one when there's none (its
    // Instance property makes a fresh one for some types).
    internal static class Game
    {
        public static T Get<T>() where T : MonoSingleton
        {
            var instance = MonoSingleton.GetInstance(typeof(T)) as T;
            return instance != null ? instance : null;
        }

        public static NewMovement Player => Get<NewMovement>();

        public static StatsManager Stats => Get<StatsManager>();

        public static bool Paused
        {
            get
            {
                var options = Get<OptionsManager>();
                return options != null && options.paused;
            }
        }

        public static string[] DifficultyNames = { "Harmless", "Lenient", "Standard", "Violent", "Brutal", "UKMD" };

        public static string DifficultyName(int difficulty) =>
            difficulty >= 0 && difficulty < DifficultyNames.Length ? DifficultyNames[difficulty] : "Difficulty " + difficulty;

        // Whether there's a level here worth recording: not the menu, the sandbox or the Cyber Grind.
        public static bool IsRecordable(NewMovement nm)
        {
            if (nm == null || Stats == null || nm.endlessMode)
            {
                return false;
            }
            if (MapInfoBase.Instance != null && MapInfoBase.Instance.sandboxTools)
            {
                return false;
            }
            switch (SceneHelper.CurrentScene)
            {
                case null:
                case "":
                case "Main Menu":
                case "Intro":
                case "Bootstrap":
                case "Endless":
                case "uk_construct":
                case "Credits":
                    return false;
            }
            var tracker = Get<PlayerTracker>();
            return tracker == null || tracker.playerType == PlayerType.FPS;
        }

        // The level you're in, for the recordings: scene (or custom level) and difficulty.
        public static LevelInfo CurrentLevel(string root, NewMovement nm)
        {
            string scene = SceneHelper.CurrentScene ?? "";
            string id = scene;
            if (SceneHelper.IsPlayingCustom && GameStateManager.Instance.currentCustomGame != null)
            {
                id = Path.Combine("Custom", GameStateManager.Instance.currentCustomGame.uniqueIdentifier ?? scene);
            }
            var prefs = Get<PrefsManager>();
            int difficulty = prefs != null ? prefs.GetInt("difficulty") : 0;
            string name = scene;
            if (MapInfoBase.Instance != null && !string.IsNullOrEmpty(MapInfoBase.Instance.levelName) && MapInfoBase.Instance.levelName != "LEVEL NAME")
            {
                name = MapInfoBase.Instance.levelName;
                if (!string.IsNullOrEmpty(MapInfoBase.Instance.layerName) && MapInfoBase.Instance.layerName != "LAYER /// NUMBER")
                {
                    name = MapInfoBase.Instance.layerName + " / " + name;
                }
            }
            var level = new LevelInfo
            {
                Key = id + "|" + difficulty,
                Folder = SessionStore.LevelFolder(root, id, DifficultyName(difficulty)),
                Scene = scene,
                Name = name,
                Difficulty = difficulty,
            };
            if (nm != null && nm.playerCollider != null)
            {
                level.CapsuleHeight = 3.5f;  // (standing: it shrinks while sliding)
                level.CapsuleRadius = nm.playerCollider.radius;
                level.CapsuleCenterY = nm.playerCollider.center.y;
            }
            return level;
        }

        // A level object's place in the scene, to find it again in a later visit: each step is its name
        // (without "(Clone)") and which of the same-named siblings it is. Checkpoints swap rooms for
        // clones of themselves, which keeps these the same.
        public static string PathOf(Transform t)
        {
            var steps = new List<string>();
            for (; t != null; t = t.parent)
            {
                string name = CleanName(t.name);
                int ordinal = 0;
                if (t.parent != null)
                {
                    for (int i = 0; i < t.GetSiblingIndex(); i++)
                    {
                        if (CleanName(t.parent.GetChild(i).name) == name)
                        {
                            ordinal++;
                        }
                    }
                }
                else
                {
                    foreach (GameObject root in t.gameObject.scene.GetRootGameObjects())
                    {
                        if (root.transform == t)
                        {
                            break;
                        }
                        if (CleanName(root.name) == name)
                        {
                            ordinal++;
                        }
                    }
                }
                steps.Add(name + "#" + ordinal);
            }
            steps.Reverse();
            return string.Join("/", steps);
        }

        // The object at a PathOf path in the current scene (the same-named one in the same place,
        // else an active one of that name, else any), or null.
        public static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            string[] steps = path.Split('/');
            IEnumerable<Transform> level = SceneManager.GetActiveScene().GetRootGameObjects().Select(g => g.transform);
            Transform found = null;
            foreach (string step in steps)
            {
                int hash = step.LastIndexOf('#');
                string name = hash >= 0 ? step.Substring(0, hash) : step;
                int.TryParse(hash >= 0 ? step.Substring(hash + 1) : "0", out int ordinal);
                List<Transform> named = level.Where(c => CleanName(c.name) == name).ToList();
                if (named.Count == 0)
                {
                    return null;
                }
                found = ordinal < named.Count ? named[ordinal] : named.FirstOrDefault(c => c.gameObject.activeInHierarchy) ?? named[0];
                level = found.Cast<Transform>();
            }
            return found;
        }

        public static string CleanName(string name) => (name ?? "").Replace("(Clone)", "").Trim();

        public static void HudMessage(string message)
        {
            var hud = Get<HudMessageReceiver>();
            if (hud != null)
            {
                hud.SendHudMessage(message, "", "", 0, silent: false);
            }
            Plugin.Log.LogInfo(message);
        }
    }
}
