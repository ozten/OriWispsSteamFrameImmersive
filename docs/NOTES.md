# Notes

## Environment (2026-10-09)
- Ori WotW, Steam appid 1057090, Unity 2018.4.24f1, IL2CPP (GameAssembly.dll), D3D11 → DXVK → Turnip (Adreno 750)
- Proton Experimental (ARM64) ships `vrclient_x64.dll` (Windows OpenVR → native bridge); the prefix already has
  `AppData/Local/openvr/openvrpaths.vrpath` pointing at `C:\vrclient`.
- Native `vrcompositor` (linuxarm64) is running; `libopenvr_api.so` arm64 is available.
- boot.config has `vr-enabled=0` (the Unity build includes VR support but it's switched off).
- Saves: `<prefix>/AppData/Local/Ori and the Will of The Wisps/saveFile0*.uberstate` (Steam Cloud enabled)
- Unity log: `<prefix>/AppData/LocalLow/Moon Studios/OriAndTheWilloftheWisps/output_log.txt`

## Iteration-speed ideas
- Live tuning: expose IPD/convergence/enable as hotkeys + config reloaded on change, so most tweaks need no restart.
- Hot reload: keep one tiny host plugin that never changes; it loads the real logic DLL from bytes on a hotkey.
  The reloadable code must not register new Il2Cpp-injected types (the host owns the single injected MonoBehaviour).
- Skip intro/title: a patch that skips logos and auto-continues the last save.
- Launch/kill from the terminal: `steam steam://rungameid/1057090`, `pkill -f oriwotw.exe`, tail BepInEx/LogOutput.log.

## 2026-10-09: BepInEx first launch under FEX
- BepInEx be.788 crashed twice, about 10 s in, with `Fatal error. System.AccessViolationException` (BepInEx/ErrorLog.log).
  It had downloaded unity-libs but `interop/` was empty, so it died during Cpp2IL/interop generation.
- The same Cpp2IL + Il2CppInterop code running natively (linux-arm64 .NET 8) works: 94 dummy assemblies, 126 interop assemblies, about 30 s.
  So the crash comes from emulation, not from the game's metadata. Worked around with tools/InteropGen + `UpdateInteropAssemblies = false`.

## 2026-10-09: Camera probe (docs/probes/)
- BepInEx + plugin load fine under FEX once interop is pre-generated.
- Gameplay uses ONE camera, `systems/cameraSystem/camera`: **perspective**, fov 45, near 0.3, far 5000, HDR, at z = -29.
  Post effects are components on it: UberPostProcess, ChromaticAberration, ContrastVignette, AntiAliasing, etc.
- Ori (`character` layer) sits at view depth 29.0. Art spans 17–46, `artBlurred`/`earlyZ` background goes out to about 220,
  and blurred foreground goes to -32 (behind the camera / very close). **Real 3D layering, so true stereo works.**
- The HUD (`ui` layer) is world-space geometry at depth 28.4–30, so it sits in the playfield plane and is drawn by the same camera.
  With convergence at the playfield (about 29) the HUD lands at screen depth for free.
- Most renderers are queue 2000 (only about 9 transparent at 3000).
- Window: 1280x720. The boot splash uses a separate ortho camera.
