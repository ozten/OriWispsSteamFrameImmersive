using System;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OriVR;

/// Renders Ori's gameplay camera once per eye and shows the pair side by side in the window.
///
/// Left eye: the game's own render of the main camera, redirected into LeftEye.
/// Right eye: a second cam.Render() at end of frame into RightEye. Both use the same camera, so
/// all of Ori's post-processing components run for each eye.
/// Eyes are offset via the view matrix (the camera transform is never moved) and use an
/// off-axis projection so the two frusta converge on the zero-parallax plane.
public class StereoController
{
    public Camera Cam { get; private set; }
    public RenderTexture LeftEye, RightEye;

    /// Set by F8 or by creating BepInEx/orivr-capture: save both eye images as PNGs at end of frame.
    public bool CaptureRequested;

    private float nextConfigCheck;
    private bool active;
    private int captureCount;

    private readonly VrOverlay overlay = new();
    private readonly string pluginDir;
    public StereoController(string pluginDir) => this.pluginDir = pluginDir;

    private static string CaptureTrigger => Path.Combine(Paths.BepInExRootPath, "orivr-capture");

    public void Update()
    {
        HandleKeys();
        if (Time.unscaledTime >= nextConfigCheck)
        {
            nextConfigCheck = Time.unscaledTime + 1f;
            if (Settings.ReloadIfChanged())
                Log.Info($"Config reloaded: {Describe()}");
            if (File.Exists(CaptureTrigger))
            {
                File.Delete(CaptureTrigger);
                CaptureRequested = true;
            }
        }

        var main = Camera.main;
        var want = Settings.StereoEnabled.Value && main != null && !main.orthographic;
        if (main != Cam || !want) Deactivate();
        if (want && !active) Activate(main);
        if (active) EnsureTextures();

        var wantOverlay = active && Settings.OverlayEnabled.Value;
        if (wantOverlay && overlay.Running && overlay.Mode != Settings.VrMode.Value) overlay.Stop(); // mode switched
        if (wantOverlay && !overlay.Running && !overlayFailed) overlayFailed = !overlay.Start(pluginDir);
        if (overlay.Running) overlay.Poll();
        InputBridge.Poll(overlay.Running && overlay.Mode == "Scene");
        if (!wantOverlay)
        {
            overlay.Hide();
            overlayFailed = false;
        }
    }

    private bool overlayFailed;

    private void HandleKeys()
    {
        var changed = true;
        // Ctrl+Shift+letter versions of the F-key toggles, for keyboards that need Fn for F-keys.
        if (Ctrl && Shift && Input.GetKeyDown(KeyCode.S)) Settings.StereoEnabled.Value = !Settings.StereoEnabled.Value;
        else if (Ctrl && Shift && Input.GetKeyDown(KeyCode.E)) Settings.SwapEyes.Value = !Settings.SwapEyes.Value;
        else if (Ctrl && Shift && Input.GetKeyDown(KeyCode.V)) Settings.OverlayEnabled.Value = !Settings.OverlayEnabled.Value;
        else if (Ctrl && Shift && Input.GetKeyDown(KeyCode.R)) overlay.Recenter();
        else if (Ctrl && Shift && Input.GetKeyDown(KeyCode.M))
            Settings.VrMode.Value = Settings.VrMode.Value == "Window" ? "Scene" : "Window";
        else if (Input.GetKeyDown(KeyCode.F9)) Settings.StereoEnabled.Value = !Settings.StereoEnabled.Value;
        else if (Input.GetKeyDown(KeyCode.F10) && Shift) Settings.BlankWindowInVR.Value = !Settings.BlankWindowInVR.Value;
        else if (Input.GetKeyDown(KeyCode.F10)) Settings.SwapEyes.Value = !Settings.SwapEyes.Value;
        else if (Input.GetKeyDown(KeyCode.PageUp)) Settings.OverlayWidth.Value *= 1.1f;
        else if (Input.GetKeyDown(KeyCode.PageDown)) Settings.OverlayWidth.Value /= 1.1f;
        else if (Input.GetKeyDown(KeyCode.Home)) Settings.OverlayDistance.Value = Mathf.Max(0.5f, Settings.OverlayDistance.Value - 0.25f);
        else if (Input.GetKeyDown(KeyCode.End)) Settings.OverlayDistance.Value += 0.25f;
        else if (Input.GetKeyDown(KeyCode.Minus)) Settings.Separation.Value = Mathf.Max(0f, Settings.Separation.Value - 0.1f);
        else if (Input.GetKeyDown(KeyCode.Equals)) Settings.Separation.Value += 0.1f;
        else if (Input.GetKeyDown(KeyCode.LeftBracket)) Settings.ConvergenceOffset.Value -= 1f;
        else if (Input.GetKeyDown(KeyCode.RightBracket)) Settings.ConvergenceOffset.Value += 1f;
        else if (Input.GetKeyDown(KeyCode.F11) && Ctrl)
            Settings.VrMode.Value = Settings.VrMode.Value == "Window" ? "Scene" : "Window";
        else if (Input.GetKeyDown(KeyCode.F11) && Shift) overlay.Recenter();
        else if (Input.GetKeyDown(KeyCode.F11)) Settings.OverlayEnabled.Value = !Settings.OverlayEnabled.Value;
        else changed = false;
        if (!changed) return;
        Settings.Save();
        Log.Info(Describe());
    }

