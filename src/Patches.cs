using HarmonyLib;
using UltraAttempts.Recording;
using UltraAttempts.Replay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UltraAttempts
{
    // Where UltraAttempts hooks into ULTRAKILL. Recording only listens (scene loads, restarts, the results
    // screen, the camera, doors, explosions, beams, breakables, what the game makes); the replay takes over the
    // camera, Escape, and the results screen's "continue".
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

        // A gun really firing, whatever button did it: recorded as the attempt's shot. (Not the nailgun: it fires while
        // held, which the attempt has already.)
        [HarmonyPatch(typeof(Revolver), "Shoot", typeof(int)), HarmonyPrefix]
        private static void RevolverFired() => Recorder.Instance?.OnShot(Replaying);

        [HarmonyPatch(typeof(Shotgun), "Shoot", new System.Type[0]), HarmonyPrefix]
        private static void ShotgunFired() => Recorder.Instance?.OnShot(Replaying);

        [HarmonyPatch(typeof(ShotgunHammer), "Impact", new System.Type[0]), HarmonyPrefix]
        private static void HammerSwung() => Recorder.Instance?.OnShot(Replaying);

        [HarmonyPatch(typeof(RocketLauncher), nameof(RocketLauncher.Shoot), new System.Type[0]), HarmonyPrefix]
        private static void RocketFired() => Recorder.Instance?.OnShot(Replaying);

        [HarmonyPatch(typeof(Railcannon), "Shoot", new System.Type[0]), HarmonyPrefix]
        private static void RailcannonFired() => Recorder.Instance?.OnShot(Replaying);

        // A punch really starting (it was ready for one), whatever button did it: recorded as the attempt's punch.
        [HarmonyPatch(typeof(Punch), nameof(Punch.PunchStart)), HarmonyPrefix]
        private static void Punching(Punch __instance)
        {
            if (__instance != null && __instance.ready)
            {
                Recorder.Instance?.OnPunch(Replaying);
            }
        }

        // ---------------------------------------------------------------- the game's own sounds
        // (Recording.RecordSounds). Nearly all of ULTRAKILL's sounds go through its Play / PlayOneShot / PlayDelayed
        // extensions (tracked or not), which hand the source to its virtual audio; sounds that play by themselves when
        // they're spawned (explosions, Gabriel's swings and teleports) reach that virtual audio as their objects come
        // on. Each is read before it starts (its clip, volume and pitch are set by then), and never throws into the game.

        [HarmonyPatch(typeof(AudioSourceExtensions), nameof(AudioSourceExtensions.Play), typeof(AudioSource), typeof(bool)), HarmonyPrefix]
        private static void SoundPlay(AudioSource @this) => Recorder.Instance?.OnSound(@this, @this != null ? @this.clip : null, 1f, 0f, SoundPath.Play, Replaying);

        // (the two-argument PlayOneShot calls this one)
        [HarmonyPatch(typeof(AudioSourceExtensions), nameof(AudioSourceExtensions.PlayOneShot), typeof(AudioSource), typeof(AudioClip), typeof(float), typeof(bool)), HarmonyPrefix]
        private static void SoundOneShot(AudioSource @this, AudioClip clip, float volumeScale) => Recorder.Instance?.OnSound(@this, clip, volumeScale, 0f, SoundPath.OneShot, Replaying);

        [HarmonyPatch(typeof(AudioSourceExtensions), nameof(AudioSourceExtensions.PlayDelayed), typeof(AudioSource), typeof(float), typeof(bool)), HarmonyPrefix]
        private static void SoundDelayed(AudioSource @this, float delay) => Recorder.Instance?.OnSound(@this, @this != null ? @this.clip : null, 1f, delay, SoundPath.Delayed, Replaying);

        // Every way into the game's virtual audio ends here: a sound that plays by itself (playOnAwake) is picked up as
        // it's spawned or switched on, or once it's playing.
        [HarmonyPatch(typeof(VirtualAudioManager), "AddAudioSource", typeof(VirtualAudioFilter)), HarmonyPrefix]
        private static void SoundTracked(VirtualAudioFilter filter)
        {
            try
            {
                if (filter != null && Recorder.Instance != null)
                {
                    Recorder.Instance.OnAwakeSound(filter.source, Replaying);
                }
            }
            catch (System.Exception)
            {
                // (the recorder logs its own failures; this only guards the filter's own lookups)
            }
        }

        // ...and whatever the game makes (WorldDetail = Everything): its effects, and things from the frame they're
        // made. Unity's generic Instantiate<T> with a position or parent calls one of these; only noted here (they run
        // hundreds of times a second), looked at once the frame's done. Hooks into Unity itself, so patched on their
        // own: if they can't be, the rest still is (Plugin).
        [HarmonyPatch]
        internal static class Spawns
        {
            [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion)), HarmonyPostfix]
            private static void Made(UnityEngine.Object original, UnityEngine.Object __result) => Recorder.Instance?.OnSpawn(original, __result, Replaying);

            [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion), typeof(Transform)), HarmonyPostfix]
            private static void MadeUnder(UnityEngine.Object original, UnityEngine.Object __result) => Recorder.Instance?.OnSpawn(original, __result, Replaying);

            [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), typeof(UnityEngine.Object), typeof(Transform), typeof(bool)), HarmonyPostfix]
            private static void MadeIn(UnityEngine.Object original, UnityEngine.Object __result) => Recorder.Instance?.OnSpawn(original, __result, Replaying);

            [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), typeof(UnityEngine.Object)), HarmonyPostfix]
            private static void Copied(UnityEngine.Object original, UnityEngine.Object __result) => Recorder.Instance?.OnSpawn(original, __result, Replaying);
        }
    }
}
