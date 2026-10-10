using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace OriVR;

[BepInPlugin(Guid, "OriVR", "0.0.2")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.ozten.orivr";
    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        Settings.Bind(Config);
        ClassInjector.RegisterTypeInIl2Cpp<EyeHook>();
        AddComponent<Probe>();
        AddComponent<StereoController>();
        Log.LogInfo("OriVR loaded. F9 stereo on/off, F10 swap eyes, -/= separation, [/] convergence, F8 probe.");
    }
}
