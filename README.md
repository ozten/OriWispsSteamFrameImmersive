# OriVRMod

Stereoscopic 3D for *Ori and the Will of the Wisps* in VR. The game still plays
in a flat window, but each eye gets its own render, so the game's layered 3D
scenes show real depth.

Developed on a Steam Frame (ARM64, Proton ARM64 + FEX), but nothing here is
Frame-specific in principle.

**Status:** BepInEx runs under Proton/FEX; side-by-side stereo renders in the window; SteamVR stereo overlay output is written but untested.

## Controls

| Key | Action |
|---|---|
| F9 | Stereo on/off |
| F11 | Show the 3D image in the headset (SteamVR overlay); Shift+F11 recenters it (Tab mode: re-shows the tab) |
| Ctrl+F11 | Switch VR mode: floating Window or experimental dashboard Tab |
| F10 | Swap eyes |
| `-` / `=` | Eye separation |
| `[` / `]` | Zero-parallax (screen) depth |
| F8 | Write a camera report and capture both eye images |
| F7 | Reload the core plugin |

All settings live in `BepInEx/config/com.ozten.orivr.cfg` and are re-read within a second when the file changes.

## How it works (planned)

1. A BepInEx 6 IL2CPP plugin clones the game camera and renders left/right eye
   views with a horizontal offset. The HUD is rendered at zero offset.
2. The stereo pair is shown as an OpenVR overlay with side-by-side stereo
   (`VROverlayFlags_SideBySide_Parallel`), so it floats in VR as a 3D window.

## Usage

```sh
scripts/backup-saves.sh        # back up your saves first
scripts/fetch-bepinex.sh       # download BepInEx 6 IL2CPP into vendor/
scripts/gen-interop.sh         # generate interop assemblies natively (needs .NET 8 SDK)
scripts/deploy.sh install      # copy into the game folder (tracked by a manifest)
```

Then in Steam: Ori → Properties → Launch Options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

**Turning it off:** clear the launch option (the game runs vanilla even with files
present), or run `scripts/deploy.sh uninstall` to remove every file the mod added.

Normally BepInEx generates interop assemblies on the first launch. Under FEX
(x86 emulation on ARM) that crashed with an `AccessViolationException`, so
`gen-interop.sh` runs the same Cpp2IL + Il2CppInterop steps natively (about 30 s)
and `deploy.sh` sets `UpdateInteropAssemblies = false`. Re-run both after a game update.

.NET 8 SDK without root: `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0`

## Layout

- `scripts/`: backup, fetch, interop generation, deploy/uninstall
- `tools/InteropGen/`: native interop generator (mirrors BepInEx's Il2CppInteropManager)
- `src/OriVR/`: small, stable host plugin. It owns the Il2Cpp-registered types and hot-reloads the core
- `src/OriVR.Core/`: everything else (stereo rendering, VR overlay, probe). Rebuild with `scripts/dev.sh` while the game runs and it reloads in place
- `docs/NOTES.md`: findings and environment notes
