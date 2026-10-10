using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OriVR;

[BepInPlugin(Guid, "OriVR", "0.0.1")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.ozten.orivr";
    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        AddComponent<Probe>();
        Log.LogInfo("OriVR loaded. F8 = dump cameras/scene depth.");
    }
}
