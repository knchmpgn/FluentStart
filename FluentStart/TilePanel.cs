using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FluentStart;

/// <summary>Panel that lays tiles out with <see cref="TilePacker"/>. One cell = 52 DIPs (48 tile + 4 gap, tiles carry a 2 DIP margin).</summary>
public sealed class TilePanel : Panel
{
    public const double Cell = 52;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(TilePanel),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty DraggedTileProperty = DependencyProperty.Register(
        nameof(DraggedTile), typeof(TileModel), typeof(TilePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The tile being dragged; it is excluded from the drop preview.</summary>
    public TileModel? DraggedTile
    {
        get => (TileModel?)GetValue(DraggedTileProperty);
        set => SetValue(DraggedTileProperty, value);
    }

    /// <summary>When true, tiles that change position glide to their new slot (Fluent "reposition") instead of jumping.</summary>
    public bool AnimateLayout { get; set; }

    /// <summary>The reserved slot in panel coordinates, or <see cref="Rect.Empty"/> when no slot is open.</summary>
    public Rect GapRect => _gap;

    private List<CellPos> _pos = new();
    private List<CellPos> _basePos = new();   // layout without the reserved slot: stable, so hit-testing cannot feed back on itself
    private (int W, int H)[] _sizes = Array.Empty<(int, int)>();
    private Rect _gap = Rect.Empty;
    private CellPos? _dropCell;
    private TileSize _dropSize;
    private readonly Dictionary<TileModel, CellPos> _previewPositions = new();
    private readonly Dictionary<UIElement, Point> _last = new();

    internal IReadOnlyDictionary<TileModel, CellPos> PreviewPositions => _previewPositions;

    internal void SetDropTarget(CellPos? cell, TileSize size)
    {
        if (_dropCell == cell && _dropSize == size) return;
        _dropCell = cell;
        _dropSize = size;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualAdded is FrameworkElement added)
        {
            added.DataContextChanged += OnChildDataContextChanged;
            if (added.DataContext is INotifyPropertyChanged npc) npc.PropertyChanged += TilePropertyChanged;
        }
        if (visualRemoved is FrameworkElement removed)
        {
            _last.Remove(removed);
            removed.DataContextChanged -= OnChildDataContextChanged;
            if (removed.DataContext is INotifyPropertyChanged npc) npc.PropertyChanged -= TilePropertyChanged;
        }
    }

    private void OnChildDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldNpc) oldNpc.PropertyChanged -= TilePropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newNpc) newNpc.PropertyChanged += TilePropertyChanged;
    }

    private void TilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TileModel.IsResolved) || e.PropertyName == nameof(TileModel.App)
            || e.PropertyName == nameof(TileModel.Size) || e.PropertyName == nameof(TileModel.Width)
            || e.PropertyName == nameof(TileModel.Height) || e.PropertyName == nameof(TileModel.Column)
            || e.PropertyName == nameof(TileModel.Row))
            InvalidateMeasure();
    }

    private static TileModel? ModelOf(UIElement c) => c switch
    {
        ContentPresenter cp => cp.Content as TileModel,
        FrameworkElement fe => fe.DataContext as TileModel,
        _ => null
    };

    private void Recompute()
    {
        int n = InternalChildren.Count;
        _sizes = new (int, int)[n];
        var models = new TileModel?[n];
        for (int i = 0; i < n; i++)
        {
            models[i] = ModelOf(InternalChildren[i]);
            _sizes[i] = models[i] is { IsResolved: true } ? TilePacker.CellSize(models[i]!.Size) : (0, 0);
        }

        _gap = Rect.Empty;
        _previewPositions.Clear();
        _pos = new List<CellPos>(n);
        _basePos = new List<CellPos>(n);
        int maxRow = 0;
        var previewTiles = new List<(int X, int Y, int W, int H)>();
        var previewIndices = new List<int>();
        CellPos? vacated = null;
        int vacatedWidth = 0, vacatedHeight = 0;
        if (_dropCell is { } drop)
        {
            var (dropW, dropH) = TilePacker.CellSize(_dropSize);
            _gap = new Rect(drop.X * Cell, drop.Y * Cell, Math.Min(dropW, Math.Max(1, Columns)) * Cell, dropH * Cell);
            maxRow = drop.Y + dropH;
            for (int i = 0; i < n; i++)
            {
                var model = models[i];
                if (model == null) continue;
                if (ReferenceEquals(model, DraggedTile))
                {
                    (vacatedWidth, vacatedHeight) = TilePacker.CellSize(model.Size);
                    vacated = new CellPos(Math.Clamp(model.Column, 0, Math.Max(0, Columns - vacatedWidth)), Math.Max(0, model.Row));
                    continue;
                }
                var (w, h) = TilePacker.CellSize(model.Size);
                previewTiles.Add((model.Column, model.Row, w, h));
                previewIndices.Add(i);
            }
        }
        var planned = _dropCell is { } target
            ? TilePacker.PreviewDrop(previewTiles, Columns, target, TilePacker.CellSize(_dropSize).W, TilePacker.CellSize(_dropSize).H,
                vacated, vacatedWidth, vacatedHeight)
            : null;
        var previewByIndex = new Dictionary<int, CellPos>();
        if (planned != null)
            for (int i = 0; i < planned.Count; i++)
            {
                int index = previewIndices[i];
                previewByIndex[index] = planned[i];
                _previewPositions[models[index]!] = planned[i];
            }

        for (int i = 0; i < n; i++)
        {
            var (w, h) = _sizes[i];
            if (w <= 0 || h <= 0)
            {
                _pos.Add(new CellPos(0, 0));
                _basePos.Add(new CellPos(0, 0));
                continue;
            }
            var m = models[i];
            int x = m != null ? m.Column : 0;
            int y = m != null ? m.Row : 0;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            x = Math.Clamp(x, 0, Math.Max(0, Columns - w));
            _basePos.Add(new CellPos(x, y));
            if (previewByIndex.TryGetValue(i, out var preview)) { x = preview.X; y = preview.Y; }
            _pos.Add(new CellPos(x, y));
            maxRow = Math.Max(maxRow, y + h);
        }
        _rows = Math.Max(1, maxRow);
    }

    private int _rows;

    protected override Size MeasureOverride(Size availableSize)
    {
        Recompute();
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var (w, h) = _sizes[i];
            InternalChildren[i].Measure(new Size(w * Cell, h * Cell));
        }
        return new Size(Math.Max(1, Columns) * Cell, _rows * Cell);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_gap.IsEmpty) return;
        var brush = TryFindResource("Brush.Accent") as Brush ?? SystemColors.HighlightBrush;
        var rect = new Rect(_gap.X + 2, _gap.Y + 2, Math.Max(0, _gap.Width - 4), Math.Max(0, _gap.Height - 4));
        dc.PushOpacity(0.18);
        dc.DrawRoundedRectangle(brush, null, rect, 8, 8);
        dc.Pop();
        dc.PushOpacity(0.7);
        dc.DrawRoundedRectangle(null, new Pen(brush, 1.5), rect, 8, 8);
        dc.Pop();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_pos.Count != InternalChildren.Count) Recompute();
        bool animate = AnimateLayout && Motion.Enabled;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var (w, h) = _sizes[i];
            var p = _pos[i];
            var rect = new Rect(p.X * Cell, p.Y * Cell, w * Cell, h * Cell);
            child.Arrange(rect);
            if (animate && w > 0 && _last.TryGetValue(child, out var old)) SlideFrom(child, old, rect.TopLeft);
            if (w > 0) _last[child] = rect.TopLeft;
        }
        return new Size(Math.Max(1, Columns) * Cell, _rows * Cell);
    }

    /// <summary>Starts a glide from the child's previous slot (plus any glide still in progress) to its new slot.</summary>
    private static void SlideFrom(UIElement child, Point oldPos, Point newPos)
    {
        var t = Motion.EnsureTranslate(child);
        double dx = oldPos.X + t.X - newPos.X, dy = oldPos.Y + t.Y - newPos.Y;
        if (Math.Abs(oldPos.X - newPos.X) < 0.5 && Math.Abs(oldPos.Y - newPos.Y) < 0.5) return;
        Motion.Run(t, TranslateTransform.XProperty, 0, Motion.Normal, Motion.Enter, from: dx);
        Motion.Run(t, TranslateTransform.YProperty, 0, Motion.Normal, Motion.Enter, from: dy);
    }

    /// <summary>
    /// The tile whose middle the point is over (panel coordinates), measured on the layout without the open slot; null elsewhere.
    /// Dropping an item here makes a folder instead of reordering. <paramref name="exclude"/> (the tile being dragged) never matches.
    /// </summary>
    public TileModel? TileAtCenter(Point p, TileModel? exclude)
    {
        if (_basePos.Count != InternalChildren.Count) Recompute();
        for (int i = 0; i < _sizes.Length; i++)
        {
            var (w, h) = _sizes[i];
            if (w == 0) continue;
            var model = ModelOf(InternalChildren[i]);
            if (model == null || ReferenceEquals(model, exclude)) continue;
            var cell = new Rect(_basePos[i].X * Cell, _basePos[i].Y * Cell, w * Cell, h * Cell);
            double dx = Math.Min(cell.Width * 0.28, cell.Width / 2 - 8), dy = Math.Min(cell.Height * 0.28, cell.Height / 2 - 8);
            var middle = new Rect(cell.X + dx, cell.Y + dy, Math.Max(1, cell.Width - 2 * dx), Math.Max(1, cell.Height - 2 * dy));
            if (middle.Contains(p)) return model;
        }
        return null;
    }

}
