using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OriVR;

/// Estimates how far the "subject" of the shot is from the camera, so stereo can scale with
/// camera zooms and with scenes that aren't framed like gameplay (main menu, cutscenes).
/// Uses Ori (the `character` layer) when visible; otherwise the median depth of visible renderers.
public class FocusDepth
{
    /// Gameplay framing the user-tuned Separation/ConvergenceOffset were chosen at.
    public const float Reference = 29f;

    private Renderer character;
    private float nextSearch;
    private float sceneMedian = Reference;
    private float smoothed = Reference;
    private int characterLayer = -2;
    private float lastLogged;
    private string lastSource = "";

    public float Value => smoothed;

    public void Update(Camera cam)
    {
        if (characterLayer == -2) characterLayer = LayerMask.NameToLayer("character");
        if (Time.unscaledTime >= nextSearch)
        {
            nextSearch = Time.unscaledTime + 1f;
            Search(cam);
        }

        float target;
        string source;
        if (character != null && character.enabled && character.isVisible)
        {
            target = cam.WorldToViewportPoint(character.bounds.center).z;
            source = "ori";
        }
        else
        {
            character = null;
            target = sceneMedian;
            source = "scene";
        }
        target = Mathf.Clamp(target, 1f, 500f);
        // Follow zooms quickly but don't jitter.
        smoothed = Mathf.Lerp(smoothed, target, Mathf.Clamp01(Time.unscaledDeltaTime * 6f));

        if (source != lastSource || Mathf.Abs(smoothed - lastLogged) > lastLogged * 0.25f)
        {
            Log.Info($"Focus depth {smoothed:F1} ({source})");
            lastLogged = smoothed;
            lastSource = source;
        }
    }

    private void Search(Camera cam)
    {
        var all = Object.FindObjectsOfType(Il2CppType.Of<Renderer>());
        var depths = new List<float>(all.Length);
        foreach (var o in all)
        {
            var r = o.Cast<Renderer>();
            if (!r.enabled || !r.isVisible) continue;
            if (character == null && r.gameObject.layer == characterLayer) character = r;
            var z = cam.WorldToViewportPoint(r.bounds.center).z;
            if (z > 1f) depths.Add(z);
        }
        if (depths.Count == 0) return;
        depths.Sort();
        sceneMedian = depths[depths.Count / 2];
    }
}
