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
        stereo = new StereoController();
        probe = new Probe(stereo);
        Log.Info("F9 stereo on/off, F10 swap eyes, -/= separation, [/] convergence, F8 probe+capture, F7 reload core.");
    }

    public void Update()
    {
        probe.Update();
        stereo.Update();
    }

    public void EndOfFrame() => stereo.EndOfFrame();
    public void OnPreCull(Camera cam) => stereo.OnPreCull(cam);
    public void OnPostRender(Camera cam) => stereo.OnPostRender(cam);
    public void Shutdown() => stereo.Shutdown();
}

internal static class Log
{
    public static BepInEx.Logging.ManualLogSource Source;
    public static void Info(string m) => Source.LogInfo(m);
    public static void Error(string m) => Source.LogError(m);
}
