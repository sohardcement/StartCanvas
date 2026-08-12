namespace WinTileLauncher.Models;

public sealed class AppearanceSettings
{
    public int Version { get; set; } = 1;
    public string ThemeId { get; set; } = AppearanceDefaults.ThemeId;
    public string BackgroundMode { get; set; } = AppearanceDefaults.BackgroundMode;
    public string BackgroundColor { get; set; } = AppearanceDefaults.BackgroundColor;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public double BackgroundOverlayOpacity { get; set; } = AppearanceDefaults.BackgroundOverlayOpacity;
}

public static class AppearanceDefaults
{
    public const string ThemeId = "ocean";
    public const string BackgroundMode = "theme";
    public const string BackgroundColor = "#0A1A2E";
    public const double BackgroundOverlayOpacity = 0.6;
}
