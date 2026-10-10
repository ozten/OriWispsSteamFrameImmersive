using System;
using UnityEngine;

namespace OriVR;

/// Lives on the gameplay camera's GameObject so Unity calls it around every render of that camera.
public class EyeHook : MonoBehaviour
{
    public EyeHook(IntPtr ptr) : base(ptr) { }

    private Camera cam;

    private void OnPreCull()
    {
        var s = StereoController.Instance;
        if (s == null || s.Cam == null) return;
        cam ??= GetComponent<Camera>();
        if (cam != s.Cam) return;
        s.ApplyEye(cam);
    }

    private void OnPostRender()
    {
        if (cam == null) return;
        // Restore so game code reading the camera between frames sees normal matrices.
        cam.ResetWorldToCameraMatrix();
        cam.ResetProjectionMatrix();
    }
}
