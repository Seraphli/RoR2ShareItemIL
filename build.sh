#!/usr/bin/env bash
#
# Builds the ShareItemPatch payload, injects it into RoR2.dll and writes a
# single patched RoR2.dll to ./out/RoR2.dll
#
# Usage:
#   ./build.sh                       # auto-detect the Steam install
#   GAME_DIR=/path/to/Managed ./build.sh
#   ./build.sh --install             # also copy the result into the game folder
#
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$REPO_DIR/.build"

INSTALL=0
for arg in "$@"; do
  case "$arg" in
    --install) INSTALL=1 ;;
    -h|--help) sed -n '3,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# ---------------------------------------------------------------- game dir
if [[ -z "${GAME_DIR:-}" ]]; then
  candidates=(
    "$HOME/.local/share/Steam/steamapps/common/Risk of Rain 2/Risk of Rain 2_Data/Managed"
    "$HOME/.steam/steam/steamapps/common/Risk of Rain 2/Risk of Rain 2_Data/Managed"
    "$HOME/Library/Application Support/Steam/steamapps/common/Risk of Rain 2/Risk of Rain 2_Data/Managed"
  )
  for c in "${candidates[@]}"; do
    if [[ -f "$c/RoR2.dll" ]]; then GAME_DIR="$c"; break; fi
  done
fi

if [[ -z "${GAME_DIR:-}" || ! -f "$GAME_DIR/RoR2.dll" ]]; then
  echo "Could not locate Risk of Rain 2's Managed folder." >&2
  echo "Set it explicitly:  GAME_DIR='/path/to/Risk of Rain 2_Data/Managed' ./build.sh" >&2
  exit 1
fi

echo "== game dir : $GAME_DIR"
echo

# ---------------------------------------------------------------- build
echo "== building payload + patcher =="
dotnet build "$REPO_DIR/src/ShareItemPatch/ShareItemPatch.csproj" \
  -c Release -o "$BUILD_DIR/payload" -v quiet --nologo \
  -p:GameDir="$GAME_DIR"

dotnet build "$REPO_DIR/src/ShareItemPatcher/ShareItemPatcher.csproj" \
  -c Release -o "$BUILD_DIR/patcher" -v quiet --nologo

HELPER_DLL="$BUILD_DIR/payload/ShareItemPatch.dll"

# ---------------------------------------------------------------- patch
OUT_DLL="$REPO_DIR/out/RoR2.dll"
echo
echo "== patching RoR2.dll =="
dotnet "$BUILD_DIR/patcher/ShareItemPatcher.dll" \
  "$GAME_DIR/RoR2.dll" \
  "$HELPER_DLL" \
  "$OUT_DLL" \
  "$GAME_DIR"

echo
echo "== done: $OUT_DLL =="

if [[ "$INSTALL" == "1" ]]; then
  BACKUP="$GAME_DIR/RoR2.dll.bak"
  if [[ ! -f "$BACKUP" ]]; then
    cp "$GAME_DIR/RoR2.dll" "$BACKUP"
    echo "   backup -> $BACKUP"
  fi
  cp "$OUT_DLL" "$GAME_DIR/RoR2.dll"
  echo "   installed -> $GAME_DIR/RoR2.dll"
else
  echo
  echo "To install, back up the original and copy the patched dll over it:"
  echo "   cp '$GAME_DIR/RoR2.dll' '$GAME_DIR/RoR2.dll.bak'"
  echo "   cp '$OUT_DLL' '$GAME_DIR/RoR2.dll'"
  echo "(or re-run with --install)"
fi
