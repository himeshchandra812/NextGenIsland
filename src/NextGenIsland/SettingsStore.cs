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
    public bool NotificationsEnabled { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 5;
    public bool QuietMode { get; set; }
    public bool BatteryWarningsEnabled { get; set; } = true;
    public bool NetworkMonitoringEnabled { get; set; } = true;
    public string SelectedNetworkInterface { get; set; } = "";
    public List<string> DisabledCardIds { get; set; } = new();
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
