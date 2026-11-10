using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentStart;

/// <summary>Picks a tile color from an app icon (saturation-weighted average, darkened so white labels stay readable).</summary>
public static class ColorExtractor
{
    private const double MaxLuminance = 0.42;

    /// <returns>null for empty or essentially grayscale icons (callers fall back to the system accent).</returns>
    public static Color? Dominant(BitmapSource src)
    {
        try
        {
            BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            double sr = 0, sg = 0, sb = 0, sw = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 160) continue;
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max == 0) continue;
                double sat = (max - min) / (double)max;
                double weight = sat * sat;
                if (weight < 0.04) continue;
                sr += r * weight; sg += g * weight; sb += b * weight; sw += weight;
            }
            if (sw < 8) return null;

            double R = sr / sw, G = sg / sw, B = sb / sw;
            double lum = (0.299 * R + 0.587 * G + 0.114 * B) / 255.0;
            if (lum > MaxLuminance) { double k = MaxLuminance / lum; R *= k; G *= k; B *= k; }
            return Color.FromRgb((byte)Math.Round(R), (byte)Math.Round(G), (byte)Math.Round(B));
        }
        catch { return null; }
    }
}
