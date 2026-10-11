using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using SmartInput;
using Valve.VR;

namespace OriVR;

/// In Scene mode the Frame hands its controllers to us as VR controllers and stops emulating a
/// gamepad for the game. This reads them through SteamVR Input (an action manifest with a default
/// binding for the Frame controller, which is shaped like a split gamepad: A/B/X/Y/Menu on the
/// right, d-pad/View on the left) and feeds them into Ori's own gamepad reader
/// (SmartInput.XboxControllerInput.GetAxis / GetButton) with Harmony postfixes. Real gamepad input
/// still passes through; the larger value wins.
public static class InputBridge
{
    private static Harmony harmony;
    public static bool Active { get; private set; }

    private enum B { A, B, X, Y, LB, RB, LS, RS, Start, Back, DUp, DDown, DLeft, DRight, Count }
    private static readonly string[] ButtonNames =
        { "a", "b", "x", "y", "lb", "rb", "ls", "rs", "start", "back", "dpad_up", "dpad_down", "dpad_left", "dpad_right" };

    // SteamVR Input handles (valid while the current OpenVR session lasts).
    private static bool manifestSet;
    private static ulong actionSet, leftStick, rightStick, leftTrigger, rightTrigger;
    private static readonly ulong[] buttonHandles = new ulong[(int)B.Count];
    private static VRActiveActionSet_t[] activeSets;

    // Latest state, written on the main thread in Poll().
    private static float lx, ly, rx, ry, lt, rt;
    private static readonly bool[] buttons = new bool[(int)B.Count];
    private static string lastRaw = "";

    public static void Install()
    {
        if (harmony != null) return;
        try
        {
            harmony = new Harmony("com.ozten.orivr.input");
            var t = typeof(XboxControllerInput);
            harmony.Patch(AccessTools.Method(t, nameof(XboxControllerInput.GetAxis)),
                postfix: new HarmonyMethod(typeof(InputBridge), nameof(AxisPostfix)));
            harmony.Patch(AccessTools.Method(t, nameof(XboxControllerInput.GetButton)),
                postfix: new HarmonyMethod(typeof(InputBridge), nameof(ButtonPostfix)));
            Log.Info("Input bridge installed (VR controllers -> game pad).");
        }
        catch (Exception e)
        {
            Log.Error($"Input bridge install failed: {e}");
            harmony = null;
        }
    }

    public static void Uninstall()
    {
        Active = false;
        try { harmony?.UnpatchSelf(); }
        catch (Exception e) { Log.Error($"Input bridge uninstall: {e}"); }
        harmony = null;
    }

    /// Call right after VR_Init(Scene). Writes the action manifest + Frame binding next to the
    /// plugin and registers it (SteamVR wants it before the first input call of the session).
    public static void OnSessionStart(string pluginDir)
    {
        manifestSet = false;
        try
        {
            var dir = Path.Combine(pluginDir, "input");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bindings_frame_controller.json"), FrameBindings());
            var manifest = Path.Combine(dir, "actions.json");
            File.WriteAllText(manifest, Manifest());
            var input = OpenVR.Input;
            var err = input.SetActionManifestPath(Path.GetFullPath(manifest));
            if (err != EVRInputError.None) { Log.Error($"SetActionManifestPath: {err}"); return; }
            input.GetActionSetHandle("/actions/ori", ref actionSet);
            input.GetActionHandle("/actions/ori/in/left_stick", ref leftStick);
            input.GetActionHandle("/actions/ori/in/right_stick", ref rightStick);
            input.GetActionHandle("/actions/ori/in/lt", ref leftTrigger);
            input.GetActionHandle("/actions/ori/in/rt", ref rightTrigger);
            for (var i = 0; i < (int)B.Count; i++)
                input.GetActionHandle("/actions/ori/in/" + ButtonNames[i], ref buttonHandles[i]);
            activeSets = new[] { new VRActiveActionSet_t { ulActionSet = actionSet } };
            manifestSet = true;
            Log.Info($"SteamVR Input manifest registered ({manifest}).");
        }
        catch (Exception e) { Log.Error($"SteamVR Input setup failed: {e}"); }
    }

