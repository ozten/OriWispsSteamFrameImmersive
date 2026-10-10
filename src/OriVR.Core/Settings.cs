using System.IO;
using BepInEx.Configuration;

namespace OriVR;

/// Config in BepInEx/config/com.ozten.orivr.cfg. The file is re-read when it changes on disk,
/// so values can be tuned live (by hand or from a script) without restarting the game.
public static class Settings
{
    public static ConfigEntry<bool> StereoEnabled;
    public static ConfigEntry<float> Separation;
    public static ConfigEntry<float> ConvergenceOffset;
    public static ConfigEntry<bool> SwapEyes;
    public static ConfigEntry<float> EyeResolutionScale;
    public static ConfigEntry<bool> MonoWhenPaused;
    public static ConfigEntry<bool> OverlayEnabled;
    public static ConfigEntry<float> OverlayWidth;
    public static ConfigEntry<float> OverlayDistance;

    private static ConfigFile config;
    private static System.DateTime lastWrite;

    public static void Bind(ConfigFile cfg)
    {
        config = cfg;
        StereoEnabled = cfg.Bind("Stereo", "Enabled", false, "Render side-by-side stereo. Toggle in game with F9.");
        Separation = cfg.Bind("Stereo", "Separation", 0.8f,
            "Distance between the eyes in world units. Ori's playfield is about 29 units from the camera. Keys: - / =");
        ConvergenceOffset = cfg.Bind("Stereo", "ConvergenceOffset", -14f,
            "Zero-parallax depth relative to the playfield (z=0 plane). Positive pushes the screen plane further away. Keys: [ / ]");
        SwapEyes = cfg.Bind("Stereo", "SwapEyes", false, "Swap left/right images (cross-eyed viewing). Key: F10");
        MonoWhenPaused = cfg.Bind("Stereo", "MonoWhenPaused", true,
            "Show the same image to both eyes while the game is paused (menus sit close to the camera and don't fuse).");
        EyeResolutionScale = cfg.Bind("Stereo", "EyeResolutionScale", 1.0f, "Per-eye render size relative to the game window.");
        OverlayEnabled = cfg.Bind("VR", "OverlayEnabled", false,
            "Show the stereo pair in the headset as a floating SteamVR 3D window (needs Stereo.Enabled). Key: F11; Shift+F11 recenters.");
        OverlayWidth = cfg.Bind("VR", "OverlayWidth", 2.5f, "Width of the floating window in meters.");
        OverlayDistance = cfg.Bind("VR", "OverlayDistance", 2.5f, "Distance from your head in meters (applied on recenter).");
        lastWrite = File.GetLastWriteTimeUtc(cfg.ConfigFilePath);
    }

    /// Call periodically; reloads the config if it was edited externally.
    public static bool ReloadIfChanged()
    {
        var t = File.GetLastWriteTimeUtc(config.ConfigFilePath);
        if (t == lastWrite) return false;
        lastWrite = t;
        config.Reload();
        return true;
    }

    public static void Save()
    {
        config.Save();
        lastWrite = File.GetLastWriteTimeUtc(config.ConfigFilePath);
    }
}
