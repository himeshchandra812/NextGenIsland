using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    // Island sizes (the window is island height + ShadowPad so the shadow is not clipped).
    private const double CompactWidth = 330, BannerWidth = 410, ExpandedWidth = 420;
    private const double CompactHeight = 44, BannerHeight = 66, ExpandedHeight = 180;
    private const double ShadowPad = 14;
    private const double NavSlot = 28;

    private static readonly string[] ModuleKeys = { "Media", "Notifications", "Clipboard", "System", "Shelf", "Reminders", "Agents", "Widgets", "Web", "Calls", "Themes" };

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
    private DispatcherTimer? _collapseTimer;

    private bool _expanded, _allowExit, _ready;
    private bool _batteryWarningFired;
    private bool _isHovering, _isPinned, _suspendCollapse;
    private bool _bannerActive;

    private string _metricsSummary = "CPU --% \u2022 RAM --%";
    private string? _mediaLabel;
    private string _paletteKey = "none";

    // Media progress / seeking
    private TimeSpan _duration, _position;
    private bool _canSeek, _dragging;
    private DateTime _seekHoldUntil = DateTime.MinValue;

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

        _ready = true;
        InitModules();
        NavMedia.IsChecked = true;      // fires Nav_Checked -> SelectModule("Media")
        CompactTime.Text = DateTime.Now.ToString("HH:mm");

        _timer.Start();
        await RefreshMetricsAsync();
    }

    // The island is always anchored to the top-centre of the work area. It is never draggable.
    private void PlaceIsland()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
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
                if (ViewClipboard.Visibility == Visibility.Visible) _ = RenderClipboardAsync();
            });
        }
        catch (Exception ex) { Debug.WriteLine("Clipboard capture: " + ex.Message); }
    }

    private async Task OnTimerTickAsync()
    {
        CompactTime.Text = DateTime.Now.ToString("HH:mm");
        await RefreshMetricsAsync();
        await _mediaService.RefreshMediaStateAsync();
        TickModules();
    }

    // ---------------------------------------------------------------- metrics

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
                GpuText.Text = "\u2014";
                GpuTempText.Text = "Offline";
                GpuNameText.Text = "GPU unavailable";
                GpuBar.Value = 0;
                VramText.Text = "\u2014";
                VramBar.Value = 0;
            }

            if (b.HasBattery)
            {
                BatteryGlyphText.Text = b.Glyph;
                BatteryPercentText.Text = $"{b.Percent}%";
                BatteryStatusText.Text = b.StatusText;
            }
            else
            {
                BatteryGlyphText.Text = "\u26A1";
                BatteryPercentText.Text = "AC";
                BatteryStatusText.Text = "Desktop power";
            }

            if (_settings.BatteryWarningsEnabled && b.IsLow)
            {
                if (!_batteryWarningFired)
                {
                    _batteryWarningFired = true;
                    _notificationService.PostNotification(
                        "Battery",
                        "Low Battery Warning",
                        $"Battery is at {b.Percent}%. Connect charger to avoid shutdown.",
                        isSystemAlert: true);
                }
            }
            else if (!b.IsLow)
            {
                _batteryWarningFired = false;
            }

            if (n != null && n.IsConnected)
            {
                NetworkNameText.Text = n.AdapterName;
                NetworkStatusDot.Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88));
                NetworkSpeedText.Text = $"{n.DownloadSpeedText} | {n.UploadSpeedText}";
            }
            else
            {
                NetworkNameText.Text = "Offline";
                NetworkStatusDot.Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                NetworkSpeedText.Text = "Disconnected";
            }

            _metricsSummary = $"CPU {m.Cpu:0}% \u2022 RAM {m.Ram:0}%";
            UpdateCompactText();
        }
        catch (Exception ex) { Debug.WriteLine("RefreshMetricsAsync error: " + ex); }
    }

    private void UpdateCompactText() => CompactMetrics.Text = _mediaLabel ?? _metricsSummary;

    // ---------------------------------------------------------------- notifications

    private void OnActiveNotificationChanged(IslandNotification? notif)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (notif != null)
            {
                BannerAppNameText.Text = notif.AppName;
                BannerTitleText.Text = notif.Title;
                BannerBodyText.Text = notif.Message;

                SetBadge(true);
                _bannerActive = true;
            }
            else
            {
                _bannerActive = false;
            }

            if (!_expanded) ShowCollapsedState();
        });
    }

    private void OnNotificationHistoryChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            int count = _notificationService.History.Count;
            NotificationCountText.Text = count > 0 ? $"{count} ITEMS" : "0 NEW";
            SetBadge(count > 0);

            RenderNotifications();
        });
    }

    private void SetBadge(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        BellBadge.Visibility = v;
        CompactBellBadge.Visibility = v;
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
            OpenNotifications();
        }
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e) => _notificationService.DismissCurrent();
    private void DismissCurrentNotification_Click(object sender, RoutedEventArgs e) => _notificationService.DismissCurrent();
    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _notificationService.ClearHistory();

    private void BellButton_Click(object sender, RoutedEventArgs e)
    {
        OpenNotifications();
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
        QuietModeBtn.Content = _settings.QuietMode ? "Quiet: On" : "Quiet: Off";
        QuietModeBtn.Foreground = _settings.QuietMode
            ? new SolidColorBrush(Color.FromRgb(255, 159, 10))
            : Brushes.White;
    }

    private void OpenWindowsNotificationSettings_Click(object sender, RoutedEventArgs e)
        => NotificationService.OpenWindowsNotificationSettings();

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.PostNotification(
            "NextGen Island",
            "Test notification",
            "Banner, queue and auto-dismiss are working.",
            isSystemAlert: false);
    }

    // ---------------------------------------------------------------- media + album colours

    private void OnMediaStateChanged(NextGenIsland.Services.MediaState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            MediaTitleText.Text = state.Title;
            MediaArtistText.Text = state.Artist;

            if (state.HasMedia)
            {
                MediaStatusBadge.Text = state.IsPlaying ? "PLAYING" : "PAUSED";
                MediaAppText.Text = !string.IsNullOrWhiteSpace(state.AppName) ? "\u2022 " + state.AppName : "";
                MediaPlayPauseButton.Content = state.IsPlaying ? "\uE769" : "\uE768";

                if (state.Thumbnail != null)
                {
                    if (!ReferenceEquals(MediaArtBrush.ImageSource, state.Thumbnail))
                        MediaArtBrush.ImageSource = state.Thumbnail;
                    MediaArtBorder.Visibility = Visibility.Visible;
                    MediaDefaultArt.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MediaArtBorder.Visibility = Visibility.Collapsed;
                    MediaDefaultArt.Visibility = Visibility.Visible;
                }

                _mediaLabel = state.IsPlaying ? "\u266B " + state.Title : null;
            }
            else
            {
                MediaStatusBadge.Text = "NO MEDIA";
                MediaAppText.Text = "";
                MediaPlayPauseButton.Content = "\uE768";
                MediaArtBorder.Visibility = Visibility.Collapsed;
                MediaDefaultArt.Visibility = Visibility.Visible;
                _mediaLabel = null;
            }

            UpdateCompactText();
            UpdatePalette(state);
            RequestLyrics(state);
            UpdateProgress(state);
        });
    }

    // Only recompute colours when the track (or artwork presence) actually changes.
    private void UpdatePalette(NextGenIsland.Services.MediaState state)
    {
        string key = state.HasMedia ? $"{state.Title}|{state.Artist}|{state.Thumbnail != null}" : "none";
        if (key == _paletteKey) return;
        _paletteKey = key;

        var palette = state.HasMedia && state.Thumbnail != null
            ? AlbumPalette.Extract(state.Thumbnail)
            : AlbumPalette.Fallback;

        _albumPalette = palette;
        ApplyPalette(EffectivePalette());
    }

    private void ApplyPalette(IslandPalette p)
    {
        AnimateStop(BgStop1, p.BackgroundStart);
        AnimateStop(BgStop2, p.BackgroundEnd);
        AnimateBrush(IslandBorderBrush, p.Border);
        AnimateBrush(NavIndicatorBrush, p.NavIndicator);
        AnimateBrush(PlayBrush, p.Accent);
        AnimateBrush(ProgressBrush, p.Accent);
        MediaPlayPauseButton.Foreground = new SolidColorBrush(p.OnAccent);
    }

    private static ColorAnimation MakeColorAnimation(Color to) => new()
    {
        To = to,
        Duration = TimeSpan.FromMilliseconds(650),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
    };

    private static void AnimateStop(GradientStop stop, Color to)
        => stop.BeginAnimation(GradientStop.ColorProperty, MakeColorAnimation(to));

    private static void AnimateBrush(SolidColorBrush brush, Color to)
        => brush.BeginAnimation(SolidColorBrush.ColorProperty, MakeColorAnimation(to));

    // ---------------------------------------------------------------- progress / seeking

    private void UpdateProgress(NextGenIsland.Services.MediaState state)
    {
        _duration = state.HasMedia ? state.Duration : TimeSpan.Zero;
        _canSeek = state.HasMedia && state.CanSeek && _duration > TimeSpan.Zero;
        ProgressHit.Cursor = _canSeek ? Cursors.Hand : Cursors.Arrow;

        // Ignore updates while the user is dragging, or just after a seek (the player may still report the old time).
        if (_dragging || DateTime.UtcNow < _seekHoldUntil) return;

        _position = state.HasMedia ? state.Position : TimeSpan.Zero;
        ShowProgress(_position, animate: true);
    }

    private void ShowProgress(TimeSpan position, bool animate)
    {
        bool hasDuration = _duration > TimeSpan.Zero;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (hasDuration && position > _duration) position = _duration;

        double ratio = hasDuration ? position.TotalSeconds / _duration.TotalSeconds : 0.0;
        double target = Math.Max(0.0, ProgressHit.ActualWidth) * Math.Clamp(ratio, 0.0, 1.0);

        if (animate)
        {
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(900)
            });
        }
        else
        {
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = target;
        }

        UpdateLyric(position);
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = hasDuration ? "-" + FormatTime(_duration - position) : "";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private TimeSpan PositionFromX(double x)
    {
        double w = ProgressHit.ActualWidth;
        if (w <= 0.0 || _duration <= TimeSpan.Zero) return TimeSpan.Zero;
        double r = Math.Clamp(x / w, 0.0, 1.0);
        return TimeSpan.FromSeconds(_duration.TotalSeconds * r);
    }

    private void ProgressHit_SizeChanged(object sender, SizeChangedEventArgs e)
        => ShowProgress(_position, animate: false);

    private void ProgressHit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_canSeek) return;
        _dragging = true;
        ProgressHit.CaptureMouse();
        ShowProgress(PositionFromX(e.GetPosition(ProgressHit).X), animate: false);
        e.Handled = true;
    }

    private void ProgressHit_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
            ShowProgress(PositionFromX(e.GetPosition(ProgressHit).X), animate: false);
    }

    private async void ProgressHit_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        var target = PositionFromX(e.GetPosition(ProgressHit).X);
        _dragging = false;
        ProgressHit.ReleaseMouseCapture();

        _position = target;
        _seekHoldUntil = DateTime.UtcNow.AddMilliseconds(800);
        ShowProgress(target, animate: false);

        await _mediaService.SeekAsync(target);
    }

    private void ProgressHit_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void MediaPrev_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    // ---------------------------------------------------------------- navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_suppressNav) return;
        if (sender is RadioButton rb && rb.Tag is string key) SelectModule(key);
    }

    // ---------------------------------------------------------------- hover / pin

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
        if (_isPinned || _suspendCollapse) return;
        if (_collapseTimer != null)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelayMs);
            _collapseTimer.Start();
        }
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer?.Stop();
        if (!_isHovering && !_isPinned && !_suspendCollapse && !ModuleBusy)
            ExpandIsland(false);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ClickPinEnabled)
        {
            _isPinned = !_isPinned;
            PinButton.Opacity = _isPinned ? 1.0 : 0.55;
            if (_isPinned) ExpandIsland(true);
            else if (!_isHovering) ExpandIsland(false);
        }
        else
        {
            ExpandIsland(!_expanded);
        }
    }

    // ---------------------------------------------------------------- expand / collapse

    private void ExpandIsland(bool expand)
    {
        _expanded = expand;
        ApplyPalette(EffectivePalette());

        if (expand)
        {
            NotificationBanner.Visibility = Visibility.Collapsed;

            ExpandedRoot.Visibility = Visibility.Visible;
            FadeTo(ExpandedRoot, 1.0, 220, 70);
            FadeTo(CompactHeader, 0.0, 110, 0, () =>
            {
                if (_expanded) CompactHeader.Visibility = Visibility.Collapsed;
            });

            AnimateIsland(ExpandedWidth, HeightFor(_activeKey), overshoot: true);
        }
        else
        {
            FadeTo(ExpandedRoot, 0.0, 120, 0, () =>
            {
                if (!_expanded) ExpandedRoot.Visibility = Visibility.Collapsed;
            });

            ShowCollapsedState();
        }
    }

    // Shows either the compact pill or the notification banner, sized correctly.
    private void ShowCollapsedState()
    {
        if (_bannerActive)
        {
            CompactHeader.Visibility = Visibility.Collapsed;
            NotificationBanner.Visibility = Visibility.Visible;
            AnimateIsland(BannerWidth, BannerHeight);
        }
        else
        {
            NotificationBanner.Visibility = Visibility.Collapsed;
            CompactHeader.Visibility = Visibility.Visible;
            FadeTo(CompactHeader, 1.0, 200, 100);
            AnimateIsland(CompactWidth, CompactHeight);
        }
    }

    // Expanding gets a small springy overshoot; collapsing is a plain ease-out.
    private void AnimateIsland(double islandWidth, double islandHeight, bool overshoot = false)
    {
        IEasingFunction ease = overshoot
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(overshoot ? 380 : 280);

        IslandContainer.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = islandWidth,
            Duration = dur,
            EasingFunction = ease
        });

        BeginAnimation(HeightProperty, new DoubleAnimation
        {
            To = islandHeight + ShadowPad + _topGap,
            Duration = dur,
            EasingFunction = ease
        });
    }

    private static void FadeTo(UIElement element, double to, int milliseconds, int delayMs = 0, Action? done = null)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        if (done != null) anim.Completed += (_, _) => done();
        element.BeginAnimation(OpacityProperty, anim);
    }

    // ---------------------------------------------------------------- dialogs

    private void OpenClipboard_Click(object sender, RoutedEventArgs e)
    {
        BeginModal();
        try { new ClipboardWindow(_store) { Owner = this }.ShowDialog(); }
        finally { EndModal(); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        BeginModal();
        try
        {
            new SettingsWindow { Owner = this }.ShowDialog();

            _settings = SettingsStore.Load();
            _notificationService.NotificationsEnabled = _settings.NotificationsEnabled;
            _notificationService.QuietMode = _settings.QuietMode;
            _notificationService.DisplayDurationSeconds = _settings.NotificationDurationSeconds;
            _networkMonitor.SetSelectedInterface(_settings.SelectedNetworkInterface);
            UpdateQuietModeUI();
            ApplyCardVisibility();
            ApplyPalette(EffectivePalette());
            ApplyShape();
            _lyricKey = "x";   // forces RequestLyrics to re-evaluate after a settings change
        }
        finally { EndModal(); }
    }

    // While a dialog is open the island must not collapse underneath it.
    private void BeginModal()
    {
        _suspendCollapse = true;
        _collapseTimer?.Stop();
    }

    private void EndModal()
    {
        _suspendCollapse = false;
        _isHovering = IslandContainer.IsMouseOver;
        if (!_isHovering && !_isPinned) _collapseTimer?.Start();
    }

    // ---------------------------------------------------------------- lifetime (unchanged behaviour)

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit) { e.Cancel = true; Hide(); _tray?.ShowBalloon("Still running in the system tray."); return; }

        _timer.Stop();
        _focusTimer.Stop();
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

        PlaceIsland();
        Topmost = false;
        Topmost = true;
        Activate();
    }

    public void ExitApplication() { _allowExit = true; Close(); }
}
