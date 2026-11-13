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
        // A door opened (B = 1) or closed (B = 0). A: index into DoorPaths. At sample 0 there is one
        // for every door in the level, with its state when the attempt started.
        Door = 4,
    }

    // One enemy an attempt met: where it was, how it was turned, which animation it was in, from when it
    // showed up until it died (or the attempt ended). Sample k lines up with attempt sample
    // Start + k * RawAttempt.EnemyStride.
    public sealed class EnemyTrack
    {
        public int Id;              // unique within the attempt
        public int Type = -1;       // (int)EnemyType, -1 if unknown
        public string Name = "";    // its GameObject's name, without "(Clone)"
        public float Scale = 1f;    // its uniform scale (lossyScale.y)
        public int Start;           // the attempt sample its first sample lines up with
        public bool Died;           // ended because it died (else it was still around when the attempt ended, or vanished)

        public int Count;
        public int[] PosX = new int[16], PosY = new int[16], PosZ = new int[16];               // centimetres
        public short[] RotX = new short[16], RotY = new short[16], RotZ = new short[16], RotW = new short[16];  // * 32767, w >= 0
        public int[] Anim = new int[16];            // its Animator's layer-0 state fullPathHash (0: no animator)
        public ushort[] AnimTime = new ushort[16];  // normalized time in that state * 65535 (looping states wrapped, others clamped to 1)
        public byte[] Health = new byte[16];        // health relative to when it showed up (255: untouched)

        public void Add(int px, int py, int pz, short rx, short ry, short rz, short rw, int anim, ushort animTime, byte health)
        {
            if (Count == PosX.Length)
            {
                Reserve(Count * 2);
            }
            PosX[Count] = px;
            PosY[Count] = py;
            PosZ[Count] = pz;
            RotX[Count] = rx;
            RotY[Count] = ry;
            RotZ[Count] = rz;
            RotW[Count] = rw;
            Anim[Count] = anim;
            AnimTime[Count] = animTime;
            Health[Count] = health;
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
            Array.Resize(ref RotX, capacity);
            Array.Resize(ref RotY, capacity);
            Array.Resize(ref RotZ, capacity);
            Array.Resize(ref RotW, capacity);
            Array.Resize(ref Anim, capacity);
            Array.Resize(ref AnimTime, capacity);
            Array.Resize(ref Health, capacity);
        }

        public EnemyTrack Clone()
        {
            var copy = (EnemyTrack)MemberwiseClone();
            copy.PosX = (int[])PosX.Clone();
            copy.PosY = (int[])PosY.Clone();
            copy.PosZ = (int[])PosZ.Clone();
            copy.RotX = (short[])RotX.Clone();
            copy.RotY = (short[])RotY.Clone();
            copy.RotZ = (short[])RotZ.Clone();
            copy.RotW = (short[])RotW.Clone();
            copy.Anim = (int[])Anim.Clone();
            copy.AnimTime = (ushort[])AnimTime.Clone();
            copy.Health = (byte[])Health.Clone();
            return copy;
        }
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

        // Enemies are sampled every EnemyStride attempt samples.
        public int EnemyStride = 2;
        public List<EnemyTrack> Enemies = new List<EnemyTrack>();
        // Doors, by Game.PathOf (EventKind.Door's A indexes this).
        public List<string> DoorPaths = new List<string>();

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

        public int AddDoorPath(string path)
        {
            int index = DoorPaths.IndexOf(path);
            if (index < 0)
            {
                index = DoorPaths.Count;
                DoorPaths.Add(path);
            }
            return index;
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
            copy.Enemies = Enemies.ConvertAll(e => e.Clone());
            copy.DoorPaths = new List<string>(DoorPaths);
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
