using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NextGenIsland.Core;
using NextGenIsland.Services;

namespace NextGenIsland;

// Voice commands: press Ctrl+Alt+Space (or the mic button), say something like "open Chrome and Calculator and set the
// volume to thirty percent". The pill shows a listening wave, then "Working on it", then what was done.
// Speech is recognised offline on this PC (Whisper); the microphone is open only while the pill says "Listening".
public partial class MainWindow
{
    private const double VoiceWidth = 350, VoiceHeight = 46;
    private const int VoiceHotkeyId = 0x4E47;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000, VkSpace = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly VoiceRecorder _voiceRecorder = new();
    private SpeechToText? _stt;
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime _holdStart;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private bool _voiceActive, _voiceBusy, _voiceListening, _voiceHotkeyOk;
    private volatile float _voiceLevel;
    private string _voiceMode = "";
    private double _voicePhase;
    private readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private readonly DispatcherTimer _voiceHideTimer = new();
    private Border[] _voiceBars = Array.Empty<Border>();

    private static readonly Brush VoiceWhite = Frozen(Colors.White);
    private static readonly Brush VoiceOrange = Frozen(Color.FromRgb(255, 159, 10));
    private static readonly Brush VoiceGreen = Frozen(Color.FromRgb(48, 209, 88));
    private static readonly Brush VoiceRed = Frozen(Color.FromRgb(255, 105, 97));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void InitVoice(IntPtr hwnd)
    {
        _voiceBars = VoiceBars.Children.OfType<Border>().ToArray();
        foreach (var bar in _voiceBars) bar.RenderTransform = new ScaleTransform(1, 0.25);

        _voiceRecorder.Level += l => _voiceLevel = l;
        _voiceRecorder.Finished += wav => Dispatcher.InvokeAsync(() => OnVoiceFinishedAsync(wav));
        _voiceTimer.Tick += (_, _) => AnimateVoiceBars();
        _voiceHideTimer.Tick += (_, _) => EndVoice();
        _holdTimer.Tick += (_, _) =>
        {
            // Hold-to-talk: keep recording while Space is held, send when it is released.
            bool down = (GetAsyncKeyState((int)VkSpace) & 0x8000) != 0;
            if (!down && (DateTime.UtcNow - _holdStart).TotalMilliseconds > 250)
            {
                _holdTimer.Stop();
                _voiceRecorder.StopNow();
            }
        };

        // Ctrl+Alt+Space works from any app. If another program already owns it, the mic button still works.
        _voiceHotkeyOk = RegisterHotKey(hwnd, VoiceHotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        if (!_voiceHotkeyOk) AppLog.Write("Voice hotkey Ctrl+Alt+Space could not be registered (already in use?)");
    }

    private void ShutdownVoice()
    {
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, VoiceHotkeyId); } catch { }
        _voiceRecorder.Dispose();
        _stt?.Dispose();
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e) => OnVoiceTrigger(false);

    // First press starts listening, a second press while listening ends it early.
    public void OnVoiceTrigger() => OnVoiceTrigger(false);

    private void OnVoiceTrigger(bool fromHotkey)
    {
        if (_voiceListening)
        {
            if (!_holdTimer.IsEnabled) _voiceRecorder.StopNow();   // in hold mode the key release ends it
            return;
        }
        _ = StartVoiceAsync(fromHotkey);
    }

    private async Task StartVoiceAsync(bool fromHotkey)
    {
        if (!_ready || !_settings.VoiceEnabled || _voiceBusy) return;
        _voiceBusy = true;
        try
        {
            string modelKey = SpeechToText.NormalizeKey(_settings.VoiceModel);
            if (!SpeechToText.IsModelReady(modelKey) && !await EnsureSpeechModelAsync(modelKey))
            {
                if (!_voiceActive) EndVoice();   // declined; after a failed download the error message is still showing
                return;
            }

            if (_stt == null || _stt.Key != modelKey) { _stt?.Dispose(); _stt = new SpeechToText(modelKey); }

            // Hold-to-talk only when the hotkey started this and Space is still down (the first-run download prompt can delay it).
            bool hold = fromHotkey && _settings.VoiceHoldToTalk && (GetAsyncKeyState((int)VkSpace) & 0x8000) != 0;
            ShowVoice("listening", hold ? "Listening\u2026 release to send" : "Listening\u2026");
            _voiceListening = true;
            if (hold) { _holdStart = DateTime.UtcNow; _holdTimer.Start(); }
            if (!_voiceRecorder.Start(hold))
            {
                _voiceListening = false;
                _holdTimer.Stop();
                ShowVoiceResult("Microphone not available", false);
            }
            // otherwise the flow continues in OnVoiceFinishedAsync
        }
        catch (Exception ex)
        {
            AppLog.Write("Voice start failed", ex);
            _voiceListening = false;
            ShowVoiceResult("Voice command failed", false);
        }
    }

    // One-time download of the offline speech model, with the user's OK.
    private async Task<bool> EnsureSpeechModelAsync(string modelKey)
    {
        BeginModal();
        MessageBoxResult answer;
        try
        {
            answer = MessageBox.Show(this,
                "Voice commands need an offline speech model (about " + SpeechToText.SizeOf(modelKey) + "). It is downloaded once and kept on this PC; " +
                "what you say is processed here and never sent anywhere.\n\nDownload it now?",
                "NextGen Island - voice commands", MessageBoxButton.YesNo, MessageBoxImage.Question);
        }
        finally { EndModal(); }
        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            ShowVoice("download", "Downloading speech model 0%");
            var progress = new Progress<double>(p =>
                VoiceText.Text = "Downloading speech model " + Math.Round(p * 100) + "%");
            await SpeechToText.DownloadModelAsync(modelKey, progress);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech model download failed", ex);
            ShowVoiceResult("Download failed. Check your internet and try again.", false);
            return false;
        }
    }

    private async Task OnVoiceFinishedAsync(byte[]? wav)
    {
        _voiceListening = false;
        _holdTimer.Stop();
        if (wav == null) { ShowVoiceResult("Didn't hear anything", false); return; }

        ShowVoice("working", "Working on it\u2026");
        string heard;
        try { heard = await Task.Run(() => _stt!.TranscribeAsync(wav)); }
        catch (Exception ex)
        {
            AppLog.Write("Speech recognition failed", ex);
            ShowVoiceResult("Speech recognition failed (see log)", false);
            return;
        }
        if (heard.Length == 0) { ShowVoiceResult("Didn't catch that", false); return; }

        VoiceText.Text = "\u201C" + heard + "\u201D";
        var actions = VoiceCommands.Parse(heard);
        if (actions.Count == 0) { ShowVoiceResult("Didn't understand: " + heard, false); return; }

        var done = new List<string>();
        bool allOk = true;
        foreach (var a in actions)
        {
            var (message, ok) = await RunVoiceActionAsync(a);
            done.Add(message);
            allOk &= ok;
        }
        ShowVoiceResult(string.Join(" \u00B7 ", done), allOk);
    }

    private async Task<(string Message, bool Ok)> RunVoiceActionAsync(VoiceAction a)
    {
        try
        {
            switch (a.Kind)
            {
                case VoiceActionKind.Open:
                {
                    string? opened = await Task.Run(() => AppLauncher.Open(a.Text));
                    if (opened != null) return ("Opened " + opened, true);
                    // Not an app or site we know: maybe it is a file ("open resume").
                    return await FindAndOpenFileAsync(a.Text, "Couldn't open " + a.Text);
                }
                case VoiceActionKind.SetVolume:
                    return SetVolumePercent(a.Value);
                case VoiceActionKind.VolumeDelta:
                {
                    int cur = (int)Math.Round((_devices.GetVolume() ?? 0.5f) * 100);
                    return SetVolumePercent(cur + a.Value);
                }
                case VoiceActionKind.Mute:
                    _devices.SetMuted(true);
                    return ("Muted", true);
                case VoiceActionKind.Unmute:
                    _devices.SetMuted(false);
                    return ("Unmuted", true);
                case VoiceActionKind.SetBrightness:
                    return await SetBrightnessPercentAsync(a.Value);
                case VoiceActionKind.BrightnessDelta:
                {
                    int cur = await Task.Run(() => _devices.GetBrightness()) ?? -1;
                    if (cur < 0) return ("Brightness isn't available on this display", false);
                    return await SetBrightnessPercentAsync(cur + a.Value);
                }
                default:
                    return await RunMoreVoiceActionAsync(a);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Voice action failed: " + a.Kind, ex);
            return ("That didn't work", false);
        }
    }

    private (string, bool) SetVolumePercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _devices.SetVolume(percent / 100f);
        if (percent > 0) _devices.SetMuted(false);
        return ($"Volume {percent}%", true);
    }

    private async Task<(string, bool)> SetBrightnessPercentAsync(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool ok = await Task.Run(() => _devices.SetBrightness(percent));
        return ok ? ($"Brightness {percent}%", true) : ("Brightness isn't available on this display", false);
    }

    // ---------------------------------------------------------------- pill display

    private void ShowVoice(string mode, string text)
    {
        _voiceMode = mode;
        VoiceText.Text = text;
        _voiceHideTimer.Stop();
        _hudTimer.Stop();
        _hudActive = false;

        VoiceBars.Visibility = mode == "result" || mode == "download" ? Visibility.Collapsed : Visibility.Visible;
        VoiceIcon.Text = mode == "working" ? "\uE9F5" : "\uE720";
        VoiceIcon.Foreground = mode == "working" ? VoiceOrange : VoiceWhite;
        foreach (var bar in _voiceBars) bar.Background = mode == "working" ? VoiceOrange : VoiceWhite;

        if (mode == "listening" || mode == "working") _voiceTimer.Start(); else _voiceTimer.Stop();

        bool first = !_voiceActive;
        _voiceActive = true;
        if (_expanded) { ExpandIsland(false); }
        else if (first) ShowCollapsedState();
    }

    private void ShowVoiceResult(string text, bool ok)
    {
        _voiceBusy = true;
        ShowVoice("result", text);
        VoiceIcon.Text = ok ? "\uE73E" : "\uE7BA";
        VoiceIcon.Foreground = ok ? VoiceGreen : VoiceRed;
        _voiceHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(6500, 2400 + text.Length * 35));
        _voiceHideTimer.Start();
    }

    private void EndVoice()
    {
        _voiceTimer.Stop();
        _voiceHideTimer.Stop();
        _holdTimer.Stop();
        _voiceListening = false;
        _voiceBusy = false;
        if (!_voiceActive) return;
        _voiceActive = false;
        if (!_expanded) ShowCollapsedState();
        else Present(VoiceBanner, false);
    }

    private void AnimateVoiceBars()
    {
        _voicePhase += 0.35;
        bool listening = _voiceMode == "listening";
        for (int i = 0; i < _voiceBars.Length; i++)
        {
            double wave = 0.5 + 0.5 * Math.Sin(_voicePhase * (listening ? 1.0 : 0.7) + i * 0.9);
            double target = listening ? 0.22 + _voiceLevel * (0.35 + 0.65 * wave) : 0.30 + 0.45 * wave;
            var t = (ScaleTransform)_voiceBars[i].RenderTransform;
            t.ScaleY += (Math.Clamp(target, 0.18, 1.0) - t.ScaleY) * 0.5;   // ease towards the target so it does not flicker
        }
    }
}
