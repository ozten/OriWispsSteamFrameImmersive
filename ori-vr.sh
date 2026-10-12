#!/usr/bin/env bash
# Ori VR mod installer for Linux / SteamOS (Proton).
#
#   bash ori-vr.sh install     install or update (Ori must be closed; ~1 min)
#   bash ori-vr.sh uninstall   remove everything; the game is vanilla again
#   bash ori-vr.sh disable     keep the files but start Ori without the mod
#   bash ori-vr.sh enable      undo disable
#   bash ori-vr.sh status
#
# No Steam launch option is needed: the mod loader is enabled with a DLL override in Ori's own
# Proton prefix. Every file install adds is listed in a manifest, so uninstall removes exactly
# those. Saves are backed up to ~/OriVR-backups before installing or uninstalling.
#
# Environment overrides: ORIVR_ZIP=<local release zip> (instead of downloading), STEAM_ROOT, ORI_DIR.
set -euo pipefail

VERSION="0.1.0"
REPO="ozten/OriWispsSteamFrameImmersive"
APPID=1057090
MOD_URL="https://github.com/$REPO/releases/download/v$VERSION/OriVR-$VERSION.zip"
BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip"
BEPINEX_SHA256="f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a"
UNITY_VERSION="2018.4.24f1"
UNITY_LIBS_URL="https://unity.bepinex.dev/libraries/2018.4.24.zip"
UNITY_LIBS_SHA256="a2d2c2fe8a15dc873407e11bcf69084564d4a96301c78450b2c46176e33195ee"

CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/orivr"
BACKUPS="$HOME/OriVR-backups"
REG_KEY='[Software\\Wine\\AppDefaults\\oriwotw.exe\\DllOverrides]'

