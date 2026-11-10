using System;

namespace FluentStart;

/// <summary>
/// Window-width arithmetic for the Start menu. The tile grid is <c>columns</c> small cells wide (a Medium tile is 2 cells, Wide/Large 4),
/// so only even column counts keep every tile whole; the window snaps to those widths when resized.
/// </summary>
public static class TileLayout
{
    public const double Rail = 48, ListPane = 300, Pad = 40, Cell = 52;
    public const int MinColumns = 6, MaxColumns = 16;

    /// <summary>Window width in DIPs for a column count.</summary>
    public static double WidthFor(int columns, bool singlePane)
    {
        double tiles = columns * Cell + Pad;
        // single-pane windows use one width for both panes so switching panes never resizes the window
        return singlePane ? Rail + Math.Max(ListPane + 60, tiles) : Rail + ListPane + tiles;
    }

    /// <summary>Even columns within [6, 16].</summary>
    public static int Normalize(int columns) => Math.Clamp(columns - columns % 2, MinColumns, MaxColumns);

    /// <summary>Allowed column count whose window width is closest to <paramref name="widthDip"/> (never wider than <paramref name="maxWidthDip"/> unless even the minimum is).</summary>
    public static int Nearest(double widthDip, bool singlePane, double maxWidthDip = double.MaxValue)
    {
        int best = MinColumns;
        double bestDiff = double.MaxValue;
        for (int c = MinColumns; c <= MaxColumns; c += 2)
        {
            double w = WidthFor(c, singlePane);
            if (w > maxWidthDip && c > MinColumns) break;
            double diff = Math.Abs(w - widthDip);
            if (diff < bestDiff) { bestDiff = diff; best = c; }
        }
        return best;
    }

    /// <summary>Largest allowed column count not exceeding <paramref name="preferred"/> that fits in <paramref name="maxWidthDip"/>.</summary>
    public static int Fit(int preferred, bool singlePane, double maxWidthDip)
    {
        for (int c = Normalize(preferred); c > MinColumns; c -= 2)
            if (WidthFor(c, singlePane) <= maxWidthDip) return c;
        return MinColumns;
    }
}
