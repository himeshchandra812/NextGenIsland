using System.IO;
using System.Windows;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace NextGenIsland.Services;

// Shows other apps' Windows toast notifications on the island.
//
// Windows only lets packaged apps use the official notification listener, so this reads the toast history Windows
// keeps for the signed-in user (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db). It works on a private
// copy of that file, never writes to it, and only runs when the user turns the option on. It is not an official API,
// so a Windows update could change it; failures are written to the log and never affect the rest of the app.
public sealed class WindowsNotificationReader : IDisposable
{
    private readonly NotificationService _target;
    private readonly string _dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");
    private CancellationTokenSource? _cts;
    private long _lastRow = -1;
    private string _lastStamp = "";
    private int _failures;

    public WindowsNotificationReader(NotificationService target) => _target = target;

    public bool Running => _cts != null;

    public void SetEnabled(bool enabled)
    {
        if (enabled && _cts == null)
        {
            _cts = new CancellationTokenSource();
            _lastRow = -1;
            _lastStamp = "";
            _failures = 0;
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
            AppLog.Write("Other-app notifications: on");
        }
        else if (!enabled && _cts != null)
        {
            _cts.Cancel();
            _cts = null;
            AppLog.Write("Other-app notifications: off");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { Poll(); }
            catch (Exception ex)
            {
                _lastStamp = "";   // try again on the next round
                if (_failures++ < 3) AppLog.Write("Reading Windows notifications failed", ex);
            }
            try { await Task.Delay(2000, token); } catch (TaskCanceledException) { return; }
        }
    }

    private void Poll()
    {
        if (!File.Exists(_dbPath))
        {
            if (_failures++ < 1) AppLog.Write("Windows notification database not found: " + _dbPath);
            return;
        }

        // Skip the copy when nothing was written since the last look.
        string wal = _dbPath + "-wal";
        string stamp = Stamp(_dbPath) + "|" + Stamp(wal);
        if (stamp == _lastStamp) return;
        _lastStamp = stamp;

        string tmp = Path.Combine(Path.GetTempPath(), "nextgenisland-wpn-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        string copy = Path.Combine(tmp, "wpn.db");
        try
        {
            CopyShared(_dbPath, copy);
            if (File.Exists(wal)) CopyShared(wal, copy + "-wal");
            if (File.Exists(_dbPath + "-shm")) CopyShared(_dbPath + "-shm", copy + "-shm");

            var found = new List<(long Row, string App, string Title, string Body)>();
            using (var conn = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                conn.Open();

                if (_lastRow < 0)
                {
                    // First look: remember where the history ends so old notifications are not replayed.
                    using var max = conn.CreateCommand();
                    max.CommandText = "SELECT IFNULL(MAX(rowid), 0) FROM Notification";
                    _lastRow = Convert.ToInt64(max.ExecuteScalar() ?? 0);
                    return;
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT n.rowid, h.PrimaryId, n.Payload FROM Notification n " +
                    "LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                    "WHERE n.rowid > $last AND n.Type = 'toast' ORDER BY n.rowid";
                cmd.Parameters.AddWithValue("$last", _lastRow);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long row = r.GetInt64(0);
                    _lastRow = Math.Max(_lastRow, row);
                    string app = FriendlyApp(r.IsDBNull(1) ? "" : r.GetString(1));
                    string? xml = r.IsDBNull(2) ? null : r.GetValue(2) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : r.GetValue(2).ToString();
                    if (xml == null) continue;
                    var (title, body) = ParseToast(xml);
                    if (title.Length == 0 && body.Length == 0) continue;
                    found.Add((row, app, title, body));
                }
            }

            if (found.Count == 0) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var f in found) _target.PostNotification(f.App, f.Title, f.Body);
            }));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static string Stamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-"; }
        catch { return "?"; }
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    private static (string Title, string Body) ParseToast(string xml)
    {
        try
        {
            int lt = xml.IndexOf('<');
            if (lt > 0) xml = xml[lt..];
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants("binding")
                .SelectMany(b => b.Elements("text"))
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (texts.Count == 0) return ("", "");
            return (texts[0], string.Join(" ", texts.Skip(1)));
        }
        catch { return ("", ""); }
    }

    // "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" -> "WindowsTerminal", "chrome.exe" -> "Chrome", "com.squirrel.slack.slack" -> "Slack".
    private static string FriendlyApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Windows App";
        string s = id;
        int bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        s = s.Replace('-', ' ');
        return s.Length == 0 ? "Windows App" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    public void Dispose() => SetEnabled(false);
}