say() { printf '%s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

find_steam() {
  for d in "${STEAM_ROOT:-}" "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    [ -n "$d" ] && [ -d "$d/steamapps" ] && { STEAM_ROOT="$d"; return; }
  done
  die "Steam not found; set STEAM_ROOT"
}

# Ori can live in any Steam library folder.
find_ori() {
  [ -n "${ORI_DIR:-}" ] && { [ -f "$ORI_DIR/oriwotw.exe" ] || die "no oriwotw.exe in ORI_DIR=$ORI_DIR"; LIB="$(cd "$ORI_DIR/../../.." && pwd)"; return; }
  local libs lib
  libs=$(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"/\1/p' "$STEAM_ROOT/steamapps/libraryfolders.vdf" 2>/dev/null || true)
  for lib in "$STEAM_ROOT" $libs; do
    if [ -f "$lib/steamapps/appmanifest_$APPID.acf" ] && [ -f "$lib/steamapps/common/Ori and the Will of the Wisps/oriwotw.exe" ]; then
      LIB="$lib"; ORI_DIR="$lib/steamapps/common/Ori and the Will of the Wisps"; return
    fi
  done
  die "Ori and the Will of the Wisps not found in your Steam libraries"
}

setup() {
  [ -n "${ORI_DIR:-}" ] || find_steam
  find_ori
  MANIFEST="$ORI_DIR/.orivrmod-manifest"
  PFX="$LIB/steamapps/compatdata/$APPID/pfx"
}

ori_running() { pgrep -f '[o]riwotw\.exe' >/dev/null; }

# Wine writes the registry back when Ori's wineserver exits, so wait for it before editing.
wait_closed() {
  ori_running && die "quit Ori first (from its menu, so your progress is saved)"
  local i pid
  for i in $(seq 20); do
    pid=""
    for p in $(pgrep -x wineserver || true); do
      tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -q "compatdata/$APPID" && pid=$p
    done
    [ -z "$pid" ] && return
    sleep 1
  done
  die "Ori's Wine is still shutting down; try again in a few seconds"
}

backup_saves() {
  local saves="$PFX/drive_c/users/steamuser/AppData/Local/Ori and the Will of The Wisps"
  [ -d "$saves" ] || return 0
  mkdir -p "$BACKUPS"
  local out="$BACKUPS/saves-$(date +%Y%m%d-%H%M%S).tar.gz"
  tar -czf "$out" -C "$(dirname "$saves")" "$(basename "$saves")"
  say "Saves backed up to $out"
}

fetch() {  # fetch <url> <file> [sha256]
  local url="$1" file="$2" sum="${3:-}"
  mkdir -p "$(dirname "$file")"
  if [ ! -f "$file" ] || { [ -n "$sum" ] && ! echo "$sum  $file" | sha256sum -c --status; }; then
    say "Downloading $(basename "$file")..."
    curl -fL --progress-bar -o "$file.part" "$url" || die "download failed: $url"
    mv "$file.part" "$file"
  fi
  [ -z "$sum" ] || echo "$sum  $file" | sha256sum -c --status || { rm -f "$file"; die "checksum mismatch: $(basename "$file")"; }
}

copy_tree() {  # copy_tree <src-dir> <dest-subdir-in-game>
  local src="$1" sub="$2" f rel dst
  while IFS= read -r f; do
    rel="${sub:+$sub/}${f#./}"; dst="$ORI_DIR/$rel"
    if [ -e "$dst" ] && ! grep -qxF "$rel" "$MANIFEST" 2>/dev/null; then
      die "refusing to overwrite a file this installer didn't create: $rel"
    fi
    mkdir -p "$(dirname "$dst")"
    cp -f "$src/$f" "$dst"
    grep -qxF "$rel" "$MANIFEST" 2>/dev/null || echo "$rel" >> "$MANIFEST"
  done < <(cd "$src" && find . -type f)
}

reg_remove() {
  local reg="$PFX/user.reg"
  [ -f "$reg" ] || return 0
  # Via ENVIRON: awk -v would turn the key's \\ into \.
  # Blank lines are held back and dropped if our section follows them (reg_add put one there).
  REG_KEY="$REG_KEY" awk '
    /^\[/ { skip = (index($0, ENVIRON["REG_KEY"]) == 1); if (skip) blanks = "" }
    skip { next }
    /^$/ { blanks = blanks "\n"; next }
    { printf "%s%s\n", blanks, $0; blanks = "" }
    END { printf "%s", blanks }' "$reg" > "$reg.orivr" && mv "$reg.orivr" "$reg"
}

reg_add() {
  local reg="$PFX/user.reg"
  [ -f "$reg" ] || die "Ori's Proton prefix isn't set up yet: start Ori once without the mod, quit, then run install again"
  reg_remove
  [ -z "$(tail -n 1 "$reg")" ] || echo >> "$reg"
  printf '%s %s\n"winhttp"="native,builtin"\n' "$REG_KEY" "$(date +%s)" >> "$reg"
}

reg_enabled() { grep -qF "$REG_KEY" "$PFX/user.reg" 2>/dev/null; }

interop_tool() {
  case "$(uname -m)" in
    aarch64|arm64) echo "InteropGen-linux-arm64" ;;
    x86_64) echo "InteropGen-linux-x64" ;;
    *) die "unsupported CPU: $(uname -m)" ;;
  esac
}

