# UltraAttempts

Every attempt at an ULTRAKILL level, played back at once, like Geometry Dash's attempt replays. A BepInEx 5 plugin.

It records each attempt as you play a level. An attempt runs from the level's start (or a checkpoint) until you die, restart or reach the final pit. When you finally beat the level, every attempt plays at the same moment as ghost V1s: the game's own V1 model, animated, holding the weapon you had. A thousand V1s set off together, pop one by one where they died, and the last one standing is the run that made it. Follow any one of them, through its eyes or from behind, and you see what it saw: its enemies fighting it, its doors opening.

- **Recording isn't a cheat.** It only watches: nothing in the game changes, ranks and leaderboards still count, and it needs no cheat menu. It's always on (it can be turned off in the config).
- **Watching is.** Mid-level, the replay is in ULTRAKILL's cheat menu (it freezes the level while it plays). After you beat a level it's over, so no cheats are needed: by default every attempt plays as you continue from the results screen, before the next level loads.

## Watching

**After beating a level:** on the results screen, continue as usual. If it took 2 or more attempts, they all play first, then the next level loads. Or press **F8** on the results screen to watch them and come back to the results.

**Any time, from the ULTRAATTEMPTS menu:** pause and press **WATCH** (see [the menu](#the-ultraattempts-menu)). No cheats needed.

**Or from the cheat menu:** turn on ULTRAKILL's cheats (the usual way), open the cheat menu (**Home** or **~**), and under **ATTEMPT REPLAY**:

| Cheat | |
|---|---|
| **Watch Every Attempt** | Plays the replay once you close the menu. |
| **Replay Camera** | NEXT TO DIE, BEST RUN, OVERVIEW or FREE CAM (the camera it starts with). |
| **Replay View** | EYES (first person) or BEHIND (third person), when following an attempt. |
| **Replay Attempts** | AUTO, THIS RUN, LAST WIN or EVERYTHING. |

As with any of ULTRAKILL's cheats, turning cheats on mid-level means that level gets no rank.

### Cameras

- **Next to die:** through the eyes of whichever attempt dies next. When it dies the camera backs out of its head to watch the head fly off, then the screen flashes red and the camera cuts to the next one to die, until only the winner is left. It skips attempts that die within 0.75 s of a cut, so the early deaths don't flicker past.
- **Best run:** through the eyes of the winning attempt (or the fastest finish; if none finished, the one that lived longest).
- **Overview:** behind and above every attempt still alive, framing them all as the pack thins out. The mouse moves it around and the wheel zooms.
- **Free cam:** fly anywhere: WASD, the mouse, Q/E down/up, Shift fast, Ctrl slow, the wheel for speed.

### During a replay

| Key | |
|---|---|
| **Tab** / **1-4** | Camera (next to die, best run, overview, free cam) |
| **V** | Eyes / behind |
| **Q** / **E** | Follow the previous / next attempt still alive |
| **Mouse**, **wheel** | Look around and zoom (behind, overview), or fly (free cam) |
| **← / →** | Seek 5 s (Shift: 1 s) |
| **↑ / ↓** | Speed (0.1x to 8x) |
| **Space** | Pause (at the end: watch again) |
| **R** | From the start |
| **M** | Death markers on/off |
| **O** | Other attempts' worlds on/off |
| **H** | Hide the HUD |
| **Esc** / **Backspace** | Leave |

The HUD shows how many attempts are alive, dead and finished, and a timeline with the deaths piled up where they happened. For the attempt you're following it shows:
- its number
- when it dies, or its time for the win
- when it was played ("13 NOV 18:42"), and "DAY 3" if the run took more than a day
- its HP, its weapon and how many enemies it has killed so far

Through an attempt's eyes the camera is smooth. It follows a curve through the recording, averaged over a short window, so slides, dashes and flicks don't jitter. Cuts and seeks stay sharp.

### What the ghosts show

Each attempt is V1: the third-person V1 from the platformer secret level, at V1's real size and animated from what you did. It runs, jumps, falls, slides and dashes where you did, faces where you headed, and holds your real weapon in its hand, in its variant's colours. Through a ghost's eyes, that weapon sits bottom right like the game's own view.
- **The attempt you follow and the winner** look like the real V1, with a glow and an outline in their colour, and you can see them through walls.
- **The rest of the crowd** are see-through and tinted in their own colour. The winner is gold, and the attempt you were on when you opened the replay is white.
- **The 24 nearest** are fully animated (**AnimatedGhosts**). Everyone else uses V1 posed in 13 ways (a run cycle, jump, fall, slide, dash), so a crowd of a thousand still moves like V1s and stays fast.

Each ghost shows a tracer for every shot (up to where it hit the level) and throws its punches. It flashes red when hurt. The winner leaves a gold trail.

When an attempt dies, its head pops off and flies: up and on the way it was going, spinning, bouncing off the floor and walls until it comes to rest. Its body falls over where it stood, blood bursts from the neck and pools under it, and after a couple of seconds it all fades. A red marker stays where it died (M hides them). The crystal ghosts shatter instead. It's only drawn (no gore is left in the level), and it stays cheap with 30 attempts dying in the same spot. **DeathAnimations** turns it off (then they just pop).

If V1's model can't be loaded, the ghosts are V2 instead, and if that fails too, the plain crystals from 1.0. **GhostModel** can also pick them. `BepInEx\LogOutput.log` says which it used.

### What the followed attempt shows

Following an attempt (next to die, best run, or Q/E) shows its world as it was:
- **Its enemies:** the real enemy models, animated, where they were, taking its hits and dying when it killed them.
- **Its doors:** opening and closing when they did for it, every kind: sliding doors, big doors that swing open (the way it opened them, from the side it came from) and doors with sliding or animated panels, each at its own speed. A door it never got to stays as it was before any attempt opened it.
- **The level's own enemies,** frozen where the replay stopped them, hide meanwhile.

In the overview and free cam, the winner's world shows. Everything goes back exactly as it was when the replay ends.

With **WorldDetail** = Everything (the default), each attempt also records everything else that moves or happens near V1, within 150 m:
- everyone's projectiles (bullets, pellets, rockets, grenades, cannonballs, nails, sawblades, coins, harpoons)
- gibs, debris and shells, and props that get knocked about
- the level's moving platforms, elevators, pistons, trams and rotating doors
- every explosion, beam and breakable that breaks

When you follow that attempt:
- Its projectiles and debris are drawn in their real materials.
- The level's platforms and props move as they did for it, and go back afterwards.
- Its explosions are the game's own: white for a moment, then their colour, growing and fading as the game does it, see-through (**ExplosionOpacity**) so they don't hide the fight. Its beams play, and what it broke breaks.

The other attempts' worlds play at the same time: the 20 nearest living attempts, each see-through in its own colour, with their enemies, props, projectiles, explosions and beams. So all your attempts' fights happen on top of each other.
- **O** turns them on and off, and the HUD says how many are showing.
- **OverlayWorlds** and **OverlayOpacity** set this in the config. Recordings with only enemies overlay their enemies.

### What you hear

Each attempt records the game's own sounds as you heard them: your gunshots and punches, footsteps, explosions, the enemies, Gabriel's taunts and every other voice line, doors. Not the music. A replay plays them again with the game's own clips:
- **The attempt you follow** (or, in the overview and free cam, the winner) plays its sounds in full, from where they were. Through its eyes they sound as they did.
- **Every other attempt** plays its sounds quieter (**OtherAttemptsVolume**, 0.3; 0 for only the followed one's), the nearest first. The same sound from many attempts at once (28 gunshots as the level starts) plays once, a little louder.
- Pausing pauses them, speeding up or slowing down pitches them up or down, and a seek or a camera cut stops them (no burst of everything skipped over).
- Attempts recorded before sounds were play their shots with the game's sound for that gun.

The death pop and the win chime still play on top. A clip the level hasn't loaded is skipped; `BepInEx\LogOutput.log` says how many were found.

ULTRAKILL switches rooms off behind doors that close behind you. While a replay plays, those rooms come back (as if you'd walked back through the door), so earlier attempts aren't running through nothing. They switch off again after.

## Runs and attempts

Every attempt at a level and difficulty counts, like Geometry Dash's attempt counter: across deaths, checkpoint restarts, full restarts, leaving and coming back, game launches, and wins. **ATTEMPT 37** shows for a moment at the start of each attempt. The count only goes back to 1 when you reset it (see the menu below). Levels played with 1.0 count on from the attempts already recorded.

A **run** is every attempt since you last beat the level (or reset its count). It's what a replay shows by default. Beating the level saves the run whether you watch it or not, and the next attempt starts a new run while the count carries on.

What a replay shows (**Replay Attempts**):
- **This run:** every attempt since the level was last beaten, including the one you're on right now (in white).
- **Last win:** every attempt it took the last time you beat the level.
- **Everything:** every attempt ever recorded at this level and difficulty.
- **Auto:** this run if it has any attempts, else the last win.

Every attempt starts at the same moment in the replay: from the level's start, or from the checkpoint it respawned at.

A run can take days. It carries on across game launches until the level is beaten, and attempts recorded by 1.0 carry on too. The replay shows which day each attempt was, and how many days the whole run took.

## The ULTRAATTEMPTS menu

Pause the game (or go to the main menu): an **ULTRAATTEMPTS** button sits in the top-left corner. It opens:
- **This level:** its attempt count and how many since the last win.
  - **WATCH** plays every attempt right away, no cheats needed.
  - **RESET** (click twice) starts the count again at 1. The run in progress ends without a win, and every recording stays.
  - **EXPORT** saves the level's runs to a zip.
- **Settings:**
  - **After beating a level:** play the attempts first, only if you press F8, or never (just save them).
  - **What to record:** everything that moves, V1 with enemies and doors, or only V1.
  - **Ghost model:** V1, V2 or crystals.
  - The "ATTEMPT 37" banner, and recording itself, on or off.
- **Every level:** each level's count, runs and wins, with **WATCH**, **RESET** and **EXPORT** for each. WATCH on another level (or from the main menu) loads it, and its attempts play once you've landed. Custom levels can only be watched from inside them.
- **EXPORT EVERYTHING**, **IMPORT** and **OPEN FOLDER.** Exports go in `BepInEx\UltraAttempts\Exports`. To import, put zips in `BepInEx\UltraAttempts\Import` and press IMPORT:
  - Their runs join your levels; runs you already have are skipped.
  - The zips move to `Import\Done`.
  - Importing never changes a level's count.

## How it's recorded

At 60 samples a second of game time (so pausing and hitstops take no room), read each frame right after the game's camera has moved. Each sample holds:
- V1's position
- where the eyes are and where they look, without ULTRAKILL's screen shake (it shakes every frame you slide, and up to a metre on shotgun blasts and slams), so seeing through an attempt's eyes is steady
- HP, movement (on the ground, sliding, dashing), shots and punches
- weapon switches and gravity changes, as events

Recordings from 1.0 had the shake in them; the replay takes it out of those (about 25 cm of jitter per sample down to 4 on real runs).

With **WorldDetail** above Off, each attempt also records:
- each enemy around (where it is, which way it faces, its animation, its health, when it dies)
- every door's state at the start, then each open and close

With **RecordSounds** (on by default, whatever WorldDetail is), it also records each of the game's sounds as it starts: its clip by name (found again among the game's loaded clips later), when and where (V1's own go with the ghost), how loud, pitched and spread, when it stopped if it was cut off or looped, and where it went if it moved. Not the music, and nothing out of earshot. About 5 KB a minute in a fight.

With Everything, it also records whatever else moves (see above): each thing with what it looks like (its meshes and materials by name, found again among the game's loaded assets later, or a coloured box) and where it was, plus explosions, beams and breakables as events. A busy fight adds about 40 KB a minute. Worlds are read back from the run file only when a replay needs them, so long runs still load quickly.

Recordings are kept small. Each channel (positions, rotations, ...) is stored on its own. Positions and rotations are stored as the difference from where the two samples before predict them to be: moving at a steady speed or turning at a steady rate leaves nothing to store. Everything goes in as zigzag varints, and the whole attempt is deflated.
- **Non-stop movement:** about 14 KB a minute at 60 Hz.
- **With 10 enemies around and 30 doors:** about 32 KB a minute.
- **Standing still next to an idling enemy:** about 200 bytes a minute. A run is one file, written on a background thread as each attempt ends, so dying never hitches the game. A crash can't corrupt a run: half-written attempts are cut off.

Recordings go in `BepInEx\UltraAttempts\<level>\<difficulty>\`, one `.ukattempts` file per run, with the level's attempt count in `attempts.count`. The newest 30 finished runs of each level are kept. Deleting old ones never lowers the count.

## Settings

In `BepInEx\config\dev.ultracraft.attempts.cfg` (made the first time the game starts with UltraAttempts):

| Setting | Default | |
|---|---|---|
| Recording / Enabled | true | Record attempts at all. |
| Recording / SampleRate | 60 | Samples per second (10-60). (A config from 1.0 that still says 30 moves to 60 once.) |
| Recording / RecordWeapons | true | Record the weapon, shots and punches too. |
| Recording / WorldDetail | Everything | Off (only V1), Enemies (enemies and doors), Everything (whatever moves: much bigger recordings). |
| Recording / RecordSounds | true | Record the game's own sounds too (not the music), to hear them in a replay. |
| Recording / ContinueRunsAcrossVisits | true | Leaving a level keeps its run going (off: every visit is a new run). |
| Recording / KeepRunsPerLevel | 30 | Finished runs kept per level and difficulty (0: all). |
| Recording / ShowAttemptCounter | true | "ATTEMPT 37" at the start of each attempt. |
| Recording / Folder | (empty) | Where recordings go (empty: `BepInEx\UltraAttempts`). |
| Replay / Camera | NextToDie | NextToDie, BestRun, Overview or FreeCam. |
| Replay / FirstPerson | true | Through the eyes (or from behind). |
| Replay / Attempts | Auto | Auto, ThisRun, LastFinishedRun or Everything. |
| Replay / AtLevelEnd | Auto | Auto: play before the next level. Prompt: only with F8 on the results screen. Off. |
| Replay / MinAttemptsAtLevelEnd | 2 | Only play at the level's end if it took at least this many. |
| Replay / ShowWeapons | true | Weapons, shots and punches on the ghosts. |
| Replay / DeathMarkers | true | A red marker where each attempt died. |
| Replay / Sounds, Volume | true, 0.6 | A pop for each death, a chime for the win. |
| Replay / OtherAttemptsVolume | 0.3 | How loud the other attempts' sounds are next to the followed one's (0: only the followed one's). The game's sounds also follow Sounds, Volume and the game's own sound effects volume. |
| Replay / GhostOpacity | 0.35 | How solid the ghosts are. |
| Replay / GhostColors | Rainbow | Rainbow, or Progress (red for the first attempts to green for the last). |
| Replay / GhostModel | V1 | V1, V2 or Crystal (V1 falls back to V2, V2 to Crystal). |
| Replay / AnimatedGhosts | 24 | How many ghosts are fully animated at once (the rest use pre-posed V1s). |
| Replay / ShowWorld | true | Show the followed attempt's enemies and doors (the level's own enemies hide meanwhile). |
| Replay / OverlayWorlds, OverlayOpacity | true, 0.18 | With Everything: every other attempt's world too, see-through in its colour. |
| Replay / CameraSmoothing | 0.05 | Seconds of smoothing on the eyes camera (0: exactly as recorded). |
| Replay / CameraShake | false | Through an attempt's eyes: shake when it gets hurt, as the game does. |
| Replay / MaxGhosts | 2000 | Most attempts in one replay (the newest, plus every win). |
| Replay / MinShotSeconds | 0.75 | Next-to-die camera: the shortest a shot lasts. |
| Replay / RevealRooms | true | Bring back rooms switched off behind doors while a replay plays. |
| Replay / Loop | false | Start over at the end. |
| Replay / ExplosionOpacity | 0.5 | How solid the followed attempt's explosions are (1: as in the game). |
| Replay / DeathAnimations | true | Heads fly off, bodies fall over, blood (and the camera backs out to watch). Off: they just pop. |
| Keys / Watch, Exit | F8, Backspace | Watch on the results screen; leave a replay. |

It records the levels (and custom levels), not the main menu, the Cyber Grind or the sandbox.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (5.4.23 or newer, the Windows x64 zip) into ULTRAKILL's folder: unzip it next to `ULTRAKILL.exe`, then start the game once so BepInEx makes its folders.
2. Build UltraAttempts (below), or put a built `UltraAttempts.dll` into `ULTRAKILL\BepInEx\plugins\UltraAttempts\`.

To take it out again, delete `BepInEx\plugins\UltraAttempts\` (and `BepInEx\UltraAttempts\` for the recordings).

Made for ULTRAKILL as of November 2026. If a game update breaks something, `BepInEx\LogOutput.log` says what.

## Building from source

Needs the .NET SDK (6 or newer) and ULTRAKILL with BepInEx installed.

```
dotnet build UltraAttempts.csproj -c Release
```

If ULTRAKILL isn't in `D:\SteamLibrary\steamapps\common\ULTRAKILL` or `C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL`, add `-p:GameDir="X:\path\to\ULTRAKILL"`. The build copies `UltraAttempts.dll` into `BepInEx\plugins\UltraAttempts\`.

Tests (no game needed for the first):

```
dotnet run -c Release --project tests\RecordingTests   # the recording format: exact round trips, old versions, damage, runs, crashes, pruning, counting, reset, export/import, sizes
dotnet run -c Release --project tests\RecordingTests -- "<a copy of BepInEx\UltraAttempts>"   # every real run decodes; how shaky its eyes are
dotnet run -c Release --project tests\PatchCheck       # every hook into ULTRAKILL finds its method in the real game
```

And in the game itself (ULTRAKILL closed; Git Bash):

```
dotnet build -c Release tests/InGameTest/UltraAttempts.SelfTest.csproj
tests/InGameTest/run-selftest.sh
```

ULTRAKILL starts in a window and plays itself for a few minutes:
- In 0-1 it dies 4 times and restarts, then beats the level.
- It checks the run that was recorded, and that continuing plays every attempt before 0-2 loads.
- It tries every camera.
- In 0-2 it watches a replay from the cheat menu, then checks the game goes back to normal.

It saves screenshots and `selftest.log` into `tests/InGameTest/out`, then quits. Your saves, preferences and window settings are backed up first and put back exactly after, and the test's recordings stay out of yours.

The code:
- `src/Recording`:
  - `Recorder.cs` watches V1 and cuts attempts (at death, restart, checkpoint respawn, leaving, the final pit).
  - `WorldRecorder.cs` records the enemies and doors, and `ThingRecorder.cs` everything else.
  - `SoundRecorder.cs` records the game's sounds.
  - `RawAttempt.cs` is an attempt's samples, and `AttemptCodec.cs` packs them.
  - `SessionFile.cs` and `SessionStore.cs` are the run files and the folder, written on a background task.
- `src/Replay`:
  - `ReplayDirector.cs` freezes the level, loads the attempts on a background task, runs the clock, the cameras and the controls, then puts everything back. `Ghost.cs` is one attempt ready to play: smooth curves through its samples, its world.
  - `GhostRenderer.cs`, `GhostModels.cs` and `WeaponModels.cs` draw the V1 ghosts, their weapons, bursts, markers, tracers and trails onto ULTRAKILL's camera, so they get its pixelation and palette.
  - `WorldPlayback.cs` plays the followed attempt's enemies, and `DoorPlayback.cs` its doors. `DeathEffects.cs` is the deaths (heads, bodies, blood). `GhostWorld.cs` reads attempts' worlds back as they're needed, and `ThingPlayback.cs` draws the things and the other attempts' see-through worlds.
  - `Puppet.cs` makes visual-only copies of the game's own models (V1, V2, enemies) from its Addressables.
  - `ReplayHud.cs` is the overlay (in ULTRAKILL's VCR font), `ReplaySounds.cs` the synthesized sounds, `RoomReveal.cs` the rooms.
  - `GameSounds.cs` plays the game's own sounds back, every attempt's.
- `src/AttemptsMenu.cs` is the ULTRAATTEMPTS menu on the pause screen.
- `src/Cheats.cs` is the cheat menu, and `src/Patches.cs` the hooks into ULTRAKILL:
  - Recording: scene loads, restarts, the results screen, the camera, doors opening and closing, explosions, beams and breakables.
  - Recording sounds: the game's Play, PlayOneShot and PlayDelayed, and its virtual audio picking up the sounds that play by themselves.
  - The replay: the camera, Escape, and the results screen's continue.
  - Doors switching rooms off.

## Credits

- **UltraAttempts** by [ReimaginedPixel](https://github.com/ReimaginedPixel). The idea comes from Geometry Dash's attempt replays.
- ULTRAKILL by Arsi "Hakita" Patala / New Blood Interactive. UltraAttempts isn't affiliated with either and contains none of their files.

## License

[MIT](LICENSE)
