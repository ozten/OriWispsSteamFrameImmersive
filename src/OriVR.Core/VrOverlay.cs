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
    private const string TabKey = "com.ozten.orivr.tab";
    private static bool libraryLoaded;

    private ulong handle, thumbHandle;

    /// The mode ("Window" or "Tab") this overlay was created in.
    public string Mode { get; private set; } = "";
    private bool IsTab => Mode == "Tab";
    private bool IsScene => Mode == "Scene";
    private RenderTexture sceneTex;
    private readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];
    private bool sceneErrorLogged, identified;
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
            placed = false;
            ApplyLayout();
            if (IsTab) OpenVR.Overlay.ShowDashboard(TabKey);
            else OpenVR.Overlay.ShowOverlay(handle);
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
            Mode = Settings.VrMode.Value is "Tab" or "Scene" ? Settings.VrMode.Value : "Window";
            OpenVR.Init(ref err, IsScene ? EVRApplicationType.VRApplication_Scene : EVRApplicationType.VRApplication_Overlay);
            if (err != EVRInitError.None)
            {
                Log.Error($"OpenVR init failed: {err}");
                return false;
            }
            if (IsScene)
            {
                if (OpenVR.Compositor == null)
                {
                    Log.Error("OpenVR compositor unavailable for Scene mode.");
                    OpenVR.Shutdown();
                    Mode = "";
                    return false;
                }
                OpenVR.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);
                sceneErrorLogged = false;
                identified = false;
                Log.Info($"OpenVR scene app: canRender={OpenVR.Compositor.CanRenderScene()}");
            }
            var overlay = OpenVR.Overlay;
            EVROverlayError oerr;
            if (IsTab)
            {
                oerr = overlay.CreateDashboardOverlay(TabKey, "Ori 3D", ref handle, ref thumbHandle);
                if (oerr == EVROverlayError.KeyInUse) oerr = overlay.FindOverlay(TabKey, ref handle);
                var thumb = Path.Combine(pluginDir, "thumbnail.png");
                if (oerr == EVROverlayError.None && thumbHandle != 0 && File.Exists(thumb))
                    overlay.SetOverlayFromFile(thumbHandle, thumb);
            }
            else
            {
                oerr = overlay.CreateOverlay(Key, "Ori VR", ref handle);
                if (oerr == EVROverlayError.KeyInUse) oerr = overlay.FindOverlay(Key, ref handle);
            }
            if (oerr != EVROverlayError.None)
            {
                Log.Error($"CreateOverlay failed: {oerr}");
                OpenVR.Shutdown();
                return false;
            }
            overlay.SetOverlayFlag(handle, VROverlayFlags.SideBySide_Parallel, true);
            if (!IsTab) overlay.SetOverlaySortOrder(handle, 100);
            initialized = true;
            Running = true;
            placed = false;
            ApplyLayout();
            if (IsTab)
            {
                overlay.ShowDashboard(TabKey);
                uint flags = 0;
                overlay.GetOverlayFlags(handle, ref flags);
                Log.Info($"VR dashboard tab created: handle=0x{handle:X} thumb=0x{thumbHandle:X} flags=0x{flags:X} " +
                         $"(SBS honoured={(flags & (uint)VROverlayFlags.SideBySide_Parallel) != 0}) dashboardDevice={overlay.GetPrimaryDashboardDevice()}");
            }
            else overlay.ShowOverlay(handle);
            lastTabState = "";
            Log.Info($"VR overlay started ({Mode}).");
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
        if (IsTab) { Stop(); return; } // a dashboard tab can't be hidden; destroy it so the Frame's screen returns
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
        handle = thumbHandle = 0;
        if (sceneTex != null) { sceneTex.Release(); sceneTex = null; }
        Mode = "";
        if (sbs != null) { sbs.Release(); sbs = null; }
        Log.Info("VR overlay stopped.");
    }

    private bool recenterRequested;

    /// Move the window straight ahead of where the headset is looking (yaw only, so it stays upright)
    /// and remember that spot.
    public void Recenter()
    {
        if (IsTab && initialized)
        {
            OpenVR.Overlay.ShowDashboard(TabKey);
            Log.Info("VR dashboard tab re-shown.");
            return;
        }
        recenterRequested = true;
        placed = false;
    }

    /// The window's anchor (viewer position + facing) is saved in the config, so it stays put
    /// across F9/F11 toggles, core reloads and game restarts. It only moves on Shift+F11, or the
    /// first time ever (no saved anchor).
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
        if (IsTab || (placed && distance == lastDistance)) return;
        lastDistance = distance;

        if (recenterRequested || !TryParseAnchor(Settings.OverlayAnchor.Value, out _, out _))
        {
            recenterRequested = false;
            OpenVR.System?.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);
            var hm = poses[0].bPoseIsValid ? poses[0].mDeviceToAbsoluteTracking
                                           : OpenVR.System.GetSeatedZeroPoseToStandingAbsoluteTrackingPose();
            var f = new Vector3(-hm.m2, 0f, -hm.m10); // poses look down their -Z
            f = f.sqrMagnitude > 1e-4f ? f.normalized : new Vector3(0f, 0f, -1f);
            Settings.OverlayAnchor.Value = FormattableString.Invariant($"{hm.m3:F3},{hm.m7:F3},{hm.m11:F3},{f.x:F4},{f.z:F4}");
            Settings.Save();
            Log.Info($"VR window anchor set from headset (valid={poses[0].bPoseIsValid}).");
        }
        TryParseAnchor(Settings.OverlayAnchor.Value, out var eye, out var fwd);

        var pos = eye + fwd * distance;
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
        Log.Info($"VR window placed {distance:F2} m ahead, {width:F2} m wide, at {pos}.");
    }

    private static bool TryParseAnchor(string s, out Vector3 eye, out Vector3 fwd)
    {
        eye = new Vector3(0f, 1.2f, 0f);
        fwd = new Vector3(0f, 0f, -1f);
        var p = (s ?? "").Split(',');
        if (p.Length != 5) return false;
        var v = new float[5];
        for (var i = 0; i < 5; i++)
            if (!float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i]))
                return false;
        var f = new Vector3(v[3], 0f, v[4]);
        if (f.sqrMagnitude < 1e-4f) return false;
        eye = new Vector3(v[0], v[1], v[2]);
        fwd = f.normalized;
        return true;
    }

    private string lastTabState = "";
    private float nextPoll;

    /// Spike diagnostics for Tab mode: log whenever the dashboard's visibility or our tab's active
    /// state changes, so we can see whether the Frame keeps our tab up during play.
    public void Poll()
    {
        if (!(IsTab || IsScene) || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + 0.5f;
        var o = OpenVR.Overlay;
        if (IsScene)
        {
            var sc = OpenVR.Compositor;
            var focusProc = sc.GetCurrentSceneFocusProcess();
            if (!identified && focusProc != 0 && OpenVR.System.IsInputAvailable())
            {
                // Tell SteamVR this scene app is Ori (otherwise it shows up as "wine-preloader").
                identified = true;
                var aerr = OpenVR.Applications.IdentifyApplication(focusProc, "steam.app.1057090");
                Log.Info($"IdentifyApplication(pid {focusProc}, steam.app.1057090): {aerr}");
            }
            var st = $"dashboardVisible={o.IsDashboardVisible()} windowVisible={o.IsOverlayVisible(handle)} " +
                     $"sceneFocus={OpenVR.System.IsInputAvailable()} sceneProcess={sc.GetCurrentSceneFocusProcess()} fullscreen={sc.IsFullscreen()}";
            if (st == lastTabState) return;
            lastTabState = st;
            Log.Info($"VR scene state: {st}");
            return;
        }
        uint flags = 0;
        o.GetOverlayFlags(handle, ref flags);
        var state = $"dashboardVisible={o.IsDashboardVisible()} ourTabActive={o.IsActiveDashboardOverlay(handle)} " +
                    $"overlayVisible={o.IsOverlayVisible(handle)} sbs={(flags & (uint)VROverlayFlags.SideBySide_Parallel) != 0}";
        if (state == lastTabState) return;
        lastTabState = state;
        Log.Info($"VR tab state: {state}");
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
        if (IsScene) SubmitScene();
    }

    /// Scene mode: we're the VR app, so the compositor needs an eye frame from us every frame or it
    /// fades to its loading grid. For now the "environment" is plain black; the 3D window itself is
    /// still our overlay, which the compositor draws on top.
    private void SubmitScene()
    {
        var c = OpenVR.Compositor;
        var werr = c.WaitGetPoses(renderPoses, gamePoses);
        if (sceneTex == null)
        {
            sceneTex = new RenderTexture(256, 256, 0, RenderTextureFormat.Default) { name = "OriVR_Scene" };
            sceneTex.Create();
            var prev = RenderTexture.active;
            RenderTexture.active = sceneTex;
            GL.Clear(true, true, new Color(0.02f, 0.02f, 0.03f, 1f));
            RenderTexture.active = prev;
        }
        var tex = new Texture_t { handle = sceneTex.GetNativeTexturePtr(), eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
        var bounds = new VRTextureBounds_t { uMin = 0, vMin = 0, uMax = 1, vMax = 1 };
        var e1 = c.Submit(EVREye.Eye_Left, ref tex, ref bounds, EVRSubmitFlags.Submit_Default);
        var e2 = c.Submit(EVREye.Eye_Right, ref tex, ref bounds, EVRSubmitFlags.Submit_Default);
        if ((werr != EVRCompositorError.None || e1 != EVRCompositorError.None || e2 != EVRCompositorError.None) && !sceneErrorLogged)
        {
            sceneErrorLogged = true;
            Log.Error($"Scene submit: waitGetPoses={werr} left={e1} right={e2}");
        }
    }
}
