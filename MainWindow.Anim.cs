using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NextGenIsland;

// Animation helpers: soft fades between island states, a small drop-in for the expanded content,
// and a cached render while the island changes size.
public partial class MainWindow
{
    private readonly TranslateTransform _expandShift = new();
    private readonly Dictionary<UIElement, int> _presentGen = new();
    private readonly BitmapCache _animCache = new();

    private void InitAnimations()
    {
        ExpandedRoot.RenderTransform = _expandShift;
    }

    // Fades an element in (after delayMs) or out, and collapses it once it has faded.
    private void Present(UIElement e, bool show, int delayMs = 0)
    {
        int gen = _presentGen.TryGetValue(e, out int g) ? g + 1 : 1;
        _presentGen[e] = gen;

        if (show)
        {
            if (e.Visibility != Visibility.Visible)
            {
                e.BeginAnimation(OpacityProperty, null);
                e.Opacity = 0.0;
                e.Visibility = Visibility.Visible;
            }
            FadeTo(e, 1.0, (int)(190 * AnimFactor), (int)(delayMs * AnimFactor));
        }
        else if (e.Visibility == Visibility.Visible)
        {
            FadeTo(e, 0.0, (int)(110 * AnimFactor), 0, () =>
            {
                if (_presentGen.TryGetValue(e, out int now) && now == gen) e.Visibility = Visibility.Collapsed;
            });
        }
    }

    // The expanded content drops in a few pixels while it fades, and lifts slightly when it leaves.
    private void ShiftExpanded(bool opening)
    {
        var anim = opening
            ? new DoubleAnimation(-10.0, 0.0, TimeSpan.FromMilliseconds(380 * AnimFactor))
              { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation { To = -6.0, Duration = TimeSpan.FromMilliseconds(140 * AnimFactor),
                                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        _expandShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // The outgoing tab fades and slides away while the new one slides in.
    private void FadeOutView(string key, FrameworkElement view, double dx)
    {
        view.IsHitTestVisible = false;
        var slide = new TranslateTransform();
        view.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0.0, -dx * 0.7, TimeSpan.FromMilliseconds(170 * AnimFactor))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(140 * AnimFactor));
        fade.Completed += (_, _) =>
        {
            if (_activeKey == key) return;      // the user came back to it; the new fade-in owns it now
            view.Visibility = Visibility.Collapsed;
            view.BeginAnimation(OpacityProperty, null);
            view.Opacity = 1.0;
            view.IsHitTestVisible = true;
        };
        view.BeginAnimation(OpacityProperty, fade);
    }

    // List rows rise in one after another when a tab opens (first few rows only, so it stays quick).
    private void StaggerIn(System.Windows.Controls.Panel panel)
    {
        int n = Math.Min(panel.Children.Count, 6);
        for (int i = 0; i < n; i++)
        {
            if (panel.Children[i] is not FrameworkElement row || row.RenderTransform is not (null or MatrixTransform)) continue;
            var lift = new TranslateTransform(0, 8);
            row.RenderTransform = lift;
            row.Opacity = 0.0;
            var begin = TimeSpan.FromMilliseconds((70 + i * 45) * AnimFactor);
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320 * AnimFactor))
            { BeginTime = begin, EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(240 * AnimFactor))
            { BeginTime = begin });   // base value stays 0 until the delayed animation starts, then holds at 1
        }
    }
}
