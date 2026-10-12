# Third-party components

The release zip and the installer bring in these components, each under its own license:

| Component | Where | License |
|---|---|---|
| [OpenVR SDK](https://github.com/ValveSoftware/openvr) (`openvr_api.dll`, `openvr_api.cs`) | plugins, source | BSD 3-Clause (`plugins/openvr_api.LICENSE`) |
| [BepInEx](https://github.com/BepInEx/BepInEx) 6 be.788 | downloaded by the installer; its libraries are also bundled in `tools/InteropGen-*` | LGPL-2.1 |
| [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | bundled in `tools/InteropGen-*` | LGPL-3.0 |
| [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), AsmResolver, Iced | bundled in `tools/InteropGen-*` | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) | bundled in `tools/InteropGen-*` | MIT |
| Unity reference libraries for 2018.4.24 | downloaded by the installer from unity.bepinex.dev | Unity's terms |

`tools/InteropGen-*` is `tools/InteropGen` from this repository, published as a self-contained
single-file program. The LGPL libraries inside it are unmodified; to use different versions,
rebuild it from source with `scripts/package.sh`.

The interop assemblies the installer generates are derived from the game and are created on your
machine; they are not distributed.
