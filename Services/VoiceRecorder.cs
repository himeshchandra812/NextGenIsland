using System.IO;
using NAudio.Wave;

namespace NextGenIsland.Services;

// Records one spoken command from the default microphone (16 kHz, mono, 16-bit - what Whisper wants) and stops by itself
// when you stop talking. The microphone is only open between Start() and the Finished event.
public sealed class VoiceRecorder : IDisposable
{
    private const int Rate = 16000;
    private WaveInEvent? _in;
    private MemoryStream? _pcm;
    private DateTime _start, _lastVoice;
    private bool _heardSpeech, _stopping, _finishedRaised;
    private double _noiseFloor;
    private int _calibrationBlocks;

    // 0..1 loudness of the latest audio block (raised on a background thread).
    public event Action<float>? Level;
    // WAV bytes of the recording, or null when nothing was said. Raised on a background thread.
    public event Action<byte[]?>? Finished;

    public bool IsRecording => _in != null && !_stopping;

    private bool _hold;

    // holdToTalk: record for as long as the caller wants (it calls StopNow when the key is released) instead of waiting for silence.
    public bool Start(bool holdToTalk = false)
    {
        _hold = holdToTalk;
        if (_in != null) return false;
        try
        {
            _pcm = new MemoryStream();
            _heardSpeech = holdToTalk;
            _stopping = _finishedRaised = false;
            _noiseFloor = 0; _calibrationBlocks = 0;
            _start = _lastVoice = DateTime.UtcNow;
            _in = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 60 };
            _in.DataAvailable += OnData;
            _in.RecordingStopped += OnStopped;
            _in.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Microphone could not be opened", ex);
            Cleanup();
            return false;
        }
    }

    // Ends the recording now (used when the hotkey is pressed a second time).
    public void StopNow()
    {
        if (_in == null || _stopping) return;
        _stopping = true;
        try { _in.StopRecording(); } catch { OnStopped(null, new StoppedEventArgs()); }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_stopping) return;
        _pcm?.Write(e.Buffer, 0, e.BytesRecorded);

        double sum = 0;
        int n = e.BytesRecorded / 2;
        for (int i = 0; i < n; i++)
        {
            short s = BitConverter.ToInt16(e.Buffer, i * 2);
            sum += (double)s * s;
        }
        double rms = n == 0 ? 0 : Math.Sqrt(sum / n) / 32768.0;
        Level?.Invoke((float)Math.Clamp(rms * 7.0, 0.0, 1.0));

        // Learn the room's background noise from the first ~0.3 s, then call anything clearly louder "speech".
        if (_calibrationBlocks < 5)
        {
            _noiseFloor = Math.Max(_noiseFloor, rms);
            _calibrationBlocks++;
            return;
        }
        if (_hold)
        {
            if ((DateTime.UtcNow - _start).TotalSeconds > 30) StopNow();
            return;
        }
        // In a loud place the background is the "noise floor"; speech has to be clearly above it.
        double threshold = Math.Max(_noiseFloor * 3.0, 0.02);
        var now = DateTime.UtcNow;
        if (rms > threshold) { _heardSpeech = true; _lastVoice = now; }

        bool silenceAfterSpeech = _heardSpeech && (now - _lastVoice).TotalMilliseconds > 1100;
        bool nothingSaid = !_heardSpeech && (now - _start).TotalSeconds > 6;
        bool tooLong = (now - _start).TotalSeconds > 20;
        if (silenceAfterSpeech || nothingSaid || tooLong) StopNow();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        byte[]? wav = null;
        try
        {
            if (_heardSpeech && _pcm != null && _pcm.Length > Rate * 2 / 3)   // at least ~0.35 s of audio
                wav = BuildWav(_pcm.ToArray());
        }
        catch (Exception ex) { AppLog.Write("Building recording failed", ex); }
        Cleanup();
        if (_finishedRaised) return;
        _finishedRaised = true;
        Finished?.Invoke(wav);
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        // Bring a quiet recording up to a normal level (at most 8x) so the recogniser gets a clear signal.
        int peak = 1;
        for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        double gain = Math.Min(8.0, 0.85 * 32767.0 / peak);
        if (gain > 1.15)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                int v = (int)Math.Round(BitConverter.ToInt16(pcm, i) * gain);
                short sv = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                pcm[i] = (byte)(sv & 0xFF); pcm[i + 1] = (byte)((sv >> 8) & 0xFF);
            }
        }

        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    private void Cleanup()
    {
        var wi = _in;
        _in = null;
        if (wi != null)
        {
            wi.DataAvailable -= OnData;
            wi.RecordingStopped -= OnStopped;
            try { wi.Dispose(); } catch { }
        }
        _pcm?.Dispose();
        _pcm = null;
    }

    public void Dispose() => Cleanup();
}
