using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NextGenIsland.Services;
using Windows.Devices.Radios;

namespace NextGenIsland;

// Utility tab: brightness and volume sliders, Wi-Fi / Bluetooth / Mute / Quiet tiles, battery and power mode.
public partial class MainWindow
{
    private readonly DeviceControls _devices = new();
    private const double SliderHeight = 132, SliderWidth = 66;

    private float _vol;
    private bool _muted;
    private int _bright = -1;
    private RadioStates _radios = new(false, false, false, false);
    private bool _utilBusy, _utilRefreshing, _sliderDrag;

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private static readonly SolidColorBrush DarkInk = Frozen(0xFF, 0x14, 0x14, 0x16);
    private static readonly SolidColorBrush TileOff = Frozen(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush TileBlue = Frozen(0xFF, 0x1F, 0x5F, 0xC8);
    private static readonly SolidColorBrush TileRed = Frozen(0xFF, 0x8C, 0x2F, 0x34);
    private static readonly SolidColorBrush TilePurple = Frozen(0xFF, 0x58, 0x45, 0xA8);

    private void InitUtility()
    {
        var clip = new RectangleGeometry(new Rect(0, 0, SliderWidth, SliderHeight), 20, 20);
        clip.Freeze();
        BrightnessTrack.Clip = clip;
        VolumeTrack.Clip = clip;

        _devices.VolumeChanged += (v, m) => Dispatcher.InvokeAsync(() => OnVolumeEvent(v, m));
        _devices.BrightnessChanged += b => Dispatcher.InvokeAsync(() => OnBrightnessEvent(b));
        _hudQuietUntil = DateTime.Now.AddSeconds(3);   // no pop-up for the initial readings
        _devices.Start();
        _ = RefreshUtilityAsync();
    }

    // Raised for every volume change, including the keyboard keys.
    private void OnVolumeEvent(float volume, bool muted)
    {
        bool changed = Math.Abs(volume - _vol) > 0.004f || muted != _muted;
        _vol = volume;
        _muted = muted;
        UpdateVolumeUi();
        UpdateMuteTile();
        if (changed) ShowHud("volume", volume, muted);
    }

    private void OnBrightnessEvent(int brightness)
    {
        bool changed = brightness != _bright;
        _bright = brightness;
        UpdateBrightnessUi();
        if (changed) ShowHud("brightness", brightness / 100.0, false);
    }

    private async Task RefreshUtilityAsync()
    {
        if (_utilRefreshing) return;
        _utilRefreshing = true;
        try
        {
            var vol = _devices.GetVolume();
            var muted = _devices.GetMuted();
            if (vol != null) _vol = vol.Value;
            if (muted != null) _muted = muted.Value;
            UpdateVolumeUi();
            UpdateMuteTile();

            int? b = await Task.Run(() => _devices.GetBrightness());
            if (b != null) _bright = b.Value;
            UpdateBrightnessUi();

            UpdatePowerButtons(_devices.GetPowerMode());
            UpdateBatteryTile();
            UpdateQuietTile();

            _radios = await _devices.GetRadiosAsync();
            UpdateRadioTiles();
        }
        catch (Exception ex) { Debug.WriteLine("RefreshUtility: " + ex.Message); }
        finally { _utilRefreshing = false; }
    }

    // ---------------------------------------------------------------- sliders

    private static void SetSliderUi(Border fill, TextBlock pct, TextBlock icon, double v, string text)
    {
        v = Math.Clamp(v, 0.0, 1.0);
        fill.Height = SliderHeight * v;
        pct.Text = text;
        pct.Foreground = v >= 0.86 ? DarkInk : Brushes.White;
        icon.Foreground = v >= 0.14 ? DarkInk : Brushes.White;
    }

    private void UpdateVolumeUi()
    {
        bool silent = _muted || _vol <= 0.001f;
        VolumeIcon.Text = silent ? "\uE74F" : "\uE767";
        SetSliderUi(VolumeFill, VolumePct, VolumeIcon, _muted ? 0.0 : _vol,
                    _muted ? "Muted" : $"{Math.Round(_vol * 100):0}%");
    }

    private void UpdateBrightnessUi()
    {
        bool ok = _devices.BrightnessSupported && _bright >= 0;
        BrightnessTrack.Opacity = ok ? 1.0 : 0.5;
        BrightnessTrack.ToolTip = ok ? "Brightness" : "Brightness is not available on this display";
        SetSliderUi(BrightnessFill, BrightnessPct, BrightnessIcon, ok ? _bright / 100.0 : 0.0,
                    ok ? $"{_bright}%" : "N/A");
    }

    private void Slider_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        _sliderDrag = true;
        b.CaptureMouse();
        ApplySlider(b, e.GetPosition(b).Y);
        e.Handled = true;
    }

