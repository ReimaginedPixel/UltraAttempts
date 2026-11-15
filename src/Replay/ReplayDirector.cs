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
    // controls lock, the HUD hides), the ghosts run on real time, and the camera follows one of them
    // (seeing its enemies and doors as they were then), frames them all, or flies free. Everything goes
    // back as it was after.
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
        private const float DeathAnimationHold = 1.1f;   // (DeathAnimations: long enough to see its head fly)
        private const float EndPadding = 0.75f;
        private const float SummarySeconds = 4f;
        private const float ChaseLag = 0.12f;
        private const float ChaseLift = 1.1f;      // the chase camera's pivot, this far above the eyes
        private const float ChaseClose = 2.6f;     // nearer than this, the followed V1 goes see-through
        private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };
        private static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

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
        public int RunDays { get; private set; }   // how many days the run shown (the best one's) took
        public Camera Camera => cc != null ? cc.cam : null;
        public float Flash { get; set; }
        public string Notice { get; private set; } = "";   // a line shown for a moment (a key's setting changed)
        public float NoticeAt { get; private set; } = -100f;
        public int OverlaysShown { get; private set; }     // other attempts' worlds drawn this frame
        public bool Following => Follow != null && (mode == ReplayCamera.NextToDie || mode == ReplayCamera.BestRun || manual != null);

        // Through an attempt's eyes: its animated weapon and the game's HUD (FirstPersonView).
        private FirstPersonView firstPersonView;
        public bool GameHudShown => firstPersonView != null && firstPersonView.HudShown;

        // Inside the followed attempt's head (once the cut's done, or nearly): only its weapon shows, as a viewmodel.
        private bool InItsEyes => Following && firstPerson && (!Blending || (camPos - followPose.Eye).sqrMagnitude < 1.5f * 1.5f);

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
        private Ghost.Pose[] poses = new Ghost.Pose[0];   // every ghost alive this frame, by Index
        private Ghost.Pose followPose;                    // the followed one, smoothed: what the camera sees
        private float screenShake = 1f;

        private float chaseYaw;
        private float chasePitch = 12f;
        private float chaseDistance = 7.5f;
        private Quaternion chaseHeading = Quaternion.identity;
        private float chaseReach;
        private bool chaseFresh = true;
        private Vector3 overviewPos;
        private Vector3 overviewVelocity;
        private Quaternion overviewRot = Quaternion.identity;
        private Vector3 overviewCenter;
        private Vector3 overviewCenterVelocity;
        private bool overviewFresh = true;
        private float overviewYaw;
        private float overviewUserYaw;
        private float overviewUserPitch;
        private float overviewZoom = 1f;
        private Vector3 freePos;
        private Vector3 freeVelocity;
        private Quaternion freeLook = Quaternion.identity;
        private float freeYaw;
        private float freePitch;
        private float freeSpeed = 18f;

        private GameObject root;
        private GameState state;
        private GhostRenderer renderer;
        private WorldPlayback world;
        private ThingPlayback things;
        private WorldCache cache;
        private readonly List<Ghost> overlaid = new List<Ghost>();   // the other attempts whose worlds show now (nearest first)
        private readonly List<Ghost> overlayPicks = new List<Ghost>();
        private float overlaysPickedAt = -10f;
        private bool overlaysBroken;
        private ReplaySounds sounds;
        private CameraController cc;
        private Vector3 savedLocalPos;
        private Quaternion savedLocalRot;
        private Camera hudCam;
        private bool hudCamWasEnabled;
        private Canvas hiddenCanvas;
        private bool hiddenCanvasWasEnabled;
        private SectionPlayback sections;   // the level's parts as the attempt shown had them
        private int environment;

        private sealed class LoadResult
        {
            public List<Ghost> Ghosts = new List<Ghost>();
            public string Label = "";
            public string Error;
        }

        private readonly Comparison<Ghost> byDistance;

        public ReplayDirector()
        {
            Instance = this;
            byDistance = (a, b) => FromCamera(a).CompareTo(FromCamera(b));
        }

        // ---------------------------------------------------------------- starting and stopping

        public void RequestFromCheat()
        {
            RequestFromMenu(null);
        }

        // Watch from the ULTRAATTEMPTS menu (or the cheat menu): starts once the menus are closed.
        // folder: another recordings folder of this same level (another difficulty), or null for this one.
        public void RequestFromMenu(string folder)
        {
            if (State != Phase.Idle)
            {
                return;
            }
            origin = Origin.Cheat;
            source = Settings.Source.Value;
            folderOverride = folder;
            waitingIn = Game.Stats;
            State = Phase.Waiting;
        }

        // Watch another level's attempts from the menu: load it, then play once V1 has landed in it.
        public static void WatchLevel(string scene, string folder)
        {
            pendingScene = scene;
            pendingFolder = folder;
            pendingSince = Time.unscaledTime;
            pendingReadyAt = -1f;
            Time.timeScale = 1f;  // (as the pause menu's Quit Mission does)
            SceneHelper.LoadScene(scene);
        }

        private static string pendingScene;
        private static string pendingFolder;
        private static float pendingSince;
        private static float pendingReadyAt;
        private string folderOverride;

        private void WatchWhenLoaded()
        {
            if (pendingScene == null)
            {
                return;
            }
            if (Time.unscaledTime - pendingSince > 90f)
            {
                pendingScene = null;  // (never got there)
                return;
            }
            bool there = SceneHelper.CurrentScene == pendingScene && SceneHelper.PendingScene == null && Game.Player != null && Recorder.Instance?.Level != null && !Game.Paused;
            if (!there)
            {
                pendingReadyAt = -1f;
                return;
            }
            if (pendingReadyAt < 0f)
            {
                pendingReadyAt = Time.unscaledTime + 1.5f;  // (let V1 land first)
            }
            if (Time.unscaledTime >= pendingReadyAt && !Game.Player.dead)
            {
                string folder = pendingFolder;
                pendingScene = null;
                RequestFromMenu(folder);
            }
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
            sections = Settings.RevealRooms.Value ? new SectionPlayback(cc.cam, root.transform) : null;
            Freeze();

            environment = LayerMaskDefaults.Get(LMD.Environment);
            renderer = new GhostRenderer(cc.cam, root.transform);
            world = new WorldPlayback(cc.cam, root.transform);
            things = new ThingPlayback(cc.cam);
            cache = new WorldCache();
            overlaid.Clear();
            overlaysPickedAt = -10f;
            overlaysBroken = false;
            sounds = new ReplaySounds(root);
            gameSounds = new GameSounds(root.transform, cc.cam);
            firstPersonView = new FirstPersonView(cc, hudCam, hiddenCanvas);
            var prefs = Game.Get<PrefsManager>();
            screenShake = prefs != null ? Mathf.Clamp(prefs.GetFloat("screenShake", 1f), 0f, 2f) : 1f;
            LevelName = level.Name;
            Ghosts = new List<Ghost>();
            Best = null;
            Follow = null;
            manual = null;
            RunDays = 0;
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
            overviewFresh = true;
            chaseFresh = true;
            InitFreeCam();

            RawAttempt snapshot = source == ReplaySource.LastFinishedRun ? null : Recorder.Instance.Snapshot();
            string thisRun = Plugin.Store.CurrentRunPath(level.Key, out int runAttempts);
            if (folderOverride != null && folderOverride != level.Folder)
            {
                // Another difficulty's recordings of this level: its own run in progress (the newest
                // run, if it isn't won or closed), else its last win; not the attempt you're on.
                level = new LevelInfo
                {
                    Key = level.Key + "|other",
                    Folder = folderOverride,
                    Scene = level.Scene,
                    Name = level.Name,
                    Difficulty = level.Difficulty,
                    CapsuleHeight = level.CapsuleHeight,
                    CapsuleRadius = level.CapsuleRadius,
                    CapsuleCenterY = level.CapsuleCenterY,
                };
                snapshot = null;
                thisRun = null;
                runAttempts = 0;
                List<string> runs = SessionStore.RunFiles(folderOverride);
                if (runs.Count > 0)
                {
                    try
                    {
                        SessionInfo newest = SessionFile.Scan(runs[runs.Count - 1]);
                        if (!newest.Complete && !newest.Closed)
                        {
                            thisRun = newest.Path;
                            runAttempts = newest.Attempts;
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("reading the newest run: " + e.Message);
                    }
                }
            }
            folderOverride = null;
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
                // (first, while the doors, the level's objects and its enemies are all still there to put back)
                things?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting the level's objects back after a replay: " + e);
            }
            things = null;
            try
            {
                world?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting the level back after a replay: " + e);
            }
            world = null;
            cache?.Dispose();
            cache = null;
            overlaid.Clear();
            // (before the HUD camera goes back: it puts back your gun and HUD as it found them)
            firstPersonView?.Dispose();
            firstPersonView = null;
            DropSections();
            try
            {
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
            StopGameSounds();
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
            poses = new Ghost.Pose[0];
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

        // Worlds kept decoded from loading (the newest attempts'); the rest are read again when they're wanted.
        private const int WorldsKeptFromLoading = 16;

        // Off the main thread: wait for recordings still being written, read the runs (one attempt at a time),
        // decode.
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
                var runs = new List<string>();
                List<string> files = SessionStore.RunFiles(level.Folder);
                switch (source)
                {
                    case ReplaySource.ThisRun:
                        result.Label = "THIS RUN";
                        if (thisRun != null && File.Exists(thisRun))
                        {
                            runs.Add(thisRun);
                        }
                        break;
                    case ReplaySource.LastFinishedRun:
                        result.Label = "LAST WIN";
                        snapshot = null;
                        for (int i = files.Count - 1; i >= 0; i--)
                        {
                            if (SessionFile.Scan(files[i]).Complete)
                            {
                                runs.Add(files[i]);
                                break;
                            }
                        }
                        break;
                    case ReplaySource.Everything:
                        result.Label = "EVERY ATTEMPT";
                        runs.AddRange(files);
                        break;
                }

                int order = 0;
                bool renumber = source == ReplaySource.Everything;
                int snapshotRun = runs.Count;  // (the run being played: its file's, once it has one)
                var withWorlds = new Queue<Ghost>();
                for (int r = 0; r < runs.Count; r++)
                {
                    if (SamePath(runs[r], thisRun))
                    {
                        snapshotRun = r;
                    }
                    SessionHeader header = null;
                    int run = r;
                    try
                    {
                        header = SessionFile.Scan(runs[r]).Header;
                        SessionFile.Read(runs[r], raw =>
                        {
                            if (raw.Count < 2)
                            {
                                return;
                            }
                            Ghost g = Ghost.FromRaw(raw, header);
                            g.Order = order++;
                            g.Run = run;
                            if (renumber)
                            {
                                g.Number = g.Order + 1;
                            }
                            result.Ghosts.Add(g);
                            if (g.World != null)
                            {
                                // (only the newest few stay decoded: they're the likeliest to be watched first)
                                withWorlds.Enqueue(g);
                                if (withWorlds.Count > WorldsKeptFromLoading)
                                {
                                    Ghost old = withWorlds.Dequeue();
                                    if (old.Source != null)
                                    {
                                        old.World = null;
                                    }
                                }
                            }
                        }, Plugin.Log.LogWarning);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"skipping {Path.GetFileName(runs[r])}: {e.Message}");
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
                    g.Run = snapshotRun;
                    if (renumber)
                    {
                        g.Number = g.Order + 1;
                    }
                    result.Ghosts.Add(g);
                }
                Dates(result.Ghosts);

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

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // When each attempt was played ("13 NOV 18:42", local time), and which day of its run that was:
        // runs go on across game launches, so one can take days.
        private static void Dates(List<Ghost> ghosts)
        {
            var first = new Dictionary<int, DateTime>();
            var last = new Dictionary<int, DateTime>();
            var times = new DateTime[ghosts.Count];
            var known = new bool[ghosts.Count];
            for (int i = 0; i < ghosts.Count; i++)
            {
                Ghost g = ghosts[i];
                if (!LocalTime(g.StartedUtcTicks, out times[i]))
                {
                    continue;
                }
                known[i] = true;
                DateTime day = times[i].Date;
                if (!first.TryGetValue(g.Run, out DateTime f) || day < f)
                {
                    first[g.Run] = day;
                }
                if (!last.TryGetValue(g.Run, out DateTime l) || day > l)
                {
                    last[g.Run] = day;
                }
            }
            int year = DateTime.Now.Year;
            for (int i = 0; i < ghosts.Count; i++)
            {
                if (!known[i])
                {
                    continue;
                }
                Ghost g = ghosts[i];
                DateTime when = times[i];
                g.Day = (when.Date - first[g.Run]).Days + 1;
                g.RunDays = (last[g.Run] - first[g.Run]).Days + 1;
                string date = when.Day + " " + Months[when.Month - 1] + (when.Year != year ? " " + when.Year : "");
                g.When = $"{date} {when.Hour:00}:{when.Minute:00}";
            }
        }

        private static bool LocalTime(long utcTicks, out DateTime local)
        {
            local = default;
            if (utcTicks <= 0 || utcTicks > DateTime.MaxValue.Ticks)
            {
                return false;
            }
            try
            {
                local = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
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
            RunDays = Best.RunDays;
            poses = new Ghost.Pose[count];
            for (int i = 0; i < count; i++)
            {
                Ghosts[i].Index = i;
            }
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
            foreach (Ghost g in Ghosts)
            {
                cache.Adopt(g);
            }
            try
            {
                world.Prepare(Ghosts);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("getting the replay's world ready (no enemies or doors this time; the ghosts still play): " + e);
                world.Dispose();
                world = null;
            }
            try
            {
                things.Prepare(Ghosts);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("getting the replay's things ready (none this time; the ghosts still play): " + e);
                things.Dispose();
                things = null;
            }
            PrepareGameSounds();
            firstPersonView?.Prepare(Ghosts);
            State = Phase.Playing;
            BeginBlend();
            int withWorlds = Ghosts.Count(g => g.HasWorldData), everything = Ghosts.Count(g => g.HasEverything);
            Plugin.Log.LogInfo($"replaying {count} attempts at {LevelName} ({result.Label.ToLowerInvariant()}), {total:0.0} s"
                + (RunDays > 1 ? $", a run of {RunDays} days" : "")
                + $"; {withWorlds} with their enemies, {everything} with everything else ({Ghosts.Sum(g => g.ThingCount)} things), {cache.Held} worlds decoded up front");
        }

        // ---------------------------------------------------------------- every frame

        public void Update()
        {
            switch (State)
            {
                case Phase.Idle:
                    WatchKeyOnResults();
                    WatchWhenLoaded();
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
                if (loading != null && loading.IsCompleted && SectionsReady(loading.Result))
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
            cache?.Update();
            if (t > before && t - before < 0.5f)
            {
                Happenings(before, t);
            }
            PoseAll();
            UpdateCamera(dt);
            UpdateGameSounds(before, menu);
            // (not once it's dead: the camera backs out to watch its head fly)
            bool eyesAlive = Following && firstPerson && t <= Follow.Duration;
            firstPersonView?.Frame(Following ? Follow : null, t, eyesAlive, eyesAlive && InItsEyes);
            Draw();
        }

        // Where every ghost alive is this frame, worked out once (the overview and the drawing both use it).
        private void PoseAll()
        {
            if (poses.Length != Ghosts.Count)
            {
                poses = new Ghost.Pose[Ghosts.Count];
            }
            for (int i = 0; i < Ghosts.Count; i++)
            {
                Ghost g = Ghosts[i];
                if (g.AliveAt(t))
                {
                    poses[i] = g.PoseAt(t);
                }
            }
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
                    firstPersonView?.OnGUI();  // (the first person HUD's lookalike, under the replay's overlay)
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
            chaseFresh = true;
            overlaysPickedAt = -10f;
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
                chaseFresh = true;
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
            if (kb.oKey.wasPressedThisFrame)
            {
                Settings.OverlayWorlds.Value = !Settings.OverlayWorlds.Value;
                overlaysPickedAt = -10f;
                Notice = Settings.OverlayWorlds.Value ? "OTHER ATTEMPTS' WORLDS: ON" : "OTHER ATTEMPTS' WORLDS: OFF";
                NoticeAt = Time.unscaledTime;
            }
            if (kb.gKey.wasPressedThisFrame)
            {
                // (through an attempt's eyes: the game's HUD with its health and weapon)
                Settings.FirstPersonHud.Value = !Settings.FirstPersonHud.Value;
                Notice = Settings.FirstPersonHud.Value ? "GAME HUD (THROUGH ITS EYES): ON" : "GAME HUD (THROUGH ITS EYES): OFF";
                NoticeAt = Time.unscaledTime;
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
                chaseDistance = Mathf.Clamp(chaseDistance * Mathf.Pow(0.87f, scroll), 3f, 40f);
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
                // (eases into and out of moving, rather than jerking to full speed and dead still)
                Vector3 wish = Quaternion.Euler(freePitch, freeYaw, 0f) * move * speed;
                freeVelocity = Vector3.Lerp(freeVelocity, wish, 1f - Mathf.Exp(-dt / 0.1f));
                freePos += freeVelocity * dt;
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
                overviewFresh = true;
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
            freeVelocity = Vector3.zero;
            Vector3 euler = camRot.eulerAngles;
            freeYaw = euler.y;
            freePitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -89f, 89f);
            freeLook = Quaternion.Euler(freePitch, freeYaw, 0f);
        }

        private void Seek(float seconds)
        {
            t = Mathf.Clamp(t + seconds, 0f, total);
            summaryAt = -1f;
            chaseFresh = true;
            overlaysPickedAt = -10f;
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
                if (t < manual.Duration + HoldAfter(manual))
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
            if (Follow != null && autoPicked && Follow.Dies && t < Follow.Duration + HoldAfter(Follow))
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
                chaseFresh = true;
                BeginBlend();
            }

            Vector3 pos;
            Quaternion rot;
            if (Follow != null)
            {
                // (averaged over a few hundredths of a second either side: steady, without lagging behind;
                // nothing carries over from one attempt or moment to the next, so cuts and seeks stay crisp)
                followPose = Follow.SmoothPoseAt(Mathf.Min(t, Follow.Duration), Settings.CameraSmoothing.Value);
                if (firstPerson)
                {
                    pos = followPose.Eye;
                    rot = followPose.Look;
                    if (t > Follow.Duration && Follow.EndReason == AttemptEnd.Death && Settings.DeathAnimations.Value)
                    {
                        WatchDeath(t - Follow.Duration, ref pos, ref rot);
                    }
                }
                else
                {
                    Chase(followPose, dt, out pos, out rot);
                }
            }
            else if (mode == ReplayCamera.Overview)
            {
                Overview(dt, out pos, out rot);
            }
            else
            {
                // (a touch of smoothing on the mouse, so a fly-by doesn't jitter)
                freeLook = Quaternion.Slerp(freeLook, Quaternion.Euler(freePitch, freeYaw, 0f), 1f - Mathf.Exp(-dt / 0.04f));
                rot = freeLook;
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
                if (Follow != null && firstPerson && Settings.CameraShake.Value)
                {
                    camPos += Shake(Follow, Mathf.Min(t, Follow.Duration));
                }
            }
        }

        // How long the camera stays on an attempt after it ends.
        private static float HoldAfter(Ghost g) => g.EndReason == AttemptEnd.Death && Settings.DeathAnimations.Value ? DeathAnimationHold : DeathHold;

        // Through a dying attempt's eyes (DeathAnimations): the camera backs out of its head, behind and above
        // where it looked, and turns to watch its head fly off.
        private void WatchDeath(float age, ref Vector3 pos, ref Quaternion rot)
        {
            float k = Mathf.SmoothStep(0f, 1f, age / 0.4f);
            Vector3 up = followPose.Up;
            Vector3 ahead = Vector3.ProjectOnPlane(rot * Vector3.forward, up);
            if (ahead.sqrMagnitude < 1e-4f)
            {
                ahead = Vector3.ProjectOnPlane(rot * Vector3.up, up);
            }
            ahead = ahead.normalized;
            Vector3 from = Vector3.Lerp(pos, Collide(pos, pos - ahead * 3.5f + up * 1.5f), k);
            Vector3 target = renderer.DeathHead(Follow, t, out Vector3 head) ? Vector3.Lerp(head, pos, 0.35f) : pos;
            Vector3 toward = target - from;
            if (toward.sqrMagnitude > 0.01f)
            {
                rot = Quaternion.Slerp(rot, Quaternion.LookRotation(toward, up), k);
            }
            pos = from;
        }

        // Behind and above the followed attempt. Its mouse turns it at once; the camera swings round after
        // it (rather than jerking with every flick), pulls in at once in front of a wall, and eases back out.
        // It turns about a point above its head, so the V1 (3.3 m tall, and solid) sits low in the picture
        // rather than in front of what it's heading for.
        private void Chase(in Ghost.Pose p, float dt, out Vector3 pos, out Quaternion rot)
        {
            Vector3 forward = Vector3.ProjectOnPlane(p.Look * Vector3.forward, p.Up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(p.Look * Vector3.up, p.Up);
            }
            Quaternion heading = Quaternion.LookRotation(forward.normalized, p.Up);
            chaseHeading = chaseFresh ? heading : Quaternion.Slerp(chaseHeading, heading, 1f - Mathf.Exp(-dt / ChaseLag));
            rot = chaseHeading * Quaternion.AngleAxis(chaseYaw, Vector3.up) * Quaternion.AngleAxis(chasePitch, Vector3.right);
            Vector3 back = rot * Vector3.back;
            Vector3 pivot = Collide(p.Eye, p.Eye + p.Up * ChaseLift);
            float reach = Vector3.Distance(pivot, Collide(pivot, pivot + back * chaseDistance));
            chaseReach = chaseFresh || reach < chaseReach ? reach : Mathf.Lerp(chaseReach, reach, 1f - Mathf.Exp(-dt * 5f));
            chaseFresh = false;
            pos = pivot + back * chaseReach;
        }

        // ULTRAKILL's own shake when it got hurt (damage / 20, wearing off at 3 a second), times the game's
        // screen shake setting. Worked out from the replay's time, so pausing holds it still.
        private Vector3 Shake(Ghost g, float time)
        {
            int now = Mathf.Clamp((int)(time * g.Rate), 0, g.Count - 1);
            float amount = 0f;
            for (int k = Mathf.Max(1, now - g.Rate / 2); k <= now; k++)
            {
                int lost = g.Hp[k - 1] - g.Hp[k];
                if (lost > 0)
                {
                    amount = Mathf.Max(amount, lost / 20f - (time - k / (float)g.Rate) * 3f);
                }
            }
            amount = Mathf.Min(amount, 1f) * screenShake * 0.5f;
            if (amount <= 0.001f)
            {
                return Vector3.zero;
            }
            int tick = (int)(time * 60f);  // (a new jolt every 60th of a second, as the game makes one a frame)
            Vector3 offset = camRot * new Vector3(Noise(tick * 2), Noise(tick * 2 + 1), 0f) * amount;
            float length = offset.magnitude;
            if (length < 1e-3f)
            {
                return Vector3.zero;
            }
            if (Physics.Raycast(camPos, offset / length, out RaycastHit hit, length + 0.2f, environment, QueryTriggerInteraction.Ignore))
            {
                offset *= Mathf.Max(0f, hit.distance - 0.2f) / length;  // (not into a wall)
            }
            return offset;
        }

        // -1..1, the same for the same n.
        private static float Noise(int n)
        {
            unchecked
            {
                uint h = (uint)n * 747796405u + 2891336453u;
                h = ((h >> (int)((h >> 28) + 4u)) ^ h) * 277803737u;
                h = (h >> 22) ^ h;
                return h / 2147483648f - 1f;
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
                Ghost.Pose p = poses[g.Index];
                sum += p.Body;
                heading += p.Body - g.Body[Mathf.Max(0, p.Sample - g.Rate / 2)];
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
                        spread += Vector3.Distance(poses[g.Index].Body, center);
                    }
                }
                spread /= n;
            }
            // (the middle glides on when attempts die or a straggler drags it, instead of jumping)
            if (overviewFresh)
            {
                overviewCenter = center;
                overviewCenterVelocity = Vector3.zero;
                overviewFresh = false;
            }
            else
            {
                overviewCenter = Vector3.SmoothDamp(overviewCenter, center, ref overviewCenterVelocity, 0.3f, Mathf.Infinity, dt);
            }
            center = overviewCenter;
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
                    // (the nailgun held down: 15 tracers a second, whatever the sample rate)
                    bool shot = (flags & SampleFlags.Shot) != 0 || (w.Slot == 3 && (flags & SampleFlags.FireHeld) != 0 && k % Mathf.Max(1, g.Rate / 15) == 0);
                    if (shot && w.Slot > 0 && !ShotsShown(g))
                    {
                        // (aimed from the eyes, where the game aims; drawn from the gun in its hand)
                        Vector3 eye = g.Body[k] + g.Eye[k];
                        Vector3 dir = g.Look[k] * Vector3.forward;
                        Vector3 muzzle = renderer.Muzzle(g, k, eye, g.Look[k]);
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
            // (inside its head once the cut's done, or nearly: the solid V1 isn't drawn round the camera)
            bool pov = InItsEyes;
            bool close = Following && !firstPerson && chaseReach < ChaseClose;
            int alive = 0;
            renderer.BeginFrame(t);
            foreach (Ghost g in Ghosts)
            {
                if (!g.AliveAt(t))
                {
                    continue;
                }
                alive++;
                bool followed = g == Follow;
                Ghost.Pose p = followed ? followPose : poses[g.Index];
                if (followed && pov)
                {
                    // (its weapon stays put on screen, shake and all, as a viewmodel does)
                    p.Eye = camPos;
                    p.Look = camRot;
                }
                bool featured = followed || g.IsBest;
                float alpha = featured ? 0.85f : opacity;
                if (followed && close)
                {
                    // (the camera's backed up against it by a wall: see-through, not a wall of V1 in the way)
                    featured = false;
                    alpha = opacity * 0.7f;
                }
                // (the animated viewmodel, when it has that weapon, instead of the still one)
                bool animated = followed && pov && firstPersonView != null && firstPersonView.Covers(g, p.Sample);
                renderer.DrawGhost(g, p, alpha, featured, followed && pov, weapons && !animated);
            }
            renderer.EndFrame();
            Alive = alive;

            // The followed attempt's world (nobody followed: the winner's, the run being celebrated), and the
            // other attempts' see-through over it.
            Ghost shown = Following ? Follow : Best;
            GhostWorld shownWorld = cache?.Get(shown);
            Prefetch();
            ShowSections(shown);
            if (world != null)
            {
                try
                {
                    world.Update(shown, shownWorld, t);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("showing the replay's world (off for the rest of this replay; the ghosts still play): " + e);
                    world.Dispose();
                    world = null;
                }
            }
            if (things != null)
            {
                try
                {
                    things.Update(shown, shownWorld, t);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("showing the replay's things (off for the rest of this replay; the ghosts still play): " + e);
                    things.Dispose();
                    things = null;
                }
            }
            DrawOverlays(shown);

            renderer.DrawEndings(byEnd, t, Following ? Follow : null);
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

        // ---------------------------------------------------------------- the attempts' worlds

        private const int MaxOverlays = 20;
        private const int OverlayDraws = 1800;
        private const float OverlayPickSeconds = 0.5f;

        // The worlds wanted soon, read ahead: the winner's, and the next few to die (the next-to-die camera
        // cuts to them).
        private void Prefetch()
        {
            if (cache == null)
            {
                return;
            }
            cache.Want(Best);
            cache.Want(Follow);
            for (int i = FirstEndingAfter(dying, t), n = 0; i < dying.Length && n < 3; i++, n++)
            {
                cache.Want(dying[i]);
            }
        }

        // Every other attempt still alive (the nearest MaxOverlays): its world see-through in its colour.
        private void DrawOverlays(Ghost shown)
        {
            OverlaysShown = 0;
            if (!Settings.OverlayWorlds.Value || overlaysBroken || cache == null || (world == null && things == null))
            {
                return;
            }
            if (Time.unscaledTime - overlaysPickedAt > OverlayPickSeconds)
            {
                PickOverlays(shown);
            }
            // (each a little fainter the more there are: twenty copies of one enemy stacked at the level's start
            // would add up to a solid block)
            float opacity = Settings.OverlayOpacity.Value * Mathf.Min(1f, 3f / Mathf.Sqrt(Mathf.Max(1, overlaid.Count)));
            int budget = OverlayDraws;
            foreach (Ghost g in overlaid)
            {
                if (g == shown || !g.AliveAt(t))
                {
                    continue;
                }
                GhostWorld w = cache.Get(g);
                if (w == null)
                {
                    continue;
                }
                OverlaysShown++;
                var tint = new Color(g.Color.r, g.Color.g, g.Color.b, opacity);
                try
                {
                    world?.DrawOverlay(g, w, tint, t, ref budget);
                    if (things != null && budget > 0)
                    {
                        things.DrawOverlay(g, w, tint, t, ref budget);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("showing the other attempts' worlds (off for the rest of this replay): " + e);
                    overlaysBroken = true;
                    return;
                }
                if (budget <= 0)
                {
                    things?.OverlayCut();
                    break;
                }
            }
        }

        private void PickOverlays(Ghost shown)
        {
            overlaysPickedAt = Time.unscaledTime;
            overlayPicks.Clear();
            foreach (Ghost g in Ghosts)
            {
                if (g != shown && g.HasWorldData && !g.WorldFailed && g.AliveAt(t))
                {
                    overlayPicks.Add(g);
                }
            }
            if (overlayPicks.Count > MaxOverlays)
            {
                overlayPicks.Sort(byDistance);
            }
            overlaid.Clear();
            for (int i = 0; i < overlayPicks.Count && i < MaxOverlays; i++)
            {
                overlaid.Add(overlayPicks[i]);
                cache.Want(overlayPicks[i]);
            }
        }

        private float FromCamera(Ghost g) => g.Index >= 0 && g.Index < poses.Length ? (poses[g.Index].Body - camPos).sqrMagnitude : float.MaxValue;

        // ---------------------------------------------------------------- the level's parts

        // The level's parts as the attempt shown had them (SectionPlayback). Their copies are made while the replay is
        // still loading, a frame at a time, so a cut never waits for one.
        private bool SectionsReady(LoadResult result)
        {
            if (sections == null || result.Error != null || result.Ghosts.Count == 0)
            {
                return true;
            }
            try
            {
                return sections.MakeAhead(result.Ghosts);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("getting the level's parts ready (left as they are this time; the ghosts still play): " + e);
                DropSections();
                return true;
            }
        }

        private void ShowSections(Ghost shown)
        {
            if (sections == null)
            {
                return;
            }
            try
            {
                sections.Update(shown, t);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("showing the level's parts (left as they are for the rest of this replay; the ghosts still play): " + e);
                DropSections();
            }
        }

        // (puts back what it hid and drops its copies)
        private void DropSections()
        {
            try
            {
                sections?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("putting the level's parts back after a replay: " + e);
            }
            sections = null;
        }

        // ---------------------------------------------------------------- the game's own sounds

        // Each attempt's own gunshots, explosions, enemies and voice lines (recorded with RecordSounds): see GameSounds.
        private GameSounds gameSounds;

        private void PrepareGameSounds()
        {
            try
            {
                gameSounds?.Prepare(Ghosts);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("getting the replay's sounds ready (none this time; the ghosts still play): " + e);
                StopGameSounds();
            }
        }

        // (after the camera's moved for this frame: sounds are heard from where it is)
        private void UpdateGameSounds(float before, bool menu)
        {
            if (gameSounds == null)
            {
                return;
            }
            try
            {
                gameSounds.Update(Ghosts, Following ? Follow : Best, before, t, Speed, menu || userPaused, camPos, camRot, Following && firstPerson);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("playing the replay's sounds (off for the rest of this replay; the ghosts still play): " + e);
                StopGameSounds();
            }
        }

        private void StopGameSounds()
        {
            try
            {
                gameSounds?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("stopping the replay's sounds: " + e);
            }
            gameSounds = null;
        }

        // Whether a ghost's own shots show as they really were (its projectiles and beams, drawn with its
        // world): then it needs no tracers of its own.
        private bool ShotsShown(Ghost g)
        {
            if (things == null || g.World == null || !g.HasEverything)
            {
                return false;
            }
            return g == (Following ? Follow : Best) || (Settings.OverlayWorlds.Value && overlaid.Contains(g));
        }
    }
}
