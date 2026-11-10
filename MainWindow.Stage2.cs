using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NextGenIsland;

// Stage 2: Widgets (sparklines, focus ring, weather) and the Calls tab (mic mute, speaker and mic test, camera).
public partial class MainWindow
{
    // ---------------------------------------------------------------- widgets: sparklines

    private const int SparkSamples = 30;
    private const double SparkW = 90, SparkH = 18;
    private readonly List<double> _cpuHist = new();
    private readonly List<double> _ramHist = new();

    // Called every second. CpuBar / RamBar are refreshed just before this, so we reuse their values.
    private void SampleStats()
    {
        _cpuHist.Add(CpuBar.Value);
        _ramHist.Add(RamBar.Value);
        if (_cpuHist.Count > SparkSamples) _cpuHist.RemoveAt(0);
        if (_ramHist.Count > SparkSamples) _ramHist.RemoveAt(0);
    }

    private static PointCollection SparkPoints(List<double> history)
    {
        var pts = new PointCollection();
        if (history.Count < 2) return pts;
        double step = SparkW / (SparkSamples - 1);
        double x0 = SparkW - step * (history.Count - 1);
        for (int i = 0; i < history.Count; i++)
        {
            double v = Math.Clamp(history[i], 0.0, 100.0) / 100.0;
            pts.Add(new Point(x0 + i * step, SparkH - 1.0 - v * (SparkH - 2.0)));
        }
        return pts;
    }

    private void UpdateSparklines()
    {
        CpuSpark.Points = SparkPoints(_cpuHist);
        RamSpark.Points = SparkPoints(_ramHist);
        WidgetCpuText.Text = $"CPU {CpuBar.Value:0}%";
        WidgetRamText.Text = $"RAM {RamBar.Value:0}%";
    }

    // ---------------------------------------------------------------- widgets: focus ring

    private static Geometry RingGeometry(double fraction)
    {
        const double c = 27.0, r = 23.0;   // ring is drawn on a 54 x 54 canvas
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        if (fraction <= 0.001) return Geometry.Empty;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);

        double angle = fraction * 2.0 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0.0, angle > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // The ring drains as the session counts down.
    private void UpdateFocusRing()
    {
        double total = FocusMinutes * 60.0;
        FocusArc.Data = RingGeometry(_focusSeconds / total);
    }

    // ---------------------------------------------------------------- widgets: weather (Open-Meteo, no API key)

    private static readonly HttpClient WeatherClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private DateTime _weatherAt = DateTime.MinValue;

    private static string WeatherText(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 65 or 66 or 67 => "Rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Weather"
    };

    private async Task RefreshWeatherAsync()
    {
        WeatherCityText.Text = string.IsNullOrWhiteSpace(_settings.WeatherCity)
            ? "WEATHER" : _settings.WeatherCity.ToUpperInvariant();

        if (DateTime.Now - _weatherAt < TimeSpan.FromMinutes(15)) return;
        _weatherAt = DateTime.Now;

        try
        {
            var inv = CultureInfo.InvariantCulture;
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + _settings.WeatherLatitude.ToString(inv)
                       + "&longitude=" + _settings.WeatherLongitude.ToString(inv)
                       + "&current=temperature_2m,weather_code&timezone=auto";

            string json = await WeatherClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();

            WeatherTempText.Text = Math.Round(temp).ToString("0", inv) + "\u00B0";
            WeatherDescText.Text = WeatherText(code);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Weather: " + ex.Message);
            _weatherAt = DateTime.Now - TimeSpan.FromMinutes(14);   // try again in about a minute
            WeatherDescText.Text = "Offline";
        }
    }

    // ---------------------------------------------------------------- calls

    private List<(string? Id, string Name)> _spkDevices = new() { (null, "Default") };
    private List<(string? Id, string Name)> _micDevices = new() { (null, "Default") };
    private int _spkIdx, _micIdx;
    private bool _spkTesting;
    private MicMeter? _meter;
    private DispatcherTimer? _meterTimer;
    private DateTime _meterStop;

    private static List<(string? Id, string Name)> BuildDeviceList(bool capture)
    {
        string? defName = CallAudio.DefaultName(capture);
        var list = new List<(string? Id, string Name)>
        {
            (null, defName == null ? "Default" : "Default: " + defName)
        };
        foreach (var d in CallAudio.List(capture)) list.Add((d.Id, d.Name));
        return list;
    }

