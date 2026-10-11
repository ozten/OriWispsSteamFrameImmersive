using System;
using HarmonyLib;
using SmartInput;
using Valve.VR;

namespace OriVR;

/// In Scene mode the Frame hands its controllers to us as VR controllers and stops emulating a
/// gamepad for the game. This feeds them back into Ori's own gamepad reader
/// (SmartInput.XboxControllerInput.GetAxis / GetButton) with Harmony postfixes, so the game sees a
/// normal pad. Real gamepad input still passes through; the larger value wins.
public static class InputBridge
{
    private static Harmony harmony;
    public static bool Active { get; private set; }

    // Latest VR controller state, written on the main thread in Poll().
    private static float lx, ly, rx, ry, lt, rt;
    private static ulong lButtons, rButtons;
    private static VRControllerState_t state;
    private static readonly uint StateSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VRControllerState_t>();
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

    /// Call every frame while we're the VR scene app; pass false otherwise.
    public static void Poll(bool enable)
    {
        Active = enable && harmony != null && OpenVR.System != null;
        if (!Active) return;
        var sys = OpenVR.System;
        ReadHand(sys, ETrackedControllerRole.LeftHand, out lx, out ly, out lt, out lButtons);
        ReadHand(sys, ETrackedControllerRole.RightHand, out rx, out ry, out rt, out rButtons);
        if (Settings.InputInvertY.Value) { ly = -ly; ry = -ry; }

        // Discovery log: which legacy buttons/axes the Frame's controllers actually report.
        var raw = $"L btn=0x{lButtons:X} stick=({Q(lx)},{Q(ly)}) trig={Q(lt)} | R btn=0x{rButtons:X} stick=({Q(rx)},{Q(ry)}) trig={Q(rt)}";
        if (raw != lastRaw)
        {
            lastRaw = raw;
            if (Settings.InputLogRaw.Value) Log.Info($"VR pad: {raw}");
        }
    }

    private static float Q(float v) => MathF.Round(v * 2f) / 2f;

    private static void ReadHand(CVRSystem sys, ETrackedControllerRole role, out float x, out float y, out float trig, out ulong buttons)
    {
        x = y = trig = 0f;
        buttons = 0;
        var idx = sys.GetTrackedDeviceIndexForControllerRole(role);
        if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;
        if (!sys.GetControllerState(idx, ref state, StateSize)) return;
        buttons = state.ulButtonPressed;
        x = state.rAxis0.x;
        y = state.rAxis0.y;
        trig = state.rAxis1.x;
    }

    private static bool Bit(ulong b, EVRButtonId id) => (b & (1UL << (int)id)) != 0;

    private static float VrAxis(XboxControllerInput.Axis axis) => axis switch
    {
        XboxControllerInput.Axis.LeftStickX => lx,
        XboxControllerInput.Axis.LeftStickY => ly,
        XboxControllerInput.Axis.RightStickX => rx,
        XboxControllerInput.Axis.RightStickY => ry,
        XboxControllerInput.Axis.LeftTrigger => lt,
        XboxControllerInput.Axis.RightTrigger => rt,
        _ => 0f,
    };

    // Guessed legacy mapping (Index-style): right A/B = A/B, left A/B = X/Y, grips = shoulders,
    // stick clicks = stick buttons. Refine from the discovery log.
    private static bool VrButton(XboxControllerInput.Button button) => button switch
    {
        XboxControllerInput.Button.ButtonA => Bit(rButtons, EVRButtonId.k_EButton_A),
        XboxControllerInput.Button.ButtonB => Bit(rButtons, EVRButtonId.k_EButton_ApplicationMenu),
        XboxControllerInput.Button.ButtonX => Bit(lButtons, EVRButtonId.k_EButton_A),
        XboxControllerInput.Button.ButtonY => Bit(lButtons, EVRButtonId.k_EButton_ApplicationMenu),
        XboxControllerInput.Button.LeftShoulder => Bit(lButtons, EVRButtonId.k_EButton_Grip),
        XboxControllerInput.Button.RightShoulder => Bit(rButtons, EVRButtonId.k_EButton_Grip),
        XboxControllerInput.Button.LeftStick => Bit(lButtons, EVRButtonId.k_EButton_Axis0),
        XboxControllerInput.Button.RightStick => Bit(rButtons, EVRButtonId.k_EButton_Axis0),
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
}
