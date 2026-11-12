using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NextGenIsland.Services;

/// <summary>Reads what was copied (text, image or files) into the store, and copies an entry back.</summary>
public static class ClipboardAccess
{
    private const long MaxImagePixels = 24_000_000;
    private const int MaxImageBytes = 20_000_000;

    private static readonly Dictionary<string, BitmapImage> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    // The clipboard can be briefly locked by the app that just copied, so retry a few times.
    private static async Task<T> TryAsync<T>(Func<T> read, T fallback)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return read(); }
            catch (COMException) { await Task.Delay(40); }
        }
        return fallback;
    }

    /// <summary>Stores whatever is on the clipboard. Returns a short status line, or null if nothing was saved.</summary>
    public static async Task<string?> CaptureAsync(ClipboardStore store)
    {
        int kind = await TryAsync(() =>
        {
            if (Clipboard.ContainsFileDropList()) return 1;
            if (Clipboard.ContainsText()) return 2;
            if (Clipboard.ContainsImage()) return 3;
            return 0;
        }, 0);

        switch (kind)
        {
            case 1:
            {
                var paths = await TryAsync<List<string>?>(
                    () => Clipboard.GetFileDropList().Cast<string>().Take(50).ToList(), null);
                if (paths == null || paths.Count == 0) return null;
                await store.SaveFilesAsync(paths);
                return paths.Count == 1 ? "Saved 1 file" : "Saved " + paths.Count + " files";
            }
            case 2:
            {
                var text = await TryAsync<string?>(() => Clipboard.GetText(), null);
                if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return null;
                await store.SaveIfNewAsync(text);
                var preview = text.Trim();
                if (preview.Length > 28) preview = preview[..28] + "...";
                return "Saved: \"" + preview + "\"";
            }
            case 3:
            {
                var bmp = await TryAsync<BitmapSource?>(() => Clipboard.GetImage(), null);
                if (bmp == null || (long)bmp.PixelWidth * bmp.PixelHeight > MaxImagePixels) return null;
                if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();

                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                byte[] png = await Task.Run(() =>
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    return ms.ToArray();
                });
                if (png.Length > MaxImageBytes) return null;

                await store.SaveImageAsync(png, w, h);
                return "Saved image " + w + "x" + h;
            }
            default:
                return null;
        }
    }

    /// <summary>Puts an entry back on the clipboard. Returns a short status line.</summary>
    public static string CopyBack(ClipboardItem it)
    {
        switch (it.Kind)
        {
            case "image":
            {
                if (string.IsNullOrEmpty(it.ImagePath) || !File.Exists(it.ImagePath)) return "Image file is missing";
                Clipboard.SetImage(LoadBitmap(it.ImagePath, 0));
                return "Image copied";
            }
            case "files":
            {
                var existing = it.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
                if (existing.Length == 0) return "Those files no longer exist";
                var list = new StringCollection();
                list.AddRange(existing);
                Clipboard.SetFileDropList(list);
                return existing.Length == 1 ? "Copied 1 file" : "Copied " + existing.Length + " files";
            }
            default:
                Clipboard.SetText(it.Text);
                return "Copied to clipboard";
        }
    }

    /// <summary>Small preview of a saved image, cached. Null if the file is missing or unreadable.</summary>
    public static BitmapImage? LoadThumb(string path, int width)
    {
        try
        {
            if (ThumbCache.TryGetValue(path, out var cached)) return cached;
            if (!File.Exists(path)) return null;
            var bmp = LoadBitmap(path, width);
            if (ThumbCache.Count > 24) ThumbCache.Clear();
            ThumbCache[path] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
