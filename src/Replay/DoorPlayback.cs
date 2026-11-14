using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // The level's doors as the followed attempt had them: every kind ULTRAKILL has. A plain door slides
    // between its shut and open places (Door), a big one swings its leaves round (BigDoor, the way V1 opened
    // it from), a sub-door controller slides its panels (SubDoor) or plays their animation. Each moves the
    // way the game moves it, from when the attempt opened or closed it: its own speed, easing, speeding up,
    // or going with V1's speed. Time is stopped during a replay, so their own scripts don't move them;
    // nothing about them but where they are changes (and a plain door's "in place"), and Dispose puts every
    // one back exactly.
    internal sealed class DoorPlayback
    {
        private const float Step = 1f / 60f;    // (the game moves its doors once a frame: simulated at 60 a second)
        private const int MostSteps = 1200;     // (20 s of it: any door's long done by then)

        private readonly Dictionary<string, DoorSpot> spots = new Dictionary<string, DoorSpot>();
        private readonly Dictionary<Door, DoorSpot> spotOf = new Dictionary<Door, DoorSpot>();  // (two paths, one door: one spot)
        private readonly List<DoorPart> moved = new List<DoorPart>();
        private readonly Dictionary<string, bool> before = new Dictionary<string, bool>();   // how each door was before any attempt touched it
        private Ghost doorsOf;
        private DoorSpot[] shown = Array.Empty<DoorSpot>();               // per door of the attempt shown
        private Ghost.DoorChange[][] changes = Array.Empty<Ghost.DoorChange[]>();
        private readonly List<DoorSpot> others = new List<DoorSpot>();    // doors it never saw, as they were before
        private readonly List<bool> othersOpen = new List<bool>();

        // A door found in the level, and what of it moves.
        private sealed class DoorSpot
        {
            public Door Door;
            public DoorType Type;
            public DoorPart[] Parts;
        }

        private enum PartKind
        {
            Slides,     // a plain door, or a sliding panel: its local position
            Swings,     // a big door's leaf: its local rotation
            Plays,      // an animated panel: its animator's normalized time
        }

        // One moving part, how it moves (in "travel": metres, degrees, or 1 for an animation) and how it was.
        private sealed class DoorPart
        {
            public PartKind Kind;
            public Transform T;
            public Door Door;               // a plain door: itself
            public BigDoor Big;
            public Animator Anim;
            public int State;               // the animation it plays
            public Vector3 Shut, Open;
            public Quaternion ShutTurn, OpenTurn, OpenTurnBack;
            public float Travel = 1f;
            public float Speed;             // travel a second
            public float Gradual;           // BigDoor: faster and faster (gradualSpeedMultiplier)
            public bool WithV1;             // faster when V1 is (playerSpeedMultiplier)
            public bool EaseIn;             // a plain door's slowing down at the end
            public bool Moved;
            public Vector3 HomePos;
            public Quaternion HomeRot;
            public bool HomeInPos;
            public int HomeState;
            public float HomeTime;
            public float HomeSpeed;
        }

        // ---------------------------------------------------------------- starting and stopping

        // The doors every attempt met, found in the level; and how each was before an attempt first opened or
        // closed it (for the attempts that never saw it). Returns the log's line.
        public string Prepare(IList<Ghost> ghosts)
        {
            var paths = new HashSet<string>();
            var byOrder = new List<Ghost>(ghosts);
            byOrder.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (Ghost g in byOrder)
            {
                var seen = new bool[g.DoorPaths.Length];
                foreach (Ghost.DoorChange c in g.Doors)
                {
                    if (c.Door < 0 || c.Door >= seen.Length || seen[c.Door])
                    {
                        continue;
                    }
                    seen[c.Door] = true;
                    string path = g.DoorPaths[c.Door] ?? "";
                    if (!before.ContainsKey(path))
                    {
                        before[path] = Before(c);  // (the first attempt to see it, oldest first)
                    }
                }
                foreach (string path in g.DoorPaths)
                {
                    paths.Add(path ?? "");
                }
            }
            int movable = 0, sliding = 0, big = 0, sub = 0, parts = 0;
            var counted = new HashSet<DoorSpot>();
            foreach (string path in paths)
            {
                DoorSpot spot = Spot(path);
                movable += spot != null ? 1 : 0;
                if (spot == null || !counted.Add(spot))
                {
                    continue;  // (two paths to one door: its kind counts once)
                }
                parts += spot.Parts.Length;
                switch (spot.Type)
                {
                    case DoorType.BigDoorController:
                        big++;
                        break;
                    case DoorType.SubDoorController:
                        sub++;
                        break;
                    default:
                        sliding++;
                        break;
                }
            }
            return $"doors: {movable} of {paths.Count} movable ({sliding} sliding, {big} big, {sub} sub; {parts} moving parts)";
        }

        public void Dispose()
        {
            PutBack();
            doorsOf = null;
        }

        // ---------------------------------------------------------------- every frame

        // The doors as g had them t seconds in (null: as the level has them now). Each from its last change
        // by then, moving as the game would have moved it since; one g never saw, as it was before any
        // attempt opened or closed it.
        public void Show(Ghost g, float t)
        {
            if (g != doorsOf)
            {
                PutBack();
                Begin(g);
            }
            if (g == null)
            {
                return;
            }
            float at = Mathf.Min(t, g.Duration);
            bool changed = false;
            for (int i = 0; i < shown.Length; i++)
            {
                DoorSpot spot = shown[i];
                Ghost.DoorChange[] list = changes[i];
                if (spot == null || list.Length == 0)
                {
                    continue;
                }
                foreach (DoorPart part in spot.Parts)
                {
                    changed |= Simulate(g, part, list, at);
                }
            }
            for (int i = 0; i < others.Count; i++)
            {
                foreach (DoorPart part in others[i].Parts)
                {
                    changed |= Place(part, othersOpen[i] ? 1f : 0f, reversed: part.Big != null && part.Big.reverseDirection);
                }
            }
            if (changed)
            {
                Physics.SyncTransforms();  // (so shots and the camera hit the doors where they're shown)
            }
        }

        // A new attempt to show: its doors, each one's changes in order, and the doors it never saw.
        private void Begin(Ghost g)
        {
            doorsOf = g;
            others.Clear();
            othersOpen.Clear();
            int n = g != null ? g.DoorPaths.Length : 0;
            shown = new DoorSpot[n];
            changes = new Ghost.DoorChange[n][];
            var lists = new List<Ghost.DoorChange>[n];
            for (int i = 0; i < n; i++)
            {
                shown[i] = Spot(g.DoorPaths[i]);
                lists[i] = new List<Ghost.DoorChange>(2);
            }
            if (g == null)
            {
                return;
            }
            foreach (Ghost.DoorChange c in g.Doors)
            {
                if (c.Door >= 0 && c.Door < n)
                {
                    lists[c.Door].Add(c);
                }
            }
            var mine = new HashSet<DoorSpot>();
            for (int i = 0; i < n; i++)
            {
                changes[i] = lists[i].ToArray();
                if (shown[i] != null && changes[i].Length > 0)
                {
                    mine.Add(shown[i]);
                }
            }
            foreach (KeyValuePair<string, bool> door in before)
            {
                DoorSpot spot = Spot(door.Key);
                if (spot != null && mine.Add(spot))
                {
                    others.Add(spot);
                    othersOpen.Add(door.Value);
                }
            }
        }

        // How a door was before its first change in an attempt: as found then, else the other way (it was just
        // opened or closed). Older recordings don't say: at the start it was found; later, it was shut (a door
        // found closed as its room came in, or one just opened).
        private static bool Before(in Ghost.DoorChange first)
        {
            if ((first.Detail & DoorDetail.Recorded) != 0)
            {
                return (first.Detail & DoorDetail.AsFound) != 0 ? first.Open : !first.Open;
            }
            return first.Sample == 0 && first.Open;
        }

        private static bool AsFound(in Ghost.DoorChange c, bool first)
        {
            if ((c.Detail & DoorDetail.Recorded) != 0)
            {
                return (c.Detail & DoorDetail.AsFound) != 0;
            }
            return first && (c.Sample == 0 || !c.Open);
        }

        // Where one part is at `at`, through the attempt's changes to its door (returns whether it moved).
        private bool Simulate(Ghost g, DoorPart part, Ghost.DoorChange[] list, float at)
        {
            int sample = Mathf.FloorToInt(at * g.Rate);
            bool open = Before(list[0]);
            float p = open ? 1f : 0f;
            bool reversed = Reversed(part, list[0]);
            float since = float.NegativeInfinity;
            for (int k = 0; k < list.Length && list[k].Sample <= sample; k++)
            {
                Ghost.DoorChange c = list[k];
                float when = c.Sample / (float)g.Rate;
                if (!float.IsInfinity(since))
                {
                    p = Advance(g, part, p, open, since, when);
                }
                if (AsFound(c, k == 0))
                {
                    p = c.Open ? 1f : 0f;
                    reversed = Reversed(part, c);
                }
                else if (c.Open && part.Kind == PartKind.Swings && p * part.Travel < 20f)
                {
                    reversed = Reversed(part, c);  // (BigDoor.Open only turns it the new way if it's nearly shut)
                }
                open = c.Open;
                since = when;
            }
            if (!float.IsInfinity(since))
            {
                p = Advance(g, part, p, open, since, at);
            }
            return Place(part, p, reversed);
        }

        // Which way a big door swings: as recorded, else as the level has it now.
        private static bool Reversed(DoorPart part, in Ghost.DoorChange c)
        {
            if (part.Big == null)
            {
                return false;
            }
            return (c.Detail & DoorDetail.Recorded) != 0 ? (c.Detail & DoorDetail.Reversed) != 0 : part.Big.reverseDirection;
        }

        // From p (0 shut, 1 open) at `from` towards open or shut until `to`, as the game moves it: Door.Update
        // (easing in: slower as it gets there), BigDoor.Update (faster and faster, or with V1's speed),
        // SubDoor.Update (with V1's speed), or a SubDoor's animation (its speed set once, when it was told).
        private static float Advance(Ghost g, DoorPart part, float p, bool open, float from, float to)
        {
            float goal = open ? 1f : 0f;
            if (p == goal || to <= from || part.Speed <= 0f)
            {
                return p;  // (a door with no speed never moves in the game either)
            }
            if (part.Travel <= 1e-4f)
            {
                return goal;
            }
            if (part.Kind == PartKind.Plays || (!part.EaseIn && !part.WithV1 && part.Gradual == 0f))
            {
                float speed = part.Speed * (part.WithV1 ? Mathf.Max(1f, SpeedOf(g, from) / 15f) : 1f);
                return Mathf.MoveTowards(p, goal, (to - from) * speed / part.Travel);
            }
            float temp = part.Speed;
            float time = from;
            for (int n = 0; time < to && p != goal; n++)
            {
                if (n >= MostSteps)
                {
                    return goal;
                }
                float dt = Mathf.Min(Step, to - time);
                float speed = part.Speed;
                if (part.Gradual != 0f)
                {
                    temp += dt * temp * part.Gradual;
                    speed = temp;
                }
                if (part.WithV1)
                {
                    speed = Mathf.Max(speed, speed * SpeedOf(g, time) / 15f);
                }
                if (part.EaseIn)
                {
                    float left = Mathf.Abs(goal - p) * part.Travel;
                    float k = left / Mathf.Min(part.Travel, 100f);
                    speed = Mathf.Min(part.Speed, k * 2f * part.Speed + part.Speed / 50f);
                }
                p = Mathf.MoveTowards(p, goal, dt * speed / part.Travel);
                if (part.Door != null && Mathf.Abs(goal - p) * part.Travel < Mathf.Min(0.1f, part.Travel / 1000f))
                {
                    p = goal;  // (Door.Update snaps it there when it's this close)
                }
                time += dt;
            }
            return p;
        }

        // How fast V1 went at `time` in the attempt (m/s).
        private static float SpeedOf(Ghost g, float time)
        {
            int i = Mathf.Clamp(Mathf.FloorToInt(time * g.Rate), 1, Mathf.Max(1, g.Count - 1));
            if (i >= g.Count || (g.Flags[i] & (byte)SampleFlags.Snap) != 0)
            {
                return 0f;
            }
            return Mathf.Min((g.Body[i] - g.Body[i - 1]).magnitude * g.Rate, 300f);
        }

        // Puts a part at p (0 shut, 1 open). Returns whether it moved.
        private bool Place(DoorPart part, float p, bool reversed)
        {
            if (part.T == null)
            {
                return false;
            }
            if (part.Kind == PartKind.Plays && (part.Anim == null || !part.Anim.isActiveAndEnabled))
            {
                return false;
            }
            Touch(part);
            switch (part.Kind)
            {
                case PartKind.Slides:
                {
                    if (part.Door != null)
                    {
                        part.Door.inPos = true;  // (its own Update only moves it while it isn't in place: this keeps it out of the way)
                    }
                    Vector3 pos = Vector3.LerpUnclamped(part.Shut, part.Open, p);
                    if (part.T.localPosition == pos)
                    {
                        return false;
                    }
                    part.T.localPosition = pos;
                    return true;
                }
                case PartKind.Swings:
                {
                    Quaternion rot = Quaternion.Slerp(part.ShutTurn, reversed ? part.OpenTurnBack : part.OpenTurn, p);
                    if (part.T.localRotation == rot)
                    {
                        return false;
                    }
                    part.T.localRotation = rot;
                    return true;
                }
                default:
                    part.Anim.speed = 0f;
                    part.Anim.Play(part.State, 0, Mathf.Clamp01(p));  // (inside 0..1: SubDoor.Update only steps in outside it)
                    return false;
            }
        }

        // (the first time the replay moves it: how it was)
        private void Touch(DoorPart part)
        {
            if (part.Moved)
            {
                return;
            }
            part.Moved = true;
            part.HomePos = part.T.localPosition;
            part.HomeRot = part.T.localRotation;
            if (part.Door != null)
            {
                part.HomeInPos = part.Door.inPos;
            }
            if (part.Kind == PartKind.Plays)
            {
                AnimatorStateInfo state = part.Anim.GetCurrentAnimatorStateInfo(0);
                part.HomeState = state.fullPathHash;
                part.HomeTime = state.normalizedTime;
                part.HomeSpeed = part.Anim.speed;
            }
            moved.Add(part);
        }

        private void PutBack()
        {
            bool any = false;
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                DoorPart part = moved[i];
                part.Moved = false;
                try
                {
                    if (part.Door != null)
                    {
                        part.Door.inPos = part.HomeInPos;
                    }
                    if (part.T == null)
                    {
                        continue;
                    }
                    part.T.SetLocalPositionAndRotation(part.HomePos, part.HomeRot);
                    if (part.Kind == PartKind.Plays && part.Anim != null)
                    {
                        part.Anim.speed = part.HomeSpeed;
                        if (part.Anim.isActiveAndEnabled)
                        {
                            part.Anim.Play(part.HomeState, 0, part.HomeTime);
                        }
                    }
                    any = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("putting a door back after a replay: " + e);
                }
            }
            moved.Clear();
            if (any)
            {
                Physics.SyncTransforms();
            }
        }

        // ---------------------------------------------------------------- finding them

        // A door by its path (looked up once), with its moving parts; null if it isn't in the level or nothing
        // of it can move.
        private DoorSpot Spot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (spots.TryGetValue(path, out DoorSpot spot))
            {
                return spot;
            }
            spot = null;
            try
            {
                Transform t = Game.FindByPath(path);
                Door door = t != null ? t.GetComponent<Door>() : null;
                if (door != null && !spotOf.TryGetValue(door, out spot))
                {
                    spot = Make(door);
                    spotOf[door] = spot;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"finding the door {path}: {e.Message}");
                spot = null;
            }
            spots[path] = spot;
            return spot;
        }

        private static DoorSpot Make(Door door)
        {
            var parts = new List<DoorPart>();
            switch (door.doorType)
            {
                case DoorType.Normal:
                {
                    Transform t = door.transform;
                    // (a door that's never woken up hasn't worked out its places yet: Door.GetPos's)
                    Vector3 shut = door.gotPos ? door.closedPos : t.localPosition;
                    Vector3 open = door.gotPos ? door.openPosRelative : t.localPosition + door.openPos;
                    float travel = Vector3.Distance(shut, open);
                    if (travel > 1e-4f)
                    {
                        parts.Add(new DoorPart { Kind = PartKind.Slides, T = t, Door = door, Shut = shut, Open = open, Travel = travel, Speed = door.speed, EaseIn = door.easeIn });
                    }
                    break;
                }
                case DoorType.BigDoorController:
                    foreach (BigDoor big in door.GetComponentsInChildren<BigDoor>(true))
                    {
                        Quaternion shut = big.gotPos ? big.origRotation : big.transform.localRotation;
                        // (as BigDoor.Open works out where it swings to, either way)
                        Quaternion open = Quaternion.Euler(shut.eulerAngles + big.openRotation);
                        Quaternion back = Quaternion.Euler(shut.eulerAngles - big.openRotation);
                        float travel = Quaternion.Angle(shut, open);
                        if (travel > 0.01f)
                        {
                            parts.Add(new DoorPart
                            {
                                Kind = PartKind.Swings, T = big.transform, Big = big, ShutTurn = shut, OpenTurn = open, OpenTurnBack = back,
                                Travel = travel, Speed = big.speed, Gradual = big.gradualSpeedMultiplier, WithV1 = big.playerSpeedMultiplier,
                            });
                        }
                    }
                    break;
                case DoorType.SubDoorController:
                    foreach (SubDoor sub in door.GetComponentsInChildren<SubDoor>(true))
                    {
                        DoorPart part = SubPart(sub);
                        if (part != null)
                        {
                            parts.Add(part);
                        }
                    }
                    break;
            }
            return parts.Count > 0 ? new DoorSpot { Door = door, Type = door.doorType, Parts = parts.ToArray() } : null;
        }

        private static DoorPart SubPart(SubDoor sub)
        {
            if (sub.type == SubDoorType.Animation)
            {
                Animator anim = sub.anim != null ? sub.anim : sub.GetComponent<Animator>();
                if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null || !anim.isInitialized)
                {
                    return null;  // (its room's off: it can't be played now)
                }
                float length = 0f;
                AnimatorClipInfo[] clips = anim.GetCurrentAnimatorClipInfo(0);
                if (clips.Length > 0 && clips[0].clip != null)
                {
                    length = clips[0].clip.length;
                }
                else if (anim.runtimeAnimatorController.animationClips.Length > 0 && anim.runtimeAnimatorController.animationClips[0] != null)
                {
                    length = anim.runtimeAnimatorController.animationClips[0].length;
                }
                return new DoorPart
                {
                    Kind = PartKind.Plays, T = sub.transform, Anim = anim, State = anim.GetCurrentAnimatorStateInfo(0).fullPathHash,
                    Travel = 1f, Speed = Mathf.Abs(sub.speed) / Mathf.Max(length, 0.01f), WithV1 = sub.playerSpeedMultiplier,
                };
            }
            Vector3 shut = sub.valuesSet ? sub.origPos : sub.transform.localPosition;
            float travel = sub.openPos.magnitude;
            if (travel <= 1e-4f)
            {
                return null;
            }
            return new DoorPart { Kind = PartKind.Slides, T = sub.transform, Shut = shut, Open = shut + sub.openPos, Travel = travel, Speed = sub.speed, WithV1 = sub.playerSpeedMultiplier };
        }

        // Whether t is a part of a door this moves (a plain door, a big door's leaf, a sub door's panel): then
        // nothing else in the replay should move it.
        public static bool MovesDoorPart(Transform t)
        {
            if (t == null)
            {
                return false;
            }
            Door door = t.GetComponent<Door>();
            if (door != null && door.doorType == DoorType.Normal)
            {
                return true;
            }
            if (t.GetComponent<BigDoor>() == null && t.GetComponent<SubDoor>() == null)
            {
                return false;
            }
            Door controller = t.GetComponentInParent<Door>(true);
            return controller != null && (controller.doorType == DoorType.BigDoorController || controller.doorType == DoorType.SubDoorController);
        }
    }
}
