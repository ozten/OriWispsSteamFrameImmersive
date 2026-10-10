using System;
using System.IO;
using BepInEx;
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

    /// -1 = left, +1 = right; which eye the camera is currently rendering.
    private int currentEye = -1;
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
        if (wantOverlay && !overlay.Running && !overlayFailed) overlayFailed = !overlay.Start(pluginDir);
        if (!wantOverlay)
        {
            overlay.Stop();
            overlayFailed = false;
        }
    }

    private bool overlayFailed;

    private void HandleKeys()
    {
        var changed = true;
        if (Input.GetKeyDown(KeyCode.F9)) Settings.StereoEnabled.Value = !Settings.StereoEnabled.Value;
        else if (Input.GetKeyDown(KeyCode.F10)) Settings.SwapEyes.Value = !Settings.SwapEyes.Value;
        else if (Input.GetKeyDown(KeyCode.Minus)) Settings.Separation.Value = Mathf.Max(0f, Settings.Separation.Value - 0.1f);
        else if (Input.GetKeyDown(KeyCode.Equals)) Settings.Separation.Value += 0.1f;
        else if (Input.GetKeyDown(KeyCode.LeftBracket)) Settings.ConvergenceOffset.Value -= 1f;
        else if (Input.GetKeyDown(KeyCode.RightBracket)) Settings.ConvergenceOffset.Value += 1f;
        else if (Input.GetKeyDown(KeyCode.F11) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) overlay.Recenter();
        else if (Input.GetKeyDown(KeyCode.F11)) Settings.OverlayEnabled.Value = !Settings.OverlayEnabled.Value;
        else changed = false;
        if (!changed) return;
        Settings.Save();
        Log.Info(Describe());
    }

    private static string Describe() =>
        $"stereo={Settings.StereoEnabled.Value} sep={Settings.Separation.Value:F2} conv+={Settings.ConvergenceOffset.Value:F1} swap={Settings.SwapEyes.Value} scale={Settings.EyeResolutionScale.Value:F2} overlay={Settings.OverlayEnabled.Value}";

    private void Activate(Camera cam)
    {
        Cam = cam;
        if (cam.gameObject.GetComponent<EyeHook>() == null) cam.gameObject.AddComponent<EyeHook>();
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

    /// Distance from the camera to the zero-parallax plane: the playfield (world z=0) plus offset.
    private float Convergence() =>
        Mathf.Max(1f, Mathf.Abs(Cam.transform.position.z) + Settings.ConvergenceOffset.Value);

    public void OnPreCull(Camera cam)
    {
        if (!active || cam != Cam) return;
        var e = currentEye * Settings.Separation.Value * 0.5f;
        cam.ResetWorldToCameraMatrix();
        cam.ResetProjectionMatrix();
        var view = cam.worldToCameraMatrix;
        var proj = cam.projectionMatrix;
        // Eye shifted by e along camera right == shift view-space x by -e.
        view.m03 -= e;
        // Off-axis: shift the image so points at the convergence distance land in the same place
        // for both eyes (ndc_x gains m00*e/C).
        proj.m02 -= proj.m00 * e / Convergence();
        cam.worldToCameraMatrix = view;
        cam.projectionMatrix = proj;
    }

    public void OnPostRender(Camera cam)
    {
        if (cam == null) return;
        // Restore so game code reading the camera between frames sees normal matrices.
        cam.ResetWorldToCameraMatrix();
        cam.ResetProjectionMatrix();
    }

    public void EndOfFrame()
    {
        if (!active || Cam == null) return;
        try
        {
            // Right eye: second render of the same camera.
            currentEye = 1;
            Cam.targetTexture = RightEye;
            Cam.Render();
        }
        catch (Exception ex)
        {
            Log.Error($"Right-eye render failed, disabling stereo: {ex}");
            Settings.StereoEnabled.Value = false;
        }
        finally
        {
            currentEye = -1;
            if (Cam != null) Cam.targetTexture = LeftEye;
        }
        if (CaptureRequested)
        {
            CaptureRequested = false;
            Capture();
        }
        var swap = Settings.SwapEyes.Value;
        if (overlay.Running)
        {
            overlay.Submit(swap ? RightEye : LeftEye, swap ? LeftEye : RightEye);
            DrawMono(); // the 3D image is in the headset; keep the flat window looking normal
        }
        else
        {
            DrawSideBySide();
        }
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

    private void DrawSideBySide()
    {
        var swap = Settings.SwapEyes.Value;
        var leftImg = swap ? RightEye : LeftEye;
        var rightImg = swap ? LeftEye : RightEye;
        float w = Screen.width, h = Screen.height;
        RenderTexture.active = null;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, w, 0, h); // y-up; (0,w,h,0) came out upside down
        Graphics.DrawTexture(new Rect(0, 0, w / 2, h), leftImg);
        Graphics.DrawTexture(new Rect(w / 2, 0, w / 2, h), rightImg);
        GL.PopMatrix();
    }
}
