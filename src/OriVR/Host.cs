using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace OriVR;

/// Loads OriVR.Core.dll from bytes and forwards Unity callbacks to it. Reloads the core when the
/// file changes on disk (or on F7), so code changes don't need a game restart.
public class Host : MonoBehaviour
{
    public Host(IntPtr ptr) : base(ptr) { }

    public static IModule Module { get; private set; }

    private string corePath;
    private DateTime coreWriteTime;
    private float nextCheck;
    private int reloads;
    private string lastError;

    private void Awake()
    {
        var loc = typeof(Host).Assembly.Location;
        var dir = string.IsNullOrEmpty(loc) ? BepInEx.Paths.PluginPath : Path.GetDirectoryName(loc);
        corePath = Path.Combine(dir, "OriVR.Core.dll");
        // The core references this assembly; make sure it binds to the copy already loaded.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            new AssemblyName(e.Name).Name == "OriVR" ? typeof(Host).Assembly : null;
        LoadCore();
        StartCoroutine(EndOfFrameLoop().WrapToIl2Cpp());
    }

    private void LoadCore()
    {
        if (Module != null)
        {
            Guard("Shutdown", () => Module.Shutdown());
            Module = null;
        }
        try
        {
            coreWriteTime = File.GetLastWriteTimeUtc(corePath);
            var pdbPath = Path.ChangeExtension(corePath, ".pdb");
            var asm = File.Exists(pdbPath)
                ? Assembly.Load(File.ReadAllBytes(corePath), File.ReadAllBytes(pdbPath))
                : Assembly.Load(File.ReadAllBytes(corePath));
            var type = asm.GetTypes().First(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
            var module = (IModule)Activator.CreateInstance(type);
            module.Init(new ModuleContext
            {
                Config = Plugin.Instance.Config,
                Log = Plugin.Logger,
                PluginDir = Path.GetDirectoryName(corePath),
            });
            Module = module;
            Plugin.Logger.LogInfo(reloads++ == 0 ? "Core loaded." : $"Core reloaded (#{reloads - 1}).");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Failed to load {corePath}: {e}");
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7) ||
            (Time.unscaledTime >= nextCheck && CoreChanged()))
            LoadCore();
        if (Time.unscaledTime >= nextCheck) nextCheck = Time.unscaledTime + 1f;
        if (Module != null) Guard("Update", Module.Update);
    }

    private bool CoreChanged()
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(corePath);
            // Wait until the copy has settled before loading.
            return t != coreWriteTime && (DateTime.UtcNow - t).TotalSeconds > 0.5;
        }
        catch { return false; }
    }

    private IEnumerator EndOfFrameLoop()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            if (Module != null) Guard("EndOfFrame", Module.EndOfFrame);
        }
    }

    /// Run a core callback, logging each distinct exception once instead of every frame.
    internal void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            var msg = $"Core {what} threw: {e}";
            if (msg != lastError) Plugin.Logger.LogError(msg);
            lastError = msg;
        }
    }

}
