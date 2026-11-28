using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace NextGenIsland.Services;

// Offline speech recognition with Whisper (tiny English model, ~75 MB, downloaded once with the user's OK).
public sealed class SpeechToText : IDisposable
{
    // Bigger models understand speech better in noisy places but take longer to download and to run.
    //   tiny  ~75 MB   fastest
    //   base  ~142 MB  good balance (default)
    //   small ~466 MB  best accuracy, slower
    private static readonly Dictionary<string, long> MinBytes = new() { ["tiny"] = 60_000_000, ["base"] = 130_000_000, ["small"] = 440_000_000 };
    private static readonly Dictionary<string, string> SizeText = new() { ["tiny"] = "75 MB", ["base"] = "142 MB", ["small"] = "466 MB" };

    public static string NormalizeKey(string? key) => key != null && MinBytes.ContainsKey(key) ? key : "base";
    public static string SizeOf(string key) => SizeText[NormalizeKey(key)];
    private static string UrlFor(string key) => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-" + key + ".en.bin";

    public static string ModelPathFor(string key) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NextGenIsland", "models", "ggml-" + NormalizeKey(key) + ".en.bin");

    public static bool IsModelReady(string key)
    {
        try
        {
            key = NormalizeKey(key);
            string p = ModelPathFor(key);
            return File.Exists(p) && new FileInfo(p).Length > MinBytes[key];
        }
        catch { return false; }
    }

    private readonly string _key;
    public SpeechToText(string key) => _key = NormalizeKey(key);
    public string Key => _key;

    private WhisperFactory? _factory;

    public static async Task DownloadModelAsync(string key, IProgress<double> progress, CancellationToken ct = default)
    {
        key = NormalizeKey(key);
        string modelPath = ModelPathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        string part = modelPath + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(UrlFor(key), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 77_700_000;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[81920];
            long done = 0; int n; int lastPct = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                int pct = (int)(done * 100 / total);
                if (pct != lastPct) { lastPct = pct; progress.Report(Math.Min(1.0, done / (double)total)); }
            }
        }
        if (new FileInfo(part).Length < MinBytes[key])
        {
            File.Delete(part);
            throw new IOException("Downloaded model is incomplete.");
        }
        File.Move(part, modelPath, true);
    }

    public async Task<string> TranscribeAsync(byte[] wav)
    {
        _factory ??= WhisperFactory.FromPath(ModelPathFor(_key));
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt("Open Chrome and Calculator. Set the volume to thirty percent. Remind me in ten minutes to call mom. Set a timer for five minutes. Find my resume. Turn on dark mode. Turn off Bluetooth. Pause. Next song.")
            .Build();

        var sb = new StringBuilder();
        using var ms = new MemoryStream(wav);
        await foreach (var seg in processor.ProcessAsync(ms))
            sb.Append(seg.Text).Append(' ');

        // Whisper marks silence and noise as [BLANK_AUDIO], (music) and similar; drop those.
        string text = Regex.Replace(sb.ToString(), @"\[[^\]]*\]|\([^)]*\)", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public void Dispose() { _factory?.Dispose(); _factory = null; }
}
