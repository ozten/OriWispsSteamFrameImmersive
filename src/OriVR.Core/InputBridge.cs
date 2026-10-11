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
            // Ori also reads the pad through the XInput wrapper on its own polling thread (the map
            // zoom uses it). In Scene mode Steam's emulated pad is left frozen in whatever state
            // it had, so replace that state wholesale with ours.
            harmony.Patch(AccessTools.Method(typeof(J2i.Net.XInputWrapper.XInput), nameof(J2i.Net.XInputWrapper.XInput.XInputGetState)),
                postfix: new HarmonyMethod(typeof(InputBridge), nameof(XInputPostfix)));
            // Ori picks button labels and map-cursor behaviour from its "last used device" flags.
            // Our input never touches Unity's joystick API, so it keeps thinking keyboard/mouse.
            foreach (var getter in new[] { nameof(PlayerInput.WasKeyboardUsedLast), nameof(PlayerInput.WasKeyboardKeyUsedLast), nameof(PlayerInput.IsUsingMouse) })
                harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerInput), getter),
                    postfix: new HarmonyMethod(typeof(InputBridge), nameof(NotKeyboardPostfix)));
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

        TrackLastDevice();
        if (Settings.InputLogRaw.Value)
        {
            LogXInput();
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

    private static readonly float[] lastGameAxis = new float[16];

    private static void AxisPostfix(XboxControllerInput.Axis axis, ref float __result)
    {
        if (!Active) return;
        // Diagnostics: what the game's own reader returns (before we add the VR pad), on change.
        var ai = (int)axis & 15;
        var q = MathF.Round(__result * 4f) / 4f;
        if (Settings.InputLogRaw.Value && q != lastGameAxis[ai])
        {
            lastGameAxis[ai] = q;
            Log.Info($"Game axis {axis} = {__result:F2} (VR {VrAxis(axis):F2})");
        }
        var v = VrAxis(axis);
        if (MathF.Abs(v) > MathF.Abs(__result)) __result = v;
    }

    private static void ButtonPostfix(XboxControllerInput.Button button, ref bool __result)
    {
        if (Active && !__result) __result = VrButton(button);
    }

    private static float lastVrInputTime = -100f, lastKeyboardTime = -100f;
    private static string lastDeviceState = "";

    /// Main thread, every frame: note whether the VR pad or the keyboard/mouse was used last.
    private static void TrackLastDevice()
    {
        var now = UnityEngine.Time.unscaledTime;
        var any = MathF.Abs(lx) > 0.3f || MathF.Abs(ly) > 0.3f || MathF.Abs(rx) > 0.3f || MathF.Abs(ry) > 0.3f || lt > 0.3f || rt > 0.3f;
        for (var i = 0; i < (int)B.Count && !any; i++) any = buttons[i];
        if (any) lastVrInputTime = now;
        if (UnityEngine.Input.anyKeyDown) lastKeyboardTime = now; // keyboard + mouse buttons only; the VR pad isn't a Unity device
        if (VrUsedLast)
        {
            var g = GameSettings.Instance;
            if (g != null && g.CurrentControlScheme != ControlScheme.Controller) g.SetControlScheme(ControlScheme.Controller);
        }
        if (!Settings.InputLogRaw.Value) return;
        var pi = PlayerInput.Instance;
        var gs = GameSettings.Instance;
        if (pi == null || gs == null) return;
        var st = $"keyboardLast={pi.WasKeyboardUsedLast} keyLast={pi.WasKeyboardKeyUsedLast} mouse={pi.IsUsingMouse} scheme={gs.CurrentControlScheme}";
        if (st == lastDeviceState) return;
        lastDeviceState = st;
        Log.Info($"Game input device: {st}");
    }

    private static bool VrUsedLast => Active && lastVrInputTime > lastKeyboardTime;

    private static void NotKeyboardPostfix(ref bool __result)
    {
        if (VrUsedLast) __result = false;
    }

    private static uint packet;
    private static volatile string xinputSeen = "";

    private static void XInputPostfix(int dwUserIndex, ref J2i.Net.XInputWrapper.XInputState pState, ref int __result)
    {
        if (!Active || dwUserIndex != 0) return;
        var g = pState.Gamepad;
        var seen = $"res={__result} btn=0x{g.wButtons:X} LT={g.bLeftTrigger} RT={g.bRightTrigger}";
        if (Settings.InputLogRaw.Value && seen != xinputSeen) xinputSeen = seen; // logged from main thread

        int w = 0;
        if (Btn(B.DUp)) w |= 0x0001; if (Btn(B.DDown)) w |= 0x0002; if (Btn(B.DLeft)) w |= 0x0004; if (Btn(B.DRight)) w |= 0x0008;
        if (Btn(B.Start)) w |= 0x0010; if (Btn(B.Back)) w |= 0x0020; if (Btn(B.LS)) w |= 0x0040; if (Btn(B.RS)) w |= 0x0080;
        if (Btn(B.LB)) w |= 0x0100; if (Btn(B.RB)) w |= 0x0200;
        if (Btn(B.A)) w |= 0x1000; if (Btn(B.B)) w |= 0x2000; if (Btn(B.X)) w |= 0x4000; if (Btn(B.Y)) w |= 0x8000;
        g.wButtons = unchecked((short)w);
        g.bLeftTrigger = (byte)Math.Clamp((int)(lt * 255f), 0, 255);
        g.bRightTrigger = (byte)Math.Clamp((int)(rt * 255f), 0, 255);
        g.sThumbLX = Stick(lx); g.sThumbLY = Stick(ly);
        g.sThumbRX = Stick(rx); g.sThumbRY = Stick(ry);
        pState.Gamepad = g;
        pState.PacketNumber = (int)++packet;
        __result = 0; // ERROR_SUCCESS: a pad is connected
    }

    private static short Stick(float v) => (short)Math.Clamp((int)(v * 32767f), -32768, 32767);

    private static string lastXinputLogged = "";
    /// Main-thread log of what the XInput wrapper reported before we replaced it.
    public static void LogXInput()
    {
        var s = xinputSeen;
        if (s == lastXinputLogged) return;
        lastXinputLogged = s;
        Log.Info($"Game XInput (before bridge): {s}");
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
