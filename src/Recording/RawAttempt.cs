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
        // for every door in the level, with its state when the attempt started. C: DoorDetail (0 in
        // older recordings).
        Door = 4,
    }

    // What else a door event says (its C; older recordings have 0: nothing known).
    [Flags]
    public enum DoorDetail
    {
        None = 0,
        // The rest is known (every newer recording's door events have it).
        Recorded = 1,
        // The door was found like this (the attempt's start, or its room just came in), not opened or closed then.
        AsFound = 2,
        // A big door (one that swings) swings the other way: the side V1 opened it from.
        Reversed = 4,
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

    // What sort of thing a ThingTrack is (for drawing and the log; the look says what it looked like).
    public enum ThingKind : byte
    {
        Other = 0,
        Projectile = 1,   // anyone's: bullets, rockets, grenades, cannonballs, nails, sawblades, coins, harpoons...
        Debris = 2,       // gibs, chunks, shells: spawned physics bits
        Prop = 3,         // a level object knocked about (a crate, a barrel)
        Mover = 4,        // a level object that moves by itself (platforms, elevators, pistons, trams, big doors)
    }

    // What a tracked thing looked like, so a later game (another launch) can draw it again: each part's mesh and
    // materials by name (the mesh's vertex count tells same-named ones apart), where the part sits on the thing;
    // and, for when none of that can be found, its bounds (in its own space) and colour, drawn as a box.
    public sealed class ThingLook
    {
        public string Name = "";
        public List<LookPart> Parts = new List<LookPart>();
        public float CenterX, CenterY, CenterZ;
        public float SizeX = 0.3f, SizeY = 0.3f, SizeZ = 0.3f;
        public uint Color = 0xFFFFFFFFu;  // RGBA, 8 bits each (R highest)
    }

    public sealed class LookPart
    {
        public string Mesh = "";
        public int Vertices;
        public string[] Materials = Array.Empty<string>();
        public float PX, PY, PZ;                          // in the thing's own space (its transform, scale taken out)
        public float RX, RY, RZ, RW = 1f;
        public float SX = 1f, SY = 1f, SZ = 1f;
    }

    // One thing an attempt saw move (WorldDetail = Everything): projectiles, debris, props, movers. Kept as keys: a
    // key every world step while it moves, none while it stays put; it goes straight from one key to the next, and
    // a rest ends with its last key again where it started moving from. It's there from its first key (a level
    // object: all along) until End. Spawned things are drawn from their look; level objects (Path >= 0) are found
    // again and moved for real.
    public sealed class ThingTrack
    {
        public int Id;
        public ThingKind Kind;
        public int Look = -1;       // index into RawAttempt.Looks (-1: none)
        public int Path = -1;       // index into RawAttempt.ThingPaths: a level object (-1: spawned)
        public float ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f;  // its lossyScale when first seen
        public int End;             // the last attempt sample it was there at
        public bool Gone;           // went away (destroyed, switched off, out of range) before the attempt ended

        public int Count;
        public int[] Sample = new int[8];             // the attempt sample of each key (increasing)
        public int[] PosX = new int[8], PosY = new int[8], PosZ = new int[8];               // centimetres
        public short[] RotX = new short[8], RotY = new short[8], RotZ = new short[8], RotW = new short[8];  // * 32767, w >= 0
        public ushort[] Scale = new ushort[8];        // its size relative to ScaleX..Z, * 1000

        public int First => Count > 0 ? Sample[0] : End;

        public void Add(int sample, int px, int py, int pz, short rx, short ry, short rz, short rw, ushort scale)
        {
            if (Count == Sample.Length)
            {
                Reserve(Count * 2);
            }
            Sample[Count] = sample;
            PosX[Count] = px;
            PosY[Count] = py;
            PosZ[Count] = pz;
            RotX[Count] = rx;
            RotY[Count] = ry;
            RotZ[Count] = rz;
            RotW[Count] = rw;
            Scale[Count] = scale;
            Count++;
        }

        public void Reserve(int capacity)
        {
            if (capacity <= Sample.Length)
            {
                return;
            }
            Array.Resize(ref Sample, capacity);
            Array.Resize(ref PosX, capacity);
            Array.Resize(ref PosY, capacity);
            Array.Resize(ref PosZ, capacity);
            Array.Resize(ref RotX, capacity);
            Array.Resize(ref RotY, capacity);
            Array.Resize(ref RotZ, capacity);
            Array.Resize(ref RotW, capacity);
            Array.Resize(ref Scale, capacity);
        }

        public ThingTrack Clone()
        {
            var copy = (ThingTrack)MemberwiseClone();
            copy.Sample = (int[])Sample.Clone();
            copy.PosX = (int[])PosX.Clone();
            copy.PosY = (int[])PosY.Clone();
            copy.PosZ = (int[])PosZ.Clone();
            copy.RotX = (short[])RotX.Clone();
            copy.RotY = (short[])RotY.Clone();
            copy.RotZ = (short[])RotZ.Clone();
            copy.RotW = (short[])RotW.Clone();
            copy.Scale = (ushort[])Scale.Clone();
            return copy;
        }
    }

    public enum WorldEventKind : byte
    {
        // At X, Y, Z: Size its radius at the end (mm), Life how long it took to get there (ms), Color its own
        // (alpha included: it fades at 2 a second after). Newer recordings also have Look (its mesh in its
        // own material, not the white it starts in), Rot, and how it grows in To (see ExplosionGrowth).
        Explosion = 1,
        // From X, Y, Z to ToX, ToY, ToZ: Size its width (mm), Life how long it fades (ms), Color its own.
        Beam = 2,
        // A breakable broke: Look, Path, where it was (X, Y, Z) and how it was turned (Rot); Size about how big (mm).
        Break = 3,
    }

    // Something that happened at one moment (WorldDetail = Everything).
    public struct WorldEvent
    {
        public int Sample;
        public WorldEventKind Kind;
        public int X, Y, Z;           // centimetres
        public int ToX, ToY, ToZ;     // centimetres
        public int Size;              // millimetres
        public int Life;              // milliseconds
        public uint Color;            // RGBA
        public int Look;              // -1: none
        public int Path;              // -1: none
        public short RotX, RotY, RotZ, RotW;
    }

    [Flags]
    public enum SoundFlags : byte
    {
        None = 0,
        Loop = 1,
        // Played by V1 itself (its guns, its punches, its feet; anything under the player or its camera): a
        // replay plays it at the ghost.
        OnPlayer = 2,
        // Played with PlayOneShot (else Play: the source's own clip).
        OneShot = 4,
    }

    // A sound clip an attempt heard, by name (found again among the game's loaded clips later); its length
    // tells same-named clips apart.
    public struct SoundClip
    {
        public string Name;
        public int LengthMs;
    }

    // One of the game's own sounds starting (Recording.RecordSounds; format 4): which clip, when, where, how loud
    // and how it fades with distance, and when it stopped if it didn't play out by itself (a loop, a sound cut off).
    public struct SoundEvent
    {
        public int Sample;            // the attempt sample it started at
        public int Clip;              // index into RawAttempt.SoundClips
        public SoundFlags Flags;
        public int Volume;            // its source's volume (times PlayOneShot's scale) * 1000
        public int Pitch;             // * 1000 (negative: backwards)
        public byte Spatial;          // spatial blend * 255 (0: heard the same everywhere)
        public byte Rolloff;          // (int)AudioRolloffMode
        public int MinDistance;       // centimetres
        public int MaxDistance;       // centimetres
        public int X, Y, Z;           // centimetres, where it started (OnPlayer: unused)
        public int Length;            // samples it played for before it was stopped; 0: it played out by itself
    }

    // A sound still playing moved (its source went with a projectile, an enemy): where it was at that sample.
    public struct SoundMove
    {
        public int Sound;             // index into RawAttempt.Sounds
        public int Sample;
        public int X, Y, Z;           // centimetres
    }

    // How an explosion grows (0 and 0 in older recordings), kept in its event's To, which explosions didn't
    // use: ToX - X its scale when it went off, ToY - Y how much its scale grows a second until it starts to
    // fade (a quarter of that after), both in thousandths. ULTRAKILL adds 0.05 * speed to its scale every
    // physics step.
    public static class ExplosionGrowth
    {
        public static void Put(ref WorldEvent e, float startScale, float perSecond)
        {
            e.ToX = e.X + Thousandths(startScale);
            e.ToY = e.Y + Thousandths(perSecond);
            e.ToZ = e.Z;
        }

        public static float StartScale(in WorldEvent e) => (e.ToX - e.X) / 1000f;

        public static float PerSecond(in WorldEvent e) => (e.ToY - e.Y) / 1000f;

        private static int Thousandths(float v) =>
            float.IsNaN(v) ? 0 : (int)Math.Round(Math.Max(0.0, Math.Min(1_000_000.0, v * 1000.0)));
    }

    // One attempt as recorded: fixed-rate samples, already quantized (centimetres, rotation * 32767),
    // kept as one array per channel (which is also how they're stored, for compression). Sample i is
    // at i / SampleRate seconds into the attempt. Format 5's blocks keep their own parts of it in their own
    // files (Blocks/), each with its own part of Clone.
    public sealed partial class RawAttempt
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

        // Everything else that moved or happened (WorldDetail = Everything; format 3).
        public List<ThingLook> Looks = new List<ThingLook>();
        public List<string> ThingPaths = new List<string>();
        public List<ThingTrack> Things = new List<ThingTrack>();
        public List<WorldEvent> WorldEvents = new List<WorldEvent>();

        // The game's own sounds as this attempt heard them (Recording.RecordSounds; format 4). SoundsRecorded:
        // they were being recorded (older attempts, or with RecordSounds off, have none).
        public bool SoundsRecorded;
        public List<SoundClip> SoundClips = new List<SoundClip>();
        public List<SoundEvent> Sounds = new List<SoundEvent>();
        public List<SoundMove> SoundMoves = new List<SoundMove>();

        // Where this attempt's bytes are in its run file (set when read from one): its world can be read again
        // from there later instead of being kept in memory. Not saved.
        public string SourcePath;
        public long SourceOffset;
        public int SourceLength;

        public float Duration => Count <= 1 ? 0f : (Count - 1) / (float)SampleRate;

        public bool HasThings => Things.Count > 0 || WorldEvents.Count > 0;

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

        public int AddThingPath(string path)
        {
            int index = ThingPaths.IndexOf(path);
            if (index < 0)
            {
                index = ThingPaths.Count;
                ThingPaths.Add(path);
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
            copy.Looks = new List<ThingLook>(Looks);  // (looks never change once made)
            copy.ThingPaths = new List<string>(ThingPaths);
            copy.Things = Things.ConvertAll(t => t.Clone());
            copy.WorldEvents = new List<WorldEvent>(WorldEvents);
            copy.SoundClips = new List<SoundClip>(SoundClips);
            copy.Sounds = new List<SoundEvent>(Sounds);
            copy.SoundMoves = new List<SoundMove>(SoundMoves);
            copy.SourcePath = null;
            CloneArms(copy);
            CloneSections(copy);
            CloneEffects(copy);
            return copy;
        }

        // (each block's lists copied for a Clone: see Blocks/)
        partial void CloneArms(RawAttempt copy);
        partial void CloneSections(RawAttempt copy);
        partial void CloneEffects(RawAttempt copy);

        private T[] Trim<T>(T[] array)
        {
            var trimmed = new T[Count];
            Array.Copy(array, trimmed, Count);
            return trimmed;
        }
    }
}
