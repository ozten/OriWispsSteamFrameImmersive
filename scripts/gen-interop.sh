#!/usr/bin/env bash
# Generate BepInEx's IL2CPP interop assemblies natively (ARM64 .NET) into build/interop.
# BepInEx would normally do this on first launch, but under FEX that is slow and
# crashes (AccessViolationException). We pre-generate and tell BepInEx not to.
set -euo pipefail
source "$(dirname "$0")/env.sh"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
[ -x "$DOTNET" ] || die "dotnet not found; install the .NET 8 SDK (see README)"
[ -d "$VENDOR_DIR/bepinex" ] || die "run scripts/fetch-bepinex.sh first"

UNITY_VERSION="2018.4.24f1"
LIBS_ZIP="$VENDOR_DIR/${UNITY_VERSION%f*}.zip"
[ -f "$LIBS_ZIP" ] || curl -fL -o "$LIBS_ZIP" "https://unity.bepinex.dev/libraries/${UNITY_VERSION%f*}.zip"
rm -rf "$VENDOR_DIR/unity-libs"; mkdir -p "$VENDOR_DIR/unity-libs"
unzip -q "$LIBS_ZIP" -d "$VENDOR_DIR/unity-libs"
chmod 644 "$VENDOR_DIR/unity-libs"/*.dll

"$DOTNET" build -c Release -v q -o "$BUILD_DIR/tools/InteropGen" "$PROJECT_DIR/tools/InteropGen" >/dev/null
"$DOTNET" "$BUILD_DIR/tools/InteropGen/InteropGen.dll" \
  "$ORI_DIR/GameAssembly.dll" \
  "$ORI_DIR/oriwotw_Data/il2cpp_data/Metadata/global-metadata.dat" \
  "$VENDOR_DIR/unity-libs" "$BUILD_DIR/interop" "$UNITY_VERSION" | tee "$BUILD_DIR/interopgen.log" | grep -E "Cpp2IL finished|^Done in"
