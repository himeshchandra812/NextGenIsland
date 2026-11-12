using System.IO;
using Microsoft.Data.Sqlite;
namespace NextGenIsland.Services;

/// <summary>
/// One clipboard entry. Kind is "text", "image" or "files".
/// Text holds the copied text, "[Image] WxH" for images, or the file paths (one per line) for files.
/// </summary>
public sealed record ClipboardItem(
    long Id, string Text, DateTime CreatedAt, bool Pinned,
    string Kind = "text", string? ImagePath = null, string? Meta = null);

public sealed class ClipboardStore
{
    private const int MaxUnpinnedImages = 60;

    private readonly string _cs;
    private readonly string _imageDir;

    public ClipboardStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
        Directory.CreateDirectory(dir);
        _imageDir = Path.Combine(dir, "clips");
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "clipboard.db") }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS ClipboardEntries(Id INTEGER PRIMARY KEY AUTOINCREMENT, Text TEXT NOT NULL, CreatedAt TEXT NOT NULL, Pinned INTEGER NOT NULL DEFAULT 0); CREATE INDEX IF NOT EXISTS IX_ClipboardEntries_CreatedAt ON ClipboardEntries(CreatedAt DESC);";
        await cmd.ExecuteNonQueryAsync();

        // Older databases only have the text columns: add the new ones once.
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var info = c.CreateCommand();
        info.CommandText = "PRAGMA table_info(ClipboardEntries)";
        await using (var r = await info.ExecuteReaderAsync())
            while (await r.ReadAsync()) have.Add(r.GetString(1));

        if (!have.Contains("Kind")) await RunAsync(c, "ALTER TABLE ClipboardEntries ADD COLUMN Kind TEXT NOT NULL DEFAULT 'text'");
        if (!have.Contains("ImagePath")) await RunAsync(c, "ALTER TABLE ClipboardEntries ADD COLUMN ImagePath TEXT");
        if (!have.Contains("Meta")) await RunAsync(c, "ALTER TABLE ClipboardEntries ADD COLUMN Meta TEXT");
    }

    private static async Task RunAsync(SqliteConnection c, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- saving

    public async Task SaveIfNewAsync(string text) => await SaveEntryAsync("text", text, null, null);

    /// <summary>Saves a list of copied file paths as one entry.</summary>
    public async Task SaveFilesAsync(IReadOnlyList<string> paths)
        => await SaveEntryAsync("files", string.Join("\n", paths), null, paths.Count.ToString());

    /// <summary>Saves a copied image (PNG bytes) on this device and records it.</summary>
    public async Task SaveImageAsync(byte[] png, int width, int height)
    {
        Directory.CreateDirectory(_imageDir);
        string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))[..20] + ".png";
        string path = Path.Combine(_imageDir, name);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, png);

        await SaveEntryAsync("image", "[Image] " + width + "x" + height, path, width + "x" + height);
        await PruneImagesAsync();
    }

    private async Task SaveEntryAsync(string kind, string text, string? imagePath, string? meta)
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync();

        // Skip if the newest entry is identical.
        var q = c.CreateCommand();
        q.CommandText = "SELECT Kind, Text, ImagePath FROM ClipboardEntries ORDER BY Id DESC LIMIT 1";
        await using (var r = await q.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                string lastKind = r.GetString(0);
                string lastText = r.GetString(1);
                string? lastPath = r.IsDBNull(2) ? null : r.GetString(2);
                if (lastKind == kind && string.Equals(lastText, text, StringComparison.Ordinal) &&
                    string.Equals(lastPath, imagePath, StringComparison.Ordinal))
                    return;
            }
        }

        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ClipboardEntries(Text,CreatedAt,Kind,ImagePath,Meta) VALUES($t,$d,$k,$p,$m)";
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$p", (object?)imagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)meta ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- reading

    public async Task<List<ClipboardItem>> SearchAsync(string query = "")
    {
        await using var c = new SqliteConnection(_cs); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Text,CreatedAt,Pinned,Kind,ImagePath,Meta FROM ClipboardEntries WHERE ($q='' OR instr(lower(Text),lower($q))>0) ORDER BY Pinned DESC,Id DESC LIMIT 500";
        cmd.Parameters.AddWithValue("$q", query);
        var list = new List<ClipboardItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ClipboardItem(
                r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToLocalTime(), r.GetInt64(3) != 0,
                r.IsDBNull(4) ? "text" : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return list;
    }

    // ---------------------------------------------------------------- changing

    public async Task TogglePinAsync(long id, bool pin) => await ExecuteAsync("UPDATE ClipboardEntries SET Pinned=$v WHERE Id=$id", id, pin ? 1 : 0);

    public async Task DeleteAsync(long id)
    {
        await ExecuteAsync("DELETE FROM ClipboardEntries WHERE Id=$id", id, null);
        await RemoveUnusedImagesAsync();
    }

    public async Task ClearUnpinnedAsync()
    {
        await ExecuteAsync("DELETE FROM ClipboardEntries WHERE Pinned=0", null, null);
        await RemoveUnusedImagesAsync();
    }

    // Keeps the newest images and pins; removes the rest of the unpinned ones.
    private async Task PruneImagesAsync()
    {
        await ExecuteAsync(
            "DELETE FROM ClipboardEntries WHERE Kind='image' AND Pinned=0 AND Id NOT IN " +
            "(SELECT Id FROM ClipboardEntries WHERE Kind='image' AND Pinned=0 ORDER BY Id DESC LIMIT " + MaxUnpinnedImages + ")",
            null, null);
        await RemoveUnusedImagesAsync();
    }

    // Deletes image files that no entry refers to any more.
    private async Task RemoveUnusedImagesAsync()
    {
        try
        {
            if (!Directory.Exists(_imageDir)) return;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var c = new SqliteConnection(_cs))
            {
                await c.OpenAsync();
                var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT ImagePath FROM ClipboardEntries WHERE ImagePath IS NOT NULL";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) used.Add(r.GetString(0));
            }

            foreach (var file in Directory.EnumerateFiles(_imageDir, "*.png"))
                if (!used.Contains(file)) { try { File.Delete(file); } catch { } }
        }
        catch { }
    }

    private async Task ExecuteAsync(string sql, long? id, int? value)
    {
        await using var c = new SqliteConnection(_cs); await c.OpenAsync();
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        if (id.HasValue) cmd.Parameters.AddWithValue("$id", id.Value);
        if (value.HasValue) cmd.Parameters.AddWithValue("$v", value.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
