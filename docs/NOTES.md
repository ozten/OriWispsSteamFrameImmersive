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
