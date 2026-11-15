using System;
using System.Collections.Generic;

namespace UltraAttempts.Recording
{
    // Which arm V1 had out: the game's FistType, one up (0: none, or hidden).
    public enum ArmKind : byte
    {
        None = 0,
        Feedbacker = 1,       // FistType.Standard, the blue arm
        Knuckleblaster = 2,   // FistType.Heavy, the red arm
        Spear = 3,            // FistType.Spear
    }

    // The arm switched to another (or went away) at Sample.
    public struct ArmSwitch
    {
        public int Sample;
        public ArmKind Arm;
    }

    public enum ArmPart : byte
    {
        Arm = 0,        // the arm out (Feedbacker, Knuckleblaster)
        Whiplash = 1,   // the HookArm
    }

    [Flags]
    public enum ArmMoveFlags : byte
    {
        None = 0,
        Holding = 1,        // the arm's animator's Holding and SemiHolding (something held in the hand)
        SemiHolding = 2,
        Shown = 4,          // the whiplash's arm is out (its model on)
        Blend = 8,          // it was blending into the state (a transition), not cut to it
    }

    // An arm's animator went into another state at Sample (or started the same one over): State is its layer-0
    // fullPathHash, Time how far into it (normalized * 65535). Punches, jabs that hit, parries, the Knuckleblaster's
    // blast, coin flips, holding things; the whiplash thrown, pulling, caught again.
    public struct ArmMove
    {
        public int Sample;
        public ArmPart Part;
        public ArmMoveFlags Flags;
        public int State;
        public ushort Time;
    }

    // Where the whiplash was out of the hand at each sample: thrown, caught on something, pulling, coming back.
    public enum HookPhase : byte
    {
        InHand = 0,     // (never stored: those samples are left out)
        Throwing = 1,
        Caught = 2,
        Pulling = 3,
        Returning = 4,
    }

    // The whiplash's end, at the samples it was out (in order, in centimetres).
    public sealed class HookPath
    {
        public int Count;
        public int[] Sample = new int[16];
        public byte[] Phase = new byte[16];
        public int[] X = new int[16], Y = new int[16], Z = new int[16];

        public void Add(int sample, HookPhase phase, int x, int y, int z)
        {
            if (Count == Sample.Length)
            {
                Reserve(Count * 2);
            }
            Sample[Count] = sample;
            Phase[Count] = (byte)phase;
            X[Count] = x;
            Y[Count] = y;
            Z[Count] = z;
            Count++;
        }

        public void Reserve(int capacity)
        {
            if (capacity <= Sample.Length)
            {
                return;
            }
            Array.Resize(ref Sample, capacity);
            Array.Resize(ref Phase, capacity);
            Array.Resize(ref X, capacity);
            Array.Resize(ref Y, capacity);
            Array.Resize(ref Z, capacity);
        }

        public HookPath Clone()
        {
            var copy = new HookPath { Count = Count };
            copy.Reserve(Math.Max(16, Count));
            Array.Copy(Sample, copy.Sample, Count);
            Array.Copy(Phase, copy.Phase, Count);
            Array.Copy(X, copy.X, Count);
            Array.Copy(Y, copy.Y, Count);
            Array.Copy(Z, copy.Z, Count);
            return copy;
        }
    }

    // Block 1 (the attempt's): V1's arms. Which arm it had and when it switched, what each arm's animator did (so a
    // replay plays the same animations: every punch, parry, blast and coin flip, the whiplash thrown and pulled), and
    // where the whiplash's end was while it was out. Read with or without the world: every ghost may show its hook.
    public sealed partial class RawAttempt
    {
        public bool ArmsRecorded;
        public List<ArmSwitch> ArmSwitches = new List<ArmSwitch>();
        public List<ArmMove> ArmMoves = new List<ArmMove>();
        public HookPath Hook = new HookPath();

        partial void CloneArms(RawAttempt copy)
        {
            copy.ArmSwitches = new List<ArmSwitch>(ArmSwitches);
            copy.ArmMoves = new List<ArmMove>(ArmMoves);
            copy.Hook = Hook.Clone();
        }
    }

    // On disk: the switches, the moves, then the whiplash's path (each sample's step from the last, its phase, and
    // where it was as the difference from where its last two places predict). Each part says how many it has, so
    // a later version adds its own after these.
    internal static class ArmsBlock
    {
        public static bool Has(RawAttempt a) => a.ArmsRecorded;

        public static void Encode(AttemptCodec.VarWriter w, RawAttempt a)
        {
            w.UInt((uint)a.ArmSwitches.Count);
            int last = 0;
            foreach (ArmSwitch s in a.ArmSwitches)
            {
                w.Int(s.Sample - last);
                last = s.Sample;
                w.Byte((byte)s.Arm);
            }

            w.UInt((uint)a.ArmMoves.Count);
            last = 0;
            foreach (ArmMove m in a.ArmMoves)
            {
                w.Int(m.Sample - last);
                last = m.Sample;
                w.Byte((byte)(((byte)m.Part & 0x0F) | ((byte)m.Flags << 4)));
                w.UInt((uint)m.State);
                w.UInt(m.Time);
            }

            HookPath h = a.Hook;
            w.UInt((uint)h.Count);
            AttemptCodec.Deltas(w, h.Sample, h.Count);
            AttemptCodec.Deltas(w, h.Phase, h.Count);
            AttemptCodec.Predicted(w, h.X, h.Count);
            AttemptCodec.Predicted(w, h.Y, h.Count);
            AttemptCodec.Predicted(w, h.Z, h.Count);
        }

        public static void Decode(AttemptCodec.VarReader r, RawAttempt a)
        {
            a.ArmsRecorded = true;
            int switches = r.Count(2);
            a.ArmSwitches = new List<ArmSwitch>(switches);
            int last = 0;
            for (int i = 0; i < switches; i++)
            {
                last += r.Int();
                a.ArmSwitches.Add(new ArmSwitch { Sample = last, Arm = (ArmKind)r.Byte() });
            }

            int moves = r.Count(4);
            a.ArmMoves = new List<ArmMove>(moves);
            last = 0;
            for (int i = 0; i < moves; i++)
            {
                last += r.Int();
                byte packed = r.Byte();
                a.ArmMoves.Add(new ArmMove
                {
                    Sample = last,
                    Part = (ArmPart)(packed & 0x0F),
                    Flags = (ArmMoveFlags)(packed >> 4),
                    State = (int)r.UInt(),
                    Time = (ushort)Math.Min(ushort.MaxValue, r.UInt()),
                });
            }

            int count = r.Count(5);
            var h = new HookPath { Count = count };
            h.Reserve(Math.Max(16, count));
            AttemptCodec.Undeltas(r, h.Sample, count);
            AttemptCodec.Undeltas(r, h.Phase, count);
            AttemptCodec.Unpredicted(r, h.X, count);
            AttemptCodec.Unpredicted(r, h.Y, count);
            AttemptCodec.Unpredicted(r, h.Z, count);
            a.Hook = h;
        }
    }
}
