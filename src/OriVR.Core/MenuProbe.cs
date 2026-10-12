using System;
using System.IO;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OriVR;

/// Diagnostics for reverse engineering Ori's options menu: creating BepInEx/orivr-dumpui writes the
/// options screen's object tree (components, menu actions, label text) to orivr-ui-NN.txt.
public static class MenuProbe
{
    private static float nextCheck;
    private static int count;
    private static string Trigger => Path.Combine(Paths.BepInExRootPath, "orivr-dumpui");

    public static void Update()
    {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 1f;
        if (!File.Exists(Trigger)) return;
        File.Delete(Trigger);
        try
        {
            var sb = new StringBuilder();
            var screens = Resources.FindObjectsOfTypeAll(Il2CppType.Of<OptionsScreen>());
            sb.AppendLine($"# OptionsScreen.Instance={(OptionsScreen.Instance != null)} found={screens.Length} settings={(SettingsScreen.Instance != null)}");
            foreach (var o in screens) Walk(sb, o.Cast<OptionsScreen>().transform, 0);
            var path = Path.Combine(Paths.BepInExRootPath, $"orivr-ui-{++count:D2}.txt");
            File.WriteAllText(path, sb.ToString());
            Log.Info($"Menu probe -> {path}");
        }
        catch (Exception e) { Log.Error($"Menu probe failed: {e}"); }
    }

    private static void Walk(StringBuilder sb, Transform t, int depth)
    {
        var pad = new string(' ', depth * 2);
        var go = t.gameObject;
        sb.AppendLine($"{pad}{t.name}{(go.activeSelf ? "" : " (inactive)")} pos={t.localPosition}");
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            var name = c.GetIl2CppType().FullName;
            var extra = "";
            try
            {
                if (c.TryCast<MessageBox>() is { } mb)
                    extra = $" override='{mb.OverrideText}' provider={mb.MessageProvider?.name} msg='{Safe(() => mb.CurrentMessage)}'";
                else if (c.TryCast<CleverMenuItem>() is { } mi)
                    extra = $" pressed={Type(mi.Pressed)} highlight={Type(mi.Highlight)} activated={Type(mi.Activated)} visible={Type(mi.Visible)}";
                else if (c.TryCast<ToggleCleverMenuItemLeftRight>() is { } lr)
                    extra = $" left={Type(lr.LeftAction)} right={Type(lr.RightAction)}";
                else if (c.TryCast<CleverMenuItemSelectionManager>() is { } sm)
                    extra = $" items={sm.m_menuItems?.Count}";
            }
            catch (Exception e) { extra = $" !{e.GetType().Name}"; }
            sb.AppendLine($"{pad}  [{name}]{extra}");
        }
        for (var i = 0; i < t.childCount; i++) Walk(sb, t.GetChild(i), depth + 1);
    }

    private static string Type(Il2CppSystem.Object o) =>
        o == null ? "-" : $"{o.GetIl2CppType().Name}@{o.TryCast<Component>()?.gameObject.name}";

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return "?"; }
    }
}
