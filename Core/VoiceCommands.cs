using System.Text.RegularExpressions;

namespace NextGenIsland.Core;

public enum VoiceActionKind
{
    Open, SetVolume, VolumeDelta, Mute, Unmute, SetBrightness, BrightnessDelta,
    Remind, Timer, FindFile, DarkMode, Wifi, Bluetooth, PowerMode,
    MediaPlay, MediaPause, MediaNext, MediaPrevious,
    Unknown
}

// One thing the user asked for. Value depends on the kind: a percentage (volume/brightness), a +/- step (Delta kinds),
// seconds from now (Remind/Timer; -1 = no time), 0/1/2 = off/on/toggle (DarkMode, Wifi, Bluetooth) or a PowerMode (0 efficiency, 1 balanced, 2 performance).
// Text is an app/site/file name, a reminder text or the unrecognised words.
public sealed record VoiceAction(VoiceActionKind Kind, int Value = 0, string Text = "");

// Turns a spoken sentence such as "open Chrome and Calculator and set the volume to thirty percent" into a list of actions.
// Plain rules, no internet and no AI service, so the same words always do the same thing.
public static class VoiceCommands
{
    private static readonly string[] OpenVerbs = { "open", "launch", "start", "run", "show me", "go to" };

    public static List<VoiceAction> Parse(string spoken)
    {
        var actions = new List<VoiceAction>();
        string text = Normalize(spoken);
        if (text.Length == 0) return actions;

        // Split into pieces at "and", "then" and commas. An open-command carries over to bare names that follow it:
        // "open chrome and calculator" -> open chrome, open calculator.
        var pieces = Regex.Split(text, @"\s*(?:,|\band then\b|\bthen\b|\band also\b|\balso\b|\band\b)\s*")
                          .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        bool openContext = false;
        foreach (var piece in pieces)
        {
            var parsed = ParsePiece(piece, openContext);
            if (parsed.Count == 0) continue;
            openContext = parsed[^1].Kind == VoiceActionKind.Open;
            actions.AddRange(parsed);
        }
        return actions;
    }

    private static List<VoiceAction> ParsePiece(string p, bool openContext)
    {
        var extra = ParseExtra(p);
        if (extra != null) return extra;

        // Volume
        if (p.Contains("volume") || p is "louder" or "quieter" or "mute" or "unmute" || p.StartsWith("mute ") || p.StartsWith("unmute "))
        {
            p = NumberWordsToDigits(p);
            if (p.Contains("unmute") || p.Contains("un mute")) return One(VoiceActionKind.Unmute);
            if (Regex.IsMatch(p, @"\bmute\b")) return One(VoiceActionKind.Mute);
            int? n = FirstNumber(p);
            if (Regex.IsMatch(p, @"\b(max|maximum|full)\b")) return One(VoiceActionKind.SetVolume, 100);
            if (Regex.IsMatch(p, @"\b(up|increase|raise|louder|higher)\b")) return One(VoiceActionKind.VolumeDelta, n ?? 10);
            if (Regex.IsMatch(p, @"\b(down|decrease|lower|reduce|quieter|softer)\b")) return One(VoiceActionKind.VolumeDelta, -(n ?? 10));
            if (n != null) return One(VoiceActionKind.SetVolume, Math.Clamp(n.Value, 0, 100));
            return One(VoiceActionKind.Unknown, 0, p);
        }

        // Brightness
        if (p.Contains("brightness") || p is "brighter" or "dimmer" or "dim the screen")
        {
            p = NumberWordsToDigits(p);
            int? n = FirstNumber(p);
            if (Regex.IsMatch(p, @"\b(max|maximum|full)\b")) return One(VoiceActionKind.SetBrightness, 100);
            if (Regex.IsMatch(p, @"\b(up|increase|raise|brighter|higher)\b")) return One(VoiceActionKind.BrightnessDelta, n ?? 15);
            if (Regex.IsMatch(p, @"\b(down|decrease|lower|reduce|dimmer|dim)\b")) return One(VoiceActionKind.BrightnessDelta, -(n ?? 15));
            if (n != null) return One(VoiceActionKind.SetBrightness, Math.Clamp(n.Value, 0, 100));
            return One(VoiceActionKind.Unknown, 0, p);
        }

        // Open an app or website
        foreach (var verb in OpenVerbs)
        {
            if (p.StartsWith(verb + " "))
            {
                string target = CleanTarget(p[(verb.Length + 1)..]);
                return target.Length == 0 ? One(VoiceActionKind.Unknown, 0, p) : One(VoiceActionKind.Open, 0, target);
            }
        }
        if (openContext)
        {
            string target = CleanTarget(p);
            if (target.Length > 0) return One(VoiceActionKind.Open, 0, target);
        }
        return One(VoiceActionKind.Unknown, 0, p);
    }


