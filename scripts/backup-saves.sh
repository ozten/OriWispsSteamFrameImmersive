#!/usr/bin/env bash
# Copy Ori's save folder (from the Proton prefix) into backups/<timestamp>/.
set -euo pipefail
source "$(dirname "$0")/env.sh"
[ -d "$ORI_SAVE_DIR" ] || die "save dir not found: $ORI_SAVE_DIR"
dest="$PROJECT_DIR/backups/saves-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$dest"
cp -a "$ORI_SAVE_DIR/." "$dest/"
echo "Saved $(ls "$dest" | wc -l) files to $dest"
