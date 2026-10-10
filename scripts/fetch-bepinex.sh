#!/usr/bin/env bash
# Download BepInEx 6 (IL2CPP, win-x64) into vendor/bepinex/. Not committed to git.
set -euo pipefail
source "$(dirname "$0")/env.sh"
mkdir -p "$VENDOR_DIR"
zip="$VENDOR_DIR/$(basename "${BEPINEX_URL//%2B/+}")"
[ -f "$zip" ] || curl -fL -o "$zip" "$BEPINEX_URL"
rm -rf "$VENDOR_DIR/bepinex"
mkdir -p "$VENDOR_DIR/bepinex"
unzip -q "$zip" -d "$VENDOR_DIR/bepinex"
echo "BepInEx unpacked to $VENDOR_DIR/bepinex"
