using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace OriVR;

[BepInPlugin(Guid, "OriVR", "0.0.3")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.ozten.orivr";
    internal static ManualLogSource Logger;
    internal static Plugin Instance;

    public override void Load()
    {
        Logger = Log;
        Instance = this;
        ClassInjector.RegisterTypeInIl2Cpp<EyeHook>();
        AddComponent<Host>();
    }
}
