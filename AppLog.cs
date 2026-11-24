using System.IO;
using System.Text;

namespace NextGenIsland;

// Small file log in %LOCALAPPDATA%\NextGenIsland\app.log (trimmed at 512 KB) so problems on other PCs can be diagnosed.
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");
    public static string FilePath { get; } = Path.Combine(Dir, "app.log");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(FilePath, FilePath + ".old", true);
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").AppendLine(message);
                if (ex != null) sb.AppendLine(ex.ToString());
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch { }
    }
}
