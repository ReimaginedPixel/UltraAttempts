using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraAttempts.Recording
{
    // What an attempt met, so a replay following it can show it: the enemies around (where each was, how
    // it was turned, its animation and health, until it died or went away) and the doors (open or
    // closed). Like the rest of the recorder it only reads. Enemies are sampled every EnemyStride-th
    // attempt sample, interpolated between frames like V1 is (so they don't stutter with the frame rate).
    internal sealed class WorldRecorder
    {
        // Enemies tracked at once at most (more wait for a free slot).
        private const int MaxTracks = 96;

        // Further than this between two frames is a teleport, not movement.
        private const float SnapDistance = 20f;

        // Door paths kept per scene before starting over (checkpoint respawns make new doors).
        private const int MaxDoorPaths = 4096;

        // Enemy rotations go in steps of 16 (of 32767: 0.06 degrees, nothing anyone could see), which pack
        // far smaller: enemies that keep turning cost about a third less.
        private const int RotationStep = 16;

        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public int Anim;          // layer 0's state (fullPathHash), 0: no animator
            public float AnimTime;    // its normalizedTime as the animator gives it (whole loops included)
            public bool AnimLoops;
            public float Health;
        }

        // An enemy being tracked: its components (looked up once), and how it was last frame and this one.
        private sealed class Live
        {
            public EnemyIdentifier Enemy;
            public int Key;
            public Transform Transform;
            public Animator Animator;
            public EnemyTrack Track;
            public float FullHealth;
            public Pose Before;
            public Pose Now;
        }

        private struct DoorState
        {
            public int Index;   // in the attempt's DoorPaths
            public bool Open;   // as last recorded
        }

        private readonly List<Live> live = new List<Live>();
        private readonly Dictionary<int, Live> byEnemy = new Dictionary<int, Live>();
        private readonly Stack<Live> spare = new Stack<Live>();

        // Working out a door's path walks the hierarchy, so each door's is kept while the scene lasts.
        private readonly Dictionary<int, string> doorPaths = new Dictionary<int, string>();
        private readonly Dictionary<int, DoorState> doors = new Dictionary<int, DoorState>();

        private RawAttempt attempt;
        private int nextId;
        private bool failed;
        private static bool warnedFull;
        private static bool warnedFailure;

        // Everything else (WorldDetail = Everything): projectiles, props, movers, explosions, beams.
        public ThingRecorder Things { get; } = new ThingRecorder();

        // A new scene: nothing from the last one carries over.
        public void EnterScene()
        {
            End();
            doorPaths.Clear();
            Things.EnterScene();
        }

        // An attempt starts, with as much of its world as asked for (Off: none).
        public void Begin(RawAttempt a, WorldDetail detail)
        {
            End();
            bool record = detail != WorldDetail.Off;
            Things.Begin(a, detail == WorldDetail.Everything);
            attempt = record ? a : null;
            failed = false;
            nextId = 0;
            if (attempt == null)
            {
                return;
            }
            try
            {
                if (doorPaths.Count > MaxDoorPaths)
                {
                    doorPaths.Clear();
                }
                // Every door in play, open or closed, at sample 0. Doors switched off with their room
                // (and the copies checkpoints keep) come in when they open or close.
                foreach (Door door in UnityEngine.Object.FindObjectsOfType<Door>())
                {
                    if (door != null)
                    {
                        Record(door, 0);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // The attempt is over: the enemies still around just stop there (Died stays false).
        public void End()
        {
            foreach (Live l in live)
            {
                Release(l);
            }
            live.Clear();
            byEnemy.Clear();
            doors.Clear();
            attempt = null;
            Things.End();
        }

        // Once a frame, before its samples: how each tracked enemy (and thing) is now.
        public void Frame()
        {
            Things.Frame();
            if (attempt == null || failed)
            {
                return;
            }
            try
            {
                for (int i = 0; i < live.Count; i++)
                {
                    Live l = live[i];
                    l.Before = l.Now;
                    if (l.Enemy != null)
                    {
                        l.Now = Read(l);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // An enemy sample, at attempt sample `sample`, `between` of the way from last frame to this one.
        public void Tick(int sample, float between)
        {
            Things.Tick(sample, between);
            if (attempt == null || failed)
            {
                return;
            }
            try
            {
                Follow(between);
                Discover(sample);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A door's Open or Close just ran (sample: the attempt sample it shows from).
        public void OnDoor(Door door, int sample)
        {
            if (attempt == null || failed || door == null)
            {
                return;
            }
            try
            {
                if (!Record(door, sample) || !door.open || door.activatedRooms == null)
                {
                    return;
                }
                // Opening brings the rooms behind it in: their doors are in play from now on.
                foreach (GameObject room in door.activatedRooms)
                {
                    if (room != null && room.activeInHierarchy)
                    {
                        foreach (Door inside in room.GetComponentsInChildren<Door>())
                        {
                            Record(inside, sample);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // The enemies being tracked: a sample each, and the end of those that died or went away.
        private void Follow(float between)
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Live l = live[i];
                EnemyIdentifier enemy = l.Enemy;
                if (enemy == null || !enemy.gameObject.activeInHierarchy)
                {
                    Stop(i, !ReferenceEquals(enemy, null) && enemy.dead);  // (a destroyed enemy's fields still read)
                    continue;
                }
                Add(l, Blend(l.Before, l.Now, between));
                if (enemy.dead)
                {
                    Stop(i, died: true);
                }
            }
        }

        // Enemies that showed up since the last sample.
        private void Discover(int sample)
        {
            var tracker = Game.Get<EnemyTracker>();
            List<EnemyIdentifier> enemies = tracker != null ? tracker.enemies : null;
            if (enemies == null)
            {
                return;
            }
            // (the tracker's own list, not GetCurrentEnemies: that one makes a new list every call)
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyIdentifier enemy = enemies[i];
                if (enemy == null || enemy.dead || byEnemy.ContainsKey(enemy.GetInstanceID()) || !enemy.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (live.Count >= MaxTracks)
                {
                    if (!warnedFull)
                    {
                        warnedFull = true;
                        Plugin.Log.LogInfo($"more than {MaxTracks} enemies around at once: the replay shows the first {MaxTracks}");
                    }
                    return;
                }
                Track(enemy, sample);
            }
        }

        private void Track(EnemyIdentifier enemy, int sample)
        {
            Live l = spare.Count > 0 ? spare.Pop() : new Live();
            l.Enemy = enemy;
            l.Key = enemy.GetInstanceID();
            l.Transform = enemy.transform;
            l.Animator = enemy.GetComponentInChildren<Animator>();
            l.FullHealth = enemy.health;
            l.Track = new EnemyTrack
            {
                Id = nextId++,
                Type = (int)enemy.enemyType,
                Name = Game.CleanName(enemy.gameObject.name),
                Scale = l.Transform.lossyScale.y,
                Start = sample,
            };
            l.Now = Read(l);
            l.Before = l.Now;
            live.Add(l);
            byEnemy[l.Key] = l;
            attempt.Enemies.Add(l.Track);
            Add(l, l.Now);
        }

        private void Stop(int index, bool died)
        {
            Live l = live[index];
            l.Track.Died = died;
            live.RemoveAt(index);
            byEnemy.Remove(l.Key);
            Release(l);
        }

        private void Release(Live l)
        {
            l.Enemy = null;
            l.Transform = null;
            l.Animator = null;
            l.Track = null;
            spare.Push(l);
        }

        private static Pose Read(Live l)
        {
            var p = new Pose();
            l.Transform.GetPositionAndRotation(out p.Position, out p.Rotation);
            Animator animator = l.Animator;
            if (animator != null && animator.isActiveAndEnabled && animator.isInitialized && animator.runtimeAnimatorController != null)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                p.Anim = state.fullPathHash;
                p.AnimTime = state.normalizedTime;
                p.AnimLoops = state.loop;
            }
            p.Health = l.Enemy.health;
            return p;
        }

        // Between last frame and this one (the nearer of the two for what doesn't blend).
        private static Pose Blend(Pose before, Pose now, float t)
        {
            if (t >= 1f || (now.Position - before.Position).sqrMagnitude > SnapDistance * SnapDistance)
            {
                return now;
            }
            Pose p = t < 0.5f ? before : now;
            p.Position = Vector3.LerpUnclamped(before.Position, now.Position, t);
            p.Rotation = Quaternion.Slerp(before.Rotation, now.Rotation, t);
            if (before.Anim == now.Anim && now.AnimTime >= before.AnimTime)
            {
                p.AnimTime = Mathf.LerpUnclamped(before.AnimTime, now.AnimTime, t);  // (not across a restart)
            }
            return p;
        }

        private static void Add(Live l, Pose p)
        {
            Recorder.Quantize(p.Rotation, out short rx, out short ry, out short rz, out short rw);
            float time = p.AnimLoops ? p.AnimTime - Mathf.Floor(p.AnimTime) : Mathf.Clamp01(p.AnimTime);
            float health = l.FullHealth > 0f ? p.Health / l.FullHealth : 1f;
            l.Track.Add(
                Recorder.Cm(p.Position.x), Recorder.Cm(p.Position.y), Recorder.Cm(p.Position.z),
                Coarse(rx), Coarse(ry), Coarse(rz), Coarse(rw),
                p.Anim, (ushort)Mathf.Clamp(Mathf.RoundToInt(time * 65535f), 0, 65535),
                (byte)Mathf.Clamp(Mathf.RoundToInt(health * 255f), 0, 255));
        }

        internal static short Coarse(short value) =>
            (short)(Mathf.Clamp(Mathf.RoundToInt(value / (float)RotationStep), short.MinValue / RotationStep, short.MaxValue / RotationStep) * RotationStep);

        // Records a door's state if this attempt hasn't got it already (true if it was recorded).
        private bool Record(Door door, int sample)
        {
            int key = door.GetInstanceID();
            bool open = door.open;
            if (doors.TryGetValue(key, out DoorState known))
            {
                if (known.Open == open)
                {
                    return false;
                }
            }
            else
            {
                if (!doorPaths.TryGetValue(key, out string path))
                {
                    path = Game.PathOf(door.transform);
                    doorPaths[key] = path;
                }
                known.Index = attempt.AddDoorPath(path);
            }
            known.Open = open;
            doors[key] = known;
            attempt.Events.Add(new AttemptEvent { Sample = sample, Kind = EventKind.Door, A = known.Index, B = open ? 1 : 0 });
            return true;
        }

        // Something in the game didn't look as expected: the rest of this attempt goes without its world
        // (V1 itself is still recorded).
        private void Fail(Exception e)
        {
            failed = true;
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording the enemies and doors failed, so this attempt has none from here on (it's still recorded): " + e);
            }
        }
    }
}
