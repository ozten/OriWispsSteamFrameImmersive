# OriVRMod

Stereoscopic 3D for *Ori and the Will of the Wisps* on a VR headset. The game
becomes a VR app and plays on a big screen in front of you, with each eye
getting its own render, so the game's layered scenes show real depth.

Developed on a Steam Frame (ARM64, Proton ARM64 + FEX), but nothing here is
Frame-specific in principle.

**Status:** playable on the Steam Frame. 3D, Frame controllers and the in-game
toggle all work. SteamVR still shows its menu ("wine-preloader") once at launch:
press *Resume Game*.

![Options > Gameplay with the Immersive 3D toggle](docs/images/options-immersive-3d.jpg)

## Playing

- **Immersive 3D** lives in *Options → Gameplay*. Off shows the same image to
  both eyes (flat 2D) without leaving VR, so you can switch at any time.
- **Recenter** with the headset's own recenter (Steam Frame: hold the Steam button).
  The screen sits straight ahead of SteamVR's seated origin; the mod stores no
  position of its own.
- Play with the Frame controllers; they act as a gamepad.

## Settings and developer keys

All settings live in `BepInEx/config/com.ozten.orivr.cfg` and are re-read within
a second when the file changes. These keyboard keys are for development and tuning:

| Key | Action |
|---|---|
| F9 | 3D on/off (same as the menu toggle) |
| F11 | Show/hide the headset view |
| Shift+F11 | Recenter |
| Ctrl+F11 | VR mode: Scene (default, game is a VR app) or Window (floating overlay next to the Frame's own screen) |
| Ctrl+Shift+S / E / V / R / M | No-Fn versions of F9 / F10 / F11 / Shift+F11 / Ctrl+F11 |
| F10 | Swap eyes |
| `-` / `=` | Eye separation |
| `[` / `]` | Zero-parallax (screen) depth |
| PgUp / PgDn, Home / End | Screen size, distance |
| F8 | Write a camera report and capture both eye images |
| F7 | Reload the core plugin |

## How it works

1. A BepInEx 6 IL2CPP plugin redirects the game camera into a left-eye texture
   and renders it a second time per frame, offset sideways with an off-axis
   projection, for the right eye. Separation and screen depth follow the camera's
   zoom, so cutscenes and menus stay comfortable.
2. The game registers with SteamVR as a scene app and shows the pair as a
   side-by-side stereo overlay (`VROverlayFlags_SideBySide_Parallel`), so the
   Frame hides its own flat screen and you see one 3D screen.
3. Frame controller input comes in through SteamVR Input and is fed to the game
   as a gamepad (Harmony patches on its controller readers).
4. The *Immersive 3D* option is a clone of the game's own "Show Map Trail" row,
   added to the Gameplay options page at runtime.

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

## License

MIT, see [LICENSE](LICENSE). `src/OriVR.Core/ThirdParty/openvr_api.cs` is Valve's, under its own license (`ThirdParty/OPENVR_LICENSE`).
