# UltraAttempts

Every attempt at an ULTRAKILL level, played back at once, like Geometry Dash's attempt replays. A BepInEx 5 plugin.

It records each attempt as you play a level. An attempt runs from the level's start (or a checkpoint) until you die, restart or reach the final pit. When you finally beat the level, every attempt plays at the same moment as see-through ghosts. A thousand V1s set off together, pop one by one where they died, and the last one standing is the run that made it.

- **Recording isn't a cheat.** It only watches: nothing in the game changes, ranks and leaderboards still count, and it needs no cheat menu. It's always on (it can be turned off in the config).
- **Watching is.** Mid-level, the replay is in ULTRAKILL's cheat menu (it freezes the level while it plays). After you beat a level it's over, so no cheats are needed: by default every attempt plays as you continue from the results screen, before the next level loads.

## Watching

**After beating a level:** on the results screen, continue as usual. If it took 2 or more attempts, they all play first, then the next level loads. Or press **F8** on the results screen to watch them and come back to the results.

**Any time, from the cheat menu:** turn on ULTRAKILL's cheats (the usual way), open the cheat menu (**Home** or **~**), and under **ATTEMPT REPLAY**:

| Cheat | |
|---|---|
| **Watch Every Attempt** | Plays the replay once you close the menu. |
| **Replay Camera** | NEXT TO DIE, BEST RUN, OVERVIEW or FREE CAM (the camera it starts with). |
| **Replay View** | EYES (first person) or BEHIND (third person), when following an attempt. |
| **Replay Attempts** | AUTO, THIS RUN, LAST WIN or EVERYTHING. |

As with any of ULTRAKILL's cheats, turning cheats on mid-level means that level gets no rank.

### Cameras

- **Next to die:** through the eyes of whichever attempt dies next. When it dies the screen flashes red and the camera cuts to the next one to die, until only the winner is left. It skips attempts that die within 0.75 s of a cut, so the early deaths don't flicker past.
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
| **H** | Hide the HUD |
| **Esc** / **Backspace** | Leave |

The HUD shows how many attempts are alive, dead and finished, and a timeline with the deaths piled up where they happened. For the attempt you're following it shows its number, when it dies (or its time, for the win), its HP and its weapon.

### What the ghosts show

Each attempt is a see-through V1 (a crystal body with a visor), in its own colour. The winner is gold, and the attempt you were on when you opened the replay is white. Each ghost is the size V1 really is, slides and dashes where you did, and looks where you looked. With weapons recorded (on by default) it also holds the weapon you had, in its variant's colour (blue, green, red, gold), shows a tracer for every shot (up to where it hit the level), and throws its punches. Where an attempt died it bursts, and a red marker stays (M hides them). The winner leaves a gold trail.

ULTRAKILL switches rooms off behind doors that close behind you. While a replay plays, those rooms come back (as if you'd walked back through the door), so earlier attempts aren't running through nothing. They switch off again after.

## Runs and attempts

A **run** is every attempt at a level and difficulty from your first try until you beat it, so the attempt count goes up across deaths, checkpoint restarts and full restarts. Leaving a level before beating it keeps its run going: next time the attempts count on from there, like Geometry Dash. Beating the level ends the run, and the next attempt starts a new one at 1. **ATTEMPT 37** shows for a moment at the start of each attempt.

What a replay shows (**Replay Attempts**):
- **This run:** every attempt since the level was last beaten, including the one you're on right now (in white).
- **Last win:** every attempt it took the last time you beat the level.
- **Everything:** every attempt ever recorded at this level and difficulty.
- **Auto:** this run if it has any attempts, else the last win.

Every attempt starts at the same moment in the replay: from the level's start, or from the checkpoint it respawned at.

## How it's recorded

At 30 samples a second of game time (so pausing and hitstops take no room), interpolated between frames. Each sample holds V1's position, where the eyes are, where they look, HP, movement (on the ground, sliding, dashing), shots and punches, and weapon switches as events.

Recordings are kept small. Each channel (positions, rotations, ...) is stored on its own. Positions and rotations are stored as the difference from where the two samples before predict them to be: moving at a steady speed or turning at a steady rate leaves nothing to store. Everything goes in as zigzag varints, and the whole attempt is deflated. A minute of non-stop movement is about 11 KB (about 190 bytes a second), and a minute of standing still is under 100 bytes. A run is one file, written on a background thread as each attempt ends, so dying never hitches the game. A crash can't corrupt a run: half-written attempts are cut off.

Recordings go in `BepInEx\UltraAttempts\<level>\<difficulty>\`, one `.ukattempts` file per run. The newest 30 finished runs of each level are kept.

## Settings

In `BepInEx\config\dev.ultracraft.attempts.cfg` (made the first time the game starts with UltraAttempts):

| Setting | Default | |
|---|---|---|
| Recording / Enabled | true | Record attempts at all. |
| Recording / SampleRate | 30 | Samples per second (10-60). |
| Recording / RecordWeapons | true | Record the weapon, shots and punches too. |
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
| Replay / GhostOpacity | 0.35 | How solid the ghosts are. |
| Replay / GhostColors | Rainbow | Rainbow, or Progress (red for the first attempts to green for the last). |
| Replay / MaxGhosts | 2000 | Most attempts in one replay (the newest, plus every win). |
| Replay / MinShotSeconds | 0.75 | Next-to-die camera: the shortest a shot lasts. |
| Replay / RevealRooms | true | Bring back rooms switched off behind doors while a replay plays. |
| Replay / Loop | false | Start over at the end. |
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
dotnet run -c Release --project tests\RecordingTests   # the recording format: exact round trips, runs, crashes, pruning, sizes
dotnet run -c Release --project tests\PatchCheck       # every hook into ULTRAKILL finds its method in the real game
```

The code:
- `src/Recording`: `Recorder.cs` watches V1 and cuts attempts (at death, restart, checkpoint respawn, leaving, the final pit). `RawAttempt.cs` is an attempt's samples. `AttemptCodec.cs` packs them. `SessionFile.cs` and `SessionStore.cs` are the run files and the folder, written on a background task.
- `src/Replay`: `ReplayDirector.cs` freezes the level, loads the attempts on a background task, runs the clock, the cameras and the controls, then puts everything back. `GhostRenderer.cs` draws the ghosts, bursts, markers, tracers and trails, from meshes made in code, onto ULTRAKILL's camera (so they get its pixelation and palette). `ReplayHud.cs` is the overlay (in ULTRAKILL's VCR font), `ReplaySounds.cs` the synthesized sounds, `RoomReveal.cs` the rooms.
- `src/Cheats.cs` is the cheat menu, and `src/Patches.cs` the hooks into ULTRAKILL:
  - Scene loads, restarts and the results screen (recording).
  - The camera, Escape and the results screen's continue (the replay).
  - Doors switching rooms off.

## Credits

- **UltraAttempts** by [ReimaginedPixel](https://github.com/ReimaginedPixel). The idea comes from Geometry Dash's attempt replays.
- ULTRAKILL by Arsi "Hakita" Patala / New Blood Interactive. UltraAttempts isn't affiliated with either and contains none of their files.

## License

[MIT](LICENSE)
