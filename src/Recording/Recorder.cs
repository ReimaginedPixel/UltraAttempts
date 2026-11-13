using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraAttempts.Recording
{
    // Records every attempt: from the level's start (or a checkpoint respawn) until V1 dies, restarts,
    // leaves or reaches the final pit. Nothing in the game changes; it only watches. Samples are taken
    // at a fixed rate of game time (so pausing and hitstops take no room), interpolated between frames.
    //
    // Attempts start and end in LateUpdate. Each frame's samples are taken right after the camera has
    // moved (CameraLateUpdate), so V1's body and eyes are read at the same moment, and the eyes without
    // the screen shake. When the camera is off (V1 is dead, a cutscene), LateUpdate takes them instead.
    internal sealed class Recorder
    {
        public static Recorder Instance { get; private set; }

        // Seconds of the fall into the final pit kept after the level is beaten.
        private const float FinishTail = 1.25f;

        // Further than this in one frame is a teleport, not movement.
        private const float SnapDistance = 20f;

        // How far the walking bob lowers the camera at most (CameraController's targetPos).
        private const float BobDepth = 0.2f;

        // Enemies are sampled about this often (every EnemyStride-th sample).
        private const int EnemyRate = 30;

        private readonly SessionStore store;
        private readonly WorldRecorder world = new WorldRecorder();

        private StatsManager sceneStats;
        private string sceneName;
        private string leavingFrom;
        private bool sceneHadAttempt;
        private LevelInfo level;

        private RawAttempt current;
        private bool live;           // recording right now (the camera's turn to take samples)
        private int steppedFrame = -1;
        private double clock;        // game time at the last step
        private double time;
        private double nextTick;
        private double interval;
        private Frame last;
        private SampleFlags pulses;
        private bool finishing;
        private double finishAt;

        private GameObject lastWeapon;
        private Vector3 lastUp;
        private CheckPoint lastCheckpoint;
        private readonly Dictionary<GameObject, (string name, int variant)> weaponInfo = new Dictionary<GameObject, (string, int)>();

        private int cameraSteps;
        private int ownSteps;
        private bool reported;
        private int errors;

        // "ATTEMPT 37" at the start of each attempt.
        public int BannerNumber { get; private set; }
        public float BannerShownAt { get; private set; } = -100f;

        public LevelInfo Level => level;

        public bool Recording => current != null;

        public Recorder(SessionStore store)
        {
            this.store = store;
            Instance = this;
        }

        private struct Frame
        {
            public Vector3 Body;
            public Vector3 Eye;
            public Quaternion Look;
            public byte Hp;
            public SampleFlags State;
        }

        // The attempt being played right now, as it is so far (for a replay started mid-level).
        public RawAttempt Snapshot()
        {
            return current != null && current.Count > 1 ? current.Clone(AttemptEnd.InProgress) : null;
        }

        public void LateUpdate(bool replaying)
        {
            live = false;
            StatsManager sm = Game.Stats;
            NewMovement nm = Game.Player;

            if (!ReferenceEquals(sm, sceneStats))
            {
                EnterScene(sm, nm);
            }
            if (level == null || nm == null || replaying)
            {
                clock = Time.timeAsDouble;  // (time spent not recording isn't part of the attempt)
                return;
            }
            if (!Settings.Record.Value || !Game.IsRecordable(nm))
            {
                if (current != null && !Settings.Record.Value)
                {
                    End(AttemptEnd.Quit);
                }
                clock = Time.timeAsDouble;
                return;
            }
            if (SceneHelper.PendingScene != null)
            {
                clock = Time.timeAsDouble;
                return;  // (the level is being unloaded)
            }

            if (current == null)
            {
                if (nm.dead || nm.levelOver || sm.infoSent || Game.Paused)
                {
                    return;
                }
                Begin(sm, nm);
            }

            if (nm.dead)
            {
                End(AttemptEnd.Death);
                return;
            }

            live = true;
            CameraController cc = nm.cc;
            if ((cc == null || !cc.isActiveAndEnabled) && Step(nm))
            {
                ownSteps++;
            }

            if (nm.levelOver || sm.infoSent)
            {
                if (!finishing)
                {
                    finishing = true;
                    finishAt = time + FinishTail;
                }
                else if (time >= finishAt || sm.infoSent)
                {
                    End(AttemptEnd.Finish);
                }
            }
        }

        // Right after CameraController.LateUpdate: this frame's samples, with V1's body and camera both
        // where the frame shows them. (Before it, the camera would still be where it was last frame.)
        public void CameraLateUpdate(CameraController camera, bool replaying)
        {
            if (!live || current == null || replaying || !ReferenceEquals(Game.Stats, sceneStats) || SceneHelper.PendingScene != null)
            {
                return;
            }
            try
            {
                NewMovement nm = Game.Player;
                if (nm == null || nm.dead || !ReferenceEquals(nm.cc, camera))
                {
                    return;
                }
                if (Step(nm))
                {
                    cameraSteps++;
                }
            }
            catch (Exception e)
            {
                // (inside the camera's own update: never let it throw, and don't flood the log)
                if (errors++ < 3)
                {
                    Plugin.Log.LogError("recording: " + e);
                }
            }
        }

        // A door's Open or Close ran.
        public void OnDoor(Door door, bool replaying)
        {
            if (current != null && !replaying)
            {
                world.OnDoor(door, current.Count);
            }
        }

        // ULTRAKILL is about to load a scene: restarting this level (no checkpoint), or leaving it.
        public void OnLeavingScene(string target)
        {
            leavingFrom = SceneHelper.CurrentScene;
            if (current == null)
            {
                return;
            }
            if (finishing)
            {
                End(AttemptEnd.Finish);
            }
            else
            {
                End(SceneHelper.SanitizeLevelPath(target ?? "") == sceneName ? AttemptEnd.Restart : AttemptEnd.Quit);
            }
        }

        // Respawning at a checkpoint (after dying, or "restart from checkpoint" while alive).
        public void OnRestart()
        {
            if (current != null)
            {
                End(finishing ? AttemptEnd.Finish : AttemptEnd.Restart);
            }
        }

        // The results screen is coming up: the level's beaten (whichever way it ended).
        public void OnResults()
        {
            if (current != null)
            {
                End(AttemptEnd.Finish);
            }
        }

        // Ends the attempt now (before a replay at the level's end, so it includes the winning one).
        public void FinishNow()
        {
            if (current != null && finishing)
            {
                End(AttemptEnd.Finish);
            }
        }

        private void EnterScene(StatsManager sm, NewMovement nm)
        {
            if (current != null)
            {
                // (missed the scene load: it's gone now, so no last sample)
                RawAttempt attempt = current;
                current = null;
                world.End();
                attempt.End = finishing ? AttemptEnd.Finish : AttemptEnd.Quit;
                Submit(attempt, attempt.End);
            }
            world.EnterScene();
            sceneStats = sm;
            sceneHadAttempt = false;
            finishing = false;
            weaponInfo.Clear();
            level = null;
            if (sm == null)
            {
                return;  // (between scenes, or a menu)
            }
            string scene = SceneHelper.CurrentScene;
            bool restart = leavingFrom == scene;
            sceneName = scene;
            level = Game.IsRecordable(nm) ? Game.CurrentLevel(Plugin.RecordingsRoot, nm) : null;
            if (level != null && !restart && !Settings.ContinueRunsAcrossVisits.Value)
            {
                store.ForgetRun(level.Key);
            }
        }

        private void Begin(StatsManager sm, NewMovement nm)
        {
            int rate = Mathf.Clamp(Settings.SampleRate.Value, 10, 60);
            interval = 1.0 / rate;
            int number = store.BeginAttempt(level);
            current = new RawAttempt
            {
                Number = number,
                Start = sceneHadAttempt ? AttemptStart.Checkpoint : AttemptStart.LevelStart,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
                SampleRate = rate,
                LevelTimeStart = sm.seconds,
                WeaponsRecorded = Settings.RecordWeapons.Value,
                EnemyStride = Mathf.Max(1, Mathf.RoundToInt(rate / (float)EnemyRate)),
            };
            sceneHadAttempt = true;
            finishing = false;
            time = 0.0;
            pulses = SampleFlags.None;
            lastWeapon = null;
            lastUp = Vector3.up;
            lastCheckpoint = sm.currentCheckPoint;
            steppedFrame = Time.frameCount;  // (this frame is the first sample)
            clock = Time.timeAsDouble;
            cameraSteps = 0;
            ownSteps = 0;
            world.Begin(current, Settings.World.Value != WorldDetail.Off);
            last = Capture(nm);
            Emit(nm, last, current, 1f);
            nextTick = interval;

            BannerNumber = number;
            BannerShownAt = Time.unscaledTime;
        }

        private void End(AttemptEnd reason)
        {
            RawAttempt attempt = current;
            current = null;
            live = false;
            NewMovement nm = Game.Player;
            if (nm != null && attempt.Count > 0)
            {
                // The last sample: where it ended (one tick on from the last).
                world.Frame();
                Emit(nm, Capture(nm), attempt, 1f);
            }
            world.End();
            attempt.End = reason;
            Report(attempt, nm);
            Submit(attempt, reason);
            finishing = false;
        }

        private void Submit(RawAttempt attempt, AttemptEnd reason)
        {
            StatsManager sm = Game.Stats;
            attempt.LevelTimeEnd = sm != null ? sm.seconds : attempt.LevelTimeStart + attempt.Duration;
            var assists = Game.Get<AssistController>();
            if (assists != null)
            {
                attempt.Cheats |= assists.cheatsEnabled;
                attempt.MajorAssists |= assists.majorEnabled;
            }
            if (attempt.Count < 2 || level == null)
            {
                return;
            }
            // (a door that moved after the last sample shows at it)
            for (int i = attempt.Events.Count - 1; i >= 0 && attempt.Events[i].Sample >= attempt.Count; i--)
            {
                AttemptEvent e = attempt.Events[i];
                e.Sample = attempt.Count - 1;
                attempt.Events[i] = e;
            }
            store.SubmitAttempt(level, attempt, reason == AttemptEnd.Finish);
        }

        // Once, for the first attempt of a game that lasts a few seconds: what got recorded, for the log
        // (checking a recording in game starts here).
        private void Report(RawAttempt a, NewMovement nm)
        {
            if (reported || a.Duration < 3f)
            {
                return;
            }
            reported = true;
            int died = 0, doorChanges = 0;
            foreach (EnemyTrack e in a.Enemies)
            {
                died += e.Died ? 1 : 0;
            }
            foreach (AttemptEvent e in a.Events)
            {
                doorChanges += e.Kind == EventKind.Door && e.Sample > 0 ? 1 : 0;
            }
            string interpolation = nm != null && nm.rb != null ? nm.rb.interpolation.ToString() : "?";
            Plugin.Log.LogInfo($"recorded attempt {a.Number}: {a.Duration:0.0} s at {a.SampleRate} samples/s ({cameraSteps} frames sampled after the camera, {ownSteps} without it); "
                + $"{a.Enemies.Count} enemies ({died} killed, sampled every {a.EnemyStride}), {a.DoorPaths.Count} doors ({doorChanges} opened or closed); V1's rigidbody interpolation: {interpolation}");
        }

        // This frame, once: a sample for every tick of game time since the last step.
        private bool Step(NewMovement nm)
        {
            int frame = Time.frameCount;
            if (frame == steppedFrame)
            {
                return false;
            }
            steppedFrame = frame;
            double now = Time.timeAsDouble;
            double dt = now - clock;
            clock = now;
            CapturePulses(nm);
            if (dt <= 0.0)
            {
                return true;
            }
            world.Frame();
            Advance(nm, dt);
            return true;
        }

        // Game time moved on by dt: a sample for every tick in it, interpolated between last frame and this.
        private void Advance(NewMovement nm, double dt)
        {
            Frame now = Capture(nm);
            double t1 = time + dt;
            bool snap = (now.Body - last.Body).sqrMagnitude > SnapDistance * SnapDistance;
            while (nextTick <= t1)
            {
                float between = (float)((nextTick - time) / dt);
                float a = snap ? 1f : between;
                Frame f = a >= 1f ? now : Lerp(last, now, a);
                if (snap)
                {
                    f.State |= SampleFlags.Snap;
                    snap = false;
                }
                Emit(nm, f, current, between);
                nextTick += interval;
            }
            time = t1;
            last = now;
        }

        private static Frame Lerp(Frame a, Frame b, float t)
        {
            return new Frame
            {
                Body = Vector3.LerpUnclamped(a.Body, b.Body, t),
                Eye = Vector3.LerpUnclamped(a.Eye, b.Eye, t),
                Look = Quaternion.Slerp(a.Look, b.Look, t),
                Hp = t < 0.5f ? a.Hp : b.Hp,
                State = t < 0.5f ? a.State : b.State,
            };
        }

        private Frame Capture(NewMovement nm)
        {
            Transform body = nm.transform;
            CameraController cc = nm.cc;
            Transform cam = cc != null ? cc.transform : body;
            var state = SampleFlags.None;
            if (nm.gc != null && nm.gc.onGround)
            {
                state |= SampleFlags.Grounded;
            }
            if (nm.sliding)
            {
                state |= SampleFlags.Sliding;
            }
            if (nm.boost)
            {
                state |= SampleFlags.Dashing;
            }
            if (Settings.RecordWeapons.Value && !nm.dead && InputAllowed())
            {
                var input = Game.Get<InputManager>()?.InputSource;
                if (input != null)
                {
                    if (input.Fire1.IsPressed)
                    {
                        state |= SampleFlags.FireHeld;
                    }
                    if (input.Fire2.IsPressed)
                    {
                        state |= SampleFlags.AltFireHeld;
                    }
                }
            }
            Vector3 position = body.position;
            return new Frame
            {
                Body = position,
                Eye = Eye(cc, cam) - position,
                Look = cam.rotation,
                Hp = (byte)Mathf.Clamp(nm.hp, 0, 255),
                State = state,
            };
        }

        // Where the eyes are without the screen shake. CameraController shakes the camera around its rest
        // place (defaultPos, which also sinks while sliding), every frame of a slide on the ground among
        // others; when it isn't shaking, walking bobs it down a little. Keep the slide and the bob, not the shake.
        private static Vector3 Eye(CameraController cc, Transform cam)
        {
            Transform parent = cam.parent;
            if (cc == null || parent == null)
            {
                return cam.position;
            }
            Vector3 rest = cc.defaultPos;
            float bob = cc.cameraShaking > 0f ? 0f : Mathf.Clamp(cam.localPosition.y - rest.y, -BobDepth, 0f);
            return parent.TransformPoint(new Vector3(rest.x, rest.y + bob, rest.z));
        }

        // Shots and punches between samples, so a click that's over by the next sample still shows.
        private void CapturePulses(NewMovement nm)
        {
            if (!Settings.RecordWeapons.Value || !InputAllowed())
            {
                return;
            }
            var inputManager = Game.Get<InputManager>();
            var input = inputManager?.InputSource;
            if (input == null || inputManager.PerformingCheatMenuCombo())
            {
                return;
            }
            var guns = Game.Get<GunControl>();
            if (input.Fire1.WasPerformedThisFrame && guns != null && guns.activated && !guns.noWeapons)
            {
                pulses |= SampleFlags.Shot;
            }
            if (input.Punch.WasPerformedThisFrame)
            {
                pulses |= SampleFlags.Punch;
            }
        }

        private static bool InputAllowed()
        {
            return !Game.Paused && GameStateManager.Instance != null && !GameStateManager.Instance.PlayerInputLocked;
        }

        // between: how far this sample is from last frame to this one (for the enemies).
        private void Emit(NewMovement nm, Frame f, RawAttempt a, float between)
        {
            int sample = a.Count;
            if (a.WeaponsRecorded)
            {
                TrackWeapon(a, sample);
            }
            TrackUp(a, sample, nm);
            TrackCheckpoint(a, sample);

            Quantize(f.Look, out short rx, out short ry, out short rz, out short rw);
            var flags = f.State | pulses;
            pulses = SampleFlags.None;
            a.Add(
                Cm(f.Body.x), Cm(f.Body.y), Cm(f.Body.z),
                CmShort(f.Eye.x), CmShort(f.Eye.y), CmShort(f.Eye.z),
                rx, ry, rz, rw,
                (byte)flags, f.Hp);
            if (sample % a.EnemyStride == 0)
            {
                world.Tick(sample, between);
            }
        }

        // A rotation as four shorts (* 32767), w >= 0 (q and -q are the same rotation).
        internal static void Quantize(Quaternion q, out short x, out short y, out short z, out short w)
        {
            float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (norm < 1e-6f || float.IsNaN(norm))
            {
                q = Quaternion.identity;
                norm = 1f;
            }
            float k = (q.w < 0f ? -1f : 1f) * RawAttempt.RotationScale / norm;
            x = (short)Mathf.RoundToInt(q.x * k);
            y = (short)Mathf.RoundToInt(q.y * k);
            z = (short)Mathf.RoundToInt(q.z * k);
            w = (short)Mathf.RoundToInt(q.w * k);
        }

        // (within +-1000 km: the codec's prediction stays inside an int)
        internal static int Cm(float metres) => Mathf.RoundToInt(Mathf.Clamp(metres, -1e6f, 1e6f) * 100f);

        private static short CmShort(float metres) => (short)Mathf.Clamp(Mathf.RoundToInt(metres * 100f), short.MinValue, short.MaxValue);

        private void TrackWeapon(RawAttempt a, int sample)
        {
            var guns = Game.Get<GunControl>();
            GameObject weapon = guns != null && !guns.noWeapons ? guns.currentWeapon : null;
            if (weapon == lastWeapon && sample > 0)
            {
                return;
            }
            lastWeapon = weapon;
            if (weapon == null)
            {
                a.Events.Add(new AttemptEvent { Sample = sample, Kind = EventKind.Weapon });
                return;
            }
            if (!weaponInfo.TryGetValue(weapon, out var info))
            {
                var icon = weapon.GetComponent<WeaponIcon>();
                WeaponDescriptor descriptor = icon != null ? icon.weaponDescriptor : null;
                string name = descriptor != null && !string.IsNullOrEmpty(descriptor.weaponName) ? descriptor.weaponName : weapon.name.Replace("(Clone)", "").Trim();
                int variant = descriptor != null ? (int)descriptor.variationColor : guns.currentVariationIndex;
                info = (name, variant);
                weaponInfo[weapon] = info;
            }
            a.Events.Add(new AttemptEvent
            {
                Sample = sample,
                Kind = EventKind.Weapon,
                A = guns.currentSlotIndex,
                B = info.variant,
                C = a.AddWeaponName(info.name),
            });
        }

        // Gravity: the player's up, when it turns (most levels: never).
        private void TrackUp(RawAttempt a, int sample, NewMovement nm)
        {
            Vector3 up = nm.transform.up;
            if (Vector3.Angle(up, lastUp) < 2f)
            {
                return;
            }
            lastUp = up;
            a.Events.Add(new AttemptEvent
            {
                Sample = sample,
                Kind = EventKind.Up,
                A = Mathf.RoundToInt(up.x * 127f),
                B = Mathf.RoundToInt(up.y * 127f),
                C = Mathf.RoundToInt(up.z * 127f),
            });
        }

        private void TrackCheckpoint(RawAttempt a, int sample)
        {
            StatsManager sm = Game.Stats;
            CheckPoint checkpoint = sm != null ? sm.currentCheckPoint : null;
            if (checkpoint == lastCheckpoint)
            {
                return;
            }
            lastCheckpoint = checkpoint;
            if (checkpoint != null && sample > 0)
            {
                a.Events.Add(new AttemptEvent { Sample = sample, Kind = EventKind.Checkpoint });
            }
        }
    }
}
