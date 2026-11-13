using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using FluentStart.Native;

namespace FluentStart;

/// <summary>One entry of a flyout menu.</summary>
public sealed class FlyoutItem
{
    public string Text { get; init; } = "";
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string? Glyph { get; init; }
    public ImageSource? Icon { get; init; }
    public Action? Click { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsHeader { get; init; }
    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Hint { get; init; }
    public IReadOnlyList<FlyoutItem>? Children { get; init; }

    /// <summary>When set, this entry is drawn as a grid of round colour swatches (8 per row) instead of a text row.</summary>
    public IReadOnlyList<Color>? Swatches { get; init; }
    public Color? SelectedColor { get; init; }
    public Action<Color>? SwatchChosen { get; init; }

    public static FlyoutItem Separator() => new() { IsSeparator = true };
    public static FlyoutItem Header(string text) => new() { Text = text, IsHeader = true, IsEnabled = false };
}

/// <summary>
/// Context menus drawn as small top-level windows so they get the same DWM backdrop (Mica / Mica Alt / Acrylic), rounded corners,
/// dark/light mode and system shadow as the Start window. (WPF's own ContextMenu popup is a layered window, which cannot have a DWM backdrop.)
/// </summary>
public static class Flyout
{
    internal static FlyoutWindow? Root;

    /// <summary>When a menu command was last chosen. The Start window uses it to stay open (and take focus back) after a command that runs inside Start.</summary>
    internal static DateTime LastActionUtc = DateTime.MinValue;

    /// <summary>Opens a menu with its top-left at the mouse cursor (flips/clamps to stay on screen).</summary>
    public static void ShowAtCursor(IReadOnlyList<FlyoutItem> items, Action? closed = null)
    {
        NativeMethods.GetCursorPos(out var pt);
        Open(items, new Rect(pt.X, pt.Y, 0, 0), FlyoutPlacement.Cursor, closed);
    }

    /// <summary>Opens a menu to the right of an element (rail buttons), bottom-aligned when there is room.</summary>
    public static void ShowBeside(FrameworkElement element, IReadOnlyList<FlyoutItem> items, Action? closed = null)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var topLeft = element.PointToScreen(new Point(0, 0));
        var rect = new Rect(topLeft.X, topLeft.Y, element.ActualWidth * dpi.DpiScaleX, element.ActualHeight * dpi.DpiScaleY);
        Open(items, rect, FlyoutPlacement.Right, closed);
    }

    public static void CloseAll() => Root?.CloseChain();

    private static void Open(IReadOnlyList<FlyoutItem> items, Rect anchorPx, FlyoutPlacement placement, Action? closed)
    {
        CloseAll();
        if (items.Count == 0) { closed?.Invoke(); return; }
        var w = new FlyoutWindow(items, null, anchorPx, placement) { OnClosedAll = closed };
        Root = w;
        w.Show();
    }
}

internal enum FlyoutPlacement { Cursor, Right }

internal sealed class FlyoutWindow : Window
{
    private const double RowHeight = 34, MaxMenuWidth = 360;

    private readonly IReadOnlyList<FlyoutItem> _items;
    private readonly FlyoutWindow? _parent;
    private readonly Rect _anchorPx;
    private readonly FlyoutPlacement _placement;
    private readonly List<(Border Row, FlyoutItem Item)> _rows = new();
    private readonly StackPanel _stack = new() { Margin = new Thickness(4) };
    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FlyoutWindow? _child;
    private int _index = -1;
    private bool _closing;
    private int _px, _py, _pw, _ph;

    public Action? OnClosedAll { get; set; }

