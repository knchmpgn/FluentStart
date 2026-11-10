using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentStart;

/// <summary>The icon of a folder tile: its apps' icons in a small grid on a soft rounded plate (2 x 2 up to four apps, 3 x 3 beyond).</summary>
internal static class FolderIcons
{
    public static ImageSource Compose(IReadOnlyList<ImageSource?> icons)
    {
        const int size = 128;
        int cols = icons.Count <= 4 ? 2 : 3;
        double pad = cols == 2 ? 14 : 12, gap = cols == 2 ? 10 : 6;
        double cell = (size - 2 * pad - gap * (cols - 1)) / cols;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)), null, new Rect(0, 0, size, size), 22, 22);
            for (int i = 0; i < Math.Min(icons.Count, cols * cols); i++)
            {
                if (icons[i] == null) continue;
                int row = i / cols, col = i % cols;
                dc.DrawImage(icons[i], new Rect(pad + col * (cell + gap), pad + row * (cell + gap), cell, cell));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
