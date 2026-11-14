using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace UltraAttempts
{
    internal enum ReplayCamera
    {
        // Through the eyes of whichever attempt dies next (each shot at least MinShotSeconds long).
        NextToDie,
        // Through the eyes of the winning attempt (the fastest finish; else the one that lived longest).
        BestRun,
        // Above and behind every attempt still alive, framing them all.
        Overview,
        // Fly anywhere: WASD, mouse, Q/E down/up, Shift fast.
        FreeCam,
    }

    internal enum ReplaySource
    {
        // This run if it has any attempts, else the last finished run.
        Auto,
        // Every attempt since the level was last beaten (including the one you're on).
        ThisRun,
        // The last time you beat the level: every attempt it took.
        LastFinishedRun,
        // Every attempt ever recorded at this level and difficulty.
        Everything,
    }

    internal enum LevelEndReplay
    {
        Off,
        // On the results screen, press the watch key to see every attempt.
        Prompt,
        // Continuing from the results screen plays every attempt first, then loads the next level.
        Auto,
    }

    internal enum WorldDetail
    {
        // Only V1.
        Off,
        // V1, the enemies around it and the doors.
        Enemies,
        // Everything that moves: also projectiles, coins, rockets, cannonballs, nails, explosions, beams,
        // physics props, gibs and moving platforms. Heavy (bigger recordings, slower to load).
        Everything,
    }

    internal enum GhostModel
    {
        // V1 itself (the platformer secret level's third-person V1), animated.
        V1,
        // V2 (the enemy's model), animated.
        V2,
        // The plain crystal ghosts.
        Crystal,
    }

    internal enum GhostColors
    {
        Rainbow,
        // Red for the first attempts through to green for the last.
        Progress,
    }

    internal static class Settings
    {
        public static ConfigEntry<bool> Record;
        public static ConfigEntry<int> SampleRate;
        public static ConfigEntry<bool> RecordWeapons;
        public static ConfigEntry<WorldDetail> World;
        public static ConfigEntry<bool> ContinueRunsAcrossVisits;
        public static ConfigEntry<int> KeepRunsPerLevel;
        public static ConfigEntry<bool> ShowAttemptCounter;
        public static ConfigEntry<string> Folder;

        // The game's own sounds: recorded with each attempt, played again in a replay.
        public static ConfigEntry<bool> RecordSounds;
        public static ConfigEntry<float> OtherAttemptsVolume;

        public static ConfigEntry<ReplayCamera> Camera;
        public static ConfigEntry<bool> FirstPerson;
        public static ConfigEntry<ReplaySource> Source;
        public static ConfigEntry<LevelEndReplay> AtLevelEnd;
        public static ConfigEntry<int> MinAttemptsAtLevelEnd;
        public static ConfigEntry<bool> ShowWeapons;
        public static ConfigEntry<bool> DeathMarkers;
        public static ConfigEntry<bool> Sounds;
        public static ConfigEntry<float> Volume;
        public static ConfigEntry<float> GhostOpacity;
        public static ConfigEntry<GhostColors> Colors;
        public static ConfigEntry<GhostModel> Model;
        public static ConfigEntry<int> AnimatedGhosts;
        public static ConfigEntry<bool> ShowWorld;
        public static ConfigEntry<bool> OverlayWorlds;
        public static ConfigEntry<float> OverlayOpacity;
        private static ConfigEntry<int> configVersion;
        public static ConfigEntry<float> CameraSmoothing;
        public static ConfigEntry<bool> CameraShake;
        public static ConfigEntry<int> MaxGhosts;
        public static ConfigEntry<float> MinShotSeconds;
        public static ConfigEntry<bool> RevealRooms;
        public static ConfigEntry<bool> Loop;

        public static ConfigEntry<Key> WatchKey;
        public static ConfigEntry<Key> ExitKey;

        public static void Bind(ConfigFile config)
        {
            Record = config.Bind("Recording", "Enabled", true,
                "Record every attempt at every level (not a cheat: nothing about the game changes, and ranks still count).");
            SampleRate = config.Bind("Recording", "SampleRate", 60,
                new ConfigDescription("Samples per second. Replays are interpolated between them; 60 is smooth even through your eyes while sliding.", new AcceptableValueRange<int>(10, 60)));
            RecordWeapons = config.Bind("Recording", "RecordWeapons", true,
                "Also record which weapon you hold (and its variant), when you shoot and when you punch, to show on the ghosts.");
            World = config.Bind("Recording", "WorldDetail", WorldDetail.Everything,
                "How much of the level each attempt records, to play it back with the attempt: Off (only V1), Enemies (the enemies around you, animated, and the doors), Everything (also every projectile, coin, rocket, cannonball, nail, explosion, beam, physics prop, gib and moving platform: much bigger recordings).");
            ContinueRunsAcrossVisits = config.Bind("Recording", "ContinueRunsAcrossVisits", true,
                "Leaving a level before beating it keeps its run going: the attempts next time count on from there (like Geometry Dash). Off: every visit is a new run.");
            KeepRunsPerLevel = config.Bind("Recording", "KeepRunsPerLevel", 30,
                "How many finished runs to keep per level and difficulty (the oldest are deleted). 0 keeps them all.");
            ShowAttemptCounter = config.Bind("Recording", "ShowAttemptCounter", true,
                "Show \"ATTEMPT 37\" for a moment at the start of each attempt.");
            Folder = config.Bind("Recording", "Folder", "",
                "Where recordings go. Empty: BepInEx\\UltraAttempts next to the game.");

            // The game's own sounds.
            RecordSounds = config.Bind("Recording", "RecordSounds", true,
                "Also record the game's own sound effects as you hear them (your guns and punches, explosions, enemies and their voice lines, doors), to play them again in a replay. Not the music.");
            OtherAttemptsVolume = config.Bind("Replay", "OtherAttemptsVolume", 0.3f,
                new ConfigDescription("In a replay, the attempt you follow plays its sounds in full; this is how loud every other attempt's are (the nearest first; the same sound from many attempts at once plays once, louder). 0: only the followed attempt's. Sounds = false or Volume = 0 silences them all.", new AcceptableValueRange<float>(0f, 1f)));

            Camera = config.Bind("Replay", "Camera", ReplayCamera.NextToDie,
                "The camera a replay starts with (Tab cycles them during the replay; also in the cheat menu).");
            FirstPerson = config.Bind("Replay", "FirstPerson", true,
                "Following an attempt: through its eyes (on) or from behind it (off). V switches during the replay.");
            Source = config.Bind("Replay", "Attempts", ReplaySource.Auto,
                "Which attempts a replay shows (also in the cheat menu).");
            AtLevelEnd = config.Bind("Replay", "AtLevelEnd", LevelEndReplay.Auto,
                "After beating a level (no cheats needed then, it's over): Auto plays every attempt before the next level loads; Prompt waits for the watch key on the results screen; Off only replays from the cheat menu.");
            MinAttemptsAtLevelEnd = config.Bind("Replay", "MinAttemptsAtLevelEnd", 2,
                "Only replay at the level's end if it took at least this many attempts.");
            ShowWeapons = config.Bind("Replay", "ShowWeapons", true,
                "Show each ghost's weapon (in its variant's colour), its shots and its punches.");
            DeathMarkers = config.Bind("Replay", "DeathMarkers", true,
                "Leave a red marker where each attempt died (M toggles during the replay).");
            Sounds = config.Bind("Replay", "Sounds", true, "A pop for each death, a chime for each finish.");
            Volume = config.Bind("Replay", "Volume", 0.6f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            GhostOpacity = config.Bind("Replay", "GhostOpacity", 0.35f,
                new ConfigDescription("How solid the ghosts are (the one the camera follows and the winner are always clearer).", new AcceptableValueRange<float>(0.05f, 1f)));
            Colors = config.Bind("Replay", "GhostColors", GhostColors.Rainbow, "How ghosts are coloured (the winner is always gold).");
            Model = config.Bind("Replay", "GhostModel", GhostModel.V1,
                "What the ghosts look like: V1 (animated), V2 (animated), or Crystal. If a model can't be loaded, the next one down is used.");
            AnimatedGhosts = config.Bind("Replay", "AnimatedGhosts", 24,
                new ConfigDescription("How many ghosts get a fully animated model at once (the followed one, the winner and the nearest). The rest use pre-posed copies, which are much cheaper.", new AcceptableValueRange<int>(1, 200)));
            ShowWorld = config.Bind("Replay", "ShowWorld", true,
                "Following an attempt: show its enemies (where they were, animated, dying when it killed them) and its doors opening and closing. The level's own frozen enemies hide meanwhile.");
            CameraSmoothing = config.Bind("Replay", "CameraSmoothing", 0.05f,
                new ConfigDescription("Seconds of smoothing on the camera when seeing through an attempt's eyes (0: none, exactly as recorded).", new AcceptableValueRange<float>(0f, 0.5f)));
            CameraShake = config.Bind("Replay", "CameraShake", false,
                "Through an attempt's eyes: shake when it gets hurt, as the game does (scaled by the game's own screen shake setting). Off: steady.");
            OverlayWorlds = config.Bind("Replay", "OverlayWorlds", true,
                "Every other attempt still alive plays its world too, see-through in its colour, over the one you follow: its enemies, and (recorded with WorldDetail = Everything) its projectiles, props, explosions and beams. The nearest 20 at once. O switches it during a replay.");
            OverlayOpacity = config.Bind("Replay", "OverlayOpacity", 0.18f,
                new ConfigDescription("How solid the other attempts' worlds are (a little less each when many are shown at once).", new AcceptableValueRange<float>(0.02f, 1f)));

            // 1.0 sampled at 30 a second, which is jumpy through an attempt's eyes: move old configs up to 60.
            configVersion = config.Bind("Internal", "ConfigVersion", 1, "(UltraAttempts' own bookkeeping: leave it.)");
            if (configVersion.Value < 2)
            {
                if (SampleRate.Value == 30)
                {
                    SampleRate.Value = 60;
                }
                configVersion.Value = 2;
            }
            MaxGhosts = config.Bind("Replay", "MaxGhosts", 2000,
                "Most attempts in one replay (the newest are kept, and every finish). 0: no limit.");
            MinShotSeconds = config.Bind("Replay", "MinShotSeconds", 0.75f,
                new ConfigDescription("Next-to-die camera: skip attempts that die sooner than this after a cut, so it doesn't flicker through the early deaths.", new AcceptableValueRange<float>(0f, 5f)));
            RevealRooms = config.Bind("Replay", "RevealRooms", true,
                "Bring back the rooms ULTRAKILL switched off behind closed doors while a replay plays, so ghosts aren't running through the void. They switch off again after.");
            Loop = config.Bind("Replay", "Loop", false, "Start over when the replay ends (instead of returning to the game).");

            WatchKey = config.Bind("Keys", "Watch", Key.F8, "On the results screen: watch every attempt.");
            ExitKey = config.Bind("Keys", "Exit", Key.Backspace, "Leave a replay (Escape works too).");
        }
    }
}
