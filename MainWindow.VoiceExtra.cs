using System.Diagnostics;
using NextGenIsland.Core;
using NextGenIsland.Services;

namespace NextGenIsland;

// Voice commands, part 2: reminders and timers, finding files, dark mode, Wi-Fi/Bluetooth, power mode and media control.
public partial class MainWindow
{
    private async Task<(string Message, bool Ok)> RunMoreVoiceActionAsync(VoiceAction a)
    {
        switch (a.Kind)
        {
            case VoiceActionKind.Timer:
            {
                AddVoiceReminder("Timer finished (" + DurationText(a.Value) + ")", a.Value);
                return ("Timer set for " + DurationText(a.Value), true);
            }
            case VoiceActionKind.Remind:
            {
                string text = string.IsNullOrWhiteSpace(a.Text) ? "Reminder" : Capitalize(a.Text);
                AddVoiceReminder(text, a.Value);
                return (a.Value > 0 ? "Reminder in " + DurationText(a.Value) : "Reminder added", true);
            }
            case VoiceActionKind.FindFile:
                return await FindAndOpenFileAsync(a.Text, "Couldn't find " + a.Text);

            case VoiceActionKind.DarkMode:
            {
                bool dark = a.Value == 2 ? !SystemTheme.IsDark() : a.Value == 1;
                return SystemTheme.SetDark(dark) ? (dark ? "Dark mode on" : "Light mode on", true) : ("Couldn't change the theme", false);
            }
            case VoiceActionKind.Wifi:
            case VoiceActionKind.Bluetooth:
            {
                bool wifi = a.Kind == VoiceActionKind.Wifi;
                string label = wifi ? "Wi-Fi" : "Bluetooth";
                var states = await _devices.GetRadiosAsync();
                bool present = wifi ? states.WifiPresent : states.BluetoothPresent;
                if (!present) return (label + " isn't available on this PC", false);
                bool current = wifi ? states.WifiOn : states.BluetoothOn;
                bool want = a.Value == 2 ? !current : a.Value == 1;
                bool ok = await _devices.SetRadioAsync(
                    wifi ? Windows.Devices.Radios.RadioKind.WiFi : Windows.Devices.Radios.RadioKind.Bluetooth, want);
                return ok ? (label + (want ? " on" : " off"), true) : ("Windows didn't allow changing " + label, false);
            }
            case VoiceActionKind.PowerMode:
            {
                var mode = a.Value switch { 2 => PowerMode.Performance, 1 => PowerMode.Balanced, _ => PowerMode.Efficiency };
                string name = mode switch { PowerMode.Performance => "Performance", PowerMode.Balanced => "Balanced", _ => "Battery saver" };
                return _devices.SetPowerMode(mode) ? (name + " mode", true) : ("Power mode isn't available on this PC", false);
            }
            case VoiceActionKind.MediaPlay:
            case VoiceActionKind.MediaPause:
            {
                var st = await _mediaService.RefreshMediaStateAsync();
                if (!st.HasMedia) return ("Nothing is playing", false);
                bool wantPlay = a.Kind == VoiceActionKind.MediaPlay;
                if (st.IsPlaying == wantPlay) return (wantPlay ? "Already playing" : "Already paused", true);
                await _mediaService.TogglePlayPauseAsync();
                return (wantPlay ? "Playing" : "Paused", true);
            }
            case VoiceActionKind.MediaNext:
                return await _mediaService.SkipNextAsync() ? ("Next track", true) : ("Nothing to skip", false);
            case VoiceActionKind.MediaPrevious:
                return await _mediaService.SkipPreviousAsync() ? ("Previous track", true) : ("Nothing to go back to", false);
            default:
                return ("Can't do \u201C" + a.Text + "\u201D yet", false);
        }
    }

    private async Task<(string Message, bool Ok)> FindAndOpenFileAsync(string query, string notFound)
    {
        var found = await Task.Run(() => FileFinder.Find(query));
        if (found == null) return (notFound, false);
        try
        {
            Process.Start(new ProcessStartInfo(found.Path) { UseShellExecute = true });
            return ("Opened " + found.Name + (found.Folder.Length > 0 ? " in " + found.Folder : ""), true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not open found file", ex);
            return ("Found " + found.Name + " but couldn't open it", false);
        }
    }

    // Reminders made by voice land in the Reminders tab and fire like any other reminder; seconds <= 0 means "no time".
    private void AddVoiceReminder(string text, int secondsFromNow)
    {
        _reminders.Insert(0, new ReminderItem
        {
            Text = text.Length > 120 ? text[..120] : text,
            Due = secondsFromNow > 0 ? DateTime.Now.AddSeconds(secondsFromNow) : null,
            Repeat = "Once"
        });
        SaveReminders();
        if (ViewReminders.Visibility == System.Windows.Visibility.Visible) RenderReminders();
    }

    private static string DurationText(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0) { int h = seconds / 3600; return h + (h == 1 ? " hour" : " hours"); }
        if (seconds >= 3600) return (seconds / 3600) + " h " + (seconds % 3600 / 60) + " min";
        if (seconds >= 60 && seconds % 60 == 0) { int m = seconds / 60; return m + (m == 1 ? " minute" : " minutes"); }
        if (seconds >= 60) return (seconds / 60) + " min " + (seconds % 60) + " s";
        return seconds + (seconds == 1 ? " second" : " seconds");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
