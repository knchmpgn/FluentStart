using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FluentStart;

public sealed record AccentColors(Color Accent, Color Light1, Color Light2, Color Light3, Color Dark1, Color Dark2, Color Dark3);

/// <summary>
/// The system accent color and its shades - never a hard-coded color.
/// Source (unverified, with fallbacks): HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentPalette (32 bytes = 8 RGBA entries:
/// Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, unused); else DWM AccentColor (ABGR) with derived shades; else SystemParameters.WindowGlassColor.
/// </summary>
public static class AccentPalette
{
    public static AccentColors Read()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] bytes && Parse(bytes) is { } palette) return palette;
        }
        catch { }

        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
                return Derive(Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF)));
        }
        catch { }

        return Derive(SystemParameters.WindowGlassColor);
    }

    public static AccentColors? Parse(byte[] p)
    {
        if (p == null || p.Length < 28) return null;
        Color At(int i) => Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
        bool empty = true;
        for (int i = 0; i < 7; i++) if (p[i * 4] != 0 || p[i * 4 + 1] != 0 || p[i * 4 + 2] != 0) { empty = false; break; }
        return empty ? null : new AccentColors(At(3), At(2), At(1), At(0), At(4), At(5), At(6));
    }

    public static AccentColors Derive(Color a) => new(
        a, Mix(a, Colors.White, 0.25), Mix(a, Colors.White, 0.45), Mix(a, Colors.White, 0.65),
        Mix(a, Colors.Black, 0.20), Mix(a, Colors.Black, 0.40), Mix(a, Colors.Black, 0.60));

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>The WinUI default accent fill: Light2 on dark surfaces, Dark1 on light ones.</summary>
    public static Color Fill(AccentColors a, bool dark) => dark ? a.Light2 : a.Dark1;

    public static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Text color that reads on <paramref name="background"/>.</summary>
    public static Color On(Color background) => Luminance(background) > 0.6 ? Colors.Black : Colors.White;
}
