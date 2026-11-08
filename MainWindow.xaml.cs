using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NextGenIsland.Services;

namespace NextGenIsland;

public partial class MainWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly ClipboardStore _store = new();
    private readonly SystemMonitorService _monitor = new();
    private readonly GpuMonitorService _gpuMonitor = new();
    private readonly MediaControllerService _mediaService = new();
    private readonly BatteryMonitorService _batteryMonitor = new();
    private readonly NetworkMonitorService _networkMonitor;
    private readonly NotificationService _notificationService = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    
    private AppSettings _settings;
    private HwndSource? _source;
    private NotifyIconHost? _tray;
    private bool _expanded, _allowExit;
    private bool _batteryWarningFired;
    private bool _isHovering;
    private bool _isPinned;
    private DispatcherTimer? _collapseTimer;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _networkMonitor = new NetworkMonitorService(_settings.SelectedNetworkInterface);

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelayMs) };
        _collapseTimer.Tick += CollapseTimer_Tick;

        _notificationService.NotificationsEnabled = _settings.NotificationsEnabled;
        _notificationService.QuietMode = _settings.QuietMode;
        _notificationService.DisplayDurationSeconds = _settings.NotificationDurationSeconds;

        _timer.Tick += async (_, _) => await OnTimerTickAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceIsland();
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowStyles.ApplyToolWindowStyle(hwnd);
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(hwnd))
            Debug.WriteLine("Could not register clipboard listener: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        
        await _store.InitializeAsync();
        _tray = new NotifyIconHost(this);

        _mediaService.MediaStateChanged += OnMediaStateChanged;
        await _mediaService.InitializeAsync();

        _notificationService.ActiveNotificationChanged += OnActiveNotificationChanged;
        _notificationService.HistoryChanged += OnNotificationHistoryChanged;
        _notificationService.WindowsListenerStatusChanged += OnWindowsListenerStatusChanged;
        await _notificationService.InitializeAsync();

        UpdateQuietModeUI();
        _timer.Start();
        await RefreshMetricsAsync();
    }

    private void PlaceIsland()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 10;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate) { _ = CaptureClipboardAsync(); handled = true; }
        return IntPtr.Zero;
    }

    private async Task CaptureClipboardAsync()
    {
        try
        {
            if (SettingsStore.Load().ClipboardPaused || !Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
            await _store.SaveIfNewAsync(text);
            await Dispatcher.InvokeAsync(() =>
            {
                var preview = text.Trim();
                if (preview.Length > 28) preview = preview[..28] + "...";
                ClipboardStatus.Text = $"Saved: \"{preview}\"";
            });
        }
        catch (Exception ex) { Debug.WriteLine("Clipboard capture: " + ex.Message); }
    }

    private async Task OnTimerTickAsync()
    {
        await RefreshMetricsAsync();
        await _mediaService.RefreshMediaStateAsync();
    }

    private async Task RefreshMetricsAsync()
    {
        try
        {
            var mTask = _monitor.ReadAsync();
            var gTask = _gpuMonitor.ReadAsync();
            await Task.WhenAll(mTask, gTask);

            var m = await mTask;
            var g = await gTask;
            var b = _batteryMonitor.Read();
            var n = _settings.NetworkMonitoringEnabled ? _networkMonitor.Read() : null;

            CpuBar.Value = m.Cpu;
            RamBar.Value = m.Ram;
            CpuText.Text = $"{m.Cpu:0}%";
            RamText.Text = $"{m.Ram:0}%";

            if (g.IsAvailable)
            {
                GpuBar.Value = g.Utilization;
                GpuText.Text = g.UtilDisplay;
                GpuTempText.Text = g.TempDisplay;
                GpuNameText.Text = g.ModelName;
                VramBar.Value = g.MemoryPercent;
                VramText.Text = g.MemoryDisplay;
            }
            else
            {
                GpuText.Text = "â€”";
                GpuTempText.Text = "Offline";
                GpuNameText.Text = "NVIDIA GPU Unavailable";
                GpuBar.Value = 0;
                VramText.Text = "â€”";
                VramBar.Value = 0;
            }

            // Battery Telemetry
            if (b.HasBattery)
            {
                BatteryGlyphText.Text = b.Glyph;
                BatteryPercentText.Text = $"{b.Percent}%";
                BatteryStatusText.Text = b.StatusText;
            }
            else
            {
                BatteryGlyphText.Text = "âš¡";
                BatteryPercentText.Text = "AC";
                BatteryStatusText.Text = "Desktop Power";
            }

            // Low Battery Warning
            if (_settings.BatteryWarningsEnabled && b.IsLow)
            {
                if (!_batteryWarningFired)
                {
                    _batteryWarningFired = true;
                    _notificationService.PostNotification(
                        "Battery",
                        "Low Battery Warning",
                        $"Battery is at {b.Percent}%. Connect charger to avoid shutdown.",
                        isSystemAlert: true
                    );
                }
            }
            else if (!b.IsLow)
            {
                _batteryWarningFired = false;
            }

            // Network Telemetry
            if (n != null && n.IsConnected)
            {
                NetworkNameText.Text = n.AdapterName;
                NetworkStatusDot.Foreground = (Brush)new SolidColorBrush(Color.FromRgb(48, 209, 88));
                NetworkSpeedText.Text = $"{n.DownloadSpeedText} | {n.UploadSpeedText}";
            }
            else
            {
                NetworkNameText.Text = "OFFLINE";
                NetworkStatusDot.Foreground = (Brush)new SolidColorBrush(Color.FromRgb(255, 69, 58));
                NetworkSpeedText.Text = "Network Disconnected";
            }

            // Compact pill summary
            string netQuick = n != null && n.IsConnected ? $" | NET {n.DownloadSpeedText}" : "";
            string batQuick = b.HasBattery ? $" | BAT {b.Percent}%" : "";
            CompactMetrics.Text = $"CPU {m.Cpu:0}% | GPU {g.Utilization:0}% | RAM {m.Ram:0}%{batQuick}{netQuick}";
        }
        catch (Exception ex) { Debug.WriteLine("RefreshMetricsAsync error: " + ex); }
    }

    private void OnActiveNotificationChanged(IslandNotification? notif)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (notif != null)
            {
                BannerAppNameText.Text = notif.AppName;
                BannerTitleText.Text = notif.Title;
                BannerBodyText.Text = notif.Message;
                BannerIconText.Text = notif.IsSystemAlert ? "âš ï¸" : "ðŸ””";

                // Update notification item in expanded view
                NotifItemAppText.Text = notif.AppName;
                NotifItemTimeText.Text = notif.Timestamp.ToString("HH:mm:ss");
                NotifItemTitleText.Text = notif.Title;
                NotifItemBodyText.Text = notif.Message;

                BellBadge.Visibility = Visibility.Visible;

                // If collapsed, animate to notification pill banner
                if (!_expanded)
                {
                    CompactHeader.Visibility = Visibility.Collapsed;
                    NotificationBanner.Visibility = Visibility.Visible;
                    AnimateHeight(84);
                }
            }
            else
            {
                // Returned to normal state
                if (!_expanded)
                {
                    NotificationBanner.Visibility = Visibility.Collapsed;
                    CompactHeader.Visibility = Visibility.Visible;
                    AnimateHeight(68);
                }
            }
        });
    }

    private void OnNotificationHistoryChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            int count = _notificationService.History.Count;
            NotificationCountText.Text = count > 0 ? $"{count} ITEMS" : "0 NEW";
            BellBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var latest = _notificationService.History.FirstOrDefault();
            if (latest != null)
            {
                NotifItemAppText.Text = latest.AppName;
                NotifItemTimeText.Text = latest.Timestamp.ToString("HH:mm:ss");
                NotifItemTitleText.Text = latest.Title;
                NotifItemBodyText.Text = latest.Message;
            }
            else
            {
                NotifItemAppText.Text = "System";
                NotifItemTimeText.Text = "â€”";
                NotifItemTitleText.Text = "No notifications";
                NotifItemBodyText.Text = "Notifications will appear here as they arrive.";
            }
        });
    }

    private void OnWindowsListenerStatusChanged(bool isAllowed)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PermissionWarningBanner.Visibility = isAllowed ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void NotificationBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            // Clicking banner expands full Island
            ExpandIsland(true);
        }
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.DismissCurrent();
    }

    private void DismissCurrentNotification_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.DismissCurrent();
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.ClearHistory();
    }

    private void BellButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandIsland(!_expanded);
    }

    private void ToggleQuietMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.QuietMode = !_settings.QuietMode;
        _notificationService.QuietMode = _settings.QuietMode;
        SettingsStore.Save(_settings);
        UpdateQuietModeUI();
    }

    private void UpdateQuietModeUI()
    {
        QuietModeBtn.Content = _settings.QuietMode ? "Quiet: ON ðŸŒ™" : "Quiet: OFF";
        QuietModeBtn.Foreground = _settings.QuietMode ? (Brush)new SolidColorBrush(Color.FromRgb(255, 159, 10)) : Brushes.White;
    }

    private void OpenWindowsNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        NotificationService.OpenWindowsNotificationSettings();
    }

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.PostNotification(
            "NextGen Island",
            "Apple-Inspired Notification",
            "Interactive notification test: queue, fluid pill animation, and auto-dismiss are working!",
            isSystemAlert: false
        );
    }

    private void OnMediaStateChanged(MediaState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            MediaTitleText.Text = state.Title;
            MediaArtistText.Text = state.Artist;

            if (state.HasMedia)
            {
                MediaStatusBadge.Text = state.IsPlaying ? "PLAYING" : "PAUSED";
                MediaStatusBadge.Foreground = state.IsPlaying ? (Brush)new SolidColorBrush(Color.FromRgb(48, 209, 88)) : (Brush)new SolidColorBrush(Color.FromRgb(142, 142, 147));
                MediaStatusBorder.Background = state.IsPlaying ? (Brush)new SolidColorBrush(Color.FromArgb(50, 48, 209, 88)) : (Brush)new SolidColorBrush(Color.FromRgb(36, 36, 46));
                MediaAppText.Text = !string.IsNullOrWhiteSpace(state.AppName) ? $"| {state.AppName}" : "";
                MediaPlayPauseButton.Content = state.IsPlaying ? "â¸" : "â–¶";

                if (state.Thumbnail != null)
                {
                    MediaThumbnail.Source = state.Thumbnail;
                    MediaThumbnail.Visibility = Visibility.Visible;
                    MediaDefaultArt.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MediaThumbnail.Visibility = Visibility.Collapsed;
                    MediaDefaultArt.Visibility = Visibility.Visible;
                }
            }
            else
            {
                MediaStatusBadge.Text = "NO MEDIA";
                MediaStatusBadge.Foreground = (Brush)new SolidColorBrush(Color.FromRgb(191, 90, 242));
                MediaStatusBorder.Background = (Brush)new SolidColorBrush(Color.FromRgb(36, 20, 46));
                MediaAppText.Text = "";
                MediaPlayPauseButton.Content = "â–¶";
                MediaThumbnail.Visibility = Visibility.Collapsed;
                MediaDefaultArt.Visibility = Visibility.Visible;
            }
        });
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void MediaPrev_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    // â”€â”€ Hover / pin handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void IslandContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        _collapseTimer?.Stop();
        if (_settings.HoverExpansionEnabled && !_expanded)
            ExpandIsland(true);
    }

    private void IslandContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        if (_isPinned) return;
        if (_collapseTimer != null)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelayMs);
            _collapseTimer.Start();
        }
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer?.Stop();
        if (!_isHovering && !_isPinned)
            ExpandIsland(false);
    }
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ClickPinEnabled)
        {
            // Toggle pin state; pinned = stays expanded regardless of hover
            _isPinned = !_isPinned;
            ExpandButton.Content = _isPinned ? "PIN" : (_expanded ? "^" : "v");
            if (_isPinned)
                ExpandIsland(true);
            else if (!_isHovering)
                ExpandIsland(false);
        }
        else
        {
            // Pin disabled: simple toggle
            ExpandIsland(!_expanded);
        }
    }

    private void ExpandIsland(bool expand)
    {
        _expanded = expand;
        if (_expanded)
        {
            NotificationBanner.Visibility = Visibility.Collapsed;
            CompactHeader.Visibility = Visibility.Visible;
            ExpandedScrollViewer.Visibility = Visibility.Visible;
            ExpandedPanel.Visibility = Visibility.Visible;

            // Cap the scroll viewer so it never pushes past the work area
            double maxAvailable = SystemParameters.WorkArea.Height - Top - 40;
            ExpandedScrollViewer.MaxHeight = maxAvailable;

            AnimateHeight(maxAvailable);
            ExpandButton.Content = "^";
        }
        else
        {
            AnimateHeight(68, () =>
            {
                ExpandedPanel.Visibility = Visibility.Collapsed;
                ExpandedScrollViewer.Visibility = Visibility.Collapsed;
            });
            ExpandButton.Content = "v";
        }
    }

    private void AnimateHeight(double targetHeight, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (onCompleted != null)
        {
            anim.Completed += (_, _) => onCompleted();
        }
        BeginAnimation(HeightProperty, anim);
    }

    private void OpenClipboard_Click(object sender, RoutedEventArgs e) => new ClipboardWindow(_store) { Owner = this }.ShowDialog();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow { Owner = this };
        w.ShowDialog();
        // Reload settings
        _settings = SettingsStore.Load();
        _notificationService.NotificationsEnabled = _settings.NotificationsEnabled;
        _notificationService.QuietMode = _settings.QuietMode;
        _notificationService.DisplayDurationSeconds = _settings.NotificationDurationSeconds;
        _networkMonitor.SetSelectedInterface(_settings.SelectedNetworkInterface);
        UpdateQuietModeUI();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit) { e.Cancel = true; Hide(); _tray?.ShowBalloon("Still running in the system tray."); return; }
        _timer.Stop();
        var hwnd = new WindowInteropHelper(this).Handle;

        NativeMethods.RemoveClipboardFormatListener(hwnd);
        _source?.RemoveHook(WndProc);
        _tray?.Dispose();
        _gpuMonitor.Dispose();
        _mediaService.Dispose();
        _notificationService.Dispose();
        Application.Current.Shutdown();
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = false;
        Topmost = true;
        Activate();

        Debug.WriteLine(
            $"Tray restore: Visible={IsVisible}, State={WindowState}, HWND={new WindowInteropHelper(this).Handle}");
    }
    public void ExitApplication() { _allowExit = true; Close(); }
}




