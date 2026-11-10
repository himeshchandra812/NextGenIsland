using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NextGenIsland.Services;

namespace NextGenIsland;

// Synced lyrics (Media tab) and the island shape (Notch / Floating pill).
public partial class MainWindow
{
    private LyricLine[] _lyrics = Array.Empty<LyricLine>();
    private string _lyricKey = "";
    private string _lyricShown = "";
    private int _lyricVersion;
    private bool _lyricsDone;
    private double _topGap;

    // ---------------------------------------------------------------- lyrics

    private void RequestLyrics(NextGenIsland.Services.MediaState state)
    {
        string key = _settings.ShowLyrics && state.HasMedia ? state.Title + "|" + state.Artist : "";
        if (key == _lyricKey) return;

        _lyricKey = key;
        _lyrics = Array.Empty<LyricLine>();
        _lyricsDone = false;
        SetLyricText("");
        if (key.Length == 0) return;

        int version = ++_lyricVersion;
        _ = LoadLyricsAsync(version, state.Title, state.Artist, state.Duration);
    }

    private async Task LoadLyricsAsync(int version, string title, string artist, TimeSpan duration)
    {
        var lines = await LyricsService.GetAsync(title, artist, duration);
        if (version != _lyricVersion) return;   // the track changed while we were waiting
        _lyrics = lines;
        _lyricsDone = true;
        UpdateLyric(_position);
    }

    private void UpdateLyric(TimeSpan position)
    {
        if (!_settings.ShowLyrics) { SetLyricText(""); return; }
        if (_lyrics.Length == 0) { SetLyricText(_lyricsDone ? "Lyrics not found" : ""); return; }

        var p = position + TimeSpan.FromMilliseconds(250);
        int idx = -1;
        for (int i = 0; i < _lyrics.Length; i++)
        {
            if (_lyrics[i].Time <= p) idx = i; else break;
        }

        if (idx < 0) { SetLyricText(""); return; }
        string text = _lyrics[idx].Text;
        SetLyricText(text.Length == 0 ? "\u266A" : text);
    }

    private void SetLyricText(string text)
    {
        if (text == _lyricShown) return;
        _lyricShown = text;
        MediaLyricText.Text = text;
        MediaLyricText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0)
        {
            // Each new line fades in while rising a few pixels.
            var rise = new TranslateTransform(0, 6);
            MediaLyricText.RenderTransform = rise;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            rise.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            MediaLyricText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200)));
        }
    }

    // ---------------------------------------------------------------- island shape

    // "Notch": hangs from the top edge. "Pill": floats a few pixels below it with all corners rounded.
    private void ApplyShape()
    {
        bool pill = _settings.IslandShape == "Pill";
        var radius = pill ? new CornerRadius(26) : new CornerRadius(0, 0, 28, 28);

        IslandContainer.CornerRadius = radius;
        GlassHighlight.CornerRadius = radius;
        GlassRim.CornerRadius = radius;
        GlassRim.BorderThickness = pill ? new Thickness(1) : new Thickness(1, 0, 1, 1);
        IslandContainer.BorderThickness = pill ? new Thickness(1) : new Thickness(1, 0, 1, 1);
        _topGap = pill ? 8 : 0;
        IslandContainer.Margin = new Thickness(0, _topGap, 0, 0);

        if (_expanded) ExpandIsland(true); else ShowCollapsedState();
    }
}
