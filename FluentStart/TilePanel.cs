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

    public static readonly DependencyProperty DropIndexProperty = DependencyProperty.Register(
        nameof(DropIndex), typeof(int), typeof(TilePanel),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DraggedTileProperty = DependencyProperty.Register(
        nameof(DraggedTile), typeof(TileModel), typeof(TilePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty DropTileSizeProperty = DependencyProperty.Register(
        nameof(DropTileSize), typeof(TileSize), typeof(TilePanel),
        new FrameworkPropertyMetadata(TileSize.Medium, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The tile being dragged out of this panel: it leaves the flow so the others close the gap.</summary>
    public TileModel? DraggedTile
    {
        get => (TileModel?)GetValue(DraggedTileProperty);
        set => SetValue(DraggedTileProperty, value);
    }

    /// <summary>
    /// Insertion index (into the child order) at which an open slot is reserved while something is dragged over this panel;
    /// -1 = no slot. The tiles after it slide away to make room and a placeholder is drawn in the slot.
    /// </summary>
    public int DropIndex
    {
        get => (int)GetValue(DropIndexProperty);
        set => SetValue(DropIndexProperty, value);
    }

    /// <summary>Size of the slot reserved at <see cref="DropIndex"/> (the size of the tile being dropped).</summary>
    public TileSize DropTileSize
    {
        get => (TileSize)GetValue(DropTileSizeProperty);
        set => SetValue(DropTileSizeProperty, value);
    }

    /// <summary>When true, tiles that change position glide to their new slot (Fluent "reposition") instead of jumping.</summary>
    public bool AnimateLayout { get; set; }

    /// <summary>The reserved slot in panel coordinates, or <see cref="Rect.Empty"/> when no slot is open.</summary>
    public Rect GapRect => _gap;

    private List<CellPos> _pos = new();
    private List<CellPos> _basePos = new();   // layout without the reserved slot: stable, so hit-testing cannot feed back on itself
    private (int W, int H)[] _sizes = Array.Empty<(int, int)>();
    private Rect _gap = Rect.Empty;
    private readonly Dictionary<UIElement, Point> _last = new();

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
        if (e.PropertyName == nameof(TileModel.IsResolved) || e.PropertyName == nameof(TileModel.App))
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

        int draggedIdx = -1;
        var dragged = DraggedTile;
        if (dragged != null)
        {
            for (int i = 0; i < n; i++) if (ReferenceEquals(models[i], dragged)) { draggedIdx = i; break; }
            if (draggedIdx >= 0) _sizes[draggedIdx] = (0, 0);   // lifted out of the flow
        }

        _gap = Rect.Empty;
        _basePos = TilePacker.Pack(_sizes, Columns, out int baseRows);
        _rows = baseRows;
        int target = DropIndex;
        if (target < 0)
        {
            _pos = _basePos;
            return;
        }

        // Reserve an open slot of the dropped tile's size at the target position.
        var (dw, dh) = TilePacker.CellSize(DropTileSize);
        var nonDragged = new List<int>(n);
        for (int i = 0; i < n; i++) if (i != draggedIdx) nonDragged.Add(i);
        int nonCount = nonDragged.Count;
        int targetNon = draggedIdx >= 0 && target > draggedIdx ? target - 1 : target;
        targetNon = Math.Clamp(targetNon, 0, nonCount);

        var virtualSizes = new List<(int W, int H)>(nonCount + 1);
        var childToVirtual = new int[n];
        for (int i = 0; i < n; i++) childToVirtual[i] = -1;
        int gapIndex = -1;
        for (int k = 0; k < nonCount; k++)
        {
            if (k == targetNon) { gapIndex = virtualSizes.Count; virtualSizes.Add((dw, dh)); }
            int childIdx = nonDragged[k];
            childToVirtual[childIdx] = virtualSizes.Count;
            virtualSizes.Add(_sizes[childIdx]);
        }
        if (gapIndex < 0) { gapIndex = virtualSizes.Count; virtualSizes.Add((dw, dh)); }

        var virtualPos = TilePacker.Pack(virtualSizes, Columns, out _rows);
        var gp = virtualPos[gapIndex];
        _gap = new Rect(gp.X * Cell, gp.Y * Cell, Math.Min(dw, Math.Max(1, Columns)) * Cell, dh * Cell);

        _pos = new List<CellPos>(n);
        for (int i = 0; i < n; i++)
        {
            int vi = childToVirtual[i];
            _pos.Add(vi >= 0 && vi < virtualPos.Count ? virtualPos[vi] : new CellPos(0, 0));
        }
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

    /// <summary>
    /// Insertion index (into the child order) for a drop at <paramref name="p"/> (panel coordinates). Measured against the layout
    /// WITHOUT the reserved slot, so the answer does not change just because tiles slid out of the way.
    /// </summary>
    public int InsertionIndexAt(Point p)
    {
        if (_basePos.Count != InternalChildren.Count) Recompute();
        int best = -1;
        double bestDist = double.MaxValue, bestCx = 0;
        for (int i = 0; i < _sizes.Length; i++)
        {
            var (w, h) = _sizes[i];
            if (w == 0) continue;
            double cx = (_basePos[i].X + w / 2.0) * Cell, cy = (_basePos[i].Y + h / 2.0) * Cell;
            double d = (cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y);
            if (d < bestDist) { bestDist = d; best = i; bestCx = cx; }
        }
        if (best < 0) return InternalChildren.Count;
        return p.X > bestCx ? best + 1 : best;
    }
}
