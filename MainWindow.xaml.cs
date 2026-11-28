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
    private const double CompactWidth = 212, BannerWidth = 410, ExpandedWidth = 420;
    private const double CompactHeight = 38, BannerHeight = 66, ExpandedHeight = 180;
    private const double ShadowPad = 14;
    private const double NavSlot = 28;

    private static readonly string[] ModuleKeys = { "Media", "Notifications", "Clipboard", "System", "Utility", "Shelf", "Reminders", "Agents", "Widgets", "Web", "Calls" };

    private readonly ClipboardStore _store = new();
    private readonly SystemMonitorService _monitor = new();
    private readonly GpuMonitorService _gpuMonitor = new();
    private readonly MediaControllerService _mediaService = new();
    private readonly BatteryMonitorService _batteryMonitor = new();
    private readonly NetworkMonitorService _networkMonitor;
    private readonly NotificationService _notificationService = new();
    private WindowsNotificationReader? _wpnReader;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private AppSettings _settings;
    private HwndSource? _source;
    private NotifyIconHost? _tray;
    private DispatcherTimer? _collapseTimer;

    private bool _expanded, _allowExit, _ready;
    private bool _batteryWarningFired;
    private bool _isHovering, _isPinned, _suspendCollapse;
    private bool _bannerActive;

    private static readonly SolidColorBrush DotOnline = Frozen(48, 209, 88), DotOffline = Frozen(255, 69, 58);
    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
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

        InitVoice(hwnd);

        await _store.InitializeAsync();

        _tray = new NotifyIconHost(this);

        _mediaService.MediaStateChanged += OnMediaStateChanged;
        await _mediaService.InitializeAsync();

        _notificationService.ActiveNotificationChanged += OnActiveNotificationChanged;
        _notificationService.HistoryChanged += OnNotificationHistoryChanged;
        _notificationService.WindowsListenerStatusChanged += OnWindowsListenerStatusChanged;
        await _notificationService.InitializeAsync();
        _wpnReader = new WindowsNotificationReader(_notificationService);
        ApplyOtherAppNotifications();

        UpdateQuietModeUI();

        _ready = true;
        InitModules();
        NavMedia.IsChecked = true;      // fires Nav_Checked -> SelectModule("Media")
        CompactTime.Text = ClockText(DateTime.Now);

        _timer.Start();
        await RefreshMetricsAsync();
        _ = ShowWelcomeOnceAsync();
    }

    // First launch only: a short banner that says how to use the island.
    private async Task ShowWelcomeOnceAsync()
    {
        try
        {
            if (_settings.WelcomeShown) return;
            await Task.Delay(3500);
            _settings.WelcomeShown = true;
            SettingsStore.Save(_settings);
            _notificationService.PostNotification(
                "NextGen Island",
                "Welcome",
                "Hover the pill to open it. Right-click the tray icon for settings.");
        }
        catch (Exception ex) { AppLog.Write("Welcome banner failed", ex); }
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
        else if (msg == WmHotkey && wParam.ToInt32() == VoiceHotkeyId) { OnVoiceTrigger(true); handled = true; }
        return IntPtr.Zero;
    }

    private async Task CaptureClipboardAsync()
    {
        try
        {
            if (SettingsStore.Load().ClipboardPaused) return;
            string? status = await ClipboardAccess.CaptureAsync(_store);
            if (status == null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                ClipboardStatus.Text = status;
                if (ViewClipboard.Visibility == Visibility.Visible) _ = RenderClipboardAsync();
            });
        }
        catch (Exception ex) { Debug.WriteLine("Clipboard capture: " + ex.Message); }
    }

    private async Task OnTimerTickAsync()
    {
        CompactTime.Text = ClockText(DateTime.Now);
        CheckFullscreen();
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

            // System-tab widgets only matter while that tab is on screen; skip the layout work otherwise.
            if (ViewSystem.IsVisible && _expanded)
            {
                CpuBar.Value = m.Cpu;
                RamBar.Value = m.Ram;
                CpuText.Text = $"{m.Cpu:0}%";
                RamText.Text = $"{m.Ram:0}%";

                if (g.IsAvailable)
                {
                    GpuBar.Value = g.Utilization;
                    GpuText.Text = g.UtilDisplay;
                    GpuTempText.Text = _settings.UseFahrenheit ? FormatTemp(g.Temperature) + "F" : g.TempDisplay;
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
                NetworkStatusDot.Foreground = DotOnline;
                NetworkSpeedText.Text = $"{n.DownloadSpeedText} | {n.UploadSpeedText}";
            }
            else
            {
                NetworkNameText.Text = "Offline";
                NetworkStatusDot.Foreground = DotOffline;
                NetworkSpeedText.Text = "Disconnected";
            }

            _pillCpu = $"{m.Cpu:0}%";
            _pillRight = g.IsAvailable ? FormatTemp(g.Temperature) : $"{m.Ram:0}%";
            _pillRightLabel = g.IsAvailable ? null : "RAM";
            UpdateCompactText();
        }
        catch (Exception ex) { Debug.WriteLine("RefreshMetricsAsync error: " + ex); }
    }

    private string _pillCpu = "--%", _pillRight = "--";
    private string? _pillRightLabel;

    // Draws the collapsed pill according to Settings > Collapsed pill shows (a song title replaces it while playing).
    private void UpdateCompactText()
    {
        if (UpdateTimerChip()) return;   // a running timer takes over the pill with its countdown ring
        bool media = _mediaLabel != null;
        CompactMetrics.Text = _mediaLabel ?? "";
        CompactMetrics.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        if (media)
        {
            CompactStats.Visibility = Visibility.Collapsed;
            CompactTime.Visibility = Visibility.Collapsed;
            return;
        }

        string mode = _settings.PillContent;
        CompactStats.Visibility = mode == "Clock" ? Visibility.Collapsed : Visibility.Visible;
        CompactTime.Visibility = mode == "CpuTemp" ? Visibility.Collapsed : Visibility.Visible;
        CompactTime.HorizontalAlignment = mode == "Clock" ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        CompactTime.Text = ClockText(DateTime.Now);

        if (mode == "ClockCpu")
        {
            CompactCpuStack.Visibility = Visibility.Collapsed;
            CompactRightLabel.Text = "CPU";
            CompactRightLabel.Visibility = Visibility.Visible;
            CompactRightValue.Text = _pillCpu;
        }
        else
        {
            CompactCpuStack.Visibility = Visibility.Visible;
            CompactCpuValue.Text = _pillCpu;
            CompactRightLabel.Text = _pillRightLabel ?? "";
            CompactRightLabel.Visibility = _pillRightLabel == null ? Visibility.Collapsed : Visibility.Visible;
            CompactRightValue.Text = _pillRight;
        }
    }

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
            _listenerAllowed = isAllowed;
            ApplyOtherAppNotifications();
        });
    }

    private bool _listenerAllowed;

    // Starts or stops the notification-history reader and shows the grey note only when other apps' notifications are unavailable.
    private void ApplyOtherAppNotifications()
    {
        _wpnReader?.SetEnabled(_settings.ShowOtherAppNotifications);
        PermissionWarningBanner.Visibility = (_listenerAllowed || _settings.ShowOtherAppNotifications)
            ? Visibility.Collapsed : Visibility.Visible;
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
        UpdateQuietTile();
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
                MediaSourceBadge.Visibility = Visibility.Visible;
                MediaSourceBadge.ToolTip = string.IsNullOrWhiteSpace(state.AppName) ? null : state.AppName;

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
                MediaSourceBadge.Visibility = Visibility.Collapsed;
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
        AnimateBrush(BadgeBrush, p.Accent);
        MediaBadgeGlyph.Foreground = new SolidColorBrush(p.OnAccent);
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

        ProgressKnob.Visibility = hasDuration ? Visibility.Visible : Visibility.Collapsed;
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

    private DispatcherTimer? _openTimer;

    // Slow / Normal / Fast multiplies the length of the island animation.
    private double AnimFactor => _settings.AnimationSpeed switch { "Slow" => 1.6, "Fast" => 0.6, _ => 1.0 };

    private void IslandContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        _collapseTimer?.Stop();
        if (!_settings.HoverExpansionEnabled || _expanded || _voiceActive) return;

        int delay = _settings.HoverOpenDelayMs;
        if (delay <= 0) { ExpandIsland(true); return; }

        if (_openTimer == null)
        {
            _openTimer = new DispatcherTimer();
            _openTimer.Tick += (_, _) =>
            {
                _openTimer?.Stop();
                if (_isHovering && !_expanded && _settings.HoverExpansionEnabled) ExpandIsland(true);
            };
        }
        _openTimer.Stop();
        _openTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _openTimer.Start();
    }

    // With "open on hover" turned off, a click on the collapsed island opens it.
    private void IslandContainer_Click(object sender, MouseButtonEventArgs e)
    {
        if (_settings.HoverExpansionEnabled || _expanded || _bannerActive || _hudActive || _voiceActive) return;
        ExpandIsland(true);
        e.Handled = true;
    }

    private void IslandContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        _openTimer?.Stop();
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
        ApplyLineLook();
        ApplyPalette(EffectivePalette());

        if (expand)
        {
            _hudActive = false;
            _hudTimer.Stop();
            Present(HudBanner, false);
            Present(VoiceBanner, false);
            Present(NotificationBanner, false);
            Present(CompactHeader, false);
            SyncExpandedHeight();

            ExpandedRoot.Visibility = Visibility.Visible;
            FadeTo(ExpandedRoot, 1.0, (int)(240 * AnimFactor), (int)(80 * AnimFactor));
            ShiftExpanded(opening: true);

            AnimateIsland(ExpandedWidth, HeightFor(_activeKey), overshoot: true);
        }
        else
        {
            ShiftExpanded(opening: false);
            FadeTo(ExpandedRoot, 0.0, (int)(110 * AnimFactor), 0, () =>
            {
                if (!_expanded) ExpandedRoot.Visibility = Visibility.Collapsed;
            });

            ShowCollapsedState();
        }
    }

    // Shows either the compact pill or the notification banner, sized correctly.
    private void ShowCollapsedState()
    {
        ApplyLineLook();
        if (_voiceActive)
        {
            // Voice command: listening wave, "Working on it", then the result.
            Present(CompactHeader, false);
            Present(NotificationBanner, false);
            Present(HudBanner, false);
            Present(VoiceBanner, true, 110);
            AnimateIsland(VoiceWidth, VoiceHeight, overshoot: true, amplitude: 0.10);
            return;
        }
        Present(VoiceBanner, false);
        if (_hudActive)
        {
            // Volume / brightness pop-up (also shown over a full-screen app).
            Present(CompactHeader, false);
            Present(NotificationBanner, false);
            Present(HudBanner, true, 110);
            AnimateIsland(HudWidth, HudHeight, overshoot: true, amplitude: 0.10);
            return;
        }
        Present(HudBanner, false);

        if (_fullscreen)
        {
            // Full-screen app in front: just a thin line (banners wait in the history).
            Present(CompactHeader, false);
            Present(NotificationBanner, false);
            AnimateIsland(LineWidth, LineHeight);
            return;
        }

        if (_bannerActive)
        {
            Present(CompactHeader, false);
            Present(NotificationBanner, true, 110);
            AnimateIsland(BannerWidth, BannerHeight, overshoot: true, amplitude: 0.08);
        }
        else
        {
            Present(NotificationBanner, false);
            Present(CompactHeader, true, 140);
            AnimateIsland(CompactWidth, CompactHeight);
        }
    }

    // The window is a fixed, transparent canvas; only the island itself changes size. That keeps the
    // window from being resized every frame, which is what made the old animation stutter.
    // Expanding gets a small springy overshoot; collapsing is a plain ease-out.
    private void AnimateIsland(double islandWidth, double islandHeight, bool overshoot = false, double amplitude = 0.16)
    {
        IEasingFunction ease = overshoot
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude }
            : new QuarticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds((overshoot ? 420 : 320) * AnimFactor);

        // While the island changes size, the expanded content is drawn from a cached bitmap, so it is not
        // re-rendered on every frame.
        bool cache = ExpandedRoot.Visibility == Visibility.Visible;
        if (cache) ExpandedRoot.CacheMode = _animCache;

        IslandContainer.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = islandWidth,
            Duration = dur,
            EasingFunction = ease
        });

        var heightAnim = new DoubleAnimation
        {
            To = islandHeight,
            Duration = dur,
            EasingFunction = ease
        };
        heightAnim.Completed += (_, _) => ExpandedRoot.CacheMode = null;
        IslandContainer.BeginAnimation(HeightProperty, heightAnim);
    }

    // The expanded content keeps a fixed size while the island grows around it, so it is not re-laid-out every frame.
    private void SyncExpandedHeight()
        => ExpandedRoot.Height = Math.Max(0.0, HeightFor(_activeKey) - 2.0);

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
            ApplyOtherAppNotifications();
            _networkMonitor.SetSelectedInterface(_settings.SelectedNetworkInterface);
            UpdateCompactText();
            UpdateQuietModeUI();
            ApplyCardVisibility();
            ApplyPalette(EffectivePalette());
            ApplyShape();
            _lyricKey = "x";   // forces RequestLyrics to re-evaluate after a settings change
            UpdateLyricsIcon();
            _weatherAt = DateTime.MinValue;   // a new weather city takes effect right away
            if (_activeKey == "Widgets") _ = RefreshWeatherAsync();
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
        _devices.Dispose();
        _mediaService.Dispose();
        ShutdownVoice();
        _wpnReader?.Dispose();
        _notificationService.Dispose();
        Application.Current.Shutdown();
    }

    public void OpenSettingsFromTray()
    {
        RestoreFromTray();
        Settings_Click(this, new RoutedEventArgs());
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
