using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace FluentStart;

/// <summary>Per-tile background colours: a palette of Windows accent colours plus helpers for storing and rendering a colour.</summary>
public static class TileColors
{
    /// <summary>
    /// 24 of the Windows accent colours ("Windows colors" in Settings > Personalization > Colors), chosen to be the most distinct:
    /// warm (yellow to rose), pink and purple, blue, teal, green, then neutrals. 8 per row.
    /// </summary>
    public static readonly string[] Palette =
    {
        "#FFB900", "#FF8C00", "#F7630C", "#DA3B01", "#E81123", "#EA005E", "#E3008C", "#C239B3",
        "#881798", "#8764B8", "#6B69D6", "#0063B1", "#0078D7", "#0099BC", "#00B7C3", "#038387",
        "#00B294", "#00CC6A", "#10893E", "#498205", "#7A7574", "#68768A", "#767676", "#4C4A48"
    };

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

    /// <summary>"#RRGGBB" (upper case) for any "#RRGGBB" / "RRGGBB" input, or null if it is not a colour.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
        return "#" + s.ToUpperInvariant();
    }

    public static Color? TryParse(string? value)
    {
        string? hex = Normalize(value);
        if (hex == null) return null;
        return Color.FromRgb(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Brush? BrushOf(string? value)
    {
        string? hex = Normalize(value);
        if (hex == null) return null;
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(TryParse(hex)!.Value);
            brush.Freeze();
            BrushCache[hex] = brush;
        }
        return brush;
    }

    /// <summary>White or black text, whichever reads better on the colour (relative luminance, biased towards white like Windows tiles).</summary>
    public static Brush LabelOn(string? value)
    {
        var c = TryParse(value);
        if (c == null) return Brushes.White;
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double luminance = 0.2126 * Lin(c.Value.R) + 0.7152 * Lin(c.Value.G) + 0.0722 * Lin(c.Value.B);
        return luminance > 0.45 ? Brushes.Black : Brushes.White;
    }
}