    private static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    private static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private static string Describe() =>
        $"stereo={Settings.StereoEnabled.Value} sep={Settings.Separation.Value:F2} conv+={Settings.ConvergenceOffset.Value:F1} swap={Settings.SwapEyes.Value} scale={Settings.EyeResolutionScale.Value:F2} overlay={Settings.OverlayEnabled.Value} mode={Settings.VrMode.Value} vr={Settings.OverlayWidth.Value:F2}m@{Settings.OverlayDistance.Value:F2}m blank={Settings.BlankWindowInVR.Value}";

    // Unity doesn't send OnPreCull/OnPostRender to Il2CppInterop-injected components (verified:
    // EyeHook never fired), so use the global camera callbacks instead.
    private Camera.CameraCallback preCullCb, postRenderCb;

    private void HookCallbacks()
    {
        if (preCullCb != null) return;
        preCullCb = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(OnPreCull));
        postRenderCb = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(OnPostRender));
        Camera.onPreCull = Il2CppSystem.Delegate.Combine(Camera.onPreCull, preCullCb).Cast<Camera.CameraCallback>();
        Camera.onPostRender = Il2CppSystem.Delegate.Combine(Camera.onPostRender, postRenderCb).Cast<Camera.CameraCallback>();
    }

    private void UnhookCallbacks()
    {
        if (preCullCb == null) return;
        var pre = Il2CppSystem.Delegate.Remove(Camera.onPreCull, preCullCb);
        Camera.onPreCull = pre == null ? null : pre.Cast<Camera.CameraCallback>();
        var post = Il2CppSystem.Delegate.Remove(Camera.onPostRender, postRenderCb);
        Camera.onPostRender = post == null ? null : post.Cast<Camera.CameraCallback>();
        preCullCb = postRenderCb = null;
    }

    private void Activate(Camera cam)
    {
        Cam = cam;
        UnhookCallbacks();
        EnsureTextures();
        cam.targetTexture = LeftEye;
        active = true;
        Log.Info($"Stereo ON for camera '{cam.name}' ({LeftEye.width}x{LeftEye.height} per eye). {Describe()}");
    }

    private void Deactivate()
    {
        if (!active) return;
        active = false;
        if (Cam != null)
        {
            Cam.targetTexture = null;
            Cam.ResetWorldToCameraMatrix();
            Cam.ResetProjectionMatrix();
        }
        Cam = null;
        Log.Info("Stereo OFF");
    }

    public void Shutdown()
    {
        overlay.Stop();
        Deactivate();
        UnhookCallbacks();
        if (LeftEye != null) { LeftEye.Release(); RightEye.Release(); LeftEye = RightEye = null; }
    }

    private void EnsureTextures()
    {
        var scale = Mathf.Clamp(Settings.EyeResolutionScale.Value, 0.25f, 2f);
        var w = Mathf.Max(64, (int)(Screen.width * scale));
        var h = Mathf.Max(64, (int)(Screen.height * scale));
        if (LeftEye != null && LeftEye.width == w && LeftEye.height == h) return;
        if (LeftEye != null) { LeftEye.Release(); RightEye.Release(); }
        LeftEye = MakeEye("OriVR_LeftEye", w, h);
        RightEye = MakeEye("OriVR_RightEye", w, h);
        if (active && Cam != null) Cam.targetTexture = LeftEye;
    }

    private static RenderTexture MakeEye(string name, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default) { name = name };
        rt.Create();
        return rt;
    }

    private readonly FocusDepth focus = new();
    private bool loggedMatrices;

    /// Unity never invokes camera callbacks for us in this game (OnPreCull on injected
    /// components and Camera.onPreCull both stay silent), so stereo is asymmetric:
    /// left eye = the game's own, unmodified render; right eye = a second render offset by the
    /// full separation, set up directly before cam.Render(). Off-axis projection keeps points at
    /// the convergence distance in the same place in both images.
    private void RenderRightEye()
    {
        // Separation and screen-plane depth were tuned for gameplay framing (subject ~29 units
        // away). Scale both with the current subject distance so zooms, cutscenes and the main
        // menu keep the same on-screen disparity instead of exploding.
        focus.Update(Cam);
        var k = focus.Value / FocusDepth.Reference;
        var sep = Settings.Separation.Value * k;
        var convergence = Mathf.Max(0.5f, (FocusDepth.Reference + Settings.ConvergenceOffset.Value) * k);
        var view = Cam.worldToCameraMatrix;
        var proj = Cam.projectionMatrix;
        if (!loggedMatrices)
        {
            loggedMatrices = true;
            var autoView = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Cam.transform.worldToLocalMatrix;
            var autoProj = Matrix4x4.Perspective(Cam.fieldOfView, Cam.aspect, Cam.nearClipPlane, Cam.farClipPlane);
            Log.Info($"MATRICES game view:\n{view}\nauto view:\n{autoView}\ngame proj:\n{proj}\nauto proj:\n{autoProj}");
        }
        // Eye shifted by sep along camera right == shift view-space x by -sep.
        view.m03 -= sep;
        // ndc_x of a point at distance C gains m00*sep/C; cancel it so C is the screen plane.
        proj.m02 -= proj.m00 * sep / convergence;
        Cam.worldToCameraMatrix = view;
        Cam.projectionMatrix = proj;
        Cam.targetTexture = RightEye;
        try
        {
            Cam.Render();
        }
        finally
        {
            Cam.ResetWorldToCameraMatrix();
            Cam.ResetProjectionMatrix();
            Cam.targetTexture = LeftEye;
        }
    }

    public void OnPreCull(Camera cam) { }
    public void OnPostRender(Camera cam) { }

    private bool wasPaused;

    /// Ori freezes time for its pause/exit menus.
    private static bool Paused => Time.timeScale < 0.01f;

    public void EndOfFrame()
    {
        if (!active || Cam == null) return;
        var paused = Paused;
        if (paused != wasPaused)
        {
            wasPaused = paused;
            Log.Info($"{(paused ? "Paused" : "Unpaused")} (timeScale={Time.timeScale}) cam pos={Cam.transform.position} fov={Cam.fieldOfView:F1}");
        }
        var mono = paused && Settings.MonoWhenPaused.Value;
        try
        {
            if (!mono) RenderRightEye();
        }
        catch (Exception ex)
        {
            Log.Error($"Right-eye render failed, disabling stereo: {ex}");
            Settings.StereoEnabled.Value = false;
        }
        if (CaptureRequested)
        {
            CaptureRequested = false;
            Capture();
        }
        var swap = Settings.SwapEyes.Value;
        if (overlay.Running)
        {
            if (mono) overlay.Submit(LeftEye, LeftEye);
            else overlay.Submit(swap ? RightEye : LeftEye, swap ? LeftEye : RightEye);
            // The 3D image is in the headset; the flat window shows a normal image, or black.
            if (Settings.BlankWindowInVR.Value) DrawBlack();
            else DrawMono();
        }
        else
        {
            DrawSideBySide(mono);
        }
    }

    private static void DrawBlack()
    {
        RenderTexture.active = null;
        GL.Clear(true, true, Color.black);
    }

    private void DrawMono()
    {
        float w = Screen.width, h = Screen.height;
        RenderTexture.active = null;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, w, 0, h);
        Graphics.DrawTexture(new Rect(0, 0, w, h), LeftEye);
        GL.PopMatrix();
    }

    private void Capture()
    {
        try
        {
            captureCount++;
            foreach (var (rt, eye) in new[] { (LeftEye, "left"), (RightEye, "right") })
            {
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                byte[] png = ImageConversion.EncodeToPNG(tex);
                File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, $"orivr-capture-{captureCount:D2}-{eye}.png"), png);
                Object.Destroy(tex);
            }
            Log.Info($"Captured eyes #{captureCount} ({Describe()})");
        }
        catch (Exception ex)
        {
            Log.Error($"Capture failed: {ex}");
        }
    }

    private void DrawSideBySide(bool mono)
    {
        var swap = Settings.SwapEyes.Value && !mono;
        var leftImg = swap ? RightEye : LeftEye;
        var rightImg = mono ? LeftEye : swap ? LeftEye : RightEye;
        float w = Screen.width, h = Screen.height;
        RenderTexture.active = null;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, w, 0, h); // y-up; (0,w,h,0) came out upside down
        Graphics.DrawTexture(new Rect(0, 0, w / 2, h), leftImg);
        Graphics.DrawTexture(new Rect(w / 2, 0, w / 2, h), rightImg);
        GL.PopMatrix();
    }
}