    private void Slider_Move(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.IsMouseCaptured) ApplySlider(b, e.GetPosition(b).Y);
    }

    private void Slider_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || !b.IsMouseCaptured) return;
        ApplySlider(b, e.GetPosition(b).Y);
        b.ReleaseMouseCapture();
        _sliderDrag = false;
        e.Handled = true;
    }

    private void ApplySlider(Border track, double y)
    {
        double v = Math.Clamp(1.0 - y / SliderHeight, 0.0, 1.0);
        if (track.Tag as string == "Volume")
        {
            _vol = (float)v;
            if (_muted && v > 0.0) { _muted = false; _devices.SetMuted(false); }
            _devices.SetVolume(_vol);
            UpdateVolumeUi();
            UpdateMuteTile();
        }
        else
        {
            if (!_devices.BrightnessSupported) return;
            int p = Math.Max(1, (int)Math.Round(v * 100));   // never fully black
            _bright = p;
            QueueBrightness(p);
            UpdateBrightnessUi();
        }
    }

    // WMI can take tens of milliseconds, so brightness is set on a worker thread, newest value wins.
    private readonly object _brightLock = new();
    private int _brightTarget = -1;
    private bool _brightBusy;

    private void QueueBrightness(int percent)
    {
        bool start;
        lock (_brightLock)
        {
            _brightTarget = percent;
            start = !_brightBusy;
            _brightBusy = true;
        }
        if (!start) return;

        _ = Task.Run(() =>
        {
            while (true)
            {
                int target;
                lock (_brightLock)
                {
                    target = _brightTarget;
                    if (target < 0) { _brightBusy = false; return; }
                    _brightTarget = -1;
                }
                _devices.SetBrightness(target);
            }
        });
    }

    // ---------------------------------------------------------------- tiles

    private static void PaintTile(Border tile, TextBlock state, string text, Brush background, bool enabled = true)
    {
        tile.Background = background;
        tile.Opacity = enabled ? 1.0 : 0.45;
        state.Text = text;
    }

    private void UpdateRadioTiles()
    {
        PaintTile(WifiTile, WifiState, !_radios.WifiPresent ? "N/A" : _radios.WifiOn ? "On" : "Off",
                  _radios.WifiOn ? TileBlue : TileOff, _radios.WifiPresent);
        PaintTile(BluetoothTile, BluetoothState, !_radios.BluetoothPresent ? "N/A" : _radios.BluetoothOn ? "On" : "Off",
                  _radios.BluetoothOn ? TileBlue : TileOff, _radios.BluetoothPresent);
    }

    private void UpdateMuteTile()
        => PaintTile(MuteTile, MuteState, _muted ? "Muted" : "Sound on", _muted ? TileRed : TileOff);

    private void UpdateQuietTile()
        => PaintTile(QuietTile, QuietState, _settings.QuietMode ? "On" : "Off", _settings.QuietMode ? TilePurple : TileOff);

    private void UpdateBatteryTile()
    {
        var b = _batteryMonitor.Read();
        BatteryValue.Text = b.HasBattery ? $"{b.Percent}%" : "AC";
        BatteryState.Text = !b.HasBattery ? "Desktop power" : b.IsCharging ? "Charging" : b.IsPlugged ? "Plugged in" : "On battery";
    }

    private void UtilityTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        switch (tag)
        {
            case "Wifi": _ = ToggleRadioAsync(RadioKind.WiFi); break;
            case "Bluetooth": _ = ToggleRadioAsync(RadioKind.Bluetooth); break;
            case "Mute":
                _muted = !_muted;
                _devices.SetMuted(_muted);
                UpdateVolumeUi();
                UpdateMuteTile();
                break;
            case "Quiet":
                ToggleQuietMode_Click(this, new RoutedEventArgs());
                break;
        }
        e.Handled = true;
    }

    private async Task ToggleRadioAsync(RadioKind kind)
    {
        if (_utilBusy) return;
        _utilBusy = true;
        try
        {
            bool wifi = kind == RadioKind.WiFi;
            bool present = wifi ? _radios.WifiPresent : _radios.BluetoothPresent;
            bool on = wifi ? _radios.WifiOn : _radios.BluetoothOn;
            if (!present) return;

            bool ok = await _devices.SetRadioAsync(kind, !on);
            _radios = await _devices.GetRadiosAsync();
            UpdateRadioTiles();
            if (!ok) (wifi ? WifiState : BluetoothState).Text = "Blocked";
        }
        finally { _utilBusy = false; }
    }

    // ---------------------------------------------------------------- power mode

    private void PowerMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t }) return;
        var mode = t switch
        {
            "Efficiency" => PowerMode.Efficiency,
            "Performance" => PowerMode.Performance,
            _ => PowerMode.Balanced
        };
        _devices.SetPowerMode(mode);
        UpdatePowerButtons(_devices.GetPowerMode());   // always show what Windows actually applied
    }

    private void UpdatePowerButtons(PowerMode mode)
    {
        void Paint(Button b, bool selected)
        {
            b.Background = selected ? Brushes.White : Brushes.Transparent;
            b.Foreground = selected ? DarkInk : Frozen(0xCC, 0xFF, 0xFF, 0xFF);
        }
        Paint(PowerEco, mode == PowerMode.Efficiency);
        Paint(PowerBalanced, mode == PowerMode.Balanced);
        Paint(PowerPerf, mode == PowerMode.Performance);
    }
}
