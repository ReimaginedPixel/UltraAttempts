using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraAttempts.Recording
{
    // Records every attempt: from the level's start (or a checkpoint respawn) until V1 dies, restarts,
    // leaves or reaches the final pit. Nothing in the game changes; it only watches. Samples are taken
    // at a fixed rate of game time (so pausing and hitstops take no room), interpolated between frames.
    internal sealed class Recorder
    {
        public static Recorder Instance { get; private set; }

        // Seconds of the fall into the final pit kept after the level is beaten.
        private const float FinishTail = 1.25f;

        // Further than this in one frame is a teleport, not movement.
        private const float SnapDistance = 20f;

        private readonly SessionStore store;

        private StatsManager sceneStats;
        private string sceneName;
        private string leavingFrom;
        private bool sceneHadAttempt;
        private LevelInfo level;

        private RawAttempt current;
        private float time;
        private float nextTick;
        private float interval;
        private Frame last;
        private SampleFlags pulses;
        private bool finishing;
        private float finishAt;

        private GameObject lastWeapon;
        private Vector3 lastUp;
        private CheckPoint lastCheckpoint;
        private readonly Dictionary<GameObject, (string name, int variant)> weaponInfo = new Dictionary<GameObject, (string, int)>();

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
            StatsManager sm = Game.Stats;
            NewMovement nm = Game.Player;

            if (!ReferenceEquals(sm, sceneStats))
            {
                EnterScene(sm, nm);
            }
            if (level == null || nm == null || replaying)
            {
                return;
            }
            if (!Settings.Record.Value || !Game.IsRecordable(nm))
            {
                if (current != null && !Settings.Record.Value)
                {
                    End(AttemptEnd.Quit);
                }
                return;
            }
            if (SceneHelper.PendingScene != null)
            {
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

            CapturePulses(nm);
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }
            Advance(nm, dt);

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
                current.End = finishing ? AttemptEnd.Finish : AttemptEnd.Quit;
                Submit(current, current.End);
                current = null;
            }
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
            interval = 1f / Mathf.Clamp(Settings.SampleRate.Value, 10, 60);
            int number = store.BeginAttempt(level);
            current = new RawAttempt
            {
                Number = number,
                Start = sceneHadAttempt ? AttemptStart.Checkpoint : AttemptStart.LevelStart,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
                SampleRate = Mathf.Clamp(Settings.SampleRate.Value, 10, 60),
                LevelTimeStart = sm.seconds,
                WeaponsRecorded = Settings.RecordWeapons.Value,
            };
            sceneHadAttempt = true;
            finishing = false;
            time = 0f;
            pulses = SampleFlags.None;
            lastWeapon = null;
            lastUp = Vector3.up;
            lastCheckpoint = sm.currentCheckPoint;
            last = Capture(nm);
            Emit(nm, last);
            nextTick = interval;

            BannerNumber = number;
            BannerShownAt = Time.unscaledTime;
        }

        private void End(AttemptEnd reason)
        {
            RawAttempt attempt = current;
            current = null;
            NewMovement nm = Game.Player;
            if (nm != null && attempt.Count > 0)
            {
                // The last sample: where it ended (one tick on from the last).
                Emit(nm, Capture(nm), attempt);
            }
            attempt.End = reason;
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
            store.SubmitAttempt(level, attempt, reason == AttemptEnd.Finish);
        }

        // Game time moved on by dt: a sample for every tick in it, interpolated between last frame and this.
        private void Advance(NewMovement nm, float dt)
        {
            Frame now = Capture(nm);
            float t1 = time + dt;
            bool snap = (now.Body - last.Body).sqrMagnitude > SnapDistance * SnapDistance;
            while (nextTick <= t1)
            {
                float a = snap ? 1f : Mathf.InverseLerp(time, t1, nextTick);
                Frame f = a >= 1f ? now : Lerp(last, now, a);
                if (snap)
                {
                    f.State |= SampleFlags.Snap;
                    snap = false;
                }
                Emit(nm, f);
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
            Transform cam = nm.cc != null ? nm.cc.transform : body;
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
            return new Frame
            {
                Body = body.position,
                Eye = cam.position - body.position,
                Look = cam.rotation,
                Hp = (byte)Mathf.Clamp(nm.hp, 0, 255),
                State = state,
            };
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

        private void Emit(NewMovement nm, Frame f, RawAttempt into = null)
        {
            RawAttempt a = into ?? current;
            int sample = a.Count;
            if (a.WeaponsRecorded)
            {
                TrackWeapon(a, sample);
            }
            TrackUp(a, sample, nm);
            TrackCheckpoint(a, sample);

            Quaternion q = f.Look;
            float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (norm < 1e-6f)
            {
                q = Quaternion.identity;
                norm = 1f;
            }
            float sign = q.w < 0f ? -1f : 1f;  // (q and -q are the same rotation: keep w >= 0)
            float k = sign * RawAttempt.RotationScale / norm;
            var flags = f.State | pulses;
            pulses = SampleFlags.None;
            a.Add(
                Cm(f.Body.x), Cm(f.Body.y), Cm(f.Body.z),
                CmShort(f.Eye.x), CmShort(f.Eye.y), CmShort(f.Eye.z),
                (short)Mathf.RoundToInt(q.x * k), (short)Mathf.RoundToInt(q.y * k), (short)Mathf.RoundToInt(q.z * k), (short)Mathf.RoundToInt(q.w * k),
                (byte)flags, f.Hp);
        }

        // (within +-1000 km: the codec's prediction stays inside an int)
        private static int Cm(float metres) => Mathf.RoundToInt(Mathf.Clamp(metres, -1e6f, 1e6f) * 100f);

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
