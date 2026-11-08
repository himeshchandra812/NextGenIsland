using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Text.Json;
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
    private string _activeModule = "media";
    private bool _compactHasMedia;
    private bool _compactMediaPlaying;
    private string _compactTitle = "";
    private string _compactArtist = "";
    private System.Windows.Media.ImageSource? _compactArtwork;
    private Color _albumAccent = Color.FromRgb(91, 156, 194);
    private Color _albumDeep = Color.FromRgb(29, 54, 72);
    private DispatcherTimer? _collapseTimer;
    private readonly string _extraDataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
    private readonly List<string> _reminders = new();
    private readonly List<string> _shelfFiles = new();
    private DispatcherTimer? _focusTimer;
    private int _focusSeconds = 25 * 60;

    public MainWindow()
    {
        InitializeComponent();
        SetActiveModule("media");
        _settings = SettingsStore.Load();
        LoadExtraData();
        RefreshAgents();
        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _focusTimer.Tick += (_, _) => { if (_focusSeconds > 0) _focusSeconds--; UpdateFocusTimerText(); if (_focusSeconds == 0) _focusTimer?.Stop(); };
        _networkMonitor = new NetworkMonitorService(_settings.SelectedNetworkInterface);

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelayMs) };
        _collapseTimer.Tick += CollapseTimer_Tick;

        _notificationService.NotificationsEnabled = _settings.NotificationsEnabled;
        _notificationService.QuietMode = _settings.QuietMode;
        _notificationService.DisplayDurationSeconds = _settings.NotificationDurationSeconds;

        _timer.Tick += async (_, _) => { await OnTimerTickAsync(); UpdateWidgetClock(); };
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
        UpdateCompactIdleDisplay();
        _timer.Start();
        await RefreshMetricsAsync();
    }

    private void PlaceIsland()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 10;
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
        UpdateCompactIdleDisplay();
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
            // Clicking banner opens the notification module
            SetActiveModule("notifications");
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
        SetActiveModule("notifications");
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

    private void OnMediaStateChanged(NextGenIsland.Services.MediaState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _compactHasMedia = state.HasMedia;
            _compactMediaPlaying = state.IsPlaying;
            _compactTitle = state.Title ?? "";
            _compactArtist = state.Artist ?? "";
            _compactArtwork = state.Thumbnail;
            ApplyAlbumArtworkPalette(state.Thumbnail);
            UpdateCompactIdleDisplay();

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

    private void ApplyAlbumArtworkPalette(ImageSource? artwork)
    {
        if (artwork is not BitmapSource bitmap)
        {
            _albumAccent = Color.FromRgb(91, 156, 194);
            _albumDeep = Color.FromRgb(29, 54, 72);
            AnimateIslandPalette();
            return;
        }

        try
        {
            // Downsample the artwork before reading pixels; this keeps the work lightweight.
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(
                Math.Max(1.0 / bitmap.PixelWidth, 24.0 / bitmap.PixelWidth),
                Math.Max(1.0 / bitmap.PixelHeight, 24.0 / bitmap.PixelHeight)));

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            byte[] pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            double weightedR = 0, weightedG = 0, weightedB = 0, totalWeight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i];
                double g = pixels[i + 1];
                double r = pixels[i + 2];
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                double brightness = max / 255.0;

                // Give colourful mid/high tones more influence than black borders or white text.
                double weight = 0.25 + saturation * 1.8 + (brightness > 0.12 && brightness < 0.92 ? 0.45 : 0);
                weightedR += r * weight;
                weightedG += g * weight;
                weightedB += b * weight;
                totalWeight += weight;
            }

            if (totalWeight > 0)
            {
                byte r = (byte)Math.Clamp(weightedR / totalWeight, 0, 255);
                byte g = (byte)Math.Clamp(weightedG / totalWeight, 0, 255);
                byte b = (byte)Math.Clamp(weightedB / totalWeight, 0, 255);

                // Keep the sampled colour vivid enough to read as a glass tint.
                var sampled = Color.FromRgb(r, g, b);
                _albumAccent = BoostSaturationAndBrightness(sampled);
                _albumDeep = Color.FromRgb(
                    (byte)(_albumAccent.R * 0.22),
                    (byte)(_albumAccent.G * 0.22),
                    (byte)(_albumAccent.B * 0.25));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Album palette extraction failed: " + ex.Message);
            _albumAccent = Color.FromRgb(91, 156, 194);
            _albumDeep = Color.FromRgb(29, 54, 72);
        }

        AnimateIslandPalette();
    }

    private static Color BoostSaturationAndBrightness(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0, s = max <= 0 ? 0 : delta / max, v = max;

        if (delta > 0)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);
        }
        if (h < 0) h += 360;
        s = Math.Clamp(s * 1.25 + 0.08, 0.28, 0.82);
        v = Math.Clamp(v * 1.15 + 0.08, 0.38, 0.92);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (double rr, double gg, double bb) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb(
            (byte)Math.Clamp((rr + m) * 255, 0, 255),
            (byte)Math.Clamp((gg + m) * 255, 0, 255),
            (byte)Math.Clamp((bb + m) * 255, 0, 255));
    }

    private void AnimateIslandPalette()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(AnimateIslandPalette);
            return;
        }

        var start = (IslandContainer.Background as LinearGradientBrush)?.GradientStops
            .Select(stop => stop.Color).ToArray();

        Color fromTop = start != null && start.Length > 0 ? start[0] : _albumAccent;
        Color fromMid = start != null && start.Length > 1 ? start[1] : _albumDeep;
        Color fromBottom = start != null && start.Length > 2 ? start[2] : _albumDeep;

        var top = new SolidColorBrush(fromTop);
        var middle = new SolidColorBrush(fromMid);
        var bottom = new SolidColorBrush(fromBottom);
        var border = new SolidColorBrush(Color.FromArgb(185, _albumAccent.R, _albumAccent.G, _albumAccent.B));
        top.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
            Color.FromArgb(205, _albumAccent.R, _albumAccent.G, _albumAccent.B),
            TimeSpan.FromMilliseconds(650)));
        middle.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
            Color.FromArgb(190, _albumDeep.R, _albumDeep.G, _albumDeep.B),
            TimeSpan.FromMilliseconds(650)));
        bottom.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
            Color.FromArgb(235,
                (byte)Math.Min(255, _albumDeep.R + 24),
                (byte)Math.Min(255, _albumDeep.G + 24),
                (byte)Math.Min(255, _albumDeep.B + 24)),
            TimeSpan.FromMilliseconds(650)));
        border.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
            Color.FromArgb(190, _albumAccent.R, _albumAccent.G, _albumAccent.B),
            TimeSpan.FromMilliseconds(650)));

        IslandContainer.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.9, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(top.Color, 0),
                new GradientStop(middle.Color, 0.52),
                new GradientStop(bottom.Color, 1)
            }
        };
        IslandContainer.BorderBrush = border;
        MediaStatusBorder.Background = new SolidColorBrush(Color.FromArgb(58, _albumAccent.R, _albumAccent.G, _albumAccent.B));
        CompactMediaState.Foreground = new SolidColorBrush(_albumAccent);
    }

    private void UpdateCompactIdleDisplay()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateCompactIdleDisplay);
            return;
        }

        CompactClockText.Text = DateTime.Now.ToString("h:mm tt");
        CompactDateText.Text = DateTime.Now.ToString("ddd, MMM d").ToUpperInvariant();

        CompactClockPanel.Visibility = _compactHasMedia ? Visibility.Collapsed : Visibility.Visible;
        CompactMediaPanel.Visibility = _compactHasMedia ? Visibility.Visible : Visibility.Collapsed;
        if (_compactHasMedia)
        {
            CompactMediaTitle.Text = string.IsNullOrWhiteSpace(_compactTitle) ? "Now Playing" : _compactTitle;
            CompactMediaArtist.Text = string.IsNullOrWhiteSpace(_compactArtist) ? "Media session" : _compactArtist;
            CompactMediaThumbnail.Source = _compactArtwork;
            CompactMediaState.Text = _compactMediaPlaying ? "♫" : "Ⅱ";
            CompactMediaState.Foreground = _compactMediaPlaying
                ? new SolidColorBrush(Color.FromRgb(199, 166, 255))
                : new SolidColorBrush(Color.FromRgb(142, 142, 147));
        }
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

    private void ModuleNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string module)
            SetActiveModule(module);
    }

    private void SetActiveModule(string module)
    {
        _activeModule = module;

        ModuleNotificationsView.Visibility = module == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        ModuleMediaView.Visibility = module == "media" ? Visibility.Visible : Visibility.Collapsed;
        ModuleSystemView.Visibility = module == "system" ? Visibility.Visible : Visibility.Collapsed;
        ModuleClipboardView.Visibility = module == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        ModuleRemindersView.Visibility = module == "reminders" ? Visibility.Visible : Visibility.Collapsed;
        ModuleShelfView.Visibility = module == "shelf" ? Visibility.Visible : Visibility.Collapsed;
        ModuleAgentsView.Visibility = module == "agents" ? Visibility.Visible : Visibility.Collapsed;
        ModuleWidgetsView.Visibility = module == "widgets" ? Visibility.Visible : Visibility.Collapsed;
        ModuleWebView.Visibility = module == "web" ? Visibility.Visible : Visibility.Collapsed;
        ModuleThemesView.Visibility = module == "themes" ? Visibility.Visible : Visibility.Collapsed;
        if (module == "reminders") RenderReminders();
        if (module == "shelf") RenderShelf();
        if (module == "agents") RefreshAgents();
        if (module == "widgets") { UpdateWidgetClock(); UpdateFocusTimerText(); }

        var activeBackground = new SolidColorBrush(Color.FromArgb(78, _albumAccent.R, _albumAccent.G, _albumAccent.B));
        SetNavigationButtonState(NavNotificationsButton, module == "notifications", activeBackground);
        SetNavigationButtonState(NavMediaButton, module == "media", activeBackground);
        SetNavigationButtonState(NavSystemButton, module == "system", activeBackground);
        SetNavigationButtonState(NavClipboardButton, module == "clipboard", activeBackground);
        SetNavigationButtonState(NavRemindersButton, module == "reminders", activeBackground);
        SetNavigationButtonState(NavShelfButton, module == "shelf", activeBackground);
        SetNavigationButtonState(NavAgentsButton, module == "agents", activeBackground);
        SetNavigationButtonState(NavWidgetsButton, module == "widgets", activeBackground);
        SetNavigationButtonState(NavWebButton, module == "web", activeBackground);
        SetNavigationButtonState(NavThemesButton, module == "themes", activeBackground);
    }

    private static void SetNavigationButtonState(
        System.Windows.Controls.Button button,
        bool isActive,
        Brush activeBackground)
    {
        button.Background = isActive ? activeBackground : Brushes.Transparent;
        button.Foreground = isActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(142, 142, 147));
        button.BorderBrush = isActive ? new SolidColorBrush(Color.FromRgb(75, 75, 96)) : Brushes.Transparent;
    }

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

            // Measure only the selected module. The island grows to its content instead
            // of expanding to the entire remaining desktop height.
            FrameworkElement activeView = _activeModule switch
            {
                "media" => ModuleMediaView,
                "system" => ModuleSystemView,
                "clipboard" => ModuleClipboardView,
                _ => ModuleNotificationsView
            };
            activeView.Measure(new Size(Math.Max(300, Width - 48), double.PositiveInfinity));
            double viewHeight = activeView.DesiredSize.Height;
            double scrollHeight = Math.Clamp(viewHeight + 86, 190, 480);
            double maxAvailable = Math.Max(220, SystemParameters.WorkArea.Height - Top - 40);
            scrollHeight = Math.Min(scrollHeight, Math.Max(190, maxAvailable - 82));

            ExpandedScrollViewer.MaxHeight = Math.Min(480, maxAvailable - 82);
            ExpandedScrollViewer.Height = scrollHeight;
            AnimateHeight(68 + 14 + scrollHeight);
            ExpandButton.Content = "⌃";
        }
        else
        {
            AnimateHeight(68, () =>
            {
                // Keep the root island layout visible so the compact clock/media
                // display remains rendered after the expanded panel collapses.
                ExpandedScrollViewer.Visibility = Visibility.Collapsed;
                ExpandedScrollViewer.Height = 300;
            });
            ExpandButton.Content = "⌄";
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

    private string ExtraPath(string name) => System.IO.Path.Combine(_extraDataDir, name);
    private void LoadExtraData()
    {
        try { Directory.CreateDirectory(_extraDataDir); if (File.Exists(ExtraPath("reminders.json"))) _reminders.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ExtraPath("reminders.json"))) ?? new()); } catch { }
        try { if (File.Exists(ExtraPath("shelf.json"))) _shelfFiles.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ExtraPath("shelf.json"))) ?? new()); } catch { }
    }
    private void SaveExtraData(string file, object data) { Directory.CreateDirectory(_extraDataDir); File.WriteAllText(ExtraPath(file), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); }
    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        var value = ReminderInput.Text?.Trim(); if (string.IsNullOrWhiteSpace(value)) return;
        _reminders.Insert(0, value); SaveExtraData("reminders.json", _reminders); ReminderInput.Clear(); RenderReminders();
    }
    private void RenderReminders()
    {
        if (ReminderItems == null) return; ReminderItems.Items.Clear();
        foreach (var item in _reminders.ToList()) { var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) }; var done = new Button { Content = "✓", Padding = new Thickness(7,2,7,2), Tag = item, Margin = new Thickness(0,0,7,0) }; done.Click += (_, _) => { _reminders.Remove(item); SaveExtraData("reminders.json", _reminders); RenderReminders(); }; row.Children.Add(done); row.Children.Add(new TextBlock { Text = item, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }); ReminderItems.Items.Add(row); }
    }
    private void Shelf_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    private void Shelf_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) if (File.Exists(f) && !_shelfFiles.Contains(f)) _shelfFiles.Insert(0, f); SaveExtraData("shelf.json", _shelfFiles); RenderShelf(); }
    }
    private void RenderShelf()
    {
        if (ShelfItems == null) return; ShelfItems.Items.Clear();
        foreach (var path in _shelfFiles.ToList()) { var row = new DockPanel { Margin = new Thickness(0,4,0,0) }; var remove = new Button { Content = "×", Tag = path, Padding = new Thickness(7,1,7,1), Margin = new Thickness(0,0,7,0) }; remove.Click += (_, _) => { _shelfFiles.Remove(path); SaveExtraData("shelf.json", _shelfFiles); RenderShelf(); }; row.Children.Add(remove); var open = new Button { Content = System.IO.Path.GetFileName(path), Tag = path, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6,3,6,3), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) }; open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }; row.Children.Add(open); ShelfItems.Items.Add(row); }
    }
    private void RefreshAgents_Click(object sender, RoutedEventArgs e) => RefreshAgents();
    private void RefreshAgents()
    {
        try { var matches = Process.GetProcesses().Where(p => { try { var n = p.ProcessName.ToLowerInvariant(); return n.Contains("hermes") || n.Contains("codex") || n.Contains("claude") || n.Contains("antigravity"); } catch { return false; } }).Select(p => p.ProcessName).Distinct().ToList(); AgentStatusText.Text = matches.Count == 0 ? "No recognized agent process detected. Start Hermes or another supported agent and refresh." : "Detected: " + string.Join(", ", matches); } catch { AgentStatusText.Text = "Unable to inspect local processes."; }
    }
    private void UpdateWidgetClock() { if (WidgetClockText != null) { WidgetClockText.Text = DateTime.Now.ToString("h:mm:ss tt"); WidgetDateText.Text = DateTime.Now.ToString("dddd, dd MMMM yyyy"); } }
    private void UpdateFocusTimerText() { if (FocusTimerText != null) FocusTimerText.Text = $"{_focusSeconds / 60:00}:{_focusSeconds % 60:00}"; }
    private void StartFocus_Click(object sender, RoutedEventArgs e) { if (_focusSeconds <= 0) _focusSeconds = 25 * 60; _focusTimer?.Start(); UpdateFocusTimerText(); }
    private void ResetFocus_Click(object sender, RoutedEventArgs e) { _focusTimer?.Stop(); _focusSeconds = 25 * 60; UpdateFocusTimerText(); }
    private static void OpenWeb(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
    private void OpenYouTube_Click(object sender, RoutedEventArgs e) => OpenWeb("https://www.youtube.com");
    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenWeb("https://github.com");
    private void OpenGmail_Click(object sender, RoutedEventArgs e) => OpenWeb("https://mail.google.com");
    private void OpenNotion_Click(object sender, RoutedEventArgs e) => OpenWeb("https://www.notion.so");
    private void ThemeObsidian_Click(object sender, RoutedEventArgs e) { IslandContainer.Background = new SolidColorBrush(Color.FromRgb(9,9,14)); IslandContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(48,48,61)); }
    private void ThemeGraphite_Click(object sender, RoutedEventArgs e) { IslandContainer.Background = new SolidColorBrush(Color.FromRgb(28,29,34)); IslandContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(75,77,88)); }
    private void ThemeAcrylic_Click(object sender, RoutedEventArgs e) { IslandContainer.Background = new SolidColorBrush(Color.FromArgb(235,35,38,48)); IslandContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(95,105,125)); }
}




