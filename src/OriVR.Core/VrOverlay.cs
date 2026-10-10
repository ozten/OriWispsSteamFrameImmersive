using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace OriVR;

/// Shows the eye pair in the headset as a SteamVR overlay with side-by-side stereo
/// (VROverlayFlags.SideBySide_Parallel): a floating 3D window. Goes through Proton's
/// vrclient bridge (C:\vrclient) to the native SteamVR runtime.
public class VrOverlay
{
    private const string Key = "com.ozten.orivr.overlay";
    private static bool libraryLoaded;

    private ulong handle;
    private RenderTexture sbs;
    private readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[1];
    private float lastWidth, lastDistance;
    private bool placed;

    public bool Running { get; private set; }
    private bool initialized;

    public bool Start(string pluginDir)
    {
        if (initialized)
        {
            Running = true;
            Recenter();
            ApplyLayout();
            OpenVR.Overlay.ShowOverlay(handle);
            Log.Info("VR overlay shown.");
            return true;
        }
        lastWidth = lastDistance = 0f; // force size/placement onto a newly created overlay
        try
        {
            if (!libraryLoaded)
            {
                // Load by full path; DllImport("openvr_api") then binds to the already-loaded module.
                NativeLibrary.Load(Path.Combine(pluginDir, "openvr_api.dll"));
                libraryLoaded = true;
            }
            var err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
            if (err != EVRInitError.None)
            {
                Log.Error($"OpenVR init failed: {err}");
                return false;
            }
            var overlay = OpenVR.Overlay;
            var oerr = overlay.CreateOverlay(Key, "Ori VR", ref handle);
            if (oerr == EVROverlayError.KeyInUse) oerr = overlay.FindOverlay(Key, ref handle);
            if (oerr != EVROverlayError.None)
            {
                Log.Error($"CreateOverlay failed: {oerr}");
                OpenVR.Shutdown();
                return false;
            }
            overlay.SetOverlayFlag(handle, VROverlayFlags.SideBySide_Parallel, true);
            overlay.SetOverlaySortOrder(handle, 100);
            initialized = true;
            Running = true;
            Recenter();
            ApplyLayout();
            overlay.ShowOverlay(handle);
            Log.Info("VR overlay started.");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"VR overlay start failed: {e}");
            return false;
        }
    }

    /// Hide the overlay but keep the OpenVR connection (re-creating it on every toggle left
    /// a stray, default-sized overlay behind).
    public void Hide()
    {
        if (!Running) return;
        Running = false;
        try { OpenVR.Overlay?.HideOverlay(handle); }
        catch (Exception e) { Log.Error($"VR overlay hide: {e}"); }
        Log.Info("VR overlay hidden.");
    }

    /// Full teardown (core unload / reload).
    public void Stop()
    {
        Running = false;
        if (!initialized) return;
        initialized = false;
        try
        {
            OpenVR.Overlay?.DestroyOverlay(handle);
            OpenVR.Shutdown();
        }
        catch (Exception e) { Log.Error($"VR overlay stop: {e}"); }
        handle = 0;
        if (sbs != null) { sbs.Release(); sbs = null; }
        Log.Info("VR overlay stopped.");
    }

    private bool useHead;

    /// Re-place the overlay straight ahead of the headset (yaw only, so it stays upright).
    public void Recenter()
    {
        useHead = true;
        placed = false;
    }

    private void ApplyLayout()
    {
        var overlay = OpenVR.Overlay;
        var width = Mathf.Max(0.2f, Settings.OverlayWidth.Value);
        var distance = Mathf.Max(0.3f, Settings.OverlayDistance.Value);
        if (width != lastWidth)
        {
            // For a side-by-side overlay this is the width of the 3D image each eye sees.
            overlay.SetOverlayWidthInMeters(handle, width);
            lastWidth = width;
        }
        if (placed && distance == lastDistance) return;
        lastDistance = distance;

        // The window appears straight ahead of wherever you look when it is shown (F9/F11) or
        // recentered (Shift+F11). The seated origin turned out to be unreliable on the Frame
        // (after a reboot the overlay landed out of view), so it is only a fallback.
        var hmdPos = new Vector3(0f, 1.6f, 0f);
        var fwd = new Vector3(0f, 0f, -1f);
        OpenVR.System?.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);
        var hmdValid = poses[0].bPoseIsValid;
        var hm = poses[0].mDeviceToAbsoluteTracking;
        if (hmdValid) hmdPos = new Vector3(hm.m3, hm.m7, hm.m11);
        var anchor = useHead && hmdValid ? hm : OpenVR.System.GetSeatedZeroPoseToStandingAbsoluteTrackingPose();
        var f = new Vector3(-anchor.m2, 0f, -anchor.m10); // poses look down their -Z
        if (f.sqrMagnitude > 1e-4f) fwd = f.normalized;
        if (!useHead) hmdPos = new Vector3(anchor.m3, hmdValid ? hm.m7 : anchor.m7, anchor.m11);
        var pos = hmdPos + fwd * distance;
        var z = -fwd;                            // overlay is seen from its +Z side
        var x = new Vector3(z.z, 0f, -z.x);      // up × z
        var t = new HmdMatrix34_t
        {
            m0 = x.x, m1 = 0f, m2 = z.x, m3 = pos.x,
            m4 = 0f, m5 = 1f, m6 = 0f, m7 = pos.y,
            m8 = x.z, m9 = 0f, m10 = z.z, m11 = pos.z,
        };
        overlay.SetOverlayTransformAbsolute(handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref t);
        placed = true;
        var seat = OpenVR.System.GetSeatedZeroPoseToStandingAbsoluteTrackingPose();
        Log.Info($"VR overlay placed {distance:F1} m ahead of the {(useHead && hmdValid ? "headset" : "seated origin")}, {width:F1} m wide, at {pos}. " +
                 $"hmd valid={hmdValid} pos=({hm.m3:F2},{hm.m7:F2},{hm.m11:F2}) seated=({seat.m3:F2},{seat.m7:F2},{seat.m11:F2}) seatedFwd=({-seat.m2:F2},{-seat.m10:F2})");
    }

    public void Submit(RenderTexture left, RenderTexture right)
    {
        if (!Running) return;
        ApplyLayout();
        int w = left.width, h = left.height;
        if (sbs == null || sbs.width != w * 2 || sbs.height != h)
        {
            if (sbs != null) sbs.Release();
            sbs = new RenderTexture(w * 2, h, 0, RenderTextureFormat.Default) { name = "OriVR_SBS" };
            sbs.Create();
        }
        Graphics.CopyTexture(left, 0, 0, 0, 0, w, h, sbs, 0, 0, 0, 0);
        Graphics.CopyTexture(right, 0, 0, 0, 0, w, h, sbs, 0, 0, w, 0);
        var tex = new Texture_t
        {
            handle = sbs.GetNativeTexturePtr(),
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Auto,
        };
        var err = OpenVR.Overlay.SetOverlayTexture(handle, ref tex);
        if (err != EVROverlayError.None)
        {
            Log.Error($"SetOverlayTexture failed: {err}; stopping overlay.");
            Settings.OverlayEnabled.Value = false;
        }
    }
}
