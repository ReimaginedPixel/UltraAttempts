using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UltraAttempts.Replay
{
    // Plays every attempt at a level at once, Geometry Dash style. The world freezes (time stops, your
    // controls lock, the HUD hides), the ghosts run on real time, and the camera follows one of them,
    // frames them all, or flies free. Everything goes back as it was after.
    internal sealed class ReplayDirector
    {
        public static ReplayDirector Instance { get; private set; }

        public enum Phase
        {
            Idle,
            Waiting,   // asked for from the cheat menu: starts once the menu's closed
            Loading,
            Playing,
        }

        private enum Origin
        {
            Cheat,
            LevelEnd,
        }

        private const string StateKey = "ultraattempts-replay";
        private const float BlendTime = 0.4f;
        private const float DeathHold = 0.3f;
        private const float EndPadding = 0.75f;
        private const float SummarySeconds = 4f;
        private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        public Phase State { get; private set; } = Phase.Idle;

        public bool Presenting => State == Phase.Loading || State == Phase.Playing;

        public bool Busy => State != Phase.Idle;

        // What the HUD shows.
        public List<Ghost> Ghosts { get; private set; } = new List<Ghost>();
        public Ghost Best { get; private set; }
        public Ghost Follow { get; private set; }
        public float Time01 => total > 0f ? Mathf.Clamp01(t / total) : 0f;
        public float T => t;
        public float Total => total;
        public float Speed => Speeds[speedIndex];
        public bool UserPaused => userPaused;
        public ReplayCamera Mode => mode;
        public bool FirstPerson => firstPerson;
        public bool HudVisible => hudVisible;
        public bool Summary => summaryAt >= 0f;
        public int Alive { get; private set; }
        public int DeadSoFar { get; private set; }
        public int FinishedSoFar { get; private set; }
        public float[] Histogram { get; private set; } = new float[0];
        public string LevelName { get; private set; } = "";
        public string SourceLabel { get; private set; } = "";
        public Camera Camera => cc != null ? cc.cam : null;
        public float Flash { get; set; }
        public bool Following => Follow != null && (mode == ReplayCamera.NextToDie || mode == ReplayCamera.BestRun || manual != null);

        private readonly ReplayHud hud = new ReplayHud();

        private Origin origin;
        private ReplaySource source;
        private StatsManager waitingIn;
        private bool thenNextLevel;
        private FinalRank levelEndRank;
        private FinalRank levelEndShownFor;
        private Task<LoadResult> loading;

        private Ghost[] byEnd = new Ghost[0];      // every ghost, by when it ends
        private Ghost[] deaths = new Ghost[0];     // ghosts that died (markers), by when
        private Ghost[] dying = new Ghost[0];      // died, restarted or quit, by when
        private Ghost[] finishes = new Ghost[0];
        private float total;
        private float t;
        private int speedIndex = 3;
        private bool userPaused;
        private bool hudVisible = true;
        private bool markers;
        private float summaryAt = -1f;

        private ReplayCamera mode;
        private bool firstPerson;
        private Ghost manual;
        private bool autoPicked;
        private Vector3 camPos;
        private Quaternion camRot = Quaternion.identity;
        private bool havePose;
        private Vector3 blendPos;
        private Quaternion blendRot = Quaternion.identity;
        private float blendStart = -10f;

        private float chaseYaw;
        private float chasePitch = 12f;
        private float chaseDistance = 6f;
        private Vector3 overviewPos;
        private Vector3 overviewVelocity;
        private Quaternion overviewRot = Quaternion.identity;
        private float overviewYaw;
        private float overviewUserYaw;
        private float overviewUserPitch;
        private float overviewZoom = 1f;
        private Vector3 freePos;
        private float freeYaw;
        private float freePitch;
        private float freeSpeed = 18f;

        private GameObject root;
        private GameState state;
        private GhostRenderer renderer;
        private ReplaySounds sounds;
        private CameraController cc;
        private Vector3 savedLocalPos;
        private Quaternion savedLocalRot;
        private Camera hudCam;
        private bool hudCamWasEnabled;
        private Canvas hiddenCanvas;
        private bool hiddenCanvasWasEnabled;
        private List<GameObject> revealed;
        private int environment;

        private sealed class LoadResult
        {
            public List<Ghost> Ghosts = new List<Ghost>();
            public string Label = "";
            public string Error;
        }

        public ReplayDirector()
        {
            Instance = this;
        }

        // ---------------------------------------------------------------- starting and stopping

        public void RequestFromCheat()
        {
            if (State != Phase.Idle)
            {
                return;
            }
            origin = Origin.Cheat;
            source = Settings.Source.Value;
            waitingIn = Game.Stats;
            State = Phase.Waiting;
        }

        public void CancelFromCheat()
        {
            if (State == Phase.Waiting)
            {
                State = Phase.Idle;
            }
            else if (Presenting && origin == Origin.Cheat)
            {
                Stop();
            }
        }

        public void RequestExit()
        {
            if (Presenting)
            {
                Stop();
            }
        }

        // The level's been beaten: play this run (the one just won). thenNextLevel: continue to the next
        // level after (the results screen's "continue" was pressed); else back to the results screen.
        public bool TryLevelEnd(FinalRank rank, bool thenNextLevel)
        {
            Recorder recorder = Recorder.Instance;
            if (State != Phase.Idle || recorder?.Level == null || rank == null)
            {
                return false;
            }
            recorder.FinishNow();
            Plugin.Store.CurrentRunPath(recorder.Level.Key, out int attempts);
            if (attempts < 1 || (thenNextLevel && attempts < Settings.MinAttemptsAtLevelEnd.Value))
            {
                return false;
            }
            origin = Origin.LevelEnd;
            source = ReplaySource.ThisRun;
            this.thenNextLevel = thenNextLevel;
            levelEndRank = rank;
            levelEndShownFor = rank;
            return Begin();
        }

        // Whether the results screen already had its replay (Auto plays it once).
        public bool LevelEndShown(FinalRank rank) => rank != null && levelEndShownFor == rank;

        public static bool ResultsScreenUp(out FinalRank rank)
        {
            StatsManager sm = Game.Stats;
            rank = sm != null && sm.infoSent ? sm.fr : null;
            return rank != null && rank.gameObject.activeInHierarchy;
        }

        private bool Begin()
        {
            cc = Game.Get<CameraController>();
            NewMovement nm = Game.Player;
            LevelInfo level = Recorder.Instance?.Level;
            if (cc == null || nm == null || level == null || GameStateManager.Instance == null)
            {
                State = Phase.Idle;
                return false;
            }

            root = new GameObject("UltraAttempts replay");
            state = new GameState(StateKey, root)
            {
                playerInputLock = LockMode.Lock,
                cameraInputLock = LockMode.Lock,
                cursorLock = LockMode.Lock,
            };
            GameStateManager.Instance.RegisterState(state);

            savedLocalPos = cc.transform.localPosition;
            savedLocalRot = cc.transform.localRotation;
            camPos = cc.transform.position;
            camRot = cc.transform.rotation;
            havePose = true;

            var post = Game.Get<PostProcessV2_Handler>();
            hudCam = post != null ? post.hudCam : null;
            if (hudCam == null)
            {
                Transform found = cc.transform.Find("HUD Camera");
                hudCam = found != null ? found.GetComponent<Camera>() : null;
            }
            if (hudCam != null)
            {
                hudCamWasEnabled = hudCam.enabled;
                hudCam.enabled = false;
            }
            if (origin == Origin.LevelEnd && levelEndRank != null)
            {
                Canvas canvas = levelEndRank.GetComponentInParent<Canvas>();
                hiddenCanvas = canvas != null ? canvas.rootCanvas : null;
                if (hiddenCanvas != null)
                {
                    hiddenCanvasWasEnabled = hiddenCanvas.enabled;
                    hiddenCanvas.enabled = false;
                }
            }
            revealed = Settings.RevealRooms.Value ? RoomReveal.Reveal() : null;
            Freeze();

            environment = LayerMaskDefaults.Get(LMD.Environment);
            renderer = new GhostRenderer(cc.cam, root.transform);
            sounds = new ReplaySounds(root);
            LevelName = level.Name;
            Ghosts = new List<Ghost>();
            Best = null;
            Follow = null;
            manual = null;
            total = 0f;
            t = 0f;
            summaryAt = -1f;
            userPaused = false;
            speedIndex = 3;
            Flash = 0f;
            mode = Settings.Camera.Value;
            firstPerson = Settings.FirstPerson.Value;
            markers = Settings.DeathMarkers.Value;
            overviewPos = camPos;
            overviewRot = camRot;
            InitFreeCam();

            RawAttempt snapshot = source == ReplaySource.LastFinishedRun ? null : Recorder.Instance.Snapshot();
            string thisRun = Plugin.Store.CurrentRunPath(level.Key, out int runAttempts);
            ReplaySource wanted = source;
            int maxGhosts = Settings.MaxGhosts.Value;
            loading = Task.Run(() => Load(level, wanted, thisRun, runAttempts, snapshot, maxGhosts));
            State = Phase.Loading;
            return true;
        }

        private void Stop()
        {
            if (!Presenting)
            {
                State = Phase.Idle;
                return;
            }
            State = Phase.Idle;
            Origin was = origin;
            try
            {
                RoomReveal.Hide(revealed);
                revealed = null;
                if (hudCam != null)
                {
                    hudCam.enabled = hudCamWasEnabled;
                }
                if (hiddenCanvas != null)
                {
                    hiddenCanvas.enabled = hiddenCanvasWasEnabled;
                }
                if (cc != null)
                {
                    cc.transform.localPosition = savedLocalPos;
                    cc.transform.localRotation = savedLocalRot;
                }
                Unfreeze();
                if (GameStateManager.Instance != null && GameStateManager.Instance.IsStateActive(StateKey))
                {
                    GameStateManager.Instance.PopState(StateKey);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting the game back after a replay: " + e);
            }
            renderer?.Dispose();
            sounds?.Dispose();
            renderer = null;
            sounds = null;
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
            }
            root = null;
            hudCam = null;
            hiddenCanvas = null;
            havePose = false;
            Ghosts = new List<Ghost>();
            Follow = null;
            Best = null;
            loading = null;

            if (was == Origin.Cheat)
            {
                Cheats.ReplayEnded();
            }
            else if (levelEndRank != null)
            {
                FinalRank rank = levelEndRank;
                levelEndRank = null;
                if (thenNextLevel)
                {
                    Patches.LevelChangeAllowed = true;
                    try
                    {
                        rank.LevelChange();
                    }
                    finally
                    {
                        Patches.LevelChangeAllowed = false;
                    }
                }
            }
        }

        private void Freeze()
        {
            Time.timeScale = 0f;
            SetGamePitch(0f);
        }

        private void Unfreeze()
        {
            if (Game.Paused)
            {
                return;  // (unpausing puts time back)
            }
            SetGamePitch(1f);
            var time = Game.Get<TimeController>();
            Time.timeScale = time != null ? time.timeScale * time.timeScaleModifier : 1f;
        }

        // ULTRAKILL's sound effects (not the music) go quiet, as in its pause menu.
        private static void SetGamePitch(float pitch)
        {
            var mixers = Game.Get<AudioMixerController>();
            if (mixers == null)
            {
                return;
            }
            mixers.allSound?.SetFloat("allPitch", pitch);
            mixers.doorSound?.SetFloat("allPitch", pitch);
        }

        // ---------------------------------------------------------------- loading

        // Off the main thread: wait for recordings still being written, read the runs, decode.
        private static LoadResult Load(LevelInfo level, ReplaySource source, string thisRun, int runAttempts, RawAttempt snapshot, int maxGhosts)
        {
            var result = new LoadResult();
            try
            {
                Plugin.Store.Flush();
                if (source == ReplaySource.Auto)
                {
                    source = runAttempts > 0 || snapshot != null ? ReplaySource.ThisRun : ReplaySource.LastFinishedRun;
                }
                var runs = new List<SessionData>();
                List<string> files = SessionStore.RunFiles(level.Folder);
                switch (source)
                {
                    case ReplaySource.ThisRun:
                        result.Label = "THIS RUN";
                        if (thisRun != null && File.Exists(thisRun))
                        {
                            runs.Add(SessionFile.Load(thisRun, Plugin.Log.LogWarning));
                        }
                        break;
                    case ReplaySource.LastFinishedRun:
                        result.Label = "LAST WIN";
                        snapshot = null;
                        for (int i = files.Count - 1; i >= 0; i--)
                        {
                            if (SessionFile.Scan(files[i]).Complete)
                            {
                                runs.Add(SessionFile.Load(files[i], Plugin.Log.LogWarning));
                                break;
                            }
                        }
                        break;
                    case ReplaySource.Everything:
                        result.Label = "EVERY ATTEMPT";
                        foreach (string file in files)
                        {
                            try
                            {
                                runs.Add(SessionFile.Load(file, Plugin.Log.LogWarning));
                            }
                            catch (Exception e)
                            {
                                Plugin.Log.LogWarning($"skipping {Path.GetFileName(file)}: {e.Message}");
                            }
                        }
                        break;
                }

                int order = 0;
                bool renumber = source == ReplaySource.Everything;
                foreach (SessionData run in runs)
                {
                    foreach (RawAttempt raw in run.Attempts)
                    {
                        if (raw.Count < 2)
                        {
                            continue;
                        }
                        Ghost g = Ghost.FromRaw(raw, run.Info.Header);
                        g.Order = order++;
                        if (renumber)
                        {
                            g.Number = g.Order + 1;
                        }
                        result.Ghosts.Add(g);
                    }
                }
                if (snapshot != null && snapshot.Count >= 2)
                {
                    var header = new SessionHeader
                    {
                        CapsuleHeight = level.CapsuleHeight,
                        CapsuleRadius = level.CapsuleRadius,
                        CapsuleCenterY = level.CapsuleCenterY,
                    };
                    Ghost g = Ghost.FromRaw(snapshot, header);
                    g.Order = order++;
                    if (renumber)
                    {
                        g.Number = g.Order + 1;
                    }
                    result.Ghosts.Add(g);
                }

                if (maxGhosts > 0 && result.Ghosts.Count > maxGhosts)
                {
                    int cut = result.Ghosts.Count - maxGhosts;
                    result.Ghosts = result.Ghosts.Where((g, i) => i >= cut || g.Finished).ToList();
                }
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Plugin.Log.LogError("loading attempts: " + e);
            }
            return result;
        }

        private void Loaded(LoadResult result)
        {
            if (result.Error != null || result.Ghosts.Count == 0)
            {
                Stop();
                Game.HudMessage(result.Error != null ? "<color=#FF4343>Couldn't load the attempts</color> (see BepInEx\\LogOutput.log)" : "No attempts recorded at this level yet.");
                return;
            }
            Ghosts = result.Ghosts;
            int count = Ghosts.Count;
            Best = Ghosts.Where(g => g.Finished).OrderBy(g => g.LevelTime).FirstOrDefault()
                ?? Ghosts.OrderByDescending(g => g.Duration).First();
            Best.IsBest = true;
            foreach (Ghost g in Ghosts)
            {
                float k = count > 1 ? g.Order / (float)(count - 1) : 1f;
                g.Color = Settings.Colors.Value == GhostColors.Progress
                    ? Color.HSVToRGB(Mathf.Lerp(0f, 0.38f, k), 0.75f, 1f)
                    : Color.HSVToRGB((g.Order * 0.618034f + 0.05f) % 1f, 0.6f, 1f);
                if (g.EndReason == AttemptEnd.InProgress)
                {
                    g.Color = new Color(0.9f, 0.9f, 0.95f);
                }
                if (g.Finished)
                {
                    g.Color = GhostRenderer.Gold;
                }
            }
            byEnd = Ghosts.OrderBy(g => g.Duration).ToArray();
            deaths = byEnd.Where(g => g.EndReason == AttemptEnd.Death).ToArray();
            dying = byEnd.Where(g => g.Dies).ToArray();
            finishes = byEnd.Where(g => g.Finished).ToArray();
            total = byEnd[byEnd.Length - 1].Duration;
            renderer.BuildMarkers(deaths);

            var bins = new float[160];
            foreach (Ghost g in dying)
            {
                bins[Mathf.Clamp((int)(g.Duration / Mathf.Max(total, 0.01f) * bins.Length), 0, bins.Length - 1)]++;
            }
            float max = bins.Max();
            for (int i = 0; i < bins.Length; i++)
            {
                bins[i] = max > 0f ? Mathf.Log(1f + bins[i]) / Mathf.Log(1f + max) : 0f;
            }
            Histogram = bins;
            SourceLabel = result.Label;
            State = Phase.Playing;
            BeginBlend();
            Plugin.Log.LogInfo($"replaying {count} attempts at {LevelName} ({result.Label.ToLowerInvariant()}), {total:0.0} s");
        }

        // ---------------------------------------------------------------- every frame

        public void Update()
        {
            switch (State)
            {
                case Phase.Idle:
                    WatchKeyOnResults();
                    return;
                case Phase.Waiting:
                    WaitForMenu();
                    return;
            }
            if (root == null || cc == null)
            {
                Stop();  // (the scene went away under the replay)
                return;
            }
            Time.timeScale = 0f;

            if (State == Phase.Loading)
            {
                if (loading != null && loading.IsCompleted)
                {
                    Loaded(loading.Result);
                }
                return;
            }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool menu = Game.Paused;
            if (!menu)
            {
                HandleInput(dt);
                if (State != Phase.Playing)
                {
                    return;
                }
            }
            float before = t;
            if (!menu && !userPaused && summaryAt < 0f)
            {
                t += dt * Speed;
            }
            if (t >= total + EndPadding)
            {
                t = total + EndPadding;
                if (Settings.Loop.Value)
                {
                    Restart();
                    before = t;
                }
                else if (summaryAt < 0f)
                {
                    summaryAt = Time.unscaledTime;
                }
                else if (!menu && Time.unscaledTime - summaryAt > SummarySeconds)
                {
                    Stop();
                    return;
                }
            }
            if (t > before && t - before < 0.5f)
            {
                Happenings(before, t);
            }
            UpdateCamera(dt);
            Draw();
        }

        public void LateUpdate()
        {
            if (Presenting)
            {
                Time.timeScale = 0f;
                if (cc != null && havePose)
                {
                    cc.transform.SetPositionAndRotation(camPos, camRot);
                }
            }
        }

        // (after CameraController's own LateUpdate, which would put the camera back on V1)
        public void ApplyCamera(CameraController camera)
        {
            if (Presenting && havePose && camera == cc)
            {
                camera.transform.SetPositionAndRotation(camPos, camRot);
            }
        }

        public void OnGUI()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;  // (it only draws: no need for IMGUI's other passes)
            }
            if (Presenting)
            {
                if (!Game.Paused)
                {
                    hud.Draw(this, State == Phase.Loading);  // (not over the pause or cheat menu)
                }
            }
            else if (State == Phase.Idle && Settings.AtLevelEnd.Value != LevelEndReplay.Off && ResultsScreenUp(out FinalRank rank) && !Game.Paused)
            {
                int attempts = 0;
                string path = Recorder.Instance?.Level != null ? Plugin.Store.CurrentRunPath(Recorder.Instance.Level.Key, out attempts) : null;
                if (path != null && attempts >= 1)
                {
                    bool playsFirst = Settings.AtLevelEnd.Value == LevelEndReplay.Auto && attempts >= Settings.MinAttemptsAtLevelEnd.Value && !LevelEndShown(rank);
                    hud.DrawResultsPrompt(attempts, Settings.WatchKey.Value, playsFirst);
                }
            }
        }

        private void WaitForMenu()
        {
            NewMovement nm = Game.Player;
            if (!ReferenceEquals(Game.Stats, waitingIn))
            {
                State = Phase.Idle;  // (asked for in a level that's gone now)
                return;
            }
            if (Recorder.Instance?.Level == null || nm == null)
            {
                State = Phase.Idle;
                Game.HudMessage("Nothing to replay here: attempts are recorded in levels.");
                Cheats.ReplayEnded();
                return;
            }
            var cheats = Game.Get<CheatsManager>();
            if (Game.Paused || (cheats != null && cheats.IsMenuOpen()))
            {
                return;
            }
            if (nm.dead)
            {
                State = Phase.Idle;
                Game.HudMessage("Respawn first, then watch your attempts.");
                Cheats.ReplayEnded();
                return;
            }
            if (!Begin())
            {
                Cheats.ReplayEnded();
            }
        }

        private void WatchKeyOnResults()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || Settings.AtLevelEnd.Value == LevelEndReplay.Off || Settings.WatchKey.Value == Key.None)
            {
                return;
            }
            if (keyboard[Settings.WatchKey.Value].wasPressedThisFrame && !Game.Paused && ResultsScreenUp(out FinalRank rank))
            {
                TryLevelEnd(rank, thenNextLevel: false);
            }
        }

        private void Restart()
        {
            t = 0f;
            summaryAt = -1f;
            manual = null;
            Follow = null;
            renderer.ClearTracers();
            BeginBlend();
        }

        // ---------------------------------------------------------------- input

        private void HandleInput(float dt)
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb == null)
            {
                return;
            }
            if ((Settings.ExitKey.Value != Key.None && kb[Settings.ExitKey.Value].wasPressedThisFrame))
            {
                Stop();
                return;
            }
            bool shift = kb.shiftKey.isPressed;
            if (kb.spaceKey.wasPressedThisFrame)
            {
                if (summaryAt >= 0f)
                {
                    Restart();
                }
                else
                {
                    userPaused = !userPaused;
                }
            }
            if (kb.rKey.wasPressedThisFrame)
            {
                Restart();
            }
            if (kb.tabKey.wasPressedThisFrame)
            {
                SetMode((ReplayCamera)(((int)mode + (shift ? 3 : 1)) % 4));
            }
            if (kb.digit1Key.wasPressedThisFrame)
            {
                SetMode(ReplayCamera.NextToDie);
            }
            if (kb.digit2Key.wasPressedThisFrame)
            {
                SetMode(ReplayCamera.BestRun);
            }
            if (kb.digit3Key.wasPressedThisFrame)
            {
                SetMode(ReplayCamera.Overview);
            }
            if (kb.digit4Key.wasPressedThisFrame)
            {
                SetMode(ReplayCamera.FreeCam);
            }
            if (kb.vKey.wasPressedThisFrame)
            {
                firstPerson = !firstPerson;
                Settings.FirstPerson.Value = firstPerson;
                BeginBlend();
            }
            if (kb.hKey.wasPressedThisFrame)
            {
                hudVisible = !hudVisible;
            }
            if (kb.mKey.wasPressedThisFrame)
            {
                markers = !markers;
            }
            if (kb.leftArrowKey.wasPressedThisFrame)
            {
                Seek(shift ? -1f : -5f);
            }
            if (kb.rightArrowKey.wasPressedThisFrame)
            {
                Seek(shift ? 1f : 5f);
            }
            if (kb.upArrowKey.wasPressedThisFrame)
            {
                speedIndex = Mathf.Min(speedIndex + 1, Speeds.Length - 1);
            }
            if (kb.downArrowKey.wasPressedThisFrame)
            {
                speedIndex = Mathf.Max(speedIndex - 1, 0);
            }
            if (mode != ReplayCamera.FreeCam)
            {
                if (kb.qKey.wasPressedThisFrame)
                {
                    PickManual(-1);
                }
                if (kb.eKey.wasPressedThisFrame)
                {
                    PickManual(1);
                }
            }

            Vector2 look = mouse != null ? mouse.delta.ReadValue() * 0.12f : Vector2.zero;
            float scroll = mouse != null ? Mathf.Sign(mouse.scroll.ReadValue().y) * (Mathf.Abs(mouse.scroll.ReadValue().y) > 0.01f ? 1f : 0f) : 0f;
            if (Following && !firstPerson)
            {
                chaseYaw += look.x;
                chasePitch = Mathf.Clamp(chasePitch - look.y, -30f, 75f);
                chaseDistance = Mathf.Clamp(chaseDistance * Mathf.Pow(0.87f, scroll), 2f, 40f);
            }
            else if (!Following && mode == ReplayCamera.Overview)
            {
                overviewUserYaw += look.x;
                overviewUserPitch = Mathf.Clamp(overviewUserPitch - look.y, -40f, 50f);
                overviewZoom = Mathf.Clamp(overviewZoom * Mathf.Pow(0.87f, scroll), 0.2f, 5f);
            }
            else if (mode == ReplayCamera.FreeCam)
            {
                freeYaw += look.x;
                freePitch = Mathf.Clamp(freePitch - look.y, -89f, 89f);
                freeSpeed = Mathf.Clamp(freeSpeed * Mathf.Pow(1.2f, scroll), 2f, 300f);
                var move = new Vector3(
                    (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                    (kb.eKey.isPressed ? 1f : 0f) - (kb.qKey.isPressed ? 1f : 0f),
                    (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
                float speed = freeSpeed * (shift ? 4f : 1f) * (kb.ctrlKey.isPressed ? 0.25f : 1f);
                freePos += Quaternion.Euler(freePitch, freeYaw, 0f) * move * (speed * dt);
            }
        }

        private void SetMode(ReplayCamera next)
        {
            if (next == ReplayCamera.FreeCam && mode != ReplayCamera.FreeCam)
            {
                InitFreeCam();
            }
            if (next == ReplayCamera.Overview && mode != ReplayCamera.Overview)
            {
                overviewPos = camPos;
                overviewVelocity = Vector3.zero;
                overviewRot = camRot;
            }
            mode = next;
            manual = null;
            Follow = null;
            Settings.Camera.Value = next;
            BeginBlend();
        }

        private void InitFreeCam()
        {
            freePos = camPos;
            Vector3 euler = camRot.eulerAngles;
            freeYaw = euler.y;
            freePitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -89f, 89f);
        }

        private void Seek(float seconds)
        {
            t = Mathf.Clamp(t + seconds, 0f, total);
            summaryAt = -1f;
            if (Follow != null && autoPicked)
            {
                Follow = null;
            }
            if (manual != null && !manual.AliveAt(t))
            {
                manual = null;
            }
            renderer.ClearTracers();
            BeginBlend();
        }

        // Q/E: follow the previous/next attempt still alive (by attempt number).
        private void PickManual(int direction)
        {
            List<Ghost> alive = Ghosts.Where(g => g.AliveAt(t)).OrderBy(g => g.Number).ToList();
            if (alive.Count == 0)
            {
                return;
            }
            int at = Follow != null ? alive.IndexOf(Follow) : -1;
            if (at < 0)
            {
                at = direction > 0 ? -1 : alive.Count;
            }
            manual = alive[(at + direction + alive.Count) % alive.Count];
        }

        // ---------------------------------------------------------------- the camera

        private void BeginBlend()
        {
            blendPos = camPos;
            blendRot = camRot;
            blendStart = Time.unscaledTime;
        }

        private bool Blending => Time.unscaledTime - blendStart < BlendTime;

        private Ghost PickTarget()
        {
            if (manual != null)
            {
                if (t < manual.Duration + DeathHold)
                {
                    autoPicked = false;
                    return manual;
                }
                manual = null;
            }
            switch (mode)
            {
                case ReplayCamera.NextToDie:
                    autoPicked = true;
                    return NextToDie();
                case ReplayCamera.BestRun:
                    autoPicked = false;
                    return Best;
                default:
                    return null;
            }
        }

        // The attempt that dies next (at least MinShotSeconds away, so the early deaths don't flicker
        // past); once nothing else will die, the winner.
        private Ghost NextToDie()
        {
            if (Follow != null && autoPicked && Follow.Dies && t < Follow.Duration + DeathHold)
            {
                return Follow;
            }
            float soonest = t + Settings.MinShotSeconds.Value;
            Ghost pick = null, next = null;
            int first = FirstEndingAfter(dying, t);
            for (int i = first; i < dying.Length; i++)
            {
                Ghost g = dying[i];
                if (next == null)
                {
                    next = g;
                }
                if (g.Duration >= soonest)
                {
                    pick = g;
                    break;
                }
            }
            pick = pick ?? next;
            if (pick == null && Best != null)
            {
                pick = Best;
            }
            return pick ?? Follow;
        }

        // Index of the first ghost (sorted by Duration) still alive at t.
        private static int FirstEndingAfter(Ghost[] sorted, float time)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid].Duration <= time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        private void UpdateCamera(float dt)
        {
            Ghost target = PickTarget();
            if (target != Follow)
            {
                if (Follow != null && target != null && Follow.Dies && t >= Follow.Duration && firstPerson)
                {
                    Flash = 0.45f;
                }
                Follow = target;
                BeginBlend();
            }

            Vector3 pos;
            Quaternion rot;
            if (Follow != null)
            {
                Ghost.Pose p = Follow.PoseAt(Mathf.Min(t, Follow.Duration));
                if (firstPerson)
                {
                    pos = p.Eye;
                    rot = p.Look;
                }
                else
                {
                    Vector3 forward = Vector3.ProjectOnPlane(p.Look * Vector3.forward, p.Up);
                    if (forward.sqrMagnitude < 1e-4f)
                    {
                        forward = Vector3.ProjectOnPlane(p.Look * Vector3.up, p.Up);
                    }
                    rot = Quaternion.LookRotation(forward.normalized, p.Up) * Quaternion.AngleAxis(chaseYaw, Vector3.up) * Quaternion.AngleAxis(chasePitch, Vector3.right);
                    pos = Collide(p.Eye, p.Eye - rot * Vector3.forward * chaseDistance);
                }
            }
            else if (mode == ReplayCamera.Overview)
            {
                Overview(dt, out pos, out rot);
            }
            else
            {
                rot = Quaternion.Euler(freePitch, freeYaw, 0f);
                pos = freePos;
            }

            if (Blending)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.unscaledTime - blendStart) / BlendTime);
                camPos = Vector3.Lerp(blendPos, pos, k);
                camRot = Quaternion.Slerp(blendRot, rot, k);
            }
            else
            {
                camPos = pos;
                camRot = rot;
            }
        }

        // Frames every attempt still alive: their middle, from behind (the way they're heading) and above.
        private void Overview(float dt, out Vector3 pos, out Quaternion rot)
        {
            Vector3 sum = Vector3.zero, heading = Vector3.zero;
            int n = 0;
            foreach (Ghost g in Ghosts)
            {
                if (!g.AliveAt(t))
                {
                    continue;
                }
                Vector3 at = g.PoseAt(t).Body;
                sum += at;
                heading += at - g.PoseAt(Mathf.Max(0f, t - 0.5f)).Body;
                n++;
            }
            if (n == 0)
            {
                Ghost last = Best ?? (byEnd.Length > 0 ? byEnd[byEnd.Length - 1] : null);
                if (last != null)
                {
                    sum = last.EndPose.Body;
                    n = 1;
                }
            }
            Vector3 center = n > 0 ? sum / n : overviewPos + overviewRot * Vector3.forward * 10f;
            float spread = 0f;
            if (n > 1)
            {
                foreach (Ghost g in Ghosts)
                {
                    if (g.AliveAt(t))
                    {
                        spread += Vector3.Distance(g.PoseAt(t).Body, center);
                    }
                }
                spread /= n;
            }
            heading.y = 0f;
            if (heading.sqrMagnitude > 0.5f)
            {
                float yaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
                overviewYaw = Mathf.LerpAngle(overviewYaw, yaw, 1f - Mathf.Exp(-dt * 0.8f));
            }
            float distance = Mathf.Clamp(spread * 2.2f + 9f, 10f, 220f) * overviewZoom;
            Quaternion angle = Quaternion.Euler(Mathf.Clamp(32f + overviewUserPitch, -10f, 85f), overviewYaw + overviewUserYaw, 0f);
            Vector3 desired = Collide(center, center - angle * Vector3.forward * distance);
            overviewPos = Vector3.SmoothDamp(overviewPos, desired, ref overviewVelocity, 0.35f, Mathf.Infinity, dt);
            Vector3 toCenter = center - overviewPos;
            Quaternion look = toCenter.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toCenter) : overviewRot;
            overviewRot = Quaternion.Slerp(overviewRot, look, 1f - Mathf.Exp(-dt * 6f));
            pos = overviewPos;
            rot = overviewRot;
        }

        // Pulls a camera in front of walls between it and what it looks at.
        private Vector3 Collide(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float length = d.magnitude;
            if (length < 0.01f)
            {
                return to;
            }
            if (Physics.SphereCast(from, 0.3f, d / length, out RaycastHit hit, length, environment, QueryTriggerInteraction.Ignore))
            {
                return from + d / length * Mathf.Max(0.5f, hit.distance - 0.1f);
            }
            return to;
        }

        // ---------------------------------------------------------------- drawing

        // Things that happen between two moments of playing forward: deaths (pops), finishes (chime),
        // shots and punches.
        private void Happenings(float from, float to)
        {
            int ended = 0, seed = 0;
            for (int i = FirstEndingAfter(byEnd, from); i < byEnd.Length && byEnd[i].Duration <= to; i++)
            {
                Ghost g = byEnd[i];
                if (g.Finished)
                {
                    sounds.Finish();
                }
                else if (g.Dies && ended++ == 0)
                {
                    seed = g.Number;
                }
            }
            if (ended > 0)
            {
                sounds.Death(seed, ended);
            }

            if (!Settings.ShowWeapons.Value)
            {
                return;
            }
            int spawned = 0;
            float now = Time.unscaledTime;
            foreach (Ghost g in Ghosts)
            {
                if (!g.AliveAt(to) || spawned > 48)
                {
                    continue;
                }
                int a = Mathf.FloorToInt(from * g.Rate) + 1;
                int b = Mathf.Min(Mathf.FloorToInt(to * g.Rate), g.Count - 1);
                for (int k = a; k <= b; k++)
                {
                    var flags = (SampleFlags)g.Flags[k];
                    Ghost.WeaponChange w = g.WeaponAt(k);
                    bool shot = (flags & SampleFlags.Shot) != 0 || (w.Slot == 3 && (flags & SampleFlags.FireHeld) != 0 && k % 2 == 0);
                    if (shot && w.Slot > 0)
                    {
                        Vector3 eye = g.Body[k] + g.Eye[k];
                        Vector3 dir = g.Look[k] * Vector3.forward;
                        Vector3 muzzle = eye + g.Look[k] * new Vector3(0.42f, -0.3f, 0.9f);
                        Vector3 end = Physics.Raycast(eye, dir, out RaycastHit hit, 90f, environment, QueryTriggerInteraction.Ignore) ? hit.point : eye + dir * 90f;
                        Color c = Color.Lerp(GhostRenderer.VariantColor(w.Variant), Color.white, 0.35f);
                        renderer.AddTracer(muzzle, end, c, now, 0.14f, 0.07f);
                        spawned++;
                    }
                    if ((flags & SampleFlags.Punch) != 0)
                    {
                        Vector3 eye = g.Body[k] + g.Eye[k];
                        Vector3 dir = g.Look[k] * Vector3.forward;
                        renderer.AddTracer(eye + dir * 0.5f + g.Look[k] * new Vector3(-0.3f, -0.3f, 0f), eye + dir * 1.9f, new Color(1f, 0.55f, 0.2f), now, 0.12f, 0.22f);
                        spawned++;
                    }
                }
            }
        }

        private void Draw()
        {
            float opacity = Settings.GhostOpacity.Value;
            bool weapons = Settings.ShowWeapons.Value;
            bool pov = Following && firstPerson && !Blending;
            int alive = 0;
            renderer.BeginFrame(t);
            foreach (Ghost g in Ghosts)
            {
                if (!g.AliveAt(t))
                {
                    continue;
                }
                alive++;
                Ghost.Pose p = g.PoseAt(t);
                bool followed = g == Follow;
                bool featured = followed || g.IsBest;
                renderer.DrawGhost(g, p, featured ? 0.85f : opacity, featured, followed && pov, weapons);
            }
            renderer.EndFrame();
            Alive = alive;

            for (int i = FirstEndingAfter(byEnd, t - 0.6f); i < byEnd.Length && byEnd[i].Duration <= t; i++)
            {
                Ghost g = byEnd[i];
                Ghost.Pose end = g.EndPose;
                renderer.DrawBurst(g, end.Body + end.Up * 0.5f, t - g.Duration);
            }
            if (markers)
            {
                renderer.DrawMarkers(FirstEndingAfter(deaths, t));
            }
            renderer.DrawTracers(Time.unscaledTime);
            DeadSoFar = FirstEndingAfter(dying, t);
            FinishedSoFar = FirstEndingAfter(finishes, t);

            if (Best != null && !(pov && Follow == Best))
            {
                renderer.SetTrail(true, Best, 0f, Mathf.Min(t, Best.Duration), GhostRenderer.Gold);
            }
            else
            {
                renderer.SetTrail(true, null, 0f, 0f, Color.white);
            }
            if (Follow != null && Follow != Best && !pov)
            {
                renderer.SetTrail(false, Follow, Mathf.Max(0f, t - 4f), Mathf.Min(t, Follow.Duration), Follow.Color);
            }
            else
            {
                renderer.SetTrail(false, null, 0f, 0f, Color.white);
            }
        }
    }
}
