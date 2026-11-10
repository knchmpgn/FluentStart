using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FluentStart;

/// <summary>
/// Returns a negative left margin equal to the left side bearing of the first character, so the *ink* of a letter (not its
/// text box) sits exactly on the alignment edge. Bindings: Content (string), FontFamily, FontSize, FontWeight.
/// Falls back to no offset if the font cannot be resolved.
/// </summary>
public sealed class InkInsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (values.Length < 4 || values[0] is not string text || text.Length == 0 ||
                values[1] is not FontFamily family || values[2] is not double size || values[3] is not FontWeight weight)
                return new Thickness(0);

            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs) || glyphs == null) return new Thickness(0);
            if (!glyphs.CharacterToGlyphMap.TryGetValue(text[0], out ushort glyph)) return new Thickness(0);
            if (!glyphs.LeftSideBearings.TryGetValue(glyph, out double bearing)) return new Thickness(0);

            return new Thickness(-bearing * size, 0, 0, 0);
        }
        catch
        {
            return new Thickness(0);
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
