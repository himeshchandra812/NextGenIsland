using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NextGenIsland;

// Countdown ring: while a voice timer is running, the collapsed pill shows a progress ring, the time left and "Timer"
// instead of the CPU / temperature text. The ring turns amber for the last 10 seconds and, when the timer ends,
// shows "Done" and flashes for a few seconds. Timers live in the Reminders list (ReminderItem.TimerSeconds > 0).
public partial class MainWindow
{
    private const double RingSize = 22, RingStroke = 2.5, DoneFlashSeconds = 6, WarnSeconds = 10;

    private readonly DispatcherTimer _ringTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _ringWired;
    private string? _ringShownId;   // the timer currently drawn on the pill

    // The timer to show: the one that ends first. A timer that just ended still counts for a few seconds (the "Done" flash).
    private ReminderItem? ActiveTimer(out int others)
    {
        others = 0;
        var now = DateTime.Now;
        ReminderItem? best = null;
        foreach (var r in _reminders)
        {
            if (r.TimerSeconds <= 0 || !r.Due.HasValue) continue;
            if ((now - r.Due.Value).TotalSeconds >= DoneFlashSeconds) continue;   // finished a while ago
            if (best == null) { best = r; continue; }
            others++;
            if (r.Due.Value < best.Due!.Value) best = r;
        }
        return best;
    }

    // Called from UpdateCompactText. Returns true when the pill is showing a timer (so the normal pill text is skipped).
    private bool UpdateTimerChip()
    {
        if (!_ringWired) { _ringTimer.Tick += (_, _) => RingTick(); _ringWired = true; }

        var t = ActiveTimer(out int others);
        if (t == null)
        {
            _ringShownId = null;
            _ringTimer.Stop();
            if (TimerChip.Visibility != Visibility.Collapsed) TimerChip.Visibility = Visibility.Collapsed;
            return false;
        }

        _ringShownId = t.Id;
        CompactStats.Visibility = Visibility.Collapsed;
        CompactTime.Visibility = Visibility.Collapsed;
        CompactMetrics.Visibility = Visibility.Collapsed;
        TimerChip.Visibility = Visibility.Visible;
        TimerLabelText.Text = others > 0 ? "Timer +" + others : "Timer";
        DrawTimer(t);
        if (!_ringTimer.IsEnabled) _ringTimer.Start();
        return true;
    }

    // Four times a second: redraw the ring, or rebuild the pill when the timer to show has changed or ended.
    private void RingTick()
    {
        var t = ActiveTimer(out int others);
        if (t == null || t.Id != _ringShownId) { UpdateCompactText(); return; }
        TimerLabelText.Text = others > 0 ? "Timer +" + others : "Timer";
        DrawTimer(t);
    }

    private void DrawTimer(ReminderItem t)
    {
        if (!CompactHeader.IsVisible) return;   // expanded, full screen, or another banner is showing: nothing to draw

        var now = DateTime.Now;
        double remaining = (t.Due!.Value - now).TotalSeconds;
        bool done = remaining <= 0;

        double fraction = done ? 1.0 : Math.Clamp(remaining / t.TimerSeconds, 0.0, 1.0);
        Brush brush = done ? VoiceGreen : remaining <= WarnSeconds ? VoiceOrange : VoiceWhite;
        TimerRingArc.Stroke = brush;
        TimerRingArc.Data = TimerArcGeometry(fraction);
        TimerTimeText.Foreground = brush;
        TimerTimeText.Text = done ? "Done" : TimeLeftText(remaining);

        // Flash while "Done" is showing: on for half a second, dim for half a second.
        TimerChip.Opacity = done && (int)(now.TimeOfDay.TotalMilliseconds / 500) % 2 == 1 ? 0.35 : 1.0;
    }

    private static string TimeLeftText(double remainingSeconds)
    {
        int s = (int)Math.Ceiling(remainingSeconds);
        return s >= 3600
            ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}"
            : $"{s / 60}:{s % 60:00}";
    }

    // An arc that starts at 12 o'clock and runs clockwise for `fraction` of a full circle.
    private static Geometry TimerArcGeometry(double fraction)
    {
        double r = (RingSize - RingStroke) / 2.0, c = RingSize / 2.0;
        if (fraction <= 0.001) return Geometry.Empty;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);

        double a = fraction * 2.0 * Math.PI;
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