    // ---- timers, reminders, files, dark mode, Wi-Fi, Bluetooth, power mode, media

    private static readonly Regex DurationRx = new(
        @"\b(?:in|for)\s+(?:(?<n>\d+(?:\.\d+)?)|(?<a>an?)|(?<h>half an?))\s+(?<u>seconds?|secs?|minutes?|mins?|hours?|hrs?)\b", RegexOptions.Compiled);
    private static readonly Regex DurationBeforeTimerRx = new(
        @"\b(?<n>\d+)\s*(?<u>seconds?|secs?|minutes?|mins?|hours?|hrs?)\s+timer\b", RegexOptions.Compiled);
    private static readonly Regex AtTimeRx = new(
        @"\bat\s+(?<h>\d{1,2})(?:\s+(?<m>\d{2}))?\s*(?<ap>am|pm)?\b", RegexOptions.Compiled);

    private static int UnitSeconds(string u) => u.StartsWith("h") ? 3600 : u.StartsWith("m") ? 60 : 1;

    private static bool TryDuration(string p, out int seconds, out string matched)
    {
        seconds = 0; matched = "";
        var m = DurationRx.Match(p);
        if (m.Success)
        {
            double n = m.Groups["n"].Success ? double.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
                     : m.Groups["h"].Success ? 0.5 : 1;
            seconds = (int)Math.Round(n * UnitSeconds(m.Groups["u"].Value));
            matched = m.Value;
            return seconds > 0;
        }
        m = DurationBeforeTimerRx.Match(p);
        if (m.Success)
        {
            seconds = int.Parse(m.Groups["n"].Value) * UnitSeconds(m.Groups["u"].Value);
            matched = m.Value;
            return seconds > 0;
        }
        return false;
    }

    // "at 5 30 pm" -> seconds until the next 5:30 pm (or 5:30 am if that comes first).
    private static bool TryClockTime(string p, out int seconds, out string matched)
    {
        seconds = 0; matched = "";
        var m = AtTimeRx.Match(p);
        if (!m.Success) return false;
        int h = int.Parse(m.Groups["h"].Value);
        int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        if (h > 24 || min > 59) return false;
        string ap = m.Groups["ap"].Value;
        var now = DateTime.Now;
        var candidates = new List<int>();
        if (ap == "am") candidates.Add(h % 12);
        else if (ap == "pm") candidates.Add(h % 12 + 12);
        else if (h > 12) candidates.Add(h % 24);
        else { candidates.Add(h % 12); candidates.Add(h % 12 + 12); }
        DateTime best = DateTime.MaxValue;
        foreach (int hour in candidates)
        {
            var t = now.Date.AddHours(hour).AddMinutes(min);
            if (t <= now.AddSeconds(5)) t = t.AddDays(1);
            if (t < best) best = t;
        }
        seconds = (int)Math.Max(1, (best - now).TotalSeconds);
        matched = m.Value;
        return true;
    }

