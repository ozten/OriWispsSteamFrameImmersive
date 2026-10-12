#!/usr/bin/env bash
# Build the release zip that ori-vr.sh installs: build/release/OriVR-<version>.zip
#   OriVR-<version>/plugins/   OriVR.dll, OriVR.Core.dll, openvr_api.dll
#   OriVR-<version>/tools/     InteropGen-linux-arm64, InteropGen-linux-x64 (self-contained, no .NET needed)
# Version comes from ori-vr.sh. Needs the .NET 8 SDK, vendor/ (fetch-bepinex.sh) and build/interop (gen-interop.sh).
set -euo pipefail
source "$(dirname "$0")/env.sh"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
VERSION=$(sed -n 's/^VERSION="\(.*\)"/\1/p' "$PROJECT_DIR/ori-vr.sh")
[ -n "$VERSION" ] || die "no VERSION in ori-vr.sh"
name="OriVR-$VERSION"
stage="$BUILD_DIR/release/$name"
rm -rf "$stage" "$BUILD_DIR/release/$name.zip"
mkdir -p "$stage/plugins" "$stage/tools"

"$DOTNET" build -c Release -v q "$PROJECT_DIR/src/OriVR.Core" >/dev/null || die "plugin build failed"
cp "$BUILD_DIR/plugins/OriVR.dll" "$BUILD_DIR/plugins/OriVR.Core.dll" "$BUILD_DIR/plugins/openvr_api.dll" "$stage/plugins/"

for rid in linux-arm64 linux-x64; do
  out="$BUILD_DIR/release/interopgen-$rid"
  "$DOTNET" publish "$PROJECT_DIR/tools/InteropGen" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "$out" -v q >/dev/null || die "InteropGen publish failed ($rid)"
  cp "$out/InteropGen" "$stage/tools/InteropGen-$rid"
done

cp "$PROJECT_DIR/LICENSE" "$PROJECT_DIR/THIRD-PARTY.md" "$stage/"
cp "$PROJECT_DIR/src/OriVR.Core/ThirdParty/OPENVR_LICENSE" "$stage/plugins/openvr_api.LICENSE"
(cd "$BUILD_DIR/release" && zip -qr "$name.zip" "$name")
cp "$PROJECT_DIR/ori-vr.sh" "$BUILD_DIR/release/"
echo "Built $BUILD_DIR/release/$name.zip ($(du -h "$BUILD_DIR/release/$name.zip" | cut -f1)) and ori-vr.sh"
