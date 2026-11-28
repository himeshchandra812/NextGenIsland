using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NextGenIsland;

public sealed class AppSettings
{
        public bool HoverExpansionEnabled { get; set; } = true;
    public int HoverCollapseDelayMs { get; set; } = 500;
    // Wait before a hover opens the island (0 = instant).
    public int HoverOpenDelayMs { get; set; } = 0;
    // Slow, Normal or Fast: speed of the expand / collapse animation.
    public string AnimationSpeed { get; set; } = "Normal";
    // What the collapsed pill shows: CpuTemp, Clock or ClockCpu.
    public string PillContent { get; set; } = "CpuTemp";
    public bool Use24HourClock { get; set; } = true;
    public bool UseFahrenheit { get; set; } = false;
    // Gap between the top of the screen and the floating pill, in pixels (Pill shape only).
    public int PillTopGap { get; set; } = 8;
    // Island opacity in percent (100 = solid).
    public int IslandOpacity { get; set; } = 100;
    public bool ClickPinEnabled { get; set; } = true;
    public bool ClipboardPaused { get; set; } = false;
    public bool StartWithWindows { get; set; }
    // Set once the first-run welcome banner has been shown.
    public bool WelcomeShown { get; set; }
    // Shows other apps' notifications on the island (reads the Windows notification history; off until the user enables it).
    public bool ShowOtherAppNotifications { get; set; } = false;
    // Collapses the island to a thin line at the top edge while a full-screen app is in front.
    public bool ShrinkInFullscreen { get; set; } = true;
    // Also shrink to the thin line when an ordinary maximized window is in front (needs ShrinkInFullscreen).
    public bool ShrinkWhenMaximized { get; set; } = false;
    // Shows a slider on the island when the volume or brightness keys are pressed.
    public bool ShowKeyHud { get; set; } = true;
    // Voice commands (Ctrl+Alt+Space or the mic button). The microphone is only opened when you start one.
    public bool VoiceEnabled { get; set; } = true;
    // Hold Ctrl+Alt+Space while speaking and release to send. More reliable in noisy places than waiting for silence.
    public bool VoiceHoldToTalk { get; set; } = true;
    // Speech model size: "tiny", "base" or "small". Bigger is more accurate in noise but slower and a larger download.
    public string VoiceModel { get; set; } = "base";
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
        catch (Exception ex)
        {
            // A damaged settings file must never stop the app: keep a copy for inspection and start fresh.
            AppLog.Write("Settings file unreadable, using defaults", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write to a temp file and swap it in, so a crash or power cut cannot leave a half-written file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { AppLog.Write("Saving settings failed", ex); }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (s.StartWithWindows)
                key.SetValue("NextGenIsland", "\"" + (Environment.ProcessPath ?? "") + "\"");
            else
                key.DeleteValue("NextGenIsland", false);
        }
        catch (Exception ex) { AppLog.Write("Updating start-with-Windows failed", ex); }
    }
}
