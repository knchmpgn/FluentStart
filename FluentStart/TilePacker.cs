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

    /// <summary>
    /// Returns whether a <paramref name="w"/>x<paramref name="h"/> rectangle at (<paramref name="x"/>,<paramref name="y"/>)
    /// fits in a grid of <paramref name="columns"/> without overlapping any of <paramref name="existing"/>.
    /// </summary>
    public static bool Fits(IReadOnlyList<(int X, int Y, int W, int H)> existing, int x, int y, int w, int h, int columns)
    {
        if (columns < 1) columns = 1;
        if (x < 0 || y < 0 || w < 1 || h < 1) return false;
        if (x + w > columns) return false;
        foreach (var e in existing)
        {
            if (x < e.X + e.W && x + w > e.X && y < e.Y + e.H && y + h > e.Y)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Finds the first empty cell (scanning top-to-bottom, left-to-right) for a <paramref name="w"/>x<paramref name="h"/> tile.
    /// </summary>
    public static (int X, int Y) FindPosition(IReadOnlyList<(int X, int Y, int W, int H)> existing, int w, int h, int columns)
    {
        if (columns < 1) columns = 1;
        w = Math.Max(1, Math.Min(w, columns));
        h = Math.Max(1, h);
        int maxY = 0;
        foreach (var e in existing) maxY = Math.Max(maxY, e.Y + e.H);
        for (int y = 0; y <= maxY + h; y++)
            for (int x = 0; x <= columns - w; x++)
                if (Fits(existing, x, y, w, h, columns)) return (x, y);
        return (0, maxY);
    }

    /// <summary>
    /// Finds an empty cell as close as possible to (<paramref name="targetX"/>,<paramref name="targetY"/>).
    /// </summary>
    public static (int X, int Y) FindPositionNear(IReadOnlyList<(int X, int Y, int W, int H)> existing, int w, int h, int columns, int targetX, int targetY)
    {
        if (columns < 1) columns = 1;
        w = Math.Max(1, Math.Min(w, columns));
        h = Math.Max(1, h);
        targetX = Math.Clamp(targetX, 0, Math.Max(0, columns - w));
        targetY = Math.Max(0, targetY);
        // radius 0 first
        if (Fits(existing, targetX, targetY, w, h, columns)) return (targetX, targetY);
        for (int radius = 1; radius <= 24; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    int x = targetX + dx;
                    int y = targetY + dy;
                    if (x < 0 || y < 0 || x + w > columns) continue;
                    if (Fits(existing, x, y, w, h, columns)) return (x, y);
                }
            }
        }
        // fallback: stack below everything
        int maxY = 0;
        foreach (var e in existing) maxY = Math.Max(maxY, e.Y + e.H);
        return (0, maxY);
    }

    /// <summary>Reserves a drop rectangle and moves obstructing tiles forward, preserving other positions where possible.</summary>
    public static List<CellPos> PreviewDrop(
        IReadOnlyList<(int X, int Y, int W, int H)> tiles, int columns, CellPos drop, int dropWidth, int dropHeight,
        CellPos? vacated = null, int vacatedWidth = 0, int vacatedHeight = 0)
    {
        columns = Math.Max(1, columns);
        dropWidth = Math.Clamp(dropWidth, 1, columns);
        dropHeight = Math.Max(1, dropHeight);
        var occupied = new List<(int X, int Y, int W, int H)> { (drop.X, drop.Y, dropWidth, dropHeight) };
        var positions = new CellPos[tiles.Count];
        var order = new List<int>(tiles.Count);
        for (int i = 0; i < tiles.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            int row = tiles[a].Y.CompareTo(tiles[b].Y);
            if (row != 0) return row;
            int column = tiles[a].X.CompareTo(tiles[b].X);
            return column != 0 ? column : a.CompareTo(b);
        });

        foreach (int i in order)
        {
            var tile = tiles[i];
            int w = Math.Clamp(tile.W, 1, columns);
            int h = Math.Max(1, tile.H);
            int x = Math.Clamp(tile.X, 0, columns - w);
            int y = Math.Max(0, tile.Y);
            if (!Fits(occupied, x, y, w, h, columns))
            {
                bool found = false;
                if (vacated is { } hole)
                {
                    for (int holeY = hole.Y; holeY <= hole.Y + vacatedHeight - h && !found; holeY++)
                        for (int holeX = hole.X; holeX <= hole.X + vacatedWidth - w; holeX++)
                        {
                            if (!Fits(occupied, holeX, holeY, w, h, columns)) continue;
                            x = holeX;
                            y = holeY;
                            found = true;
                            break;
                        }
                }
                if (!found)
                {
                    int startX = x;
                    for (;; y++, startX = 0)
                    {
                        for (int nextX = startX; nextX <= columns - w; nextX++)
                        {
                            if (!Fits(occupied, nextX, y, w, h, columns)) continue;
                            x = nextX;
                            found = true;
                            break;
                        }
                        if (found) break;
                    }
                }
            }
            positions[i] = new CellPos(x, y);
            occupied.Add((x, y, w, h));
        }
        return new List<CellPos>(positions);
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
