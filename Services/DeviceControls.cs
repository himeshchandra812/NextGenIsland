using System.Management;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;

namespace NextGenIsland.Services;

public enum PowerMode { Unknown, Efficiency, Balanced, Performance }

public sealed record RadioStates(bool WifiPresent, bool WifiOn, bool BluetoothPresent, bool BluetoothOn);

// Volume, brightness, Wi-Fi / Bluetooth and Windows power mode. Every call is wrapped so a missing
// device (a desktop PC has no brightness, for example) just reports "not available".
public sealed class DeviceControls : IDisposable
{
    // ------------------------------------------------------------ volume (default speakers)

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string _deviceId = "";

    // volume 0..1, muted. Raised from an audio thread.
    public event Action<float, bool>? VolumeChanged;
    // brightness 0..100. Raised from a WMI thread (or from Poll when events are unavailable).
    public event Action<int>? BrightnessChanged;

    private bool EnsureDevice()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_device != null && current.ID == _deviceId)
            {
                current.Dispose();
                return true;
            }

            DetachDevice();
            _device = current;
            _deviceId = current.ID;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            return true;
        }
        catch
        {
            DetachDevice();
            return false;
        }
    }

    private void DetachDevice()
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
            }
        }
        catch { }
        _device = null;
        _deviceId = "";
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
        => VolumeChanged?.Invoke(data.MasterVolume, data.Muted);

    public float? GetVolume()
    {
        try { return EnsureDevice() ? _device!.AudioEndpointVolume.MasterVolumeLevelScalar : null; }
        catch { return null; }
    }

    public bool? GetMuted()
    {
        try { return EnsureDevice() ? _device!.AudioEndpointVolume.Mute : null; }
        catch { return null; }
    }

    public void SetVolume(float value)
    {
        try
        {
            if (EnsureDevice())
                _device!.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
        }
        catch { }
    }

    public void SetMuted(bool muted)
    {
        try { if (EnsureDevice()) _device!.AudioEndpointVolume.Mute = muted; }
        catch { }
    }

    // ------------------------------------------------------------ brightness (laptop screen, via WMI)

    private const string WmiScope = @"root\WMI";
    private ManagementEventWatcher? _watcher;
    private bool _watching;
    private int _lastBrightness = -1;

    // False on a desktop PC or an external monitor: Windows exposes no brightness control there.
    public bool BrightnessSupported { get; private set; } = true;

    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope,
                "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                BrightnessSupported = true;
                _lastBrightness = Convert.ToInt32(o["CurrentBrightness"]);
                return _lastBrightness;
            }
            BrightnessSupported = false;
        }
        catch { BrightnessSupported = false; }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope,
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject o in searcher.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
                _lastBrightness = percent;
                return true;
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------ lifecycle

    // Starts listening for volume and brightness changes (including the keyboard keys).
    public void Start()
    {
        EnsureDevice();
        try
        {
            _watcher = new ManagementEventWatcher(new ManagementScope(WmiScope),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    int b = Convert.ToInt32(e.NewEvent["Brightness"]);
                    _lastBrightness = b;
                    BrightnessChanged?.Invoke(b);
                }
                catch { }
            };
            _watcher.Start();
            _watching = true;
        }
        catch { _watching = false; }
    }

    // Called every few seconds: follows a change of default speakers, and watches brightness
    // by polling on PCs where Windows does not send brightness events.
    public void Poll()
    {
        EnsureDevice();
        if (_watching) return;
        int before = _lastBrightness;
        int? now = GetBrightness();
        if (now != null && before >= 0 && now.Value != before) BrightnessChanged?.Invoke(now.Value);
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        _watcher = null;
        DetachDevice();
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }

    // ------------------------------------------------------------ Wi-Fi and Bluetooth

    public async Task<RadioStates> GetRadiosAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return new RadioStates(false, false, false, false);
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return new RadioStates(wifi != null, wifi?.State == RadioState.On,
                                   bt != null, bt?.State == RadioState.On);
        }
        catch { return new RadioStates(false, false, false, false); }
    }

    public async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == kind);
            if (radio == null) return false;
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ power mode (Windows 10/11 "power mode" slider)

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid scheme);

    private static readonly Guid EfficiencyGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid PerformanceGuid = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public PowerMode GetPowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return PowerMode.Unknown;
            if (g == Guid.Empty) return PowerMode.Balanced;
            if (g == EfficiencyGuid) return PowerMode.Efficiency;
            if (g == PerformanceGuid) return PowerMode.Performance;
        }
        catch { }
        return PowerMode.Unknown;
    }

    public bool SetPowerMode(PowerMode mode)
    {
        try
        {
            var g = mode switch
            {
                PowerMode.Efficiency => EfficiencyGuid,
                PowerMode.Performance => PerformanceGuid,
                PowerMode.Balanced => Guid.Empty,
                _ => (Guid?)null
            };
            return g != null && PowerSetActiveOverlayScheme(g.Value) == 0;
        }
        catch { return false; }
    }
}
