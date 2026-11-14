using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UltraAttempts.Recording;
using UltraAttempts.Replay;
using UnityEngine;

[assembly: InternalsVisibleTo("UltraAttempts.SelfTest")]

namespace UltraAttempts
{
    // UltraAttempts: records every attempt at every ULTRAKILL level, then plays them all at once like
    // Geometry Dash's attempt replays. Recording isn't a cheat (it changes nothing); watching is in the
    // cheat menu, or plays by itself once the level's beaten.
    [BepInPlugin("dev.ultracraft.attempts", "UltraAttempts", "1.3.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static SessionStore Store;
        internal static string RecordingsRoot;

        private static bool started;

        private void Awake()
        {
            if (started)
            {
                return;
            }
            started = true;
            Log = Logger;
            Settings.Bind(Config);

            RecordingsRoot = string.IsNullOrWhiteSpace(Settings.Folder.Value)
                ? Path.Combine(Paths.BepInExRootPath, "UltraAttempts")
                : Environment.ExpandEnvironmentVariables(Settings.Folder.Value.Trim());
            string testFolder = Environment.GetEnvironmentVariable("ULTRAATTEMPTS_FOLDER");
            if (!string.IsNullOrWhiteSpace(testFolder))
            {
                RecordingsRoot = testFolder;  // (the in-game self-test keeps its recordings apart)
            }
            Store = new SessionStore(RecordingsRoot, message => Log.LogWarning(message))
            {
                ContinueAcrossVisits = Settings.ContinueRunsAcrossVisits.Value,
                KeepRunsPerLevel = Settings.KeepRunsPerLevel.Value,
            };
            Settings.ContinueRunsAcrossVisits.SettingChanged += (_, __) => Store.ContinueAcrossVisits = Settings.ContinueRunsAcrossVisits.Value;
            Settings.KeepRunsPerLevel.SettingChanged += (_, __) => Store.KeepRunsPerLevel = Settings.KeepRunsPerLevel.Value;

            new Harmony("dev.ultracraft.attempts").PatchAll(typeof(Plugin).Assembly);

            var host = new GameObject("UltraAttempts");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Runtime>();

            Log.LogInfo($"recording every attempt into {RecordingsRoot}; watch them in the cheat menu (Home / ~) under ATTEMPT REPLAY");
        }
    }

    // Runs the recorder and the replay every frame (on its own object, which outlives scene loads).
    internal sealed class Runtime : MonoBehaviour
    {
        private Recorder recorder;
        private ReplayDirector director;
        private readonly AttemptsMenu menu = new AttemptsMenu();

        private void Awake()
        {
            recorder = new Recorder(Plugin.Store);
            director = new ReplayDirector();
        }

        private void Update()
        {
            try
            {
                director.Update();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("replay: " + e);
                director.RequestExit();
            }
            menu.Update();
        }

        private void LateUpdate()
        {
            try
            {
                recorder.LateUpdate(director.Presenting);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("recording: " + e);
            }
            director.LateUpdate();
        }

        private void OnGUI()
        {
            director.OnGUI();
            try
            {
                menu.OnGUI();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("menu: " + e);
            }
            if (Event.current.type == EventType.Repaint && !director.Busy && Settings.ShowAttemptCounter.Value && recorder.Recording && !Game.Paused)
            {
                ReplayHud.DrawBanner(recorder.BannerNumber, recorder.BannerShownAt);
            }
        }

        private void OnApplicationQuit()
        {
            try
            {
                recorder.OnLeavingScene(null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("saving the last attempt: " + e);
            }
            Plugin.Store.Flush(5000);
        }
    }
}
