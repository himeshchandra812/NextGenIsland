using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NextGenIsland;

// Volume / brightness pop-up: when the keyboard keys change either one, the collapsed island briefly
// turns into a slider showing the new level, then goes back.
public partial class MainWindow
{
    private const double HudWidth = 280, HudHeight = 46, HudTrackWidth = 150;

    private bool _hudActive;
    private DateTime _hudQuietUntil = DateTime.MinValue;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(1700) };
    private bool _hudTimerWired;

    // kind: "volume" or "brightness". value 0..1.
    private void ShowHud(string kind, double value, bool muted)
    {
        if (!_ready || !_settings.ShowKeyHud || _expanded || _voiceActive || DateTime.Now < _hudQuietUntil) return;

        value = Math.Clamp(value, 0.0, 1.0);
        if (kind == "volume")
            HudIcon.Text = muted || value <= 0.001 ? "\uE74F" : "\uE767";
        else
            HudIcon.Text = "\uE706";
        HudPct.Text = muted ? "Muted" : $"{Math.Round(value * 100):0}%";

        HudFill.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = HudTrackWidth * (muted ? 0.0 : value),
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        if (!_hudTimerWired) { _hudTimer.Tick += (_, _) => HideHud(); _hudTimerWired = true; }
        bool wasActive = _hudActive;
        _hudActive = true;
        if (!wasActive) ShowCollapsedState();
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void HideHud()
    {
        _hudTimer.Stop();
        if (!_hudActive) return;
        _hudActive = false;
        if (!_expanded) ShowCollapsedState();
        else Present(HudBanner, false);
    }
}
