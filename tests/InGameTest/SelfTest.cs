using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UltraAttempts;
using UltraAttempts.Recording;
using UltraAttempts.Replay;
using UnityEngine;

namespace UltraAttemptsSelfTest
{
    // Plays ULTRAKILL by itself to check UltraAttempts in the real game: dies and restarts in Level 0-1,
    // beats it (the replay should play before 0-2 loads), then watches a replay from the cheat menu in
    // 0-2. Writes selftest.log and screenshots into $ULTRAATTEMPTS_SELFTEST, then quits the game.
    [BepInPlugin("dev.ultracraft.attempts.selftest", "UltraAttempts SelfTest", "1.0.0")]
    [BepInDependency("dev.ultracraft.attempts")]
    public sealed class SelfTest : BaseUnityPlugin
    {
        private const int Deaths = 4;
        private const string First = "Level 0-1";
        private const string Second = "Level 0-2";

        private string output;
        private StreamWriter log;
        private int passed;
        private int failed;
        private bool timedOut;
        private readonly System.Random random = new System.Random(1);

        private void Awake()
        {
            output = Environment.GetEnvironmentVariable("ULTRAATTEMPTS_SELFTEST");
            if (string.IsNullOrEmpty(output))
            {
                return;
            }
            Directory.CreateDirectory(output);
            log = new StreamWriter(Path.Combine(output, "selftest.log")) { AutoFlush = true };
            Log("self-test armed");
            StartCoroutine(Watchdog());
            StartCoroutine(Run());
        }

        private void Log(string line)
        {
            log.WriteLine($"[{Time.realtimeSinceStartup,7:0.0}] {line}");
            Logger.LogInfo(line);
        }

