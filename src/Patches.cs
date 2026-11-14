using HarmonyLib;
using UltraAttempts.Recording;
using UltraAttempts.Replay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UltraAttempts
{
    // Where UltraAttempts hooks into ULTRAKILL. Recording only listens (scene loads, restarts, the results
    // screen, the camera, doors, explosions, beams, breakables); the replay takes over the camera, Escape, and
    // the results screen's "continue".
    [HarmonyPatch]
    internal static class Patches
    {
        // Set while the replay itself continues to the next level.
        public static bool LevelChangeAllowed;

        [HarmonyPatch(typeof(CheatBinds), nameof(CheatBinds.RestoreBinds)), HarmonyPrefix]
        private static void RegisterCheats() => Cheats.Register();

        // About to load a scene: this level again (a restart with no checkpoint), or somewhere else.
        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadSceneAsync)), HarmonyPrefix]
        private static void LoadingScene(string sceneName) => Recorder.Instance?.OnLeavingScene(sceneName);

        // Respawning at a checkpoint (dead, or "restart checkpoint" from the pause menu).
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.Restart)), HarmonyPrefix]
        private static void Restarting() => Recorder.Instance?.OnRestart();

        // The results screen: the level's beaten.
        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SendInfo)), HarmonyPrefix]
        private static void Results() => Recorder.Instance?.OnResults();

        // Rooms switched off behind a closing door (brought back during a replay).
        [HarmonyPatch(typeof(Door), nameof(Door.Optimize)), HarmonyPrefix]
        private static void RoomsOff(Door __instance) => RoomReveal.Track(__instance.deactivatedRooms);

        // Doors opening and closing (recorded, to show them in a replay; not the replay's own doings).
        [HarmonyPatch(typeof(Door), nameof(Door.Open)), HarmonyPostfix]
        private static void DoorOpened(Door __instance) => Recorder.Instance?.OnDoor(__instance, Replaying);

        [HarmonyPatch(typeof(Door), nameof(Door.Close)), HarmonyPostfix]
        private static void DoorClosed(Door __instance) => Recorder.Instance?.OnDoor(__instance, Replaying);

        // WorldDetail = Everything: explosions (once they know their size; its colour before it turns white),
        // hitscan beams (once they've hit), lasting beams, and breakables just before they break.
        [HarmonyPatch(typeof(Explosion), "Start"), HarmonyPostfix]
        private static void Exploded(Explosion __instance, Color ___materialColor) => Recorder.Instance?.OnExplosion(__instance, ___materialColor, Replaying);

        [HarmonyPatch(typeof(RevolverBeam), "Start"), HarmonyPostfix]
        private static void BeamShot(RevolverBeam __instance) => Recorder.Instance?.OnBeam(__instance, Replaying);

        [HarmonyPatch(typeof(ContinuousBeam), "Start"), HarmonyPostfix]
        private static void BeamOn(ContinuousBeam __instance) => Recorder.Instance?.OnContinuousBeam(__instance, Replaying);

        [HarmonyPatch(typeof(Breakable), nameof(Breakable.Break), typeof(float)), HarmonyPrefix]
        private static void Breaking(Breakable __instance, float damage)
        {
            // (Break returns early unless this breaks it)
            if (__instance != null && !__instance.unbreakable && !__instance.broken && __instance.durability <= damage)
            {
                Recorder.Instance?.OnBreak(__instance, Replaying);
            }
        }

        // After ULTRAKILL has put its camera on V1 for this frame: the recording's sample (body and eyes
        // read at the same moment), then the replay's camera.
        [HarmonyPatch(typeof(CameraController), "LateUpdate"), HarmonyPostfix]
        private static void CameraLateUpdate(CameraController __instance)
        {
            Recorder.Instance?.CameraLateUpdate(__instance, Replaying);
            ReplayDirector.Instance?.ApplyCamera(__instance);
        }

        private static bool Replaying => ReplayDirector.Instance != null && ReplayDirector.Instance.Presenting;

        // Escape during a replay leaves it (instead of pausing). The cheat menu's keys still pause, so
        // the cheat menu opens over the replay.
        [HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.Pause)), HarmonyPrefix]
        private static bool Pausing()
        {
            ReplayDirector director = ReplayDirector.Instance;
            if (director == null || !director.Presenting)
            {
                return true;
            }
            if (Input.GetKeyDown(KeyCode.Home) || Input.GetKeyDown(KeyCode.Tilde) || Input.GetKeyDown(KeyCode.BackQuote) || (Gamepad.current != null && Gamepad.current.selectButton.isPressed))
            {
                return true;
            }
            director.RequestExit();
            return false;
        }

        // Continuing from the results screen: with AtLevelEnd = Auto, every attempt plays first.
        [HarmonyPatch(typeof(FinalRank), nameof(FinalRank.LevelChange)), HarmonyPrefix]
        private static bool LevelChange(FinalRank __instance, bool force)
        {
            ReplayDirector director = ReplayDirector.Instance;
            if (director == null || LevelChangeAllowed)
            {
                return true;
            }
            if (director.Busy)
            {
                return false;  // (a click during the replay)
            }
            if (force || Settings.AtLevelEnd.Value != LevelEndReplay.Auto || director.LevelEndShown(__instance))
            {
                return true;
            }
            return !director.TryLevelEnd(__instance, thenNextLevel: true);
        }
    }
}
