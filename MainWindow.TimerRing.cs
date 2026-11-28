using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NextGenIsland;

// Countdown ring: while a voice timer is running, the island shrinks to a small pill with just a progress ring and the
// time left (instead of the CPU / temperature text). The ring turns amber for the last 10 seconds and, when the timer
// ends, shows "Done" and flashes for a few seconds. A paused timer keeps its ring frozen and dimmed, and the time blinks.
// Timers live in the Reminders list (ReminderItem.TimerSeconds > 0).
public partial class MainWindow
{
    private const double RingSize = 22, RingStroke = 2.5, DoneFlashSeconds = 6, WarnSeconds = 10;
    private const double TimerPillWidth = 124, TimerPillLongWidth = 146;   // island width while a timer runs (long = with hours)
    private double _timerPillWidth = TimerPillWidth;

    private readonly DispatcherTimer _ringTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _ringWired;
    private string? _ringShownId;   // the timer currently drawn on the pill
    private bool _timerOnPill;      // a timer is showing on the pill (also keeps it visible over full-screen apps)

    // Seconds left on a timer: frozen while paused, otherwise counting down to Due (negative once it has ended).
    private static double TimerRemaining(ReminderItem r, DateTime now) =>
        r.PausedSeconds.HasValue ? r.PausedSeconds.Value
        : r.Due.HasValue ? (r.Due.Value - now).TotalSeconds
        : 0;

    // Sort key for "which timer comes first": running timers by time left, then paused ones.
    private static double TimerRank(ReminderItem r, DateTime now) =>
        (r.PausedSeconds.HasValue ? 1e9 : 0) + TimerRemaining(r, now);

    // Timers that are running or paused (not finished), the one ending first at the top. Used by the voice commands.
    private List<ReminderItem> LiveTimers()
    {
        var now = DateTime.Now;
        return _reminders
            .Where(r => r.TimerSeconds > 0 && (r.PausedSeconds.HasValue || (r.Due.HasValue && r.Due.Value > now)))
            .OrderBy(r => TimerRank(r, now))
            .ToList();
    }

    // The timer to show on the pill. A timer that just ended still counts for a few seconds (the "Done" flash).
    private ReminderItem? ActiveTimer(out int others)
    {
        others = 0;
        var now = DateTime.Now;
        ReminderItem? best = null;
        foreach (var r in _reminders)
        {
            if (r.TimerSeconds <= 0) continue;
            bool paused = r.PausedSeconds.HasValue;
            if (!paused && !r.Due.HasValue) continue;
            if (!paused && (now - r.Due!.Value).TotalSeconds >= DoneFlashSeconds) continue;   // finished a while ago
            if (best == null) { best = r; continue; }
            others++;
            if (TimerRank(r, now) < TimerRank(best, now)) best = r;
        }
        return best;
    }

    // While a timer runs the island shrinks to just the ring and the time left; a bit wider once it shows hours.
    private void SyncTimerPillWidth(ReminderItem t)
    {
        double w = TimerRemaining(t, DateTime.Now) >= 3600 ? TimerPillLongWidth : TimerPillWidth;
        if (Math.Abs(w - _timerPillWidth) < 0.5) return;
        _timerPillWidth = w;
        if (_timerOnPill && !_expanded && CompactHeader.IsVisible) AnimateIsland(w, CompactHeight);
    }

    // Called from UpdateCompactText. Returns true when the pill is showing a timer (so the normal pill text is skipped).
    private bool UpdateTimerChip()
    {
        if (!_ringWired) { _ringTimer.Tick += (_, _) => RingTick(); _ringWired = true; }

        var t = ActiveTimer(out _);
        bool wasOnPill = _timerOnPill;
        _timerOnPill = t != null;

        if (t == null)
        {
            _ringShownId = null;
            _ringTimer.Stop();
            if (TimerChip.Visibility != Visibility.Collapsed) TimerChip.Visibility = Visibility.Collapsed;
            // The island grows back to its normal size (or, over a full-screen app, to the thin line) once the timer is gone.
            if (wasOnPill && !_expanded) ShowCollapsedState();
            return false;
        }

        _ringShownId = t.Id;
        CompactStats.Visibility = Visibility.Collapsed;
        CompactTime.Visibility = Visibility.Collapsed;
        CompactMetrics.Visibility = Visibility.Collapsed;
        TimerChip.Visibility = Visibility.Visible;
        SyncTimerPillWidth(t);
        // The island shrinks to the timer pill when a timer starts (over a full-screen app, the thin line grows into it).
        if (!wasOnPill && !_expanded) ShowCollapsedState();
        DrawTimer(t);
        if (!_ringTimer.IsEnabled) _ringTimer.Start();
        return true;
    }

    // Four times a second: redraw the ring, or rebuild the pill when the timer to show has changed or ended.
    private void RingTick()
    {
        var t = ActiveTimer(out _);
        if (t == null || t.Id != _ringShownId) { UpdateCompactText(); return; }
        SyncTimerPillWidth(t);
        DrawTimer(t);
    }

    private void DrawTimer(ReminderItem t)
    {
        if (!CompactHeader.IsVisible) return;   // expanded, full screen, or another banner is showing: nothing to draw

        var now = DateTime.Now;
        bool paused = t.PausedSeconds.HasValue;
        double remaining = TimerRemaining(t, now);
        bool done = !paused && remaining <= 0;

        double fraction = done ? 1.0 : Math.Clamp(remaining / t.TimerSeconds, 0.0, 1.0);
        Brush brush = done ? VoiceGreen : !paused && remaining <= WarnSeconds ? VoiceOrange : VoiceWhite;
        TimerRingArc.Stroke = brush;
        TimerRingArc.Data = TimerArcGeometry(fraction);
        TimerTimeText.Foreground = brush;
        TimerTimeText.Text = done ? "Done" : TimeLeftText(remaining);

        // Paused: the ring dims and the time blinks slowly. Done: the whole pill flashes (half a second on, half a second dim).
        TimerRingArc.Opacity = paused ? 0.55 : 1.0;
        TimerTimeText.Opacity = paused && (int)(now.TimeOfDay.TotalMilliseconds / 700) % 2 == 1 ? 0.3 : 1.0;
        TimerChip.Opacity = done && (int)(now.TimeOfDay.TotalMilliseconds / 500) % 2 == 1 ? 0.35 : 1.0;
    }

    private static string TimeLeftText(double remainingSeconds)
    {
        int s = Math.Max(0, (int)Math.Ceiling(remainingSeconds));
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