    public FlyoutWindow(IReadOnlyList<FlyoutItem> items, FlyoutWindow? parent, Rect anchorPx, FlyoutPlacement placement)
    {
        _items = items;
        _parent = parent;
        _anchorPx = anchorPx;
        _placement = placement;

        WindowStyle = WindowStyle.None;
        // DWM needs the resize frame for Mica; WindowChrome's zero resize border prevents resizing the menu.
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        try { SetResourceReference(FontFamilyProperty, "Font.Ui"); } catch { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); }
        FontSize = 14;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false
        });

        BuildRows();
        Content = _stack;

        // Size from content so the position can be computed before the window is shown (no flash at the wrong place).
        _stack.Measure(new Size(MaxMenuWidth, double.PositiveInfinity));
        Width = Math.Min(_stack.DesiredSize.Width, MaxMenuWidth);
        Height = _stack.DesiredSize.Height;
        foreach (var (row, _) in _rows)
            if (row.Child is Grid grid) grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        ComputePlacement();

        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (_index >= 0) OpenChild(_index); };
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Appearance.KeepBackdropActive(src);
            if (Appearance.Apply(hwnd, out _)) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "Brush.Flyout");
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, _px, _py, _pw, _ph, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            if (_parent == null)
            {
                NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);   // needed when opened from the tray icon
                Activate();
            }
            Animate();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_closing && !ChainActive()) RootWindow().CloseChain();
        }));
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _openTimer.Stop();
            if (_parent == null)
            {
                if (ReferenceEquals(Flyout.Root, this)) Flyout.Root = null;
                OnClosedAll?.Invoke();
            }
        };
    }

    // ---------- content ----------

    private void BuildRows()
    {
        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(10, 4, 10, 4) };
                sep.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
                _stack.Children.Add(sep);
                continue;
            }
            if (item.IsHeader)
            {
                var h = new TextBlock { Text = item.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                _stack.Children.Add(h);
                continue;
            }

            if (item.Swatches != null)
            {
                _stack.Children.Add(BuildSwatchGrid(item));
                continue;
            }

            var grid = new Grid { MinHeight = RowHeight - 2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement lead;
            if (item.Icon != null)
            {
                var image = new Image { Source = item.Icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                lead = image;
            }
            else
            {
                string? glyph = item.IsChecked ? "\uE73E" : item.Glyph;
                var g = new TextBlock { Text = glyph ?? "", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                g.SetResourceReference(TextBlock.FontFamilyProperty, "FluentIcons");
                g.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                lead = g;
            }
            grid.Children.Add(lead);

            var text = new TextBlock { Text = item.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (item.Children != null || item.Hint != null)
            {
                var tail = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) };
                if (item.Children != null)
                {
                    tail.Text = "\uE76C"; tail.FontSize = 10;
                    tail.SetResourceReference(TextBlock.FontFamilyProperty, "FluentIcons");
                }
                else { tail.Text = item.Hint; tail.FontSize = 12; tail.Opacity = 0.7; }
                tail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                Grid.SetColumn(tail, 2);
                grid.Children.Add(tail);
            }

            var row = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(8, 0, 10, 0), Child = grid, Background = Brushes.Transparent };
            if (!item.IsEnabled) row.Opacity = 0.4;
            int idx = _rows.Count;
            _rows.Add((row, item));
            row.MouseEnter += (_, _) => Select(idx, hover: true);
            row.MouseLeftButtonUp += (_, e) => { Activate(idx); e.Handled = true; };
            _stack.Children.Add(row);
        }
    }

    /// <summary>Round colour swatches, 8 per row. Hover outlines a swatch; the current colour has a thicker outline; click picks it.</summary>
    private FrameworkElement BuildSwatchGrid(FlyoutItem item)
    {
        var panel = new UniformGrid { Columns = 8, Margin = new Thickness(6, 6, 6, 4) };
        foreach (var color in item.Swatches!)
        {
            var c = color;
            bool current = item.SelectedColor is { } sel && sel == c;

            var dot = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(c) };
            void Rest()
            {
                dot.StrokeThickness = current ? 2.5 : 1;
                dot.SetResourceReference(Shape.StrokeProperty, current ? "Brush.Text" : "Brush.Divider");
            }
            Rest();

            var cell = new Border
            {
                Width = 32, Height = 32, Background = Brushes.Transparent, Child = dot,
                Cursor = Cursors.Hand, ToolTip = TileColors.ToHex(c)
            };
            cell.MouseEnter += (_, _) =>
            {
                Select(-1, hover: true);   // no text row stays highlighted while a swatch is hovered
                dot.StrokeThickness = 2.5;
                dot.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
            };
            cell.MouseLeave += (_, _) => Rest();
            cell.MouseLeftButtonUp += (_, e) =>
            {
                var chosen = item.SwatchChosen;
                Flyout.LastActionUtc = DateTime.UtcNow;
                RootWindow().CloseChain();
                if (chosen != null) Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => chosen(c)));
                e.Handled = true;
            };
            panel.Children.Add(cell);
        }
        return panel;
    }

    private void Select(int i, bool hover)
    {
        if (i == _index) return;
        if (_index >= 0 && _index < _rows.Count) _rows[_index].Row.ClearValue(Border.BackgroundProperty);
        _index = i;
        _openTimer.Stop();
        if (i < 0) return;

        _rows[i].Row.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        var item = _rows[i].Item;
        // Moving to a different row closes an open submenu; hovering a parent row opens its submenu after a short delay.
        if (_child != null) { _child.CloseChain(); _child = null; }
        if (hover && item.Children != null && item.IsEnabled) _openTimer.Start();
    }

    private void Activate(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        var item = _rows[i].Item;
        if (!item.IsEnabled) return;
        if (item.Children != null) { OpenChild(i); return; }

        var click = item.Click;
        Flyout.LastActionUtc = DateTime.UtcNow;
        RootWindow().CloseChain();
        if (click != null) Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, click);
    }

    private void OpenChild(int i)
    {
        var item = _rows[i].Item;
        if (item.Children == null || item.Children.Count == 0) return;
        if (_child != null) { _child.CloseChain(); _child = null; }

        var dpi = VisualTreeHelper.GetDpi(this);
        var rowTopLeft = _rows[i].Row.PointToScreen(new Point(0, 0));
        var anchor = new Rect(rowTopLeft.X, rowTopLeft.Y, _rows[i].Row.ActualWidth * dpi.DpiScaleX, _rows[i].Row.ActualHeight * dpi.DpiScaleY);
        _child = new FlyoutWindow(item.Children, this, anchor, FlyoutPlacement.Right);
        _child.Show();
    }

    // ---------- placement / animation ----------

    private void ComputePlacement()
    {
        var pt = new NativeMethods.POINT { X = (int)_anchorPx.X, Y = (int)_anchorPx.Y };
        IntPtr mon = NativeMethods.MonitorFromPoint(pt, 2);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(mon, ref mi);
        uint dpi = 96;
        try { NativeMethods.GetDpiForMonitor(mon, 0, out dpi, out _); } catch { }
        double s = dpi / 96.0;

        var work = mi.rcWork;
        _pw = (int)Math.Ceiling(Width * s);
        _ph = (int)Math.Ceiling(Height * s);
        int gap = (int)(4 * s);

        if (_placement == FlyoutPlacement.Cursor)
        {
            _px = (int)_anchorPx.X;
            _py = (int)_anchorPx.Y;
            if (_px + _pw > work.Right) _px = work.Right - _pw;
            if (_py + _ph > work.Bottom) _py = (int)_anchorPx.Y - _ph;
        }
        else
        {
            _px = (int)_anchorPx.Right + gap;
            if (_px + _pw > work.Right) _px = (int)_anchorPx.Left - _pw - gap;    // no room on the right: open to the left
            _py = _parent == null ? (int)_anchorPx.Bottom - _ph : (int)_anchorPx.Top - gap;
            if (_py + _ph > work.Bottom) _py = work.Bottom - _ph - gap;
        }
        _px = Math.Max(work.Left, _px);
        _py = Math.Max(work.Top, _py);
    }

    private void Animate()
    {
        if (!Motion.Enabled) return;
        var slide = Motion.EnsureTranslate(_stack);
        Motion.Run(_stack, OpacityProperty, 1, Motion.Fast, Motion.Enter, from: 0);
        Motion.Run(slide, TranslateTransform.YProperty, 0, Motion.Fast, Motion.Enter, from: Motion.SlideItem);
    }

    // ---------- keyboard / lifetime ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_parent != null) Close(); else CloseChain();
                e.Handled = true; break;
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter:
            case Key.Space: Activate(_index); e.Handled = true; break;
            case Key.Right:
                if (_index >= 0 && _rows[_index].Item.Children != null) { Activate(_index); e.Handled = true; }
                break;
            case Key.Left:
                if (_parent != null) { Close(); _parent.Activate(); e.Handled = true; }
                break;
        }
    }

    private void Move(int dir)
    {
        if (_rows.Count == 0) return;
        int i = _index;
        for (int n = 0; n < _rows.Count; n++)
        {
            i = ((i + dir) % _rows.Count + _rows.Count) % _rows.Count;
            if (_rows[i].Item.IsEnabled) { Select(i, hover: false); return; }
        }
    }

    private FlyoutWindow RootWindow() { var w = this; while (w._parent != null) w = w._parent; return w; }

    private bool ChainActive()
    {
        for (var w = RootWindow(); w != null; w = w._child) if (w.IsActive) return true;
        return false;
    }

    internal void CloseChain()
    {
        if (_closing) return;
        _closing = true;
        _child?.CloseChain();
        _child = null;
        if (_parent != null && ReferenceEquals(_parent._child, this)) _parent._child = null;
        try { Close(); } catch { }
    }
}
