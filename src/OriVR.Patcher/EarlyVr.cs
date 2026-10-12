using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace OriVR;

/// Runs in BepInEx's preloader, before Unity opens the game window, and connects to SteamVR as a
/// scene app right away. If the game only becomes a VR app later, the Frame has already shown it
/// on its flat screen, and on the switch SteamVR docks that screen and opens its dashboard
/// ("Resume Game"). The core plugin adopts this session (see VrOverlay.Start).
[PatcherPluginInfo("com.ozten.orivr.earlyvr", "OriVR early VR", "1.0.0")]
public class EarlyVr : BasePatcher
{
    /// Set to the OpenVR init token while the early session is waiting for the core to adopt it.
    public const string EnvVar = "ORIVR_EARLY_VR";

    [DllImport("openvr_api", EntryPoint = "VR_InitInternal2", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint InitInternal2(ref int error, int applicationType, [MarshalAs(UnmanagedType.LPStr)] string startupInfo);

    private const int VRApplication_Scene = 1;

    public override void Initialize()
    {
        try
        {
            if (!WantsScene())
            {
                Log.LogInfo("VR is off or not in Scene mode in the config; not connecting early.");
                return;
            }
            var dll = Path.Combine(Paths.PluginPath, "openvr_api.dll");
            if (!File.Exists(dll)) { Log.LogWarning($"{dll} missing; not connecting early."); return; }
            NativeLibrary.Load(dll); // DllImport("openvr_api") then binds to this module, as in the core
            var sw = Stopwatch.StartNew();
            var err = 0;
            var token = InitInternal2(ref err, VRApplication_Scene, "");
            if (err != 0) { Log.LogWarning($"Early VR init failed (EVRInitError {err}); the core will connect later."); return; }
            Environment.SetEnvironmentVariable(EnvVar, token.ToString());
            Log.LogInfo($"Connected to SteamVR as a scene app before the game window opened ({sw.ElapsedMilliseconds} ms).");
        }
        catch (Exception e) { Log.LogError($"Early VR init: {e}"); }
    }

    /// Same defaults as Settings.cs: VR on, Scene mode.
    private static bool WantsScene()
    {
        var path = Path.Combine(Paths.ConfigPath, "com.ozten.orivr.cfg");
        if (!File.Exists(path)) return true;
        string section = "", overlay = "true", mode = "Scene";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("[")) { section = line; continue; }
            var eq = line.IndexOf('=');
            if (section != "[VR]" || eq < 0 || line.StartsWith("#")) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key == "OverlayEnabled") overlay = value;
            else if (key == "Mode") mode = value;
        }
        return overlay.Equals("true", StringComparison.OrdinalIgnoreCase) && mode == "Scene";
    }
}
