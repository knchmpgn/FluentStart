using System;
using System.Collections.Generic;

namespace FluentStart;

public readonly record struct CellPos(int X, int Y);

/// <summary>
/// Win10-style tile packing: a grid of small cells, first-fit placement scanning rows top to bottom, left to right.
/// Small = 1x1, Medium = 2x2, Wide = 4x2, Large = 4x4. A (0,0) size marks a collapsed tile that takes no space.
/// </summary>
public static class TilePacker
{
    public static (int W, int H) CellSize(TileSize s) => s switch
    {
        TileSize.Small => (1, 1),
        TileSize.Medium => (2, 2),
        TileSize.Wide => (4, 2),
        TileSize.Large => (4, 4),
        _ => (2, 2)
    };

    public static List<CellPos> Pack(IReadOnlyList<(int W, int H)> sizes, int columns, out int rows)
    {
        if (columns < 1) columns = 1;
        var occupied = new List<bool[]>();
        var result = new List<CellPos>(sizes.Count);

        foreach (var (w0, h) in sizes)
        {
            if (w0 <= 0 || h <= 0) { result.Add(new CellPos(0, 0)); continue; }
            int w = Math.Min(w0, columns);

            for (int y = 0; ; y++)
            {
                int foundX = -1;
                for (int x = 0; x <= columns - w; x++)
                    if (Fits(occupied, x, y, w, h)) { foundX = x; break; }

                if (foundX < 0) continue;
                Mark(occupied, foundX, y, w, h, columns);
                result.Add(new CellPos(foundX, y));
                break;
            }
        }
        rows = occupied.Count;
        return result;
    }

    private static bool Fits(List<bool[]> occ, int x, int y, int w, int h)
    {
        for (int yy = y; yy < y + h && yy < occ.Count; yy++)
            for (int xx = x; xx < x + w; xx++)
                if (occ[yy][xx]) return false;
        return true;
    }

    private static void Mark(List<bool[]> occ, int x, int y, int w, int h, int columns)
    {
        while (occ.Count < y + h) occ.Add(new bool[columns]);
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                occ[yy][xx] = true;
    }
}
