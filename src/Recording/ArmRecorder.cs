using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UltraAttempts.Recording
{
    // V1's arms while an attempt is recorded (with Recording.RecordWeapons; format 5's block 1): which arm it had out
    // and when it switched, what that arm's animator did (every state it went into: punches, jabs that hit, parries,
    // the Knuckleblaster's blast, coin flips, holding things), and the whiplash: its animator, whether its arm was
    // out, and where its end was at each sample it was out of the hand. A replay plays the same animations on copies
    // of the game's own arms, so it doesn't have to guess them from button presses. Only reads, and cheap: a couple
    // of animator states and a position a sample.
    internal sealed class ArmRecorder
    {
        private const int MaxMoves = 20000;      // an attempt (about half an hour of punching)
        private const int MaxHook = 200000;      // samples with the whiplash out (most of an hour at 60 a second)

        // The HookArm's own private fields: where its end is, whether it's coming back, its arm's model.
        private static AccessTools.FieldRef<HookArm, Vector3> hookPoint;
        private static AccessTools.FieldRef<HookArm, bool> returning;
        private static AccessTools.FieldRef<HookArm, GameObject> hookModel;
        private static bool fieldsTried, fieldsFailed;

        // Animator states counted for the log.
        private static readonly int ParryState = Animator.StringToHash("Base Layer.Hook");   // (the arm swung up to parry)
        private static readonly int BlastState = Animator.StringToHash("Base Layer.PunchBlast");
        private static readonly int HoldingParam = Animator.StringToHash("Holding");
        private static readonly int SemiHoldingParam = Animator.StringToHash("SemiHolding");

        private RawAttempt attempt;
        private bool failed;
        private static bool warnedFailure, warnedFull;

        // What each part was doing at the last sample.
        private ArmKind lastArm;
        private Punch armPunch;
        private Animator armAnim;
        private bool armHasHolding;
        private readonly Track arm = new Track();
        private readonly Track whip = new Track();
        private HookPhase lastPhase;
        private HookArm hookArm;
        private Animator hookAnim;

        // For the log.
        private int punches, throws, caught, parries, blasts;
        private readonly HashSet<ArmKind> used = new HashSet<ArmKind>();
        private bool whiplashUsed;

        private sealed class Track
        {
            public bool Known;
            public int State;
            public float Time;
            public ArmMoveFlags Flags;

            public void Forget() => Known = false;
        }

        public void Begin(RawAttempt a, bool record)
        {
            attempt = record ? a : null;
            a.ArmsRecorded = record;
            failed = false;
            lastArm = ArmKind.None;
            armPunch = null;
            armAnim = null;
            arm.Forget();
            whip.Forget();
            lastPhase = HookPhase.InHand;
            hookArm = null;
            hookAnim = null;
            punches = throws = caught = parries = blasts = 0;
            used.Clear();
            whiplashUsed = false;
            if (record)
            {
                Fields();
            }
        }

        public void End()
        {
            attempt = null;
            armAnim = null;
            hookArm = null;
            hookAnim = null;
        }

        // A punch really started (Punch.PunchStart, ready for it): counted, whatever button did it.
        public void OnPunch()
        {
            if (attempt != null && !failed)
            {
                punches++;
            }
        }

        // Each sample as it's taken: the arm out, its animator, the whiplash.
        public void Sample(int sample)
        {
            if (attempt == null || failed)
            {
                return;
            }
            try
            {
                SampleArm(sample);
                SampleWhiplash(sample);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void SampleArm(int sample)
        {
            FistControl fists = Game.Get<FistControl>();
            Punch punch = fists != null ? fists.currentPunch : null;
            bool shown = punch != null && punch.gameObject.activeInHierarchy;
            ArmKind kind = shown ? (ArmKind)Mathf.Clamp((int)punch.type + 1, 1, 255) : ArmKind.None;
            if (kind != lastArm || sample == 0)
            {
                attempt.ArmSwitches.Add(new ArmSwitch { Sample = sample, Arm = kind });
                lastArm = kind;
                armPunch = null;
                if (kind != ArmKind.None)
                {
                    used.Add(kind);
                }
            }
            if (shown && !ReferenceEquals(punch, armPunch))
            {
                // (switched to, or the same arm made again: the shop does that)
                armPunch = punch;
                arm.Forget();
                armAnim = punch.anim != null ? punch.anim : punch.GetComponent<Animator>();
                armHasHolding = armAnim != null && HasParam(armAnim, HoldingParam) && HasParam(armAnim, SemiHoldingParam);
            }
            else if (!shown)
            {
                armAnim = null;
            }
            if (armAnim == null || !armAnim.isActiveAndEnabled)
            {
                return;
            }
            var flags = ArmMoveFlags.None;
            if (armHasHolding)
            {
                flags |= armAnim.GetBool(HoldingParam) ? ArmMoveFlags.Holding : 0;
                flags |= armAnim.GetBool(SemiHoldingParam) ? ArmMoveFlags.SemiHolding : 0;
            }
            if (Moved(arm, armAnim, flags, sample, ArmPart.Arm, out int state))
            {
                parries += state == ParryState ? 1 : 0;
                blasts += state == BlastState ? 1 : 0;
            }
        }

        private void SampleWhiplash(int sample)
        {
            if (hookArm == null)
            {
                hookArm = Game.Get<HookArm>();
                hookAnim = hookArm != null ? hookArm.GetComponent<Animator>() : null;
                if (hookArm == null)
                {
                    return;
                }
            }
            HookPhase phase = HookPhase.InHand;
            switch (hookArm.state)
            {
                case HookState.Throwing:
                    phase = HookPhase.Throwing;
                    break;
                case HookState.Caught:
                    phase = HookPhase.Caught;
                    break;
                case HookState.Pulling:
                    phase = HookPhase.Pulling;
                    break;
                default:
                    phase = returning != null && returning(hookArm) ? HookPhase.Returning : HookPhase.InHand;
                    break;
            }
            if (phase == HookPhase.Throwing && lastPhase != HookPhase.Throwing)
            {
                throws++;
                whiplashUsed = true;
            }
            if (phase == HookPhase.Caught && lastPhase == HookPhase.Throwing)
            {
                caught++;
            }
            lastPhase = phase;
            if (phase != HookPhase.InHand && hookPoint != null)
            {
                if (attempt.Hook.Count < MaxHook)
                {
                    Vector3 p = hookPoint(hookArm);
                    attempt.Hook.Add(sample, phase, Recorder.Cm(p.x), Recorder.Cm(p.y), Recorder.Cm(p.z));
                }
                else
                {
                    Full();
                }
            }
            if (hookAnim != null && hookAnim.isActiveAndEnabled)
            {
                GameObject model = hookModel != null ? hookModel(hookArm) : null;
                var flags = model != null && model.activeInHierarchy ? ArmMoveFlags.Shown : ArmMoveFlags.None;
                Moved(whip, hookAnim, flags, sample, ArmPart.Whiplash, out _);
            }
        }

        // The state its animator's going into (the one it's blending to, mid-blend): a move when that changes, when
        // it starts the same one over (a second punch, a jab replayed as it hits), or when the flags do.
        private bool Moved(Track track, Animator anim, ArmMoveFlags flags, int sample, ArmPart part, out int state)
        {
            bool blending = anim.IsInTransition(0);
            AnimatorStateInfo info = blending ? anim.GetNextAnimatorStateInfo(0) : anim.GetCurrentAnimatorStateInfo(0);
            state = info.fullPathHash;
            float time = info.normalizedTime;
            bool moved = !track.Known || state != track.State || flags != track.Flags || time < track.Time - 0.05f;
            track.Known = true;
            track.State = state;
            track.Time = time;
            track.Flags = flags;
            if (!moved)
            {
                return false;
            }
            if (attempt.ArmMoves.Count >= MaxMoves)
            {
                Full();
                return false;
            }
            // (looping states wrap round: only how far into the loop it is)
            float into = info.loop ? time - Mathf.Floor(time) : Mathf.Clamp01(time);
            attempt.ArmMoves.Add(new ArmMove
            {
                Sample = sample,
                Part = part,
                Flags = flags | (blending ? ArmMoveFlags.Blend : ArmMoveFlags.None),
                State = state,
                Time = (ushort)Mathf.RoundToInt(into * ushort.MaxValue),
            });
            return true;
        }

        private static bool HasParam(Animator anim, int hash)
        {
            foreach (AnimatorControllerParameter p in anim.parameters)
            {
                if (p.nameHash == hash)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Fields()
        {
            if (fieldsTried)
            {
                return;
            }
            fieldsTried = true;
            try
            {
                hookPoint = AccessTools.FieldRefAccess<HookArm, Vector3>("hookPoint");
                returning = AccessTools.FieldRefAccess<HookArm, bool>("returning");
                hookModel = AccessTools.FieldRefAccess<HookArm, GameObject>("model");
            }
            catch (Exception e)
            {
                hookPoint = null;
                returning = null;
                hookModel = null;
                fieldsFailed = true;
                Plugin.Log.LogWarning("the whiplash's own fields can't be read (a game update?), so replays don't show where it went: " + e.Message);
            }
        }

        // The whiplash's arm (its model, switched on while it's out), for the replay's copy of it.
        public static GameObject ModelOf(HookArm h)
        {
            Fields();
            return h != null && hookModel != null ? hookModel(h) : null;
        }

        // What an attempt's arms did, for the log.
        public string Summary(RawAttempt a)
        {
            if (!a.ArmsRecorded)
            {
                return "not recorded (RecordWeapons is off)";
            }
            var names = new List<string>();
            foreach (ArmKind k in new[] { ArmKind.Feedbacker, ArmKind.Knuckleblaster, ArmKind.Spear })
            {
                if (used.Contains(k))
                {
                    names.Add(k.ToString());
                }
            }
            if (whiplashUsed)
            {
                names.Add("Whiplash");
            }
            return $"{punches} punches, {throws} whiplash throws ({caught} caught), arms used: "
                + (names.Count > 0 ? string.Join(", ", names) : "none")
                + $"; {parries} parries, {blasts} blasts, {a.ArmSwitches.Count} arm switches, {a.ArmMoves.Count} animations, {a.Hook.Count} samples with the whiplash out"
                + (fieldsFailed ? " (where it went couldn't be read)" : "")
                + (failed ? "; FAILED partway (see the warning above)" : "");
        }

        private static void Full()
        {
            if (!warnedFull)
            {
                warnedFull = true;
                Plugin.Log.LogInfo("this attempt has recorded as much of its arms as an attempt keeps: no more for it");
            }
        }

        // Something in the game didn't look as expected: the rest of this attempt goes without its arms (everything
        // else is still recorded).
        private void Fail(Exception e)
        {
            failed = true;
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording the arms failed, so this attempt has none from here on (it's still recorded): " + e);
            }
        }
    }
}
