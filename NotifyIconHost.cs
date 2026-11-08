using System.Drawing;
using Hardcodet.Wpf.TaskbarNotification;
namespace NextGenIsland;
public sealed class NotifyIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    public NotifyIconHost(MainWindow window)
    {
        _icon = new TaskbarIcon { ToolTipText = "NextGen Island", Icon = SystemIcons.Application, ContextMenu = new System.Windows.Controls.ContextMenu() };
        var open = new System.Windows.Controls.MenuItem { Header = "Open NextGen Island" }; open.Click += (_, _) => { System.Windows.MessageBox.Show("Tray menu event received. Calling RestoreFromTray.", "NextGen Island Diagnostic"); window.RestoreFromTray(); };
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" }; exit.Click += (_, _) => window.ExitApplication();
        _icon.ContextMenu.Items.Add(open); _icon.ContextMenu.Items.Add(new System.Windows.Controls.Separator()); _icon.ContextMenu.Items.Add(exit);
        _icon.TrayLeftMouseDown += (_, _) => window.RestoreFromTray();
    }
    public void ShowBalloon(string message) { try { _icon.ShowBalloonTip("NextGen Island", message, BalloonIcon.Info); } catch { } }
    public void Dispose() => _icon.Dispose();
}

