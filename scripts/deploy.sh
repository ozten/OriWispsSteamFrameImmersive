#!/usr/bin/env bash
# Install / uninstall the mod into the Ori game folder.
#
#   deploy.sh install     copy BepInEx + built plugins into the game folder
#   deploy.sh plugins     re-copy only our built plugins (fast iteration)
#   deploy.sh uninstall   remove everything install added (game back to vanilla)
#   deploy.sh status
#
# Every file install creates is listed in $MANIFEST so uninstall removes exactly
# those, plus the folders BepInEx generates at runtime (BepInEx/, dotnet/).
# install refuses to overwrite any file it didn't create.
#
# Even when installed, nothing loads unless the Steam launch option is set:
#   WINEDLLOVERRIDES="winhttp=n,b" %command%
set -euo pipefail
source "$(dirname "$0")/env.sh"

[ -f "$ORI_DIR/oriwotw.exe" ] || die "Ori not found at $ORI_DIR"

copy_tree() {  # copy_tree <src-dir> <dest-subdir-in-game>
  local src="$1" sub="$2"
  (cd "$src" && find . -type f) | while read -r f; do
    local rel="${sub:+$sub/}${f#./}" dst="$ORI_DIR/${sub:+$sub/}${f#./}"
    if [ -e "$dst" ] && ! grep -qxF "$rel" "$MANIFEST" 2>/dev/null; then
      die "refusing to overwrite file not created by us: $rel"
    fi
    mkdir -p "$(dirname "$dst")"
    cp -f "$src/$f" "$dst"
    grep -qxF "$rel" "$MANIFEST" 2>/dev/null || echo "$rel" >> "$MANIFEST"
  done
}

install_plugins() {
  if [ -d "$BUILD_DIR/plugins" ] && [ -n "$(ls -A "$BUILD_DIR/plugins")" ]; then
    copy_tree "$BUILD_DIR/plugins" "BepInEx/plugins"
    echo "Plugins copied."
  else
    echo "No built plugins in $BUILD_DIR/plugins (that's fine for a loader-only test)."
  fi
}

configure_bepinex() {
  # Interop is pre-generated natively, so stop BepInEx regenerating it in-game (crashes under FEX).
  local cfg="$ORI_DIR/BepInEx/config/BepInEx.cfg"
  mkdir -p "$(dirname "$cfg")"
  if grep -q '^UpdateInteropAssemblies' "$cfg" 2>/dev/null; then
    sed -i 's/^UpdateInteropAssemblies = .*/UpdateInteropAssemblies = false/' "$cfg"
  else
    printf '[IL2CPP]\nUpdateInteropAssemblies = false\n' >> "$cfg"
  fi
}

case "${1:-status}" in
  install)
    ori_running && die "quit Ori first"
    [ -f "$VENDOR_DIR/bepinex/winhttp.dll" ] || die "run scripts/fetch-bepinex.sh first"
    touch "$MANIFEST"
    [ -d "$BUILD_DIR/interop" ] || die "run scripts/gen-interop.sh first"
    copy_tree "$VENDOR_DIR/bepinex" ""
    copy_tree "$BUILD_DIR/interop" "BepInEx/interop"
    configure_bepinex
    install_plugins
    echo "Installed. Enable with Steam launch option:  WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
    ;;
  plugins)
    ori_running && die "quit Ori first"
    [ -f "$MANIFEST" ] || die "not installed; run: deploy.sh install"
    install_plugins
    ;;
  uninstall)
    ori_running && die "quit Ori first"
    [ -f "$MANIFEST" ] || { echo "Nothing to uninstall."; exit 0; }
    # Keep the last BepInEx log and config for debugging.
    keep="$PROJECT_DIR/backups/last-uninstall"
    rm -rf "$keep"; mkdir -p "$keep"
    cp -a "$ORI_DIR/BepInEx/LogOutput.log" "$ORI_DIR/BepInEx/config" "$keep/" 2>/dev/null || true
    while read -r rel; do
      [ -n "$rel" ] && rm -f "$ORI_DIR/$rel"
    done < "$MANIFEST"
    rm -rf "$ORI_DIR/BepInEx" "$ORI_DIR/dotnet"
    rm -f "$MANIFEST"
    echo "Uninstalled. Game folder is vanilla (log/config kept in $keep)."
    echo "You can also remove the WINEDLLOVERRIDES launch option."
    ;;
  status)
    if [ -f "$MANIFEST" ]; then
      echo "Installed ($(wc -l < "$MANIFEST") files in manifest)."
      { ls "$ORI_DIR/BepInEx/plugins" 2>/dev/null || true; } | sed "s/^/  plugin: /"
    else
      echo "Not installed."
    fi
    ;;
  *) die "usage: deploy.sh install|plugins|uninstall|status" ;;
esac
