using System;
using UnityEngine;

namespace OriVR;

/// Attach to a camera's GameObject so Unity calls the core around every render of that camera.
public class EyeHook : MonoBehaviour
{
    public EyeHook(IntPtr ptr) : base(ptr) { }

    private Camera cam;

    private void OnPreCull()
    {
        cam ??= GetComponent<Camera>();
        try { Host.Module?.OnPreCull(cam); }
        catch (Exception e) { Plugin.Logger.LogError($"OnPreCull: {e}"); }
    }

    private void OnPostRender()
    {
        try { Host.Module?.OnPostRender(cam); }
        catch (Exception e) { Plugin.Logger.LogError($"OnPostRender: {e}"); }
    }
}
