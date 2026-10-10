using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace OriVR;

/// Contract between the stable host plugin (OriVR.dll) and the hot-reloadable core (OriVR.Core.dll).
/// The host owns everything Il2CppInterop must register (MonoBehaviours, coroutines); the core is
/// plain C# and can be swapped while the game runs.
public interface IModule
{
    void Init(ModuleContext ctx);
    void Update();
    void EndOfFrame();
    void OnPreCull(Camera cam);
    void OnPostRender(Camera cam);
    /// Undo all changes to game state (camera targets, matrices) and release resources.
    void Shutdown();
}

public sealed class ModuleContext
{
    public ConfigFile Config;
    public ManualLogSource Log;
    public string PluginDir;
}
