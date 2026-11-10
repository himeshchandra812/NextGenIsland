using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NextGenIsland;

// Thin wrapper over NAudio (package: NAudio.Wasapi) so the NAudio types stay out of MainWindow.
internal static class CallAudio
{
    private static MMDevice Open(MMDeviceEnumerator en, string? id, DataFlow flow)
        => id == null ? en.GetDefaultAudioEndpoint(flow, Role.Console) : en.GetDevice(id);

    public static string? DefaultName(bool capture)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(capture ? DataFlow.Capture : DataFlow.Render, Role.Console);
            return dev.FriendlyName;
        }
        catch { return null; }
    }

    public static List<(string Id, string Name)> List(bool capture)
    {
        var result = new List<(string Id, string Name)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var flow = capture ? DataFlow.Capture : DataFlow.Render;
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                result.Add((d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch { }
        return result;
    }

    public static bool? GetMuted(string? micId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = Open(en, micId, DataFlow.Capture);
            return dev.AudioEndpointVolume.Mute;
        }
        catch { return null; }
    }

    public static bool SetMuted(string? micId, bool muted)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = Open(en, micId, DataFlow.Capture);
            dev.AudioEndpointVolume.Mute = muted;
            return true;
        }
        catch { return false; }
    }

    // About one second of a quiet 660 Hz tone on the chosen speaker (null = Windows default).
    public static Task PlayTestToneAsync(string? speakerId) => Task.Run(async () =>
    {
        using var en = new MMDeviceEnumerator();
        using var dev = Open(en, speakerId, DataFlow.Render);
        var gen = new SignalGenerator(44100, 2) { Gain = 0.15, Frequency = 660, Type = SignalGeneratorType.Sin };
        var take = new OffsetSampleProvider(gen) { TakeSamples = 44100 * 2 };
        using var output = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
        output.Init(new SampleToWaveProvider(take));
        output.Play();
        await Task.Delay(1200);
        output.Stop();
    });
}

// Opens the microphone so its level meter reports, and exposes the live peak (0 to 1).
internal sealed class MicMeter : IDisposable
{
    private readonly MMDevice _dev;
    private readonly WasapiCapture _capture;

    public MicMeter(string? micId)
    {
        using var en = new MMDeviceEnumerator();
        _dev = micId == null ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : en.GetDevice(micId);
        _capture = new WasapiCapture(_dev);
        _capture.DataAvailable += (_, _) => { };
        _capture.StartRecording();
    }

    public float Peak => _dev.AudioMeterInformation.MasterPeakValue;

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _dev.Dispose();
    }
}
