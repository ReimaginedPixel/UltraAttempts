using UltraAttempts.Replay;

namespace UltraAttempts
{
    // The replay's place in ULTRAKILL's cheat menu (Home / ~), under ATTEMPT REPLAY: watch every
    // attempt, and three buttons that step through its settings. (Only watching is in the cheat menu:
    // recording is always on and changes nothing.)
    internal static class Cheats
    {
        public const string Category = "attempt replay";

        // Into the cheat menu with ULTRAKILL's own cheats, just before their key binds are restored.
        public static void Register()
        {
            var cheats = MonoSingleton.GetInstance(typeof(CheatsManager)) as CheatsManager;
            if (cheats == null || cheats.GetCheatInstance<WatchAttempts>() != null)
            {
                return;
            }
            cheats.RegisterCheat(new WatchAttempts(), Category);
            cheats.RegisterCheat(new CameraSetting(), Category);
            cheats.RegisterCheat(new ViewSetting(), Category);
            cheats.RegisterCheat(new SourceSetting(), Category);
        }

        // The replay's over: its button goes back off.
        public static void ReplayEnded()
        {
            var cheats = MonoSingleton.GetInstance(typeof(CheatsManager)) as CheatsManager;
            WatchAttempts watch = cheats != null ? cheats.GetCheatInstance<WatchAttempts>() : null;
            if (watch != null && watch.IsActive)
            {
                cheats.SetCheatActive(watch, false);
                cheats.UpdateCheatState(watch);
            }
        }

        internal sealed class WatchAttempts : ICheat
        {
            public string LongName => "Watch Every Attempt";
            public string Identifier => "ultraattempts.watch";
            public string ButtonEnabledOverride => "STOP";
            public string ButtonDisabledOverride => "WATCH";
            public string Icon => "ghost";
            public bool DefaultState => false;
            public StatePersistenceMode PersistenceMode => StatePersistenceMode.NotPersistent;
            public bool IsActive { get; private set; }

            // Starts once the cheat menu's closed.
            public void Enable(CheatsManager manager)
            {
                IsActive = true;
                ReplayDirector.Instance?.RequestFromCheat();
            }

            public void Disable()
            {
                IsActive = false;
                ReplayDirector.Instance?.CancelFromCheat();
            }
        }

        // Setting buttons: pressing one steps to the next value (never "on", so they stay out of the
        // active cheats list).
        internal abstract class Setting : ICheat
        {
            public abstract string LongName { get; }
            public abstract string Identifier { get; }
            protected abstract string Value { get; }
            protected abstract void Next();

            public string ButtonEnabledOverride => Value;
            public string ButtonDisabledOverride => Value;
            public string Icon => "teleport";
            public bool DefaultState => false;
            public StatePersistenceMode PersistenceMode => StatePersistenceMode.NotPersistent;
            public bool IsActive => false;

            public void Enable(CheatsManager manager) => Next();

            public void Disable()
            {
            }
        }

        internal sealed class CameraSetting : Setting
        {
            public override string LongName => "Replay Camera";
            public override string Identifier => "ultraattempts.camera";

            protected override string Value
            {
                get
                {
                    switch (Settings.Camera.Value)
                    {
                        case ReplayCamera.NextToDie: return "NEXT TO DIE";
                        case ReplayCamera.BestRun: return "BEST RUN";
                        case ReplayCamera.Overview: return "OVERVIEW";
                        default: return "FREE CAM";
                    }
                }
            }

            protected override void Next() => Settings.Camera.Value = (ReplayCamera)(((int)Settings.Camera.Value + 1) % 4);
        }

        internal sealed class ViewSetting : Setting
        {
            public override string LongName => "Replay View";
            public override string Identifier => "ultraattempts.view";
            protected override string Value => Settings.FirstPerson.Value ? "EYES" : "BEHIND";

            protected override void Next() => Settings.FirstPerson.Value = !Settings.FirstPerson.Value;
        }

        internal sealed class SourceSetting : Setting
        {
            public override string LongName => "Replay Attempts";
            public override string Identifier => "ultraattempts.source";

            protected override string Value
            {
                get
                {
                    switch (Settings.Source.Value)
                    {
                        case ReplaySource.ThisRun: return "THIS RUN";
                        case ReplaySource.LastFinishedRun: return "LAST WIN";
                        case ReplaySource.Everything: return "EVERYTHING";
                        default: return "AUTO";
                    }
                }
            }

            protected override void Next() => Settings.Source.Value = (ReplaySource)(((int)Settings.Source.Value + 1) % 4);
        }
    }
}
