using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OriVR;

/// Adds an "Immersive 3D" On/Off row to Ori's Options > Gameplay page, built by cloning the game's
/// own "Show Map Trail" row so it looks, navigates and sounds like the built-in toggles.
///
/// The options screen is a persistent (mostly inactive) object:
///   optionsScreen/*settings(gameplay)/highlightFade/pivot/mapTrail
/// A toggle row is a CleverMenuItem whose Pressed action is an ActionSequence with a
/// ToggleSettingsAction, plus text/nameText and text/stateText MessageBoxes. The clone drops the
/// game's action and calls us through CleverMenuItem.PressedCallback instead.
public static class ImmersiveMenu
{
    private const string RowName = "orivrImmersive";
    private const string Title = "Immersive 3D";
    private const string Help = "Play in 3D on your VR headset. Recenter with the headset's own recenter (Steam Frame: hold the Steam button).";

    private static GameObject row;
    private static MessageBox stateText;
    private static Il2CppSystem.Action pressedCb;
    private static float nextTry;
    private static bool lastShown, stateApplied;

    public static bool Enabled => Settings.StereoEnabled.Value;

    /// 3D on/off only. The game stays a VR app either way (off = same image to both eyes), since
    /// handing back to the Frame's flat screen pops SteamVR's dashboard and costs a VR re-init.
    public static void Toggle()
    {
        var on = !Enabled;
        Settings.StereoEnabled.Value = on;
        Settings.OverlayEnabled.Value = true;
        Settings.VrMode.Value = "Scene";
        Settings.Save();
        Log.Info($"Immersive 3D {(on ? "on" : "off")} (from the options menu).");
    }

    public static void Update()
    {
        if (row == null)
        {
            if (Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 2f;
            try { Inject(); }
            catch (Exception e) { Log.Error($"Options menu injection failed: {e}"); nextTry = float.MaxValue; }
            return;
        }
        // Keep the On/Off label in step with the setting (it can also change from the config file or keys).
        if (!stateApplied || lastShown != Enabled) RefreshState();
    }

    private static void Inject()
    {
        var screen = OptionsScreen.Instance;
        if (screen == null) return;
        var page = screen.transform.Find("*settings(gameplay)");
        var pivot = page?.Find("highlightFade/pivot");
        var template = pivot?.Find("mapTrail");
        if (template == null) { Log.Error("Options menu: mapTrail row not found; can't add Immersive 3D."); nextTry = float.MaxValue; return; }
        var existing = pivot.Find(RowName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject); // left over from a previous core

        stateApplied = false;
        row = Object.Instantiate(template.gameObject, pivot);
        row.name = RowName;
        var rowTf = row.transform;
        rowTf.localPosition = template.localPosition + new Vector3(0f, -0.4f, 0f);

        var item = row.GetComponent<CleverMenuItem>();
        item.Pressed = null;
        var pressed = rowTf.Find("pressed");
        if (pressed != null) Object.DestroyImmediate(pressed.gameObject);
        pressedCb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(Toggle));
        item.add_PressedCallback(pressedCb);

        var name = rowTf.Find("text/nameText")?.GetComponent<MessageBox>();
        if (name != null) name.SetMessageProvider(MakeProvider(Title));
        var tooltip = row.GetComponent<CleverMenuItemTooltip>();
        if (tooltip != null) tooltip.Tooltip = MakeProvider(Help);
        stateText = rowTf.Find("text/stateText")?.GetComponent<MessageBox>();

        var manager = page.GetComponent<CleverMenuItemSelectionManager>();
        var templateItem = template.GetComponent<CleverMenuItem>();
        var layout = page.GetComponent<CleverMenuItemLayout>();
        if (manager != null)
        {
            var at = manager.m_menuItems.IndexOf(templateItem);
            if (at >= 0) manager.m_menuItems.Insert(at + 1, item);
            else manager.AddMenuItem(item);
            item.SetSelectionManager(manager);
        }
        if (layout != null && layout.MenuItems != null)
        {
            var at = layout.MenuItems.IndexOf(templateItem);
            layout.MenuItems.Insert(at >= 0 ? at + 1 : layout.MenuItems.Count, item);
        }
        Log.Info($"Options menu: added '{Title}' to Gameplay (manager items={manager?.m_menuItems.Count} " +
                 $"navigation={manager?.Navigation?.Count} direction={manager?.ItemDirection} layout={layout?.MenuItems?.Count}).");
        RefreshState();
    }

    /// The cloned state text starts as the template's "[X] Yes" (with an X button icon); show the
    /// game's own On/Off instead. Retried until the texts manager exists.
    private static void RefreshState()
    {
        var texts = OptionsScreenTextsManager.Instance;
        if (stateText == null || texts == null) return;
        lastShown = Enabled;
        stateText.SetMessageProvider(lastShown ? texts.OnMessageProvider : texts.OffMessageProvider);
        var icon = stateText.transform.Find("buttonX(Clone)");
        if (icon != null) Object.Destroy(icon.gameObject);
        stateApplied = true;
    }

    /// A message provider with the same text in every language (the game's own are translated assets).
    private static MessageProvider MakeProvider(string text)
    {
        var p = ScriptableObject.CreateInstance<TranslatedMessageProvider>();
        p.hideFlags = HideFlags.DontUnloadUnusedAsset;
        p.Messages = new Il2CppSystem.Collections.Generic.List<TranslatedMessageProvider.MessageItem>();
        var m = new TranslatedMessageProvider.MessageItem();
        m.English = m.French = m.Italian = m.German = m.Spanish = m.Japanese = m.Portuguese = m.Chinese = m.Russian =
            m.TraditionalChinese = m.Czech = m.Danish = m.Dutch = m.Finnish = m.Hungarian = m.Korean = m.Norwegian =
            m.Polish = m.SpanishSpain = m.Swedish = m.Turkish = text;
        p.Messages.Add(m);
        return p;
    }

    /// Core unload: our callback lives in this assembly, so the row must go with it.
    public static void Remove()
    {
        if (row != null)
        {
            var item = row.GetComponent<CleverMenuItem>();
            var page = OptionsScreen.Instance?.transform.Find("*settings(gameplay)");
            page?.GetComponent<CleverMenuItemSelectionManager>()?.m_menuItems.Remove(item);
            page?.GetComponent<CleverMenuItemLayout>()?.MenuItems?.Remove(item);
            Object.DestroyImmediate(row);
        }
        row = null;
        stateText = null;
        stateApplied = false;
        pressedCb = null;
    }
}
