using System.Drawing;
using Hardcodet.Wpf.TaskbarNotification;
namespace NextGenIsland;
public sealed class NotifyIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    public NotifyIconHost(MainWindow window)
    {
        _icon = new TaskbarIcon { ToolTipText = "NextGen Island", Icon = LoadIcon(), ContextMenu = new System.Windows.Controls.ContextMenu() };
        var open = new System.Windows.Controls.MenuItem { Header = "Open NextGen Island" }; open.Click += (_, _) => { window.RestoreFromTray(); };
        var settings = new System.Windows.Controls.MenuItem { Header = "Settings" }; settings.Click += (_, _) => window.OpenSettingsFromTray();
        var voice = new System.Windows.Controls.MenuItem { Header = "Voice command" }; voice.Click += (_, _) => window.OnVoiceTrigger();
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" }; exit.Click += (_, _) => window.ExitApplication();
        _icon.ContextMenu.Items.Add(open); _icon.ContextMenu.Items.Add(settings); _icon.ContextMenu.Items.Add(voice); _icon.ContextMenu.Items.Add(new System.Windows.Controls.Separator()); _icon.ContextMenu.Items.Add(exit);
        _icon.TrayLeftMouseDown += (_, _) => window.RestoreFromTray();
    }
    // The tray icon is the app's own icon (embedded in the exe); falls back to the generic one if it cannot be read.
    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && Icon.ExtractAssociatedIcon(path) is Icon own) return own;
        }
        catch { }
        return SystemIcons.Application;
    }
    public void ShowBalloon(string message) { try { _icon.ShowBalloonTip("NextGen Island", message, BalloonIcon.Info); } catch { } }
    public void Dispose() => _icon.Dispose();
}

