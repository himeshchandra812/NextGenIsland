using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NextGenIsland;

/// <summary>Colours the island uses. Everything animates towards these.</summary>
public readonly record struct IslandPalette(
    Color Accent,
    Color BackgroundStart,
    Color BackgroundEnd,
    Color Border,
    Color NavIndicator,
    Color OnAccent);

/// <summary>Extracts a dominant-colour palette from album artwork.</summary>
public static class AlbumPalette
{
    // Blue / graphite fallback used when there is no artwork.
    public static readonly IslandPalette Fallback = new(
        Accent: Color.FromRgb(0x4C, 0x8D, 0xFF),
        BackgroundStart: Color.FromArgb(0xF2, 0x16, 0x1D, 0x2A),
        BackgroundEnd: Color.FromArgb(0xF2, 0x0B, 0x0E, 0x13),
        Border: Color.FromArgb(0x55, 0x4C, 0x8D, 0xFF),
        NavIndicator: Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF),
        OnAccent: Colors.White);

    private const int Bins = 12;

    public static IslandPalette Extract(ImageSource? source)
    {
        try
        {
            if (source is not BitmapSource bmp || bmp.PixelWidth < 1 || bmp.PixelHeight < 1)
                return Fallback;

            double sx = Math.Min(1.0, 32.0 / bmp.PixelWidth);
            double sy = Math.Min(1.0, 32.0 / bmp.PixelHeight);
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(sx, sy));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 4;
            var px = new byte[h * stride];
            converted.CopyPixels(px, stride, 0);

            var weight = new double[Bins];
            var sumH = new double[Bins];
            var sumS = new double[Bins];

            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                (double hue, double sat, double val) = RgbToHsv(r, g, b);
                if (val < 0.18 || sat < 0.18) continue;

                double wgt = sat * (0.35 + val);
                int bin = Math.Min(Bins - 1, (int)(hue / (360.0 / Bins)));
                weight[bin] += wgt;
                sumH[bin] += hue * wgt;
                sumS[bin] += sat * wgt;
            }

            int best = 0;
            for (int i = 1; i < Bins; i++) if (weight[i] > weight[best]) best = i;
            if (weight[best] < 0.5) return Fallback; // artwork is basically grey / black / white

            double h1 = sumH[best] / weight[best];
            double s1 = sumS[best] / weight[best];

            int second = -1;
            for (int i = 0; i < Bins; i++)
            {
                int dist = Math.Min(Math.Abs(i - best), Bins - Math.Abs(i - best));
                if (dist < 2 || weight[i] < weight[best] * 0.25) continue;
                if (second < 0 || weight[i] > weight[second]) second = i;
            }

            double h2 = second >= 0 ? sumH[second] / weight[second] : h1 + 25.0;
            double s2 = second >= 0 ? sumS[second] / weight[second] : s1;

            var accent = HsvToColor(h1, Math.Clamp(s1, 0.50, 0.90), 0.95);
            var bgStart = HsvToColor(h1, Math.Clamp(s1, 0.40, 0.85), 0.30, 0xF2);
            var bgEnd = HsvToColor(h2, Math.Clamp(s2, 0.35, 0.80), 0.12, 0xF2);

            return new IslandPalette(
                Accent: accent,
                BackgroundStart: bgStart,
                BackgroundEnd: bgEnd,
                Border: Color.FromArgb(0x88, accent.R, accent.G, accent.B),
                NavIndicator: Color.FromArgb(0x66, accent.R, accent.G, accent.B),
                OnAccent: Luminance(accent) > 0.60 ? Color.FromRgb(0x10, 0x10, 0x14) : Colors.White);
        }
        catch
        {
            return Fallback;
        }
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0.0;
        if (delta > 1e-9)
        {
            if (max == r) hue = 60.0 * (((g - b) / delta) % 6.0);
            else if (max == g) hue = 60.0 * (((b - r) / delta) + 2.0);
            else hue = 60.0 * (((r - g) / delta) + 4.0);
        }
        if (hue < 0.0) hue += 360.0;
        double sat = max <= 1e-9 ? 0.0 : delta / max;
        return (hue, sat, max);
    }

    private static Color HsvToColor(double hue, double sat, double val, byte alpha = 255)
    {
        double h = ((hue % 360.0) + 360.0) % 360.0;
        double c = val * sat;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = val - c;

        (double rr, double gg, double bb) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromArgb(alpha, ToByte(rr + m), ToByte(gg + m), ToByte(bb + m));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
}
