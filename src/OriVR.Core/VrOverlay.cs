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
    private int frames;
    private long waitTicks;
    private float statStart;
    private RenderTexture sbs;
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
                InputBridge.OnSessionStart(pluginDir);
                Identify(UnixPid());
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
        // A dashboard tab can't be hidden, and a scene app that stops submitting frames leaves the Frame
        // on its "waiting" screen: tear both down so the Frame's own screen returns.
        if (IsTab || IsScene) { Stop(); return; }
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

    /// Ask SteamVR to make the current head pose the seated origin, which is what the Frame's own
    /// recenter (hold the Steam button) does. The window follows on its own.
    public void Recenter()
    {
        if (IsTab && initialized)
        {
            OpenVR.Overlay.ShowDashboard(TabKey);
            Log.Info("VR dashboard tab re-shown.");
            return;
        }
        if (!initialized) return;
        OpenVR.Chaperone?.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated);
        Log.Info("VR seated origin reset.");
    }

    /// The window sits straight ahead of SteamVR's seated origin (at eye height, upright), so the
    /// system recenter puts it in front of you; the mod keeps no position of its own.
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

        // Identity rotation: the overlay faces +Z, toward the viewer looking down -Z.
        var t = new HmdMatrix34_t { m0 = 1f, m5 = 1f, m10 = 1f, m11 = -distance };
        overlay.SetOverlayTransformAbsolute(handle, ETrackingUniverseOrigin.TrackingUniverseSeated, ref t);
        placed = true;
        Log.Info($"VR window placed {distance:F2} m ahead of the seated origin, {width:F2} m wide.");
    }

    /// Tell SteamVR this scene app is Ori (otherwise it shows up as "wine-preloader").
    private void Identify(uint pid)
    {
        if (identified || pid == 0) return;
        var err = OpenVR.Applications.IdentifyApplication(pid, "steam.app.1057090");
        identified = err == EVRApplicationError.None;
        Log.Info($"IdentifyApplication(pid {pid}, steam.app.1057090): {err}");
    }

    /// SteamVR knows us by our Linux pid (wine-preloader), not the Windows one. Wine maps Z: to /.
    private static uint UnixPid()
    {
        try { return uint.Parse(File.ReadAllText(@"Z:\proc\self\stat").Split(' ')[0]); }
        catch (Exception e) { Log.Info($"Unix pid unavailable ({e.GetType().Name}); identifying later."); return 0; }
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
            if (!identified && focusProc != 0 && OpenVR.System.IsInputAvailable()) Identify(focusProc);
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

    /// Scene mode without eye images (no gameplay camera, e.g. loading): keep the compositor fed so
    /// SteamVR doesn't take over; the window keeps its last image.
    public void KeepAlive()
    {
        if (Running && IsScene) SubmitScene();
    }

    /// Scene mode: we're the VR app, so the compositor needs an eye frame from us every frame or it
    /// fades to its loading grid. For now the "environment" is plain black; the 3D window itself is
    /// still our overlay, which the compositor draws on top.
    private void SubmitScene()
    {
        var c = OpenVR.Compositor;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var werr = c.WaitGetPoses(renderPoses, gamePoses);
        waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        frames++;
        var now = Time.realtimeSinceStartup;
        if (now - statStart >= 5f)
        {
            var secs = now - statStart;
            var ft = new Compositor_FrameTiming { m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Compositor_FrameTiming>() };
            var hz = 0f;
            var perr = ETrackedPropertyError.TrackedProp_Success;
            if (OpenVR.System != null) hz = OpenVR.System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref perr);
            if (c.GetFrameTiming(ref ft, 0))
                Log.Info($"Compositor: {hz:F0} Hz, presents/frame={ft.m_nNumFramePresents} mispresented={ft.m_nNumMisPresented} dropped={ft.m_nNumDroppedFrames} " +
                         $"reproj=0x{ft.m_nReprojectionFlags:X} clientInterval={ft.m_flClientFrameIntervalMs:F1}ms compGpu={ft.m_flCompositorRenderGpuMs:F1}ms totalGpu={ft.m_flTotalRenderGpuMs:F1}ms");
            Log.Info($"Scene perf: {frames / secs:F1} fps, WaitGetPoses {waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F1} ms/frame, " +
                     $"vSyncCount={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate}");
            frames = 0; waitTicks = 0; statStart = now;
        }
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
