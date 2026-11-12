using System.Runtime.InteropServices;
using System.Text;

namespace NextGenIsland.Services;

/// <summary>Detects whether a full-screen app (game, video, F11 browser, presentation) is in front.</summary>
internal static class FullscreenDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>What the last check saw. Written to fullscreen.log when it changes, to help diagnose detection.</summary>
    public static string LastReport { get; private set; } = "";

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "MultitaskingViewFrame", "ForegroundStaging", "XamlExplorerHostIslandWindow", "LockApp"
    };

    /// <param name="ourWindow">The island's own window, so it can be ignored.</param>
    public static bool IsActive(IntPtr ourWindow)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) { LastReport = "no foreground window"; return false; }
            if (fg == ourWindow) { LastReport = "foreground is the island"; return false; }

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) { LastReport = "foreground is another NextGen Island window"; return false; }

            var cls = new StringBuilder(128);
            GetClassName(fg, cls, cls.Capacity);
            string cn = cls.ToString();
            if (ShellClasses.Contains(cn)) { LastReport = "shell window " + cn; return false; }

            // Only care about a full-screen app on the same monitor as the island.
            IntPtr monitor = MonitorFromWindow(fg, MonitorDefaultToNearest);
            bool sameMonitor = monitor == MonitorFromWindow(ourWindow, MonitorDefaultToNearest);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            bool gotInfo = GetMonitorInfo(monitor, ref info);
            bool gotRect = GetWindowRect(fg, out Rect r);
            bool covers = gotInfo && gotRect &&
                r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top &&
                r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;

            int state = -1;
            SHQueryUserNotificationState(out state);

            LastReport = $"class={cn} pid={pid} window=({r.Left},{r.Top},{r.Right},{r.Bottom}) " +
                         $"monitor=({info.Monitor.Left},{info.Monitor.Top},{info.Monitor.Right},{info.Monitor.Bottom}) " +
                         $"sameMonitor={sameMonitor} covers={covers} userState={state}";

            if (!sameMonitor) return false;
            // userState 3 = D3D full screen, 4 = presentation mode.
            return covers || state == 3 || state == 4;
        }
        catch
        {
            return false;
        }
    }
}
