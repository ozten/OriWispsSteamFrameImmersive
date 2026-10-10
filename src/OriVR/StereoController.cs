using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace OriVR;

/// Renders Ori's gameplay camera once per eye and shows the pair side by side in the window.
///
/// Left eye: the game's own render of the main camera, redirected into leftEye (see EyeHook).
/// Right eye: a second cam.Render() at end of frame into rightEye. Both use the same camera, so
/// all of Ori's post-processing components run for each eye.
/// Eyes are offset via the view matrix (the camera transform is never moved) and use an
/// off-axis projection so the two frusta converge on the playfield plane.
public class StereoController : MonoBehaviour
{
    public StereoController(IntPtr ptr) : base(ptr) { }

    public static StereoController Instance;

    public Camera Cam { get; private set; }
    public RenderTexture LeftEye, RightEye;
    /// -1 = left, +1 = right; read by EyeHook during the camera's callbacks.
    public int CurrentEye = -1;

    private float nextConfigCheck;
    private bool active;

    private void Awake()
    {
        Instance = this;
        StartCoroutine(EndOfFrameLoop().WrapToIl2Cpp());
    }

    private void Update()
    {
        HandleKeys();
        if (Time.unscaledTime >= nextConfigCheck)
        {
            nextConfigCheck = Time.unscaledTime + 1f;
            if (Settings.ReloadIfChanged())
                Plugin.Logger.LogInfo($"Config reloaded: {Describe()}");
        }

        var main = Camera.main;
        var want = Settings.StereoEnabled.Value && main != null && !main.orthographic;
        if (main != Cam || !want) Deactivate();
        if (want && !active) Activate(main);
        if (active) EnsureTextures();
    }

    private void HandleKeys()
    {
        var changed = true;
        if (Input.GetKeyDown(KeyCode.F9)) Settings.StereoEnabled.Value = !Settings.StereoEnabled.Value;
        else if (Input.GetKeyDown(KeyCode.F10)) Settings.SwapEyes.Value = !Settings.SwapEyes.Value;
        else if (Input.GetKeyDown(KeyCode.Minus)) Settings.Separation.Value = Mathf.Max(0f, Settings.Separation.Value - 0.1f);
        else if (Input.GetKeyDown(KeyCode.Equals)) Settings.Separation.Value += 0.1f;
        else if (Input.GetKeyDown(KeyCode.LeftBracket)) Settings.ConvergenceOffset.Value -= 1f;
        else if (Input.GetKeyDown(KeyCode.RightBracket)) Settings.ConvergenceOffset.Value += 1f;
        else changed = false;
        if (!changed) return;
        Settings.Save();
        Plugin.Logger.LogInfo(Describe());
    }

    private static string Describe() =>
        $"stereo={Settings.StereoEnabled.Value} sep={Settings.Separation.Value:F2} conv+={Settings.ConvergenceOffset.Value:F1} swap={Settings.SwapEyes.Value} scale={Settings.EyeResolutionScale.Value:F2}";

    private void Activate(Camera cam)
    {
        Cam = cam;
        if (cam.gameObject.GetComponent<EyeHook>() == null) cam.gameObject.AddComponent<EyeHook>();
        EnsureTextures();
        cam.targetTexture = LeftEye;
        active = true;
        Plugin.Logger.LogInfo($"Stereo ON for camera '{cam.name}' ({LeftEye.width}x{LeftEye.height} per eye). {Describe()}");
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
        Plugin.Logger.LogInfo("Stereo OFF");
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

    /// Called by EyeHook.OnPreCull: apply the current eye's view and projection.
    public void ApplyEye(Camera cam)
    {
        var e = CurrentEye * Settings.Separation.Value * 0.5f;
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

    private IEnumerator EndOfFrameLoop()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            if (!active || Cam == null) continue;
            try
            {
                // Right eye: second render of the same camera.
                CurrentEye = 1;
                Cam.targetTexture = RightEye;
                Cam.Render();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Right-eye render failed, disabling stereo: {ex}");
                Settings.StereoEnabled.Value = false;
            }
            finally
            {
                CurrentEye = -1;
                if (Cam != null) Cam.targetTexture = LeftEye;
            }
            DrawSideBySide();
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
        GL.LoadPixelMatrix(0, w, h, 0);
        Graphics.DrawTexture(new Rect(0, 0, w / 2, h), leftImg);
        Graphics.DrawTexture(new Rect(w / 2, 0, w / 2, h), rightImg);
        GL.PopMatrix();
    }
}
