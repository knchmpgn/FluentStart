using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentStart;

/// <summary>
/// Windows 10-style press feedback for tiles: while the button is down the tile sinks towards the point you pressed, and springs
/// back when you let go. WPF has no 3-D plane projection, so the tilt of Windows 10 is approximated by shrinking the tile about the
/// pressed point (the near edge barely moves, the far edge moves most). The 0.95 scale is an estimate.
/// </summary>
public partial class MainWindow
{
    private const double PressScale = 0.95;
    private FrameworkElement? _pressTile;
    private ScaleTransform? _pressScale;

    private void TilePress(FrameworkElement tile, MouseButtonEventArgs e)
    {
        ReleaseTilePress(instant: true);
        if (!Motion.Enabled || tile.ActualWidth < 1 || tile.ActualHeight < 1) return;
        if (!ReferenceEquals(tile.RenderTransform, Transform.Identity)) return;   // another effect owns the transform

        Point p = e.GetPosition(tile);
        tile.RenderTransformOrigin = new Point(Math.Clamp(p.X / tile.ActualWidth, 0, 1), Math.Clamp(p.Y / tile.ActualHeight, 0, 1));
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        _pressTile = tile;
        _pressScale = scale;
        Motion.Run(scale, ScaleTransform.ScaleXProperty, PressScale, Motion.Faster, Motion.Enter);
        Motion.Run(scale, ScaleTransform.ScaleYProperty, PressScale, Motion.Faster, Motion.Enter);
    }

    private void ReleaseTilePress(bool instant)
    {
        var tile = _pressTile;
        var scale = _pressScale;
        if (tile == null || scale == null) return;
        _pressTile = null;
        _pressScale = null;

        void Restore()
        {
            if (ReferenceEquals(tile.RenderTransform, scale)) tile.RenderTransform = Transform.Identity;
        }

        if (instant || !Motion.Enabled)
        {
            Motion.Cancel(scale, ScaleTransform.ScaleXProperty);
            Motion.Cancel(scale, ScaleTransform.ScaleYProperty);
            Restore();
            return;
        }
        Motion.Run(scale, ScaleTransform.ScaleYProperty, 1, Motion.Fast, Motion.Enter);
        Motion.Run(scale, ScaleTransform.ScaleXProperty, 1, Motion.Fast, Motion.Enter, done: Restore);
    }
}
