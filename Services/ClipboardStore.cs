using System.IO;
using Microsoft.Data.Sqlite;
namespace NextGenIsland.Services;

public sealed record ClipboardItem(long Id, string Text, DateTime CreatedAt, bool Pinned);

public sealed class ClipboardStore
{
    private readonly string _cs;
    public ClipboardStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "clipboard.db") }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS ClipboardEntries(Id INTEGER PRIMARY KEY AUTOINCREMENT, Text TEXT NOT NULL, CreatedAt TEXT NOT NULL, Pinned INTEGER NOT NULL DEFAULT 0); CREATE INDEX IF NOT EXISTS IX_ClipboardEntries_CreatedAt ON ClipboardEntries(CreatedAt DESC);";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveIfNewAsync(string text)
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var q = c.CreateCommand(); q.CommandText = "SELECT Text FROM ClipboardEntries ORDER BY Id DESC LIMIT 1";
        if (string.Equals(await q.ExecuteScalarAsync() as string, text, StringComparison.Ordinal)) return;
        var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO ClipboardEntries(Text,CreatedAt) VALUES($t,$d)";
        cmd.Parameters.AddWithValue("$t", text); cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ClipboardItem>> SearchAsync(string query = "")
    {
        await using var c = new SqliteConnection(_cs); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Text,CreatedAt,Pinned FROM ClipboardEntries WHERE ($q='' OR instr(lower(Text),lower($q))>0) ORDER BY Pinned DESC,Id DESC LIMIT 500";
        cmd.Parameters.AddWithValue("$q", query);
        var list = new List<ClipboardItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToLocalTime(), r.GetInt64(3) != 0));
        return list;
    }

    public async Task TogglePinAsync(long id, bool pin) => await ExecuteAsync("UPDATE ClipboardEntries SET Pinned=$v WHERE Id=$id", id, pin ? 1 : 0);
    public async Task DeleteAsync(long id) => await ExecuteAsync("DELETE FROM ClipboardEntries WHERE Id=$id", id, null);
    public async Task ClearUnpinnedAsync() => await ExecuteAsync("DELETE FROM ClipboardEntries WHERE Pinned=0", null, null);

    private async Task ExecuteAsync(string sql, long? id, int? value)
    {
        await using var c = new SqliteConnection(_cs); await c.OpenAsync();
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        if (id.HasValue) cmd.Parameters.AddWithValue("$id", id.Value);
        if (value.HasValue) cmd.Parameters.AddWithValue("$v", value.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}

