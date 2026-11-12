using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NextGenIsland;

public sealed class AppSettings
{
        public bool HoverExpansionEnabled { get; set; } = true;
    public int HoverCollapseDelayMs { get; set; } = 500;
    public bool ClickPinEnabled { get; set; } = true;
    public bool ClipboardPaused { get; set; } = false;
    public bool StartWithWindows { get; set; }
    // Collapses the island to a thin line at the top edge while a full-screen app is in front.
    public bool ShrinkInFullscreen { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 5;
    public bool QuietMode { get; set; }
    public bool BatteryWarningsEnabled { get; set; } = true;
    public bool NetworkMonitoringEnabled { get; set; } = true;
    public string SelectedNetworkInterface { get; set; } = "";
    public List<string> DisabledCardIds { get; set; } = new();
    // One of: Black, Graphite, Acrylic, LiquidGlass. Missing in old settings files, so it defaults.
    public string Theme { get; set; } = "LiquidGlass";
    // "Notch" (hangs from the top edge) or "Pill" (floating).
    public string IslandShape { get; set; } = "Pill";
    // Fetches synced lyrics from lrclib.net (sends song title, artist and length).
    public bool ShowLyrics { get; set; } = true;
    // Weather tile (Widgets). Edit these in %LOCALAPPDATA%\NextGenIsland\settings.json to change the city.
    public string WeatherCity { get; set; } = "Hyderabad";
    public double WeatherLatitude { get; set; } = 17.385;
    public double WeatherLongitude { get; set; } = 78.4867;
}

public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (s.StartWithWindows)
            key.SetValue("NextGenIsland", "\"" + (Environment.ProcessPath ?? "") + "\"");
        else
            key.DeleteValue("NextGenIsland", false);
    }
}
