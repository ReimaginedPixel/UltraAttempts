using System;
using System.Collections.Generic;

namespace UltraAttempts.Recording
{
    // How an attempt ended. InProgress: the attempt you're playing right now (only in a replay
    // started mid-level, never on disk).
    public enum AttemptEnd : byte
    {
        InProgress = 0,
        Death = 1,
        Restart = 2,
        Quit = 3,
        Finish = 4,
    }

    // Where an attempt started: the level's start, or a checkpoint you respawned at.
    public enum AttemptStart : byte
    {
        LevelStart = 0,
        Checkpoint = 1,
    }

    // One byte per sample. Shot and Punch are pulses (the button was pressed since the last
    // sample); Snap: don't interpolate into this sample (a teleport, an out-of-bounds reset).
    [Flags]
    public enum SampleFlags : byte
    {
        None = 0,
        Grounded = 1,
        Sliding = 2,
        Dashing = 4,
        FireHeld = 8,
        AltFireHeld = 16,
        Shot = 32,
        Punch = 64,
        Snap = 128,
    }

    public enum EventKind : byte
    {
        // A: slot (0: no weapon), B: variant colour (WeaponVariant), C: index into WeaponNames.
        Weapon = 1,
        // A, B, C: the player's up direction * 127 (gravity changes).
        Up = 2,
        // A new checkpoint reached.
        Checkpoint = 3,
    }

    public struct AttemptEvent
    {
        public int Sample;
        public EventKind Kind;
        public int A;
        public int B;
        public int C;
    }

    // One attempt as recorded: fixed-rate samples, already quantized (centimetres, rotation * 32767),
    // kept as one array per channel (which is also how they're stored, for compression). Sample i is
    // at i / SampleRate seconds into the attempt.
    public sealed class RawAttempt
    {
        public const float RotationScale = 32767f;

        public int Number;
        public AttemptStart Start;
        public AttemptEnd End;
        public long StartedUtcTicks;
        public int SampleRate = 30;
        public float LevelTimeStart;
        public float LevelTimeEnd;
        public bool Cheats;
        public bool MajorAssists;
        public bool WeaponsRecorded;

        public int Count;
        public int[] PosX = new int[64], PosY = new int[64], PosZ = new int[64];
        public short[] EyeX = new short[64], EyeY = new short[64], EyeZ = new short[64];
        public short[] RotX = new short[64], RotY = new short[64], RotZ = new short[64], RotW = new short[64];
        public byte[] Flags = new byte[64];
        public byte[] Hp = new byte[64];

        public List<AttemptEvent> Events = new List<AttemptEvent>();
        public List<string> WeaponNames = new List<string>();

        public float Duration => Count <= 1 ? 0f : (Count - 1) / (float)SampleRate;

        public void Add(int px, int py, int pz, short ex, short ey, short ez, short rx, short ry, short rz, short rw, byte flags, byte hp)
        {
            if (Count == PosX.Length)
            {
                Reserve(Count * 2);
            }
            PosX[Count] = px;
            PosY[Count] = py;
            PosZ[Count] = pz;
            EyeX[Count] = ex;
            EyeY[Count] = ey;
            EyeZ[Count] = ez;
            RotX[Count] = rx;
            RotY[Count] = ry;
            RotZ[Count] = rz;
            RotW[Count] = rw;
            Flags[Count] = flags;
            Hp[Count] = hp;
            Count++;
        }

        public void Reserve(int capacity)
        {
            if (capacity <= PosX.Length)
            {
                return;
            }
            Array.Resize(ref PosX, capacity);
            Array.Resize(ref PosY, capacity);
            Array.Resize(ref PosZ, capacity);
            Array.Resize(ref EyeX, capacity);
            Array.Resize(ref EyeY, capacity);
            Array.Resize(ref EyeZ, capacity);
            Array.Resize(ref RotX, capacity);
            Array.Resize(ref RotY, capacity);
            Array.Resize(ref RotZ, capacity);
            Array.Resize(ref RotW, capacity);
            Array.Resize(ref Flags, capacity);
            Array.Resize(ref Hp, capacity);
        }

        public int AddWeaponName(string name)
        {
            int index = WeaponNames.IndexOf(name);
            if (index < 0)
            {
                index = WeaponNames.Count;
                WeaponNames.Add(name);
            }
            return index;
        }

        // A trimmed copy (for the attempt still being recorded, shown in a replay as it is right now).
        public RawAttempt Clone(AttemptEnd end)
        {
            var copy = (RawAttempt)MemberwiseClone();
            copy.End = end;
            copy.PosX = Trim(PosX);
            copy.PosY = Trim(PosY);
            copy.PosZ = Trim(PosZ);
            copy.EyeX = Trim(EyeX);
            copy.EyeY = Trim(EyeY);
            copy.EyeZ = Trim(EyeZ);
            copy.RotX = Trim(RotX);
            copy.RotY = Trim(RotY);
            copy.RotZ = Trim(RotZ);
            copy.RotW = Trim(RotW);
            copy.Flags = Trim(Flags);
            copy.Hp = Trim(Hp);
            copy.Events = new List<AttemptEvent>(Events);
            copy.WeaponNames = new List<string>(WeaponNames);
            return copy;
        }

        private T[] Trim<T>(T[] array)
        {
            var trimmed = new T[Count];
            Array.Copy(array, trimmed, Count);
            return trimmed;
        }
    }
}
