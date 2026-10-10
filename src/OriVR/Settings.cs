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

    private static ConfigFile config;
    private static System.DateTime lastWrite;

    public static void Bind(ConfigFile cfg)
    {
        config = cfg;
        StereoEnabled = cfg.Bind("Stereo", "Enabled", false, "Render side-by-side stereo. Toggle in game with F9.");
        Separation = cfg.Bind("Stereo", "Separation", 1.0f,
            "Distance between the eyes in world units. Ori's playfield is about 29 units from the camera. Keys: - / =");
        ConvergenceOffset = cfg.Bind("Stereo", "ConvergenceOffset", 0f,
            "Zero-parallax depth relative to the playfield (z=0 plane). Positive pushes the screen plane further away. Keys: [ / ]");
        SwapEyes = cfg.Bind("Stereo", "SwapEyes", false, "Swap left/right images (cross-eyed viewing). Key: F10");
        EyeResolutionScale = cfg.Bind("Stereo", "EyeResolutionScale", 1.0f, "Per-eye render size relative to the game window.");
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
