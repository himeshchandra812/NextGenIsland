using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGenIsland.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Looks up time-synced (LRC) lyrics on lrclib.net, a free lyrics service.
/// Only the song title, artist and duration are sent. Results are cached for the session.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, LyricLine[]> Cache = new();
    private static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NextGenIsland/1.0");
        return c;
    }

    // Titles from browsers look like "Song - Full Video | Movie | Cast" and the "artist" is the channel name.
    private static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Noise = new(
        @"\b(full|official|lyric|lyrical|lyrics|video|audio|song|music|hd|4k|hq|visualizer|promo|teaser|version)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Returns candidate (title, artist) pairs, best guess first.
    private static List<(string Title, string Artist)> Candidates(string title, string artist)
    {
        var list = new List<(string, string)> { (title, artist) };

        string t = title.Split('|')[0];
        t = Brackets.Replace(t, " ").Trim();

        string song = t, by = artist;
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string left = t.Substring(0, dash).Trim();
            string right = t.Substring(dash + 3).Trim();
            if (Noise.Replace(right, "").Trim(' ', '-', '.').Length == 0)
                song = left;                       // "Song - Full Video"
            else { song = right; by = left; }      // "Artist - Song"
        }
        song = Regex.Replace(song, @"\s+", " ").Trim();

        if (song.Length > 0)
        {
            list.Add((song, artist));
            if (!string.Equals(by, artist, StringComparison.OrdinalIgnoreCase)) list.Add((song, by));
            list.Add((song, ""));                  // title only
        }
        return list.Distinct().ToList();
    }

    public static async Task<LyricLine[]> GetAsync(string title, string artist, TimeSpan duration)
    {
        string key = (artist + "|" + title).ToLowerInvariant();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        try
        {
            string? synced = null;
            foreach (var (t, a) in Candidates(title, artist))
            {
                synced = await TryGetAsync(t, a, duration).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(synced)) break;
                synced = await TrySearchAsync(
                    "track_name=" + Uri.EscapeDataString(t) + (a.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(a) : ""),
                    duration).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(synced)) break;
            }

            if (string.IsNullOrWhiteSpace(synced))
            {
                // Last resort: free-text search with the cleaned title.
                var best = Candidates(title, artist).Last().Title;
                synced = await TrySearchAsync("q=" + Uri.EscapeDataString(best), duration).ConfigureAwait(false);
            }

            var lines = string.IsNullOrWhiteSpace(synced) ? Array.Empty<LyricLine>() : Parse(synced);
            lock (Cache) { Cache[key] = lines; }   // "no lyrics" is cached too, so we do not hammer the service
            return lines;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("LyricsService: " + ex.Message);   // offline or timeout: not cached, will retry later
            return Array.Empty<LyricLine>();
        }
    }

    private static async Task<string?> TryGetAsync(string title, string artist, TimeSpan duration)
    {
        if (artist.Length == 0) return null;   // /api/get needs both
        string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                     "&artist_name=" + Uri.EscapeDataString(artist);
        if (duration > TimeSpan.FromSeconds(1))
            url += "&duration=" + (int)Math.Round(duration.TotalSeconds);
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return ReadSynced(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    // Picks a result with synced lyrics, preferring one whose length is within 6 seconds of the playing track.
    private static async Task<string?> TrySearchAsync(string query, TimeSpan duration)
    {
        using var resp = await Http.GetAsync("https://lrclib.net/api/search?" + query).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? firstAny = null;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("syncedLyrics", out var p) || p.ValueKind != JsonValueKind.String) continue;
            string? text = p.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            firstAny ??= text;

            if (duration > TimeSpan.FromSeconds(1) &&
                el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number &&
                Math.Abs(d.GetDouble() - duration.TotalSeconds) <= 6)
                return text;
        }
        return firstAny;
    }

    private static string? ReadSynced(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("syncedLyrics", out var p) &&
            p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static LyricLine[] Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = Stamp.Matches(line);
            if (matches.Count == 0) continue;
            var last = matches[matches.Count - 1];
            string text = line.Substring(last.Index + last.Length).Trim();
            foreach (Match m in matches)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                list.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
            }
        }
        list.Sort((x, y) => x.Time.CompareTo(y.Time));
        return list.ToArray();
    }
}
