#!/usr/bin/env bash
# Dev loop: build the plugin, copy it into the game, optionally launch Ori.
#   dev.sh          build + deploy plugins
#   dev.sh run      ...then launch Ori via Steam and follow the BepInEx log
#   dev.sh kill     quit Ori
set -euo pipefail
source "$(dirname "$0")/env.sh"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

if [ "${1:-}" = kill ]; then
  pkill -f '[o]riwotw\.exe' && echo "Ori stopped." || echo "Ori not running."
  exit 0
fi

ori_running && die "quit Ori first (or: dev.sh kill)"
"$DOTNET" build -c Release -v q "$PROJECT_DIR/src/OriVR" | grep -E "error|warning CS" || true
[ -f "$BUILD_DIR/plugins/OriVR.dll" ] || die "build failed"
"$(dirname "$0")/deploy.sh" plugins

if [ "${1:-}" = run ]; then
  steam "steam://rungameid/$ORI_APPID" >/dev/null 2>&1 &
  log="$ORI_DIR/BepInEx/LogOutput.log"
  echo "Launching Ori; following $log (Ctrl-C to stop following)"
  sleep 5; tail -F "$log"
fi
