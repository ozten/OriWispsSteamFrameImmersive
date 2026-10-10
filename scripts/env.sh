# Shared paths. Override any of these in the environment if your install differs.
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
ORI_APPID="${ORI_APPID:-1057090}"
ORI_DIR="${ORI_DIR:-$STEAM_ROOT/steamapps/common/Ori and the Will of the Wisps}"
ORI_PFX_USER="${ORI_PFX_USER:-$STEAM_ROOT/steamapps/compatdata/$ORI_APPID/pfx/drive_c/users/steamuser}"
ORI_SAVE_DIR="${ORI_SAVE_DIR:-$ORI_PFX_USER/AppData/Local/Ori and the Will of The Wisps}"
ORI_LOG="${ORI_LOG:-$ORI_PFX_USER/AppData/LocalLow/Moon Studios/OriAndTheWilloftheWisps/output_log.txt}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR_DIR="$PROJECT_DIR/vendor"
BUILD_DIR="$PROJECT_DIR/build"
MANIFEST="$ORI_DIR/.orivrmod-manifest"

BEPINEX_URL="${BEPINEX_URL:-https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip}"

die() { echo "error: $*" >&2; exit 1; }
ori_running() { pgrep -af "[o]riwotw\\.exe" | grep -vqE "^[0-9]+ (/usr)?(/bin/)?(ba)?sh " ; }
