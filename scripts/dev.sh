#!/usr/bin/env bash
# Dev loop: build the plugin and get it into the game.
#   dev.sh          build; if Ori is running, hot-reload the core, else deploy everything
#   dev.sh run      build + deploy, then launch Ori via Steam
#   dev.sh kill     quit Ori (unsaved progress is lost!)
#   dev.sh capture  ask the running game to save both eye images (BepInEx/orivr-capture-*.png)
set -euo pipefail
source "$(dirname "$0")/env.sh"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

case "${1:-}" in
  kill)
    pkill -f '[o]riwotw\.exe' && echo "Ori stopped." || echo "Ori not running."
    exit 0 ;;
  capture)
    touch "$ORI_DIR/BepInEx/orivr-capture"
    echo "Capture requested (takes effect within ~1 s while stereo is on)."
    exit 0 ;;
esac

"$DOTNET" build -c Release -v q "$PROJECT_DIR/src/OriVR.Core" | grep -E "error|warning CS" || true
[ -f "$BUILD_DIR/plugins/OriVR.Core.dll" ] || die "build failed"

if ori_running; then
  if ! cmp -s "$BUILD_DIR/plugins/OriVR.dll" "$ORI_DIR/BepInEx/plugins/OriVR.dll"; then
    echo "NOTE: the host plugin (OriVR.dll) changed; that part needs a game restart."
  fi
  "$(dirname "$0")/deploy.sh" hot
  exit 0
fi

"$(dirname "$0")/deploy.sh" plugins
if [ "${1:-}" = run ]; then
  steam "steam://rungameid/$ORI_APPID" >/dev/null 2>&1 &
  echo "Launching Ori. Log: $ORI_DIR/BepInEx/LogOutput.log"
fi
