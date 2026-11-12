using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NextGenIsland;

/// <summary>
/// Reads the current Windows desktop wallpaper and turns its dominant colour into an island palette.
/// The image is only read locally; nothing is sent anywhere.
/// </summary>
internal static class WallpaperTheme
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder? value, uint winIni);

    private const uint SpiGetDeskWallpaper = 0x0073;

    private static IslandPalette _cached = AlbumPalette.Fallback;
    private static string _key = "";
    private static DateTime _checkedAt = DateTime.MinValue;

    /// <summary>Palette for the current wallpaper. Re-checks the file at most every 20 seconds.</summary>
    public static IslandPalette Get()
    {
        if (_key.Length > 0 && DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(20)) return _cached;
        _checkedAt = DateTime.UtcNow;

        try
        {
            string? path = FindWallpaperPath();
            if (path == null)
            {
                _key = "none";
                _cached = AlbumPalette.Fallback;
                return _cached;
            }

            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (key == _key) return _cached;

            _cached = Load(path);
            _key = key;
        }
        catch
        {
            // keep whatever we had
        }
        return _cached;
    }

    private static string? FindWallpaperPath()
    {
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(SpiGetDeskWallpaper, (uint)sb.Capacity, sb, 0))
        {
            string p = sb.ToString();
            if (p.Length > 0 && File.Exists(p)) return p;
        }

        // Slideshows and some themes: Windows keeps the current image here.
        string transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    private static IslandPalette Load(string path)
    {
        var bmp = new BitmapImage();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.StreamSource = fs;
            bmp.EndInit();
        }
        bmp.Freeze();

        var palette = AlbumPalette.Extract(bmp);
        if (!palette.Equals(AlbumPalette.Fallback)) return palette;

        // Grey / dark / white wallpaper: use its average colour so the island still feels related.
        return Neutral(bmp);
    }

    private static IslandPalette Neutral(BitmapSource bmp)
    {
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        converted.CopyPixels(px, stride, 0);

        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int i = 0; i + 3 < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
        if (n == 0) return AlbumPalette.Fallback;
        r /= n; g /= n; b /= n;

        byte Lift(double v) => (byte)Math.Clamp(v + (255 - v) * 0.45, 0, 255);
        var accent = Color.FromRgb(Lift(r), Lift(g), Lift(b));
        return new IslandPalette(
            Accent: accent,
            BackgroundStart: Color.FromArgb(0xF2, (byte)(r * 0.45), (byte)(g * 0.45), (byte)(b * 0.45)),
            BackgroundEnd: Color.FromArgb(0xF2, (byte)(r * 0.18), (byte)(g * 0.18), (byte)(b * 0.18)),
            Border: Color.FromArgb(0x55, accent.R, accent.G, accent.B),
            NavIndicator: Color.FromArgb(0x66, accent.R, accent.G, accent.B),
            OnAccent: Color.FromRgb(0x10, 0x10, 0x14));
    }
}
