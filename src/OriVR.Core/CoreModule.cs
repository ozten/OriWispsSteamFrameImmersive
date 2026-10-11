using UnityEngine;

namespace OriVR;

/// Entry point the host instantiates. Everything in this assembly can be hot-reloaded.
public sealed class CoreModule : IModule
{
    private Probe probe;
    private StereoController stereo;

    public void Init(ModuleContext ctx)
    {
        Log.Source = ctx.Log;
        Settings.Bind(ctx.Config);
        stereo = new StereoController(ctx.PluginDir);
        probe = new Probe(stereo);
        Log.Info("F9 stereo on/off, F10 swap eyes, -/= separation, [/] convergence, F11 VR window (Shift+F11 recenter, or re-show tab; Ctrl+F11 Window/Scene mode), PgUp/PgDn VR size, Home/End VR distance, Shift+F10 blank flat window, F8 probe+capture, F7 reload core.");
    }

    public void Update()
    {
        probe.Update();
        stereo.Update();
    }

    public void EndOfFrame() => stereo.EndOfFrame();
    // Camera callbacks come from Camera.onPreCull/onPostRender (see StereoController), not EyeHook.
    public void OnPreCull(Camera cam) { }
    public void OnPostRender(Camera cam) { }
    public void Shutdown() => stereo.Shutdown();
}

internal static class Log
{
    public static BepInEx.Logging.ManualLogSource Source;
    public static void Info(string m) => Source.LogInfo(m);
    public static void Error(string m) => Source.LogError(m);
}