cmd_install() {
  setup
  wait_closed
  if [ -d "$ORI_DIR/BepInEx" ] && [ ! -f "$MANIFEST" ]; then
    die "$ORI_DIR/BepInEx exists but wasn't installed by this script (another mod loader?); remove it first"
  fi
  say "Installing Ori VR $VERSION into: $ORI_DIR"
  backup_saves

  WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
  local work="$WORK"
  fetch "$BEPINEX_URL" "$CACHE/bepinex-be.788.zip" "$BEPINEX_SHA256"
  fetch "$UNITY_LIBS_URL" "$CACHE/unity-libs-2018.4.24.zip" "$UNITY_LIBS_SHA256"
  local modzip="${ORIVR_ZIP:-$CACHE/OriVR-$VERSION.zip}"
  [ -n "${ORIVR_ZIP:-}" ] || fetch "$MOD_URL" "$modzip"
  unzip -q "$CACHE/bepinex-be.788.zip" -d "$work/bepinex"
  unzip -q "$CACHE/unity-libs-2018.4.24.zip" -d "$work/unity-libs"
  chmod 644 "$work/unity-libs"/*.dll # the zip stores them without read permission
  unzip -q "$modzip" -d "$work/mod"
  local mod; mod=$(find "$work/mod" -maxdepth 2 -type d -name plugins -printf '%h\n' | head -1)
  [ -n "$mod" ] || die "release zip has no plugins/ folder"

  # BepInEx's in-game interop generation crashes under FEX (x86 emulation on ARM), so generate it
  # natively. Cached per game build; a game update needs a fresh install run.
  local gamesum; gamesum=$(sha256sum "$ORI_DIR/GameAssembly.dll" | cut -c1-16)
  local interop="$CACHE/interop-$gamesum"
  if [ ! -f "$interop.complete" ]; then
    say "Generating interop assemblies for this game build (about a minute)..."
    local tool; tool="$mod/tools/$(interop_tool)"
    chmod +x "$tool"
    rm -rf "$interop"
    "$tool" "$ORI_DIR/GameAssembly.dll" "$ORI_DIR/oriwotw_Data/il2cpp_data/Metadata/global-metadata.dat" \
      "$work/unity-libs" "$interop" "$UNITY_VERSION" > "$CACHE/interopgen.log" 2>&1 \
      || die "interop generation failed; see $CACHE/interopgen.log"
    touch "$interop.complete"
  fi

  touch "$MANIFEST"
  copy_tree "$work/bepinex" ""
  copy_tree "$interop" "BepInEx/interop"
  copy_tree "$mod/plugins" "BepInEx/plugins"
  local cfg="$ORI_DIR/BepInEx/config/BepInEx.cfg"
  mkdir -p "$(dirname "$cfg")"
  if grep -q '^UpdateInteropAssemblies' "$cfg" 2>/dev/null; then
    sed -i 's/^UpdateInteropAssemblies = .*/UpdateInteropAssemblies = false/' "$cfg"
  else
    printf '[IL2CPP]\nUpdateInteropAssemblies = false\n' >> "$cfg"
  fi
  reg_add
  say ""
  say "Installed. Start Ori from Steam as usual; no launch option is needed."
  say "In the game: Options > Gameplay > Immersive 3D. When SteamVR's menu appears once at launch, press Resume Game."
  say "Uninstall any time with: bash $0 uninstall"
}

cmd_uninstall() {
  setup
  wait_closed
  reg_remove
  if [ ! -f "$MANIFEST" ]; then say "Not installed (Ori's DLL override cleared)."; return; fi
  backup_saves
  mkdir -p "$BACKUPS"
  [ -d "$ORI_DIR/BepInEx/config" ] && cp -a "$ORI_DIR/BepInEx/config" "$BACKUPS/config-$(date +%Y%m%d-%H%M%S)"
  while IFS= read -r rel; do [ -n "$rel" ] && rm -f "$ORI_DIR/$rel"; done < "$MANIFEST"
  rm -rf "$ORI_DIR/BepInEx" "$ORI_DIR/dotnet"
  rm -f "$MANIFEST"
  say "Uninstalled. Ori is vanilla again (mod settings kept in $BACKUPS)."
  say "If you had added WINEDLLOVERRIDES to Ori's launch options earlier, you can remove it."
}

cmd_disable() { setup; wait_closed; reg_remove; say "Disabled: Ori starts without the mod. Re-enable with: bash $0 enable"; }
cmd_enable() {
  setup; wait_closed
  [ -f "$MANIFEST" ] || die "not installed; run: bash $0 install"
  reg_add; say "Enabled."
}

cmd_status() {
  setup
  say "Ori: $ORI_DIR"
  if [ -f "$MANIFEST" ]; then say "Installed ($(wc -l < "$MANIFEST") files)."; else say "Not installed."; fi
  if reg_enabled; then say "Enabled (DLL override set in Ori's prefix)."; else say "Disabled (no DLL override)."; fi
}

case "${1:-}" in
  install) cmd_install ;;
  uninstall) cmd_uninstall ;;
  enable) cmd_enable ;;
  disable) cmd_disable ;;
  status) cmd_status ;;
  *) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
