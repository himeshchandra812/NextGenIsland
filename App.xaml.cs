using System.Windows;
using System.Windows.Media.Animation;
namespace NextGenIsland;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Let animations run at up to 120 fps on high-refresh displays (WPF defaults to 60).
        try
        {
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 120 });
        }
        catch { }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }
}