        private void Check(bool ok, string what)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                failed++;
            }
            Log((ok ? "PASS  " : "FAIL  ") + what);
        }

        private IEnumerator Watchdog()
        {
            yield return new WaitForSecondsRealtime(600f);
            Log("FAIL  the whole test took over 10 minutes");
            failed++;
            Finish();
        }

        private void Finish()
        {
            Log($"DONE  {passed} passed, {failed} failed");
            log.Flush();
            Application.Quit();
        }

        private IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            timedOut = false;
            float end = Time.realtimeSinceStartup + seconds;
            while (!SafeTrue(condition))
            {
                if (Time.realtimeSinceStartup > end)
                {
                    timedOut = true;
                    Log($"FAIL  timed out waiting for: {what}");
                    failed++;
                    yield break;
                }
                yield return null;
            }
        }

        private static bool SafeTrue(Func<bool> condition)
        {
            try
            {
                return condition();
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(output, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Log("screenshot " + name);
            yield return Wait(0.3f);
        }

        private static bool InLevel(string scene) =>
            SceneHelper.CurrentScene == scene && SceneHelper.PendingScene == null && Game.Stats != null && Game.Player != null;

        private static Recorder Recorder => Recorder.Instance;

        private static ReplayDirector Director => ReplayDirector.Instance;

        private static void SetMode(ReplayCamera mode) =>
            typeof(ReplayDirector).GetMethod("SetMode", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Director, new object[] { mode });

        private static void SetFirstPerson(bool on) =>
            typeof(ReplayDirector).GetField("firstPerson", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Director, on);

        // Runs about for a moment, turning, so each attempt goes its own way.
        private IEnumerator RunAround(float seconds)
        {
            NewMovement nm = Game.Player;
            float angle = (float)random.NextDouble() * 360f;
            float turn = ((float)random.NextDouble() - 0.5f) * 240f;
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && nm != null && !nm.dead)
            {
                angle += turn * Time.deltaTime;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                nm.rb.velocity = new Vector3(dir.x * 22f, nm.rb.velocity.y, dir.z * 22f);
                nm.cc.rotationY = angle;
                nm.cc.rotationX = Mathf.Sin(Time.realtimeSinceStartup * 2f) * 20f;
                nm.cc.ApplyRotations();
                yield return null;
            }
        }

        private IEnumerator DieAndRestart(int attempt)
        {
            yield return Until(() => Recorder.Recording, 40f, $"attempt {attempt} to start recording");
            Check(Recorder.BannerNumber == attempt, $"attempt {attempt} is numbered {Recorder.BannerNumber} (banner)");
            if (attempt == 1)
            {
                yield return Wait(0.4f);
                yield return Shot("01-attempt-banner");
            }
            yield return RunAround(1f + attempt * 0.35f);
            StatsManager before = Game.Stats;
            Game.Player.GetHurt(1000, false);
            yield return Until(() => !Recorder.Recording, 5f, $"attempt {attempt} to end on death");
            Check(Game.Player.dead && !Recorder.Recording, $"dying ends attempt {attempt}");
            yield return Wait(0.8f);
            Game.Stats.Restart();
            yield return Until(() => InLevel(SceneHelper.CurrentScene) && !ReferenceEquals(Game.Stats, before) || (Game.Player != null && !Game.Player.dead && ReferenceEquals(Game.Stats, before)), 60f, "the restart");
            yield return Wait(0.5f);
        }

        private IEnumerator Run()
        {
            yield return Until(() => SceneHelper.CurrentScene == "Main Menu" && SceneHelper.PendingScene == null, 150f, "the main menu");
            yield return Wait(3f);
            Check(Recorder != null && Director != null, "UltraAttempts is running");
            Log("recordings go to " + Plugin.RecordingsRoot);

            // ---------- Level 0-1: die a few times, then beat it.
            Log("loading " + First);
            SceneHelper.LoadScene(First);
            yield return Until(() => InLevel(First), 90f, First + " to load");
            if (timedOut)
            {
                Finish();
                yield break;
            }
            Check(Recorder.Level != null && Recorder.Level.Scene == First, "the level is recordable: " + Recorder.Level?.Folder);

            for (int attempt = 1; attempt <= Deaths; attempt++)
            {
                yield return DieAndRestart(attempt);
            }

            yield return Until(() => Recorder.Recording, 40f, "the winning attempt to start");
            yield return RunAround(1.5f);
            FinalPit pit = Resources.FindObjectsOfTypeAll<FinalPit>().FirstOrDefault(p => p.gameObject.scene.IsValid() && !p.fakeEnd && !p.secondPit && !p.rankless);
            Check(pit != null, "found the final pit");
            if (pit == null)
            {
                Finish();
                yield break;
            }
            for (Transform t = pit.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(true);
                }
            }
            NewMovement nm = Game.Player;
            nm.transform.position = pit.transform.position;
            nm.rb.position = pit.transform.position;
            nm.rb.velocity = Vector3.down * 5f;
            yield return Until(() => nm.levelOver, 3f, "the pit to end the level by itself");
            if (!nm.levelOver)
            {
                Log("entering the pit by hand");
                typeof(FinalPit).GetMethod("OnTriggerEnter", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(pit, new object[] { nm.GetComponent<Collider>() });
            }
            Check(nm.levelOver, "the level is over");
            yield return Until(() => !Recorder.Recording, 10f, "the winning attempt to end");
            yield return Until(() => Game.Stats.infoSent, 20f, "the results screen");
            FinalRank rank = Game.Stats.fr;
            yield return Until(() => rank.complete, 60f, "the results to finish counting");
            yield return Wait(0.5f);
            yield return Shot("02-results-prompt");

            Plugin.Store.Flush();
            string run = Plugin.Store.CurrentRunPath(Recorder.Level.Key, out int attempts);
            Check(attempts == Deaths + 1, $"the run has {attempts} attempts (expected {Deaths + 1})");
            SessionData data = SessionFile.Load(run);
            Check(data.Info.Complete, "the run is complete (won)");
            Check(data.Attempts.Take(Deaths).All(a => a.End == AttemptEnd.Death) && data.Attempts.Last().End == AttemptEnd.Finish, "4 deaths then a finish: " + string.Join(",", data.Attempts.Select(a => a.End)));
            Check(data.Attempts.All(a => a.Count > 10), "every attempt has samples: " + string.Join(",", data.Attempts.Select(a => a.Count)));
            Check(data.Attempts.Last().Count > data.Attempts.First().Count, "the winning attempt is the longest");
            Check(data.Attempts.All(a => a.Events.Any(e => e.Kind == EventKind.Weapon)), "weapons recorded");
            long bytes = new FileInfo(run).Length;
            float seconds = data.Attempts.Sum(a => a.Duration);
            Log($"run file: {bytes} bytes for {seconds:0.0} s of attempts ({bytes / Mathf.Max(seconds, 0.1f):0} bytes/s incl. header)");

            // Continue: the replay should play first.
            rank.LevelChange();
            Check(Director.Presenting, "continuing from the results plays the replay first");
            Check(SceneHelper.PendingScene == null, "...and doesn't load the next level yet");
            yield return Until(() => Director.State == ReplayDirector.Phase.Playing, 20f, "the replay to load");
            Check(Director.Ghosts.Count == Deaths + 1, $"{Director.Ghosts.Count} ghosts (expected {Deaths + 1})");
            Check(Time.timeScale == 0f, "the level is frozen");
            Check(GameStateManager.Instance.IsStateActive("ultraattempts-replay") && GameStateManager.Instance.PlayerInputLocked, "your controls are locked");
            Check(Director.Best != null && Director.Best.Finished, "the best run is the win");
            yield return Wait(0.9f);
            yield return Shot("03-replay-next-to-die");
            Check(Director.Follow != null && Director.Follow.Dies, $"next-to-die follows an attempt that dies (#{Director.Follow?.Number})");
            SetMode(ReplayCamera.BestRun);
            yield return Wait(1f);
            Check(Director.Follow == Director.Best, "best-run follows the win");
            yield return Shot("04-best-run");
            SetMode(ReplayCamera.Overview);
            yield return Wait(1.5f);
            yield return Shot("05-overview");
            SetFirstPerson(false);
            SetMode(ReplayCamera.NextToDie);
            yield return Wait(1.2f);
            yield return Shot("06-behind");
            yield return Until(() => Director.DeadSoFar >= 1, 20f, "an attempt to die in the replay");
            Check(Director.DeadSoFar >= 1, $"deaths counted as they happen ({Director.DeadSoFar})");
            yield return Shot("07-after-deaths");
            SetFirstPerson(true);
            Director.RequestExit();
            Check(!Director.Busy, "leaving the replay");
            Check(SceneHelper.PendingScene == Second || SceneHelper.CurrentScene == Second, "then the next level loads: " + SceneHelper.PendingScene);

            // ---------- Level 0-2: one death, then watch from the cheat menu.
            yield return Until(() => InLevel(Second), 90f, Second + " to load");
            if (timedOut)
            {
                Finish();
                yield break;
            }
            Check(Time.timeScale > 0f, "time runs again in the next level");
            yield return DieAndRestart(1);
            yield return Until(() => Recorder.Recording, 40f, "the second attempt in " + Second);
            yield return RunAround(2f);

            var cheats = Game.Get<CheatsManager>();
            var watch = cheats?.GetCheatInstance<Cheats.WatchAttempts>();
            Check(watch != null, "Watch Every Attempt is in the cheat menu");
            Check(cheats?.GetCheatInstance<Cheats.CameraSetting>() != null && cheats.GetCheatInstance<Cheats.SourceSetting>() != null, "the replay settings are in the cheat menu");
            if (watch == null)
            {
                Finish();
                yield break;
            }
            Transform camera = Game.Get<CameraController>().transform;
            Vector3 localBefore = camera.localPosition;
            cheats.ToggleCheat(watch);
            Check(watch.IsActive, "the cheat turns on");
            yield return Until(() => Director.State == ReplayDirector.Phase.Playing, 20f, "the cheat-menu replay");
            Check(Director.Ghosts.Count == 2, $"{Director.Ghosts.Count} ghosts: the death and the attempt in progress");
            Check(Director.Ghosts.Any(g => g.EndReason == AttemptEnd.InProgress), "the attempt in progress is in it");
            yield return Wait(1f);
            yield return Shot("08-cheat-replay");
            SetMode(ReplayCamera.Overview);
            yield return Wait(1.5f);
            yield return Shot("09-cheat-overview");
            SetMode(ReplayCamera.FreeCam);
            yield return Wait(0.5f);
            Director.RequestExit();
            yield return null;
            yield return null;
            Check(!Director.Busy && !watch.IsActive, "leaving turns the cheat back off");
            Check(Time.timeScale > 0f, "time runs again");
            Check(!GameStateManager.Instance.IsStateActive("ultraattempts-replay") && !GameStateManager.Instance.PlayerInputLocked, "your controls come back");
            Check((camera.localPosition - localBefore).sqrMagnitude < 0.01f, "the camera is back on V1");
            Check(Recorder.Recording, "the attempt in progress carries on recording");
            yield return Wait(1f);
            yield return Shot("10-back-to-playing");
            Finish();
        }
    }
}
