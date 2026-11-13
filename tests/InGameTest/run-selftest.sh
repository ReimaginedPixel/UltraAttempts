#!/usr/bin/env bash
# Runs UltraAttempts' in-game self-test: ULTRAKILL starts windowed, plays itself for a couple of minutes
# (dies and restarts in 0-1, beats it, watches the replays) and quits. Your saves, preferences and
# window settings are backed up first and put back exactly after; the test's recordings go in the
# output folder, not yours. ULTRAKILL must be closed. Build the plugin and this test first.
#   tests/InGameTest/run-selftest.sh [output folder]
set -euo pipefail

GAME="${GAME_DIR:-/d/SteamLibrary/steamapps/common/ULTRAKILL}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/out}"
REGKEY='HKCU\Software\Hakita\ULTRAKILL'

if tasklist | grep -qi '^ULTRAKILL.exe'; then
  echo "ULTRAKILL is running: close it first." >&2
  exit 2
fi

rm -rf "$OUT"
mkdir -p "$OUT/backup"
cp -r "$GAME/Saves" "$OUT/backup/Saves"
cp -r "$GAME/Preferences" "$OUT/backup/Preferences"
MSYS_NO_PATHCONV=1 reg export "$REGKEY" "$(cygpath -w "$OUT/backup/registry.reg")" /y > /dev/null
[ -s "$OUT/backup/registry.reg" ] || { echo "couldn't back up the registry settings" >&2; exit 3; }
echo "backed up saves, preferences and window settings to $OUT/backup"

restore() {
  rm -rf "$GAME/BepInEx/plugins/UltraAttempts.SelfTest"
  rm -rf "$GAME/Saves" "$GAME/Preferences"
  cp -r "$OUT/backup/Saves" "$GAME/Saves"
  cp -r "$OUT/backup/Preferences" "$GAME/Preferences"
  MSYS_NO_PATHCONV=1 reg delete "$REGKEY" /f > /dev/null 2>&1 || true
  MSYS_NO_PATHCONV=1 reg import "$(cygpath -w "$OUT/backup/registry.reg")" > /dev/null 2>&1
  echo "restored saves, preferences and window settings; removed the self-test plugin"
}
trap restore EXIT

mkdir -p "$GAME/BepInEx/plugins/UltraAttempts.SelfTest"
cp "$HERE/bin/Release/netstandard2.1/UltraAttempts.SelfTest.dll" "$GAME/BepInEx/plugins/UltraAttempts.SelfTest/"

export ULTRAATTEMPTS_SELFTEST="$(cygpath -w "$OUT")"
export ULTRAATTEMPTS_FOLDER="$(cygpath -w "$OUT/recordings")"
echo "starting ULTRAKILL (windowed)..."
( cd "$GAME" && timeout 720 ./ULTRAKILL.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 ) || true
cp "$GAME/BepInEx/LogOutput.log" "$OUT/LogOutput.log" 2>/dev/null || true

echo "---- selftest.log"
cat "$OUT/selftest.log" 2>/dev/null || echo "(no log: the self-test never started)"
echo "---- errors in BepInEx's log"
grep -n -i -E "error|exception" "$OUT/LogOutput.log" | grep -v -i "loaded" | head -40 || true
grep -q "DONE  .* 0 failed" "$OUT/selftest.log" 2>/dev/null