    private static List<VoiceAction>? ParseExtra(string p)
    {
        // Timer: "set a timer for 5 minutes", "5 minute timer"
        if (Regex.IsMatch(p, @"\btimer\b"))
        {
            string q = NumberWordsToDigits(p);
            if (TryDuration(q, out int secs, out _)) return One(VoiceActionKind.Timer, secs);
        }

        // Reminder: "remind me in 10 minutes to call mom", "remind me at 5 pm to leave", "remind me to drink water"
        if (p.StartsWith("remind me") || p.StartsWith("set a reminder") || p.StartsWith("reminder"))
        {
            string q = NumberWordsToDigits(p);
            int secs = -1;
            string task = q;
            if (TryDuration(q, out int d, out string m1)) { secs = d; task = task.Replace(m1, " "); }
            else if (TryClockTime(q, out int c, out string m2)) { secs = c; task = task.Replace(m2, " "); }
            else if (q.Contains("tomorrow"))
            {
                secs = (int)(DateTime.Today.AddDays(1).AddHours(9) - DateTime.Now).TotalSeconds;
                task = task.Replace("tomorrow", " ");
            }
            task = Regex.Replace(task, @"^(remind me|set a reminder|reminder)\s*(to|about|that|for)?\s*", "");
            task = Regex.Replace(task, @"\s+", " ").Trim();
            task = Regex.Replace(task, @"^(to|about|that)\s+", "").Trim();
            return One(VoiceActionKind.Remind, secs, task);
        }

        // Dark / light mode
        var theme = Regex.Match(p, @"\b(dark|light)\s+(mode|theme)\b");
        if (theme.Success)
        {
            bool dark = theme.Groups[1].Value == "dark";
            bool off = Regex.IsMatch(p, @"\b(off|disable|disabled|turn off|switch off)\b");
            if (Regex.IsMatch(p, @"\btoggle\b")) return One(VoiceActionKind.DarkMode, 2);
            return One(VoiceActionKind.DarkMode, (dark ^ off) ? 1 : 0);
        }

        // Wi-Fi and Bluetooth: needs an on/off word, so "open Bluetooth settings" still just opens the settings
        var radio = Regex.Match(p, @"\b(wifi|bluetooth)\b");
        if (radio.Success)
        {
            bool on = Regex.IsMatch(p, @"\b(on|enable|connect|start)\b");
            bool off = Regex.IsMatch(p, @"\b(off|disable|disconnect|stop)\b");
            bool toggle = Regex.IsMatch(p, @"\btoggle\b");
            if (on != off || toggle)
            {
                var kind = radio.Groups[1].Value == "wifi" ? VoiceActionKind.Wifi : VoiceActionKind.Bluetooth;
                return One(kind, toggle ? 2 : (on ? 1 : 0));
            }
        }

        // Power mode: "performance mode", "set balanced mode", "battery saver mode"
        if (Regex.IsMatch(p, @"\b(mode|saver)\b"))
        {
            if (Regex.IsMatch(p, @"\bperformance\b")) return One(VoiceActionKind.PowerMode, 2);
            if (Regex.IsMatch(p, @"\bbalanced\b")) return One(VoiceActionKind.PowerMode, 1);
            if (Regex.IsMatch(p, @"\b(efficiency|saver|battery)\b")) return One(VoiceActionKind.PowerMode, 0);
        }

        // Media
        if (Regex.IsMatch(p, @"^(play|resume|continue)(\s+the)?(\s+(music|song|track|media|playback))?$")) return One(VoiceActionKind.MediaPlay);
        if (Regex.IsMatch(p, @"^(pause|stop)(\s+the)?(\s+(music|song|track|media|playback))?$")) return One(VoiceActionKind.MediaPause);
        if (Regex.IsMatch(p, @"^(next|skip|play next)(\s+(the|this))?(\s+(song|track))?$")) return One(VoiceActionKind.MediaNext);
        if (Regex.IsMatch(p, @"^(previous|go back|last|play previous|play last)(\s+(the))?(\s+(song|track))?$")) return One(VoiceActionKind.MediaPrevious);

        // Find a file: "find my resume", "search for tax invoice"
        var find = Regex.Match(p, @"^(find|search for|search|look for|locate)\s+(.+)$");
        if (find.Success)
        {
            string q = Regex.Replace(find.Groups[2].Value, @"\b(my|the|a|an|file|files|document|documents|folder|named|called|for me|please)\b", " ");
            q = Regex.Replace(q, @"\s+", " ").Trim();
            if (q.Length > 0) return One(VoiceActionKind.FindFile, 0, q);
        }
        return null;
    }

    private static List<VoiceAction> One(VoiceActionKind k, int v = 0, string t = "") => new() { new VoiceAction(k, v, t) };

    private static string CleanTarget(string t)
    {
        t = Regex.Replace(t, @"\b(the|my|app|application|program|please|for me|window)\b", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    // Lower-case, drop punctuation, turn number words into digits ("thirty five percent" -> "35 percent").
    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Replace("%", " percent ");
        s = Regex.Replace(s, @"[^a-z0-9,\s]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"^(hey|ok|okay|please|can you|could you|would you)\s+", "");
        s = Regex.Replace(s, @"\bwi fi\b", "wifi");
        s = Regex.Replace(s, @"\bblue tooth\b", "bluetooth");
        s = Regex.Replace(s, @"\b([ap]) m\b", "$1m");
        return s;
    }

    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19
    };
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
    };

    private static string NumberWordsToDigits(string s)
    {
        var words = s.Split(' ');
        var outp = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            if (w == "hundred" || (w == "a" && i + 1 < words.Length && words[i + 1] == "hundred"))
            {
                outp.Add("100");
                if (w == "a") i++;
                continue;
            }
            if (Tens.TryGetValue(w, out int t))
            {
                int total = t;
                if (i + 1 < words.Length && Units.TryGetValue(words[i + 1], out int u) && u is >= 1 and <= 9) { total += u; i++; }
                outp.Add(total.ToString());
            }
            else if (Units.TryGetValue(w, out int unit)) outp.Add(unit.ToString());
            else outp.Add(w);
        }
        return string.Join(' ', outp);
    }

    private static int? FirstNumber(string s)
    {
        var m = Regex.Match(s, @"\b(\d{1,3})\b");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
