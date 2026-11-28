using System.Diagnostics;
using System.IO;

namespace NextGenIsland.Services;

public sealed record FoundFile(string Path, string Name, string Folder);

// Looks for a file by the words you said ("resume" finds "Resume 2026.pdf") in your personal folders.
// Gives up after a couple of seconds so a huge disk can never freeze the voice command.
public static class FileFinder
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "AppData", "$RECYCLE.BIN", ".git", ".vs", "packages", "venv", "__pycache__"
    };

    public static FoundFile? Find(string query, int maxMillis = 2500)
    {
        var words = query.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return null;

        var roots = new List<string>();
        void Add(Environment.SpecialFolder f)
        {
            try { var p = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add(p); } catch { }
        }
        Add(Environment.SpecialFolder.Desktop);
        Add(Environment.SpecialFolder.MyDocuments);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sub in new[] { "Downloads", "Pictures", "Videos", "Music", "OneDrive" })
        {
            string p = System.IO.Path.Combine(profile, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }

        var sw = Stopwatch.StartNew();
        FoundFile? best = null;
        double bestScore = double.MinValue;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<(string Dir, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                if (sw.ElapsedMilliseconds > maxMillis) return best;
                var (dir, depth) = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        if (!words.All(w => name.Contains(w))) continue;
                        if (!seen.Add(file)) continue;

                        // Prefer exact names, then names that start with the words, then recently changed files.
                        double score = 0;
                        string joined = string.Join(' ', words);
                        if (name == joined) score += 1000;
                        else if (name.StartsWith(words[0])) score += 300;
                        score -= name.Length;
                        try { score += Math.Max(0, 200 - (DateTime.Now - File.GetLastWriteTime(file)).TotalDays); } catch { }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new FoundFile(file, System.IO.Path.GetFileName(file),
                                System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "") ?? "");
                        }
                    }
                    if (depth < 5)
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            string dn = System.IO.Path.GetFileName(sub);
                            if (dn.StartsWith('.') || SkipDirs.Contains(dn)) continue;
                            pending.Push((sub, depth + 1));
                        }
                    }
                }
                catch { /* folder not readable: skip it */ }
            }
        }
        return best;
    }
}
