using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OriVR;

/// Diagnostics: dumps every camera and the depth spread of visible renderers, so we can
/// see how Ori's scene is layered. Runs on F8 and whenever the set of cameras changes.
public class Probe : MonoBehaviour
{
    public Probe(IntPtr ptr) : base(ptr) { }

    private string lastCameraSet = "";
    private float nextCheck;
    private int dumpCount;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
            Dump("F8");

        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 2f;
        var set = string.Join(",", Camera.allCameras.Select(c => c.name).OrderBy(n => n));
        if (set != lastCameraSet)
        {
            lastCameraSet = set;
            Dump("camera set changed");
        }
    }

    private void Dump(string reason)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# OriVR probe #{++dumpCount} ({reason}) t={Time.unscaledTime:F1}s screen={Screen.width}x{Screen.height}");
            var cams = Camera.allCameras.OrderBy(c => c.depth).ToList();
            sb.AppendLine($"## {cams.Count} active cameras (Camera.main = {Camera.main?.name ?? "null"})");
            foreach (var c in cams) DescribeCamera(sb, c);

            var main = Camera.main ?? cams.LastOrDefault(c => c.targetTexture == null);
            if (main != null) DescribeDepth(sb, main);

            var path = Path.Combine(Paths.BepInExRootPath, $"orivr-probe-{dumpCount:D2}.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"Probe #{dumpCount} ({reason}): {cams.Count} cameras -> {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Probe failed: {e}");
        }
    }

    private static void DescribeCamera(StringBuilder sb, Camera c)
    {
        var t = c.transform;
        sb.AppendLine($"- {PathOf(t)}");
        sb.AppendLine($"    depth={c.depth} ortho={c.orthographic} fov={c.fieldOfView:F2} orthoSize={c.orthographicSize:F2} near={c.nearClipPlane} far={c.farClipPlane}");
        sb.AppendLine($"    pos={t.position} rot={t.eulerAngles} clear={c.clearFlags} rect={c.rect} hdr={c.allowHDR}");
        sb.AppendLine($"    target={(c.targetTexture == null ? "screen" : $"{c.targetTexture.name} {c.targetTexture.width}x{c.targetTexture.height}")} stereoTarget={c.stereoTargetEye}");
        sb.AppendLine($"    cullingMask={LayerNames(c.cullingMask)}");
        var comps = t.gameObject.GetComponents<Component>();
        sb.AppendLine($"    components={string.Join(", ", comps.Select(x => x.GetIl2CppType().Name))}");
    }

    private static void DescribeDepth(StringBuilder sb, Camera cam)
    {
        var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>());
        var depths = new List<(float z, string layer, int queue, string name)>();
        foreach (var o in all)
        {
            var r = o.Cast<Renderer>();
            if (!r.enabled || !r.isVisible) continue;
            var z = cam.WorldToViewportPoint(r.bounds.center).z;
            var queue = r.sharedMaterial != null ? r.sharedMaterial.renderQueue : -1;
            depths.Add((z, LayerMask.LayerToName(r.gameObject.layer), queue, r.name));
        }
        sb.AppendLine($"## Visible renderers relative to '{cam.name}': {depths.Count} of {all.Length}");
        if (depths.Count == 0) return;
        var sorted = depths.OrderBy(d => d.z).ToList();
        sb.AppendLine($"    z min={sorted.First().z:F2} median={sorted[sorted.Count / 2].z:F2} max={sorted.Last().z:F2}");
        sb.AppendLine("### depth histogram (view-space distance)");
        foreach (var g in sorted.GroupBy(d => Bucket(d.z)))
            sb.AppendLine($"    {g.Key,-12} {g.Count(),5}  e.g. {string.Join(", ", g.Take(3).Select(d => d.name))}");
        sb.AppendLine("### by layer");
        foreach (var g in depths.GroupBy(d => d.layer).OrderByDescending(g => g.Count()))
            sb.AppendLine($"    {g.Key,-24} {g.Count(),5}  z {g.Min(d => d.z):F1}..{g.Max(d => d.z):F1}");
        sb.AppendLine("### by render queue (>=3000 = transparent)");
        foreach (var g in depths.GroupBy(d => d.queue).OrderBy(g => g.Key))
            sb.AppendLine($"    {g.Key,6} {g.Count(),5}");
    }

    private static string Bucket(float z) => z switch
    {
        < 0 => "behind",
        < 5 => "0-5",
        < 10 => "5-10",
        < 20 => "10-20",
        < 40 => "20-40",
        < 80 => "40-80",
        < 160 => "80-160",
        < 500 => "160-500",
        _ => "500+",
    };

    private static string LayerNames(int mask)
    {
        if (mask == -1) return "Everything";
        var names = new List<string>();
        for (var i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0)
                names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
        return string.Join("|", names);
    }

    private static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (; t != null; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
