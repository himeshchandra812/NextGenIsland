using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using NextGenIsland.Services;

namespace NextGenIsland;

// When a full-screen app is in front the collapsed island shrinks to a thin line at the top edge.
public partial class MainWindow
{
    private const double LineWidth = 96, LineHeight = 4;

    private bool _fullscreen;
    private int _fullscreenVotes;
    private Effect? _islandShadow;

    // Called once a second. Needs two matching checks in a row before switching, so quick alt-tabs do not flicker.
    private void CheckFullscreen()
    {
        bool want = _settings.ShrinkInFullscreen && FullscreenDetector.IsActive(new WindowInteropHelper(this).Handle);
        LogFullscreen(want);
        if (want == _fullscreen) { _fullscreenVotes = 0; return; }
        if (++_fullscreenVotes < 2) return;

        _fullscreenVotes = 0;
        _fullscreen = want;
        if (!_expanded) ShowCollapsedState();
    }

    // Records what the detector saw whenever it changes, so a game that is not detected can be diagnosed.
    private string _fullscreenLastLog = "";

    private void LogFullscreen(bool want)
    {
        try
        {
            string report = FullscreenDetector.LastReport;
            if (report == _fullscreenLastLog) return;
            _fullscreenLastLog = report;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "fullscreen.log");
            if (File.Exists(file) && new FileInfo(file).Length > 100_000) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss") + "  fullscreen=" + want + "  " + report + Environment.NewLine);
        }
        catch { }
    }

    // The line sits right on the screen edge with no shadow; every other state uses the normal gap and shadow.
    private void ApplyLineLook()
    {
        bool line = _fullscreen && !_expanded;
        _islandShadow ??= IslandContainer.Effect;
        IslandContainer.Effect = line ? null : _islandShadow;
        IslandContainer.Margin = new Thickness(0, line ? 0.0 : _topGap, 0, 0);
    }
}