    /// Call every frame while we're the VR scene app; pass false otherwise.
    public static void Poll(bool enable)
    {
        Active = enable && harmony != null && manifestSet && OpenVR.Input != null;
        if (!Active) return;
        var input = OpenVR.Input;
        var err = input.UpdateActionState(activeSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
        if (err != EVRInputError.None) { Active = false; return; }
        ReadVec(input, leftStick, out lx, out ly);
        ReadVec(input, rightStick, out rx, out ry);
        lt = ReadVec(input, leftTrigger, out _, out _);
        rt = ReadVec(input, rightTrigger, out _, out _);
        if (Settings.InputInvertY.Value) { ly = -ly; ry = -ry; }
        var dd = new InputDigitalActionData_t();
        var dsize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
        for (var i = 0; i < (int)B.Count; i++)
            buttons[i] = input.GetDigitalActionData(buttonHandles[i], ref dd, dsize, OpenVR.k_ulInvalidInputValueHandle) == EVRInputError.None
                         && dd.bActive && dd.bState;

        if (Settings.InputLogRaw.Value)
        {
            var pressed = "";
            for (var i = 0; i < (int)B.Count; i++) if (buttons[i]) pressed += ButtonNames[i] + " ";
            var raw = $"L=({Q(lx)},{Q(ly)}) R=({Q(rx)},{Q(ry)}) LT={Q(lt)} RT={Q(rt)} [{pressed.Trim()}]";
            if (raw != lastRaw) { lastRaw = raw; Log.Info($"VR pad: {raw}"); }
        }
    }

    private static float ReadVec(CVRInput input, ulong action, out float x, out float y)
    {
        var ad = new InputAnalogActionData_t();
        x = y = 0f;
        if (input.GetAnalogActionData(action, ref ad, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !ad.bActive)
            return 0f;
        x = ad.x;
        y = ad.y;
        return ad.x;
    }

    private static float Q(float v) => MathF.Round(v * 2f) / 2f;
    private static bool Btn(B b) => buttons[(int)b];

    private static float VrAxis(XboxControllerInput.Axis axis) => axis switch
    {
        XboxControllerInput.Axis.LeftStickX => lx,
        XboxControllerInput.Axis.LeftStickY => ly,
        XboxControllerInput.Axis.RightStickX => rx,
        XboxControllerInput.Axis.RightStickY => ry,
        XboxControllerInput.Axis.LeftTrigger => lt,
        XboxControllerInput.Axis.RightTrigger => rt,
        XboxControllerInput.Axis.DpadX => (Btn(B.DRight) ? 1f : 0f) - (Btn(B.DLeft) ? 1f : 0f),
        XboxControllerInput.Axis.DpadY => (Btn(B.DUp) ? 1f : 0f) - (Btn(B.DDown) ? 1f : 0f),
        _ => 0f,
    };

    private static bool VrButton(XboxControllerInput.Button button) => button switch
    {
        XboxControllerInput.Button.ButtonA => Btn(B.A),
        XboxControllerInput.Button.ButtonB => Btn(B.B),
        XboxControllerInput.Button.ButtonX => Btn(B.X),
        XboxControllerInput.Button.ButtonY => Btn(B.Y),
        XboxControllerInput.Button.LeftShoulder => Btn(B.LB),
        XboxControllerInput.Button.RightShoulder => Btn(B.RB),
        XboxControllerInput.Button.LeftStick => Btn(B.LS),
        XboxControllerInput.Button.RightStick => Btn(B.RS),
        XboxControllerInput.Button.Start => Btn(B.Start),
        XboxControllerInput.Button.Select => Btn(B.Back),
        XboxControllerInput.Button.LeftTrigger => lt > 0.5f,
        XboxControllerInput.Button.RightTrigger => rt > 0.5f,
        _ => false,
    };

    private static void AxisPostfix(XboxControllerInput.Axis axis, ref float __result)
    {
        if (!Active) return;
        var v = VrAxis(axis);
        if (MathF.Abs(v) > MathF.Abs(__result)) __result = v;
    }

    private static void ButtonPostfix(XboxControllerInput.Button button, ref bool __result)
    {
        if (Active && !__result) __result = VrButton(button);
    }

    private static string Manifest()
    {
        var actions = "{ \"name\": \"/actions/ori/in/left_stick\", \"type\": \"vector2\" },\n" +
                      "{ \"name\": \"/actions/ori/in/right_stick\", \"type\": \"vector2\" },\n" +
                      "{ \"name\": \"/actions/ori/in/lt\", \"type\": \"vector1\" },\n" +
                      "{ \"name\": \"/actions/ori/in/rt\", \"type\": \"vector1\" }";
        foreach (var n in ButtonNames) actions += $",\n{{ \"name\": \"/actions/ori/in/{n}\", \"type\": \"boolean\" }}";
        return "{\n\"default_bindings\": [ { \"controller_type\": \"frame_controller\", \"binding_url\": \"bindings_frame_controller.json\" } ],\n" +
               "\"actions\": [\n" + actions + "\n],\n" +
               "\"action_sets\": [ { \"name\": \"/actions/ori\", \"usage\": \"single\" } ],\n" +
               "\"localization\": [ { \"language_tag\": \"en_US\", \"/actions/ori\": \"Ori gamepad\" } ]\n}\n";
    }

    private static string FrameBindings()
    {
        static string Click(string path, string action) =>
            $"{{ \"path\": \"{path}\", \"mode\": \"button\", \"inputs\": {{ \"click\": {{ \"output\": \"/actions/ori/in/{action}\" }} }} }}";
        static string Stick(string hand, string action, string clickAction) =>
            $"{{ \"path\": \"/user/hand/{hand}/input/thumbstick\", \"mode\": \"joystick\", \"inputs\": {{ \"position\": {{ \"output\": \"/actions/ori/in/{action}\" }}, \"click\": {{ \"output\": \"/actions/ori/in/{clickAction}\" }} }} }}";
        static string Trig(string hand, string action) =>
            $"{{ \"path\": \"/user/hand/{hand}/input/trigger\", \"mode\": \"trigger\", \"inputs\": {{ \"pull\": {{ \"output\": \"/actions/ori/in/{action}\" }} }} }}";
        var sources = string.Join(",\n", new[]
        {
            Stick("left", "left_stick", "ls"), Stick("right", "right_stick", "rs"),
            Trig("left", "lt"), Trig("right", "rt"),
            Click("/user/hand/right/input/a", "a"), Click("/user/hand/right/input/b", "b"),
            Click("/user/hand/right/input/x", "x"), Click("/user/hand/right/input/y", "y"),
            Click("/user/hand/right/input/menu", "start"), Click("/user/hand/left/input/view", "back"),
            Click("/user/hand/left/input/bumper", "lb"), Click("/user/hand/right/input/bumper", "rb"),
            Click("/user/hand/left/input/dpad_up", "dpad_up"), Click("/user/hand/left/input/dpad_down", "dpad_down"),
            Click("/user/hand/left/input/dpad_left", "dpad_left"), Click("/user/hand/left/input/dpad_right", "dpad_right"),
        });
        return "{\n\"action_manifest_version\": 0, \"category\": \"steamvr_input\", \"controller_type\": \"frame_controller\",\n" +
               "\"name\": \"OriVR gamepad\", \"description\": \"Frame controllers as an Xbox pad\",\n" +
               "\"bindings\": { \"/actions/ori\": { \"sources\": [\n" + sources + "\n] } }\n}\n";
    }
}
