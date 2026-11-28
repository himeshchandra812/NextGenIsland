using System.Diagnostics;
using System.IO;

namespace NextGenIsland.Services;

// Opens an app or website by spoken name. Order: well-known names, websites, Start-menu shortcuts, then Windows' own app lookup.
public static class AppLauncher
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "chrome", ["google chrome"] = "chrome", ["edge"] = "msedge", ["microsoft edge"] = "msedge",
        ["firefox"] = "firefox", ["notepad"] = "notepad", ["calculator"] = "calc", ["calc"] = "calc", ["paint"] = "mspaint",
        ["file explorer"] = "explorer", ["explorer"] = "explorer", ["files"] = "explorer", ["settings"] = "ms-settings:",
        ["task manager"] = "taskmgr", ["command prompt"] = "cmd", ["cmd"] = "cmd", ["powershell"] = "powershell",
        ["terminal"] = "wt", ["word"] = "winword", ["excel"] = "excel", ["powerpoint"] = "powerpnt", ["outlook"] = "outlook",
        ["snipping tool"] = "snippingtool", ["camera"] = "microsoft.windows.camera:", ["clock"] = "ms-clock:",
        ["store"] = "ms-windows-store:", ["microsoft store"] = "ms-windows-store:", ["calendar"] = "outlookcal:", ["mail"] = "outlookmail:",
        ["control panel"] = "control", ["snip"] = "snippingtool", ["maps"] = "bingmaps:", ["photos"] = "ms-photos:"
    };

    private static readonly Dictionary<string, string> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com", ["gmail"] = "https://mail.google.com", ["google"] = "https://www.google.com",
        ["github"] = "https://github.com", ["whatsapp web"] = "https://web.whatsapp.com", ["twitter"] = "https://x.com",
        ["x"] = "https://x.com", ["facebook"] = "https://www.facebook.com", ["instagram"] = "https://www.instagram.com",
        ["reddit"] = "https://www.reddit.com", ["netflix"] = "https://www.netflix.com", ["chatgpt"] = "https://chatgpt.com",
        ["claude"] = "https://claude.ai", ["linkedin"] = "https://www.linkedin.com", ["amazon"] = "https://www.amazon.in",
        ["google maps"] = "https://maps.google.com", ["google drive"] = "https://drive.google.com", ["drive"] = "https://drive.google.com"
    };

    // Returns the friendly name that was opened, or null if nothing matched.
    public static string? Open(string spokenName)
    {
        string name = spokenName.Trim();
        if (name.Length == 0) return null;

        try
        {
            if (Known.TryGetValue(name, out var target)) { Shell(target); return Title(name); }
            if (Sites.TryGetValue(name, out var url)) { Shell(url); return Title(name); }

            string? lnk = FindStartMenuShortcut(name);
            if (lnk != null) { Shell(lnk); return Title(Path.GetFileNameWithoutExtension(lnk)); }

            // Last try: Windows' App Paths lookup (works for things like "spotify" or "vlc" when they are registered).
            Shell(name.Replace(" ", ""));
            return Title(name);
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not open '" + name + "'", ex);
            return null;
        }
    }

    private static void Shell(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string Title(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string? FindStartMenuShortcut(string name)
    {
        string wanted = Squash(name);
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };
        string? startsWith = null, contains = null;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                string n = Squash(Path.GetFileNameWithoutExtension(f));
                if (n.Contains("uninstall")) continue;
                if (n == wanted) return f;
                if (startsWith == null && n.StartsWith(wanted)) startsWith = f;
                else if (contains == null && n.Contains(wanted)) contains = f;
            }
        }
        return startsWith ?? contains;
    }

    private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