    private void LoadCallDevices()
    {
        string? oldSpk = _spkDevices[Math.Min(_spkIdx, _spkDevices.Count - 1)].Id;
        string? oldMic = _micDevices[Math.Min(_micIdx, _micDevices.Count - 1)].Id;

        _spkDevices = BuildDeviceList(false);
        _micDevices = BuildDeviceList(true);
        _spkIdx = Math.Max(0, _spkDevices.FindIndex(d => d.Id == oldSpk));
        _micIdx = Math.Max(0, _micDevices.FindIndex(d => d.Id == oldMic));

        SpeakerNameText.Text = _spkDevices[_spkIdx].Name;
        MicNameText.Text = _micDevices[_micIdx].Name;
    }

    private void UpdateMicUi()
    {
        bool? muted = CallAudio.GetMuted(_micDevices[_micIdx].Id);
        if (muted == null)
        {
            MicStateText.Text = "No microphone";
            MicHintText.Text = "Nothing to mute";
            MicBrush.Color = Color.FromRgb(90, 90, 96);
            return;
        }
        MicStateText.Text = muted.Value ? "Mic muted" : "Mic on";
        MicHintText.Text = muted.Value ? "Click to unmute" : "Click to mute";
        MicBrush.Color = muted.Value ? Color.FromRgb(255, 69, 58) : Color.FromRgb(48, 209, 88);
    }

    private void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        string? id = _micDevices[_micIdx].Id;
        bool? muted = CallAudio.GetMuted(id);
        if (muted == null) { CallStatusText.Text = "No microphone found"; return; }
        if (!CallAudio.SetMuted(id, !muted.Value)) CallStatusText.Text = "Could not change the microphone";
        else CallStatusText.Text = "";
        UpdateMicUi();
    }

    private void SpeakerPick_Click(object sender, MouseButtonEventArgs e)
    {
        if (_spkTesting) return;
        _spkIdx = (_spkIdx + 1) % _spkDevices.Count;
        SpeakerNameText.Text = _spkDevices[_spkIdx].Name;
        e.Handled = true;
    }

    private void MicPick_Click(object sender, MouseButtonEventArgs e)
    {
        StopMicTest();
        _micIdx = (_micIdx + 1) % _micDevices.Count;
        MicNameText.Text = _micDevices[_micIdx].Name;
        UpdateMicUi();
        e.Handled = true;
    }

    private async void SpeakerTest_Click(object sender, RoutedEventArgs e)
    {
        if (_spkTesting) return;
        _spkTesting = true;
        SpeakerTestButton.IsEnabled = false;
        CallStatusText.Text = "Playing a test tone...";
        try
        {
            await CallAudio.PlayTestToneAsync(_spkDevices[_spkIdx].Id);
            CallStatusText.Text = "Did you hear it?";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Speaker test: " + ex.Message);
            CallStatusText.Text = "Could not play on this device";
        }
        finally
        {
            _spkTesting = false;
            SpeakerTestButton.IsEnabled = true;
        }
    }

    private void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (_meter != null) { StopMicTest(); return; }

        try { _meter = new MicMeter(_micDevices[_micIdx].Id); }
        catch (Exception ex)
        {
            Debug.WriteLine("Mic test: " + ex.Message);
            _meter = null;
            CallStatusText.Text = "Microphone unavailable. Check Windows privacy settings.";
            return;
        }

        MicTestButton.Content = "Stop";
        CallStatusText.Text = "Say something...";
        _meterStop = DateTime.UtcNow.AddSeconds(6);
        _meterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _meterTimer.Tick -= MeterTimer_Tick;
        _meterTimer.Tick += MeterTimer_Tick;
        _meterTimer.Start();
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_meter == null || DateTime.UtcNow >= _meterStop) { StopMicTest(); return; }
        try { MicLevelBar.Value = Math.Min(1.0, _meter.Peak * 1.5); }
        catch { StopMicTest(); }
    }

    private void StopMicTest()
    {
        _meterTimer?.Stop();
        _meter?.Dispose();
        _meter = null;
        MicLevelBar.Value = 0;
        MicTestButton.Content = "Test";
        CallStatusText.Text = "";
    }

    private void CheckCamera_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });
            CallStatusText.Text = "Opened the Camera app";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Camera: " + ex.Message);
            CallStatusText.Text = "Could not open the Camera app";
        }
    }
}
