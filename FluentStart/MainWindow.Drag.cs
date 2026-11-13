using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FluentStart;

/// <summary>
/// Drag and drop for tiles, apps (pin) and tile groups, modelled on the Windows 10 Start menu:
///  - the other tiles dim and shrink slightly while a tile is dragged, and slide aside to leave an empty slot;
///  - a tile dropped between groups, above the first, or below the last gets a thin bar showing where a NEW group will appear
///    (the groups below slide down to make room); release to create it;
///  - a whole group is moved by its grip (the two short lines at the right of its name) and a bar shows where it will land;
///  - no tooltips, labels or shadows; the pointer stays a normal arrow.
/// Everything a drag can touch lives in this window, so it is handled with mouse capture instead of OLE.
/// The dim/shrink amounts and slide timings are estimates (Microsoft does not publish them).
/// </summary>
public partial class MainWindow
{
    private enum DropKind { None, Group, NewGroup, Unpin, Reorder, IntoTile }

    private const double DimOpacity = 0.62, DimScale = 0.94, GroupShift = 30;

    private sealed class GroupHost
    {
        public StackPanel Host = null!;
        public TileGroup Group = null!;
        public TranslateTransform Shift = null!;
    }

    private sealed class DragSession
    {
        public object Payload = null!;
        public FrameworkElement Source = null!;
        public GroupHost? SourceHostInfo;             // group drags
        public Border Ghost = null!;
        public Rectangle Bar = null!;                 // new-group / group-move placeholder
        public bool BarShown;
        public Vector Grab;
        public Point Origin;                          // where the ghost started (RootGrid coordinates)
        public double ScrollAtStart;
        public List<TilePanel> Panels = new();
        public List<GroupHost> Hosts = new();
        public List<(UIElement Child, ScaleTransform? Scale, TileModel? Model)> Dimmed = new();
        public TileModel? TargetTile;                 // drop-into-tile (makes / fills a folder)
        public TileModel? HotTile;
        public TilePanel? Hover;
        public DropKind Kind;
        public TileGroup? TargetGroup;
        public TileGroup? SlotBefore;                 // new group / moved group goes before this group (null = at the end)
        public int Slot = -1;
        public int CellX;
        public int CellY;
        public bool OverList;
        public bool Settling;
        public long Tick = Stopwatch.GetTimestamp();
    }

    private DragSession? _drag;

    // ---------- starting a drag ----------

    private void Tile_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not TileModel t || !ExceededDragDistance(e)) return;
        BeginDrag((FrameworkElement)sender, t);
    }

    private void GroupHeader_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not TileGroup grp || grp.IsEditing || !ExceededDragDistance(e)) return;
        BeginDrag((FrameworkElement)sender, grp);
    }

    private void ListItem_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not ListRow r || r.IsFolder || r.App.IsEditing || !ExceededDragDistance(e)) return;
        BeginDrag((FrameworkElement)sender, r.App);
    }

    private void BeginDrag(FrameworkElement source, object payload)
    {
        _dragCandidate = null;
        if (_drag != null) return;
        ReleaseTilePress(instant: true);   // the press effect ends the moment a drag starts
        if (payload is AppEntry && !TilesScroll.IsVisible) return;          // nothing to drop an app onto
        if (!Mouse.Capture(RootGrid, CaptureMode.Element)) return;

        _listScroll?.Cancel();
        _tilesScroll?.Cancel();
        _resultsScroll?.Cancel();

        var s = new DragSession { Payload = payload, Source = source, ScrollAtStart = TilesScroll.VerticalOffset };
        _drag = s;
        _suppressHide = true;
        Mouse.OverrideCursor = Cursors.Arrow;

        Point pointer = Mouse.GetPosition(RootGrid);
        Point topLeft = source.TranslatePoint(new Point(0, 0), RootGrid);
        BuildGhost(s, source, payload, pointer, topLeft);

        // Tile panels glide their tiles; the dragged tile leaves its flow. Group blocks can slide to open room for a new group.
        CollectChildren(TilesScroll, s.Panels);
        foreach (var p in s.Panels)
        {
            p.AnimateLayout = true;
            if (payload is TileModel tm) p.DraggedTile = tm;
            if (FindAncestorWhere(p, d => d is StackPanel { Tag: "TileGroupHost" }) is StackPanel host && host.DataContext is TileGroup g)
                s.Hosts.Add(new GroupHost { Host = host, Group = g, Shift = Motion.EnsureTranslate(host) });
        }
        if (payload is TileGroup moving) s.SourceHostInfo = s.Hosts.Find(h => ReferenceEquals(h.Group, moving));

        if (payload is not TileGroup) DimTiles(s);
        if (payload is AppEntry) Motion.Run(source, OpacityProperty, 0.5, Motion.Fast, Motion.Enter);
        if (payload is TileModel) Motion.Set(source, OpacityProperty, 0);
        if (s.SourceHostInfo != null) Motion.Run(s.SourceHostInfo.Host, OpacityProperty, 0.5, Motion.Fast, Motion.Enter);

        s.Bar = new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Opacity = 0, IsHitTestVisible = false };
        s.Bar.SetResourceReference(Shape.FillProperty, "Brush.Accent");

        DragLayer.Visibility = Visibility.Visible;
        DragLayer.Children.Add(s.Bar);
        DragLayer.Children.Add(s.Ghost);

        RootGrid.MouseMove += Drag_MouseMove;
        RootGrid.MouseLeftButtonUp += Drag_MouseUp;
        RootGrid.LostMouseCapture += Drag_LostCapture;
        CompositionTarget.Rendering += Drag_Frame;

        UpdateDrag(pointer);
    }

    private void BuildGhost(DragSession s, FrameworkElement source, object payload, Point pointer, Point topLeft)
    {
        FrameworkElement body;
        double w, h;

        if (payload is TileModel && Snapshot(source) is { } snap)
        {
            // The tile itself, full size and fully opaque - it is the one thing that is not dimmed.
            w = source.ActualWidth; h = source.ActualHeight;
            body = new Image { Source = snap, Width = w, Height = h, Stretch = Stretch.Fill };
            s.Grab = pointer - topLeft;
            s.Origin = topLeft;
        }
        else if (payload is TileGroup grp)
        {
            var host = FindAncestorWhere(source, d => d is StackPanel { Tag: "TileGroupHost" }) as FrameworkElement;
            if (host != null && Snapshot(host) is { } groupSnap)
            {
                // The whole section (name and tiles), as a translucent copy that follows the pointer - like Windows 10.
                Point hostTop = host.TranslatePoint(new Point(0, 0), RootGrid);
                w = host.ActualWidth; h = host.ActualHeight;
                body = new Image { Source = groupSnap, Width = w, Height = h, Stretch = Stretch.Fill, Opacity = 0.85 };
                s.Grab = pointer - hostTop;
                s.Origin = hostTop;
            }
            else
            {
                var label = new TextBlock { Text = string.IsNullOrWhiteSpace(grp.Name) ? "Group" : grp.Name, FontWeight = FontWeights.SemiBold, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                var pill = new Border { Padding = new Thickness(12, 6, 12, 6), Child = label };
                pill.SetResourceReference(Border.BackgroundProperty, "Brush.Flyout");
                body = pill;
                body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = body.DesiredSize.Width; h = body.DesiredSize.Height;
                s.Grab = new Vector(w / 2, h / 2);
                s.Origin = new Point(topLeft.X, topLeft.Y - 2);
            }
        }
        else   // an app from the list: a tile-shaped chip showing the icon, i.e. what will be pinned
        {
            var icon = payload is AppEntry a ? a.Icon : (payload is TileModel tm ? tm.App?.Icon : null);
            w = h = 56;
            var img = new Image { Source = icon, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var chip = new Border { Child = img };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            body = chip;
            s.Grab = new Vector(w / 2, h / 2);
            s.Origin = new Point(topLeft.X + 12, topLeft.Y - 8);
        }

        s.Ghost = new Border { Width = w, Height = h, Child = body, IsHitTestVisible = false };
        Canvas.SetLeft(s.Ghost, pointer.X - s.Grab.X);
        Canvas.SetTop(s.Ghost, pointer.Y - s.Grab.Y);
    }

    /// <summary>The other tiles dim and shrink slightly while a tile is being dragged.</summary>
    private void DimTiles(DragSession s)
    {
        foreach (var panel in s.Panels)
        {
            foreach (UIElement child in panel.Children)
            {
                var model = (child as ContentPresenter)?.Content as TileModel;
                if (model != null && ReferenceEquals(model, s.Payload)) continue;

                ScaleTransform? scale = null;
                if (VisualTreeHelper.GetChildrenCount(child) > 0 && VisualTreeHelper.GetChild(child, 0) is FrameworkElement inner
                    && ReferenceEquals(inner.RenderTransform, Transform.Identity))
                {
                    scale = new ScaleTransform(1, 1);
                    inner.RenderTransformOrigin = new Point(0.5, 0.5);
                    inner.RenderTransform = scale;
                    Motion.Run(scale, ScaleTransform.ScaleXProperty, DimScale, Motion.Fast, Motion.Enter);
                    Motion.Run(scale, ScaleTransform.ScaleYProperty, DimScale, Motion.Fast, Motion.Enter);
                }
                Motion.Run(child, OpacityProperty, DimOpacity, Motion.Fast, Motion.Enter);
                s.Dimmed.Add((child, scale, model));
            }
        }
    }

    /// <summary>The tile an item would be dropped into is lifted back to full size and opacity while the others stay dimmed.</summary>
    private static void SetHotTile(DragSession s, TileModel? tile)
    {
        if (ReferenceEquals(s.HotTile, tile)) return;
        void Apply(TileModel? model, bool hot)
        {
            if (model == null) return;
            foreach (var (child, scale, owner) in s.Dimmed)
            {
                if (!ReferenceEquals(owner, model)) continue;
                Motion.Run(child, OpacityProperty, hot ? 1 : DimOpacity, Motion.Fast, Motion.Enter);
                if (scale == null) continue;
                Motion.Run(scale, ScaleTransform.ScaleXProperty, hot ? 1.08 : DimScale, Motion.Fast, Motion.Enter);
                Motion.Run(scale, ScaleTransform.ScaleYProperty, hot ? 1.08 : DimScale, Motion.Fast, Motion.Enter);
            }
        }
        Apply(s.HotTile, false);
        Apply(tile, true);
        s.HotTile = tile;
    }

    private static void UndimTiles(DragSession s)
    {
        foreach (var (child, scale, _) in s.Dimmed)
        {
            Motion.Run(child, OpacityProperty, 1, Motion.Fast, Motion.Enter);
            if (scale == null) continue;
            Motion.Run(scale, ScaleTransform.ScaleXProperty, 1, Motion.Fast, Motion.Enter);
            Motion.Run(scale, ScaleTransform.ScaleYProperty, 1, Motion.Fast, Motion.Enter);
        }
    }

    // ---------- tracking the pointer ----------

    private void Drag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { Settling: false }) return;
        UpdateDrag(e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void Drag_Frame(object? sender, EventArgs e)
    {
        var s = _drag;
        if (s == null || s.Settling) return;

        double dt = Stopwatch.GetElapsedTime(s.Tick).TotalSeconds;
        s.Tick = Stopwatch.GetTimestamp();
        dt = Math.Clamp(dt, 0, 0.05);

        if (!TilesScroll.IsVisible || TilesScroll.ScrollableHeight <= 0) return;
        Point p = Mouse.GetPosition(TilesScroll);
        if (p.X < 0 || p.X > TilesScroll.ActualWidth) return;

        const double edge = 56;
        double v = 0;
        if (p.Y < edge) v = -(edge - Math.Max(0, p.Y)) / edge;
        else if (p.Y > TilesScroll.ActualHeight - edge) v = (p.Y - (TilesScroll.ActualHeight - edge)) / edge;
        if (v == 0) return;

        TilesScroll.ScrollToVerticalOffset(TilesScroll.VerticalOffset + Math.Clamp(v, -1, 1) * 700 * dt);
        UpdateDrag(Mouse.GetPosition(RootGrid));
    }

    /// <summary>Position of a group block ignoring the temporary slide used to open room for a new group.</summary>
    private Rect LayoutBounds(GroupHost h)
    {
        var b = h.Host.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, h.Host.ActualWidth, h.Host.ActualHeight));
        b.Y -= h.Shift.Y;
        return b;
    }

    private void UpdateDrag(Point pointer)
    {
        var s = _drag;
        if (s == null || s.Settling) return;

        Canvas.SetLeft(s.Ghost, pointer.X - s.Grab.X);
        Canvas.SetTop(s.Ghost, pointer.Y - s.Grab.Y);

        // Over the app list? (dropping a tile there unpins it - there is deliberately no label, the tile just fades)
        bool overList = false;
        if (s.Payload is TileModel)
        {
            var hit = VisualTreeHelper.HitTest(RootGrid, pointer)?.VisualHit;
            for (DependencyObject? d = hit; d != null; d = ParentOf(d))
                if (ReferenceEquals(d, AppList)) { overList = true; break; }
        }

        DropKind kind = DropKind.None;
        TilePanel? panel = null;
        TileGroup? target = null, before = null;
        TileModel? intoTile = null;
        int index = 0, slot = -1;

        Point inPane = RootGrid.TranslatePoint(pointer, TilesScroll);
        bool paneHit = TilesScroll.IsVisible && inPane.X >= 0 && inPane.Y >= 0
                       && inPane.X <= TilesScroll.ActualWidth && inPane.Y <= TilesScroll.ActualHeight;
        int n = s.Hosts.Count;

        if (overList) kind = DropKind.Unpin;
        else if (paneHit)
        {
            var bounds = new Rect[n];
            for (int i = 0; i < n; i++) bounds[i] = LayoutBounds(s.Hosts[i]);

            if (s.Payload is TileGroup moving)
            {
                // The group lands before the first group whose middle is below the pointer.
                int k = 0;
                while (k < n && pointer.Y > bounds[k].Top + bounds[k].Height / 2) k++;
                int from = s.Hosts.FindIndex(h => ReferenceEquals(h.Group, moving));
                int to = k > from ? k - 1 : k;
                if (from >= 0 && to != from)
                {
                    kind = DropKind.Reorder;
                    slot = k;
                    before = k < n ? s.Hosts[k].Group : null;
                }
            }
            else if (n == 0)
            {
                kind = DropKind.NewGroup; slot = 0;
            }
            else
            {
                int inside = -1;
                for (int i = 0; i < n; i++)
                    if (pointer.Y >= bounds[i].Top && pointer.Y <= bounds[i].Bottom) { inside = i; break; }

                if (inside >= 0)
                {
                    panel = FindChild<TilePanel>(s.Hosts[inside].Host);
                    if (panel != null)
                    {
                        target = s.Hosts[inside].Group;
                        // Over the middle of a tile: drop INTO it (folder). A folder cannot be dropped into another tile.
                        bool canJoin = s.Payload is not TileModel { IsFolderTile: true } && s.Payload is not AppEntry { IsFolder: true };
                        intoTile = canJoin ? panel.TileAtCenter(Mouse.GetPosition(panel), s.Payload as TileModel) : null;
                        if (intoTile != null)
                        {
                            kind = DropKind.IntoTile;
                            panel = null;   // no slot opens: the tiles stay where they are
                        }
                        else
                        {
                            kind = DropKind.Group;
                            index = panel.InsertionIndexAt(Mouse.GetPosition(panel));
                        }
                    }
                }
                else
                {
                    // Above the first group, in the gap between two groups, or below the last one: a new group.
                    int k = 0;
                    while (k < n && pointer.Y > bounds[k].Bottom) k++;
                    kind = DropKind.NewGroup;
                    slot = k;
                    before = k < n ? s.Hosts[k].Group : null;
                }
            }
        }

        s.Kind = kind;
        s.TargetTile = intoTile;
        SetHotTile(s, intoTile);
        s.TargetGroup = target;
        s.SlotBefore = before;
        s.CellX = 0;
        s.CellY = 0;

        // Compute the target cell for a tile drop in this group (free-form placement).
        if (kind == DropKind.Group && panel != null)
        {
            var (w, h) = TilePacker.CellSize(panel.DropTileSize);
            Point pp = Mouse.GetPosition(panel);
            int cx0 = (int)Math.Floor(pp.X / TilePanel.Cell);
            int cy0 = (int)Math.Floor(pp.Y / TilePanel.Cell);
            cx0 = Math.Clamp(cx0, 0, Math.Max(0, panel.Columns - w));
            cy0 = Math.Max(0, cy0);

            var occupied = new List<(int, int, int, int)>();
            if (target != null)
                foreach (var tm in target.Tiles)
                    if (!ReferenceEquals(tm, s.Payload as TileModel))
                        occupied.Add((tm.Column, tm.Row, TilePacker.CellSize(tm.Size).W, TilePacker.CellSize(tm.Size).H));
            var (fx, fy) = TilePacker.FindPositionNear(occupied, w, h, panel.Columns, cx0, cy0);
            s.CellX = fx;
            s.CellY = fy;
        }

        // Open (or close) the empty slot among the tiles of the group under the pointer.
        if (!ReferenceEquals(s.Hover, panel))
        {
            if (s.Hover != null) s.Hover.DropIndex = -1;
            s.Hover = panel;
        }
        if (panel != null)
        {
            panel.DropTileSize = s.Payload is TileModel tm ? tm.Size : TileSize.Medium;
            panel.DropIndex = -1;   // free-form placement: no slot opens, tiles stay where they are
        }

        ApplySlot(s, slot);

        if (overList != s.OverList)
        {
            s.OverList = overList;
            Motion.Run(s.Ghost, OpacityProperty, overList ? 0.55 : 1, Motion.Faster, null);
        }
    }

    /// <summary>Shows the thin bar for a new / moved group and slides the groups below it down to make room.</summary>
    private void ApplySlot(DragSession s, int slot)
    {
        int n = s.Hosts.Count;
        if (slot != s.Slot)
        {
            s.Slot = slot;
            for (int i = 0; i < n; i++)
                Motion.Run(s.Hosts[i].Shift, TranslateTransform.YProperty, slot >= 0 && i >= slot ? GroupShift : 0, Motion.Normal, Motion.Enter);
        }

        if (slot < 0)
        {
            if (s.BarShown) { s.BarShown = false; Motion.Run(s.Bar, OpacityProperty, 0, Motion.Faster, null); }
            return;
        }

        double left, top, width;
        if (n == 0)
        {
            Point o = TilesScroll.TranslatePoint(new Point(12, 12), RootGrid);
            left = o.X; top = o.Y; width = 4 * TilePanel.Cell;
        }
        else
        {
            Rect first = LayoutBounds(s.Hosts[0]);
            left = first.Left;
            width = Math.Max(first.Width, 4 * TilePanel.Cell);
            top = slot < n ? LayoutBounds(s.Hosts[slot]).Top + 6 : LayoutBounds(s.Hosts[n - 1]).Bottom + 8;
        }

        s.Bar.Width = width;
        Canvas.SetLeft(s.Bar, left);
        if (!s.BarShown)
        {
            Motion.Set(s.Bar, Canvas.TopProperty, top);
            s.BarShown = true;
            Motion.Run(s.Bar, OpacityProperty, 0.8, Motion.Fast, Motion.Enter);
        }
        else if (Math.Abs(Canvas.GetTop(s.Bar) - top) > 0.5)
        {
            Motion.Run(s.Bar, Canvas.TopProperty, top, Motion.Fast, Motion.Enter);
        }
    }

    // ---------- finishing ----------

    private void Drag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CommitDrag();
    }

    private void Drag_LostCapture(object sender, MouseEventArgs e)
    {
        if (_drag is { Settling: false }) CancelDrag();
    }

    /// <summary>Esc, losing the mouse, or releasing over nothing: the item returns to where it came from.</summary>
    private void CancelDrag()
    {
        var s = _drag;
        if (s == null || s.Settling) return;
        s.Settling = true;

        // Put the layout back; the source tile stays hidden until the ghost has landed on it.
        if (s.Payload is TileModel) Motion.Set(s.Source, OpacityProperty, 0);
        foreach (var p in s.Panels) { p.DropIndex = -1; p.DraggedTile = null; }
        ApplySlot(s, -1);
        UndimTiles(s);

        double scrolled = s.Payload is AppEntry ? 0 : TilesScroll.VerticalOffset - s.ScrollAtStart;
        Fly(s, new Point(s.Origin.X, s.Origin.Y - scrolled), () => FinishDrag(commit: false));
    }

    private void CommitDrag()
    {
        var s = _drag;
        if (s == null || s.Settling) return;
        if (s.Kind == DropKind.None) { CancelDrag(); return; }
        s.Settling = true;

        if (s.Kind == DropKind.IntoTile && s.TargetTile != null)
        {
            // Shrink into the middle of the tile it is dropped on.
            var entry = s.Dimmed.Find(d => ReferenceEquals(d.Model, s.TargetTile));
            Point into = new(Canvas.GetLeft(s.Ghost), Canvas.GetTop(s.Ghost));
            if (entry.Child != null)
            {
                Point centre = entry.Child.TranslatePoint(new Point(entry.Child.RenderSize.Width / 2, entry.Child.RenderSize.Height / 2), RootGrid);
                into = new Point(centre.X - s.Ghost.Width / 2, centre.Y - s.Ghost.Height / 2);
            }
            Motion.Run(s.Ghost, OpacityProperty, 0, Motion.Fast, null);
            ApplySlot(s, -1);
            UndimTiles(s);
            Fly(s, into, () => FinishDrag(commit: true), duration: Motion.Fast);
            return;
        }

        if (s.Kind == DropKind.Group && s.Hover is { } panel && !panel.GapRect.IsEmpty)
        {
            // Settle into the open slot (tiles carry a 2 DIP margin inside their cell).
            Rect gap = panel.GapRect;
            Point land = panel.TranslatePoint(new Point(gap.X + 2, gap.Y + 2), RootGrid);
            ApplySlot(s, -1);
            UndimTiles(s);
            Fly(s, land, () => FinishDrag(commit: true));
            return;
        }

        // New group, group move, unpin: the item simply fades out as the change lands.
        Point to = new(Canvas.GetLeft(s.Ghost), Canvas.GetTop(s.Ghost));
        Motion.Run(s.Ghost, OpacityProperty, 0, Motion.Fast, null);
        UndimTiles(s);
        Fly(s, to, () => FinishDrag(commit: true), duration: Motion.Fast);
    }

    private static void Fly(DragSession s, Point to, Action done, Duration? duration = null)
    {
        Duration d = duration ?? Motion.Normal;
        Motion.Run(s.Ghost, Canvas.LeftProperty, to.X, d, Motion.Enter);
        Motion.Run(s.Ghost, Canvas.TopProperty, to.Y, d, Motion.Enter, done: done);
    }

    private void FinishDrag(bool commit)
    {
        var s = _drag;
        if (s == null) return;

        RootGrid.MouseMove -= Drag_MouseMove;
        RootGrid.MouseLeftButtonUp -= Drag_MouseUp;
        RootGrid.LostMouseCapture -= Drag_LostCapture;
        CompositionTarget.Rendering -= Drag_Frame;
        _drag = null;

        // The slide that opened room for a new group is undone instantly: the real layout is about to change.
        foreach (var h in s.Hosts) Motion.Set(h.Shift, TranslateTransform.YProperty, 0);
        if (commit) ApplyDrop(s);

        foreach (var p in s.Panels) { p.DropIndex = -1; p.DraggedTile = null; }
        Motion.Set(s.Source, OpacityProperty, 1);
        if (s.SourceHostInfo != null) Motion.Set(s.SourceHostInfo.Host, OpacityProperty, 1);
        Mouse.OverrideCursor = null;
        if (ReferenceEquals(Mouse.Captured, RootGrid)) Mouse.Capture(null);
        _suppressHide = false;

        // Remove the ghost only after the new layout is in place, so there is never a frame where neither it nor the tile shows.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            DragLayer.Children.Clear();
            DragLayer.Visibility = Visibility.Collapsed;
            foreach (var p in s.Panels) p.AnimateLayout = false;
            foreach (var (child, scale, _) in s.Dimmed)
            {
                Motion.Set(child, OpacityProperty, 1);
                if (scale != null && VisualTreeHelper.GetChildrenCount(child) > 0 && VisualTreeHelper.GetChild(child, 0) is FrameworkElement inner
                    && ReferenceEquals(inner.RenderTransform, scale))
                    inner.RenderTransform = Transform.Identity;
            }
        }));
    }

    /// <summary>Instant teardown (the menu is closing mid-drag).</summary>
    private void AbortDrag()
    {
        var s = _drag;
        if (s == null) return;
        s.Settling = true;
        FinishDrag(commit: false);
    }

    private void ApplyDrop(DragSession s)
    {
        switch (s.Kind)
        {
            case DropKind.Group:
                if (s.TargetGroup != null) PlaceInGroup(s.Payload, s.TargetGroup, s.CellX, s.CellY);
                break;

            case DropKind.NewGroup:
            {
                var created = _model.AddGroup("New group");
                MoveGroupBefore(created, s.SlotBefore);
                PlaceInGroup(s.Payload, created, 0, 0);
                _model.PruneEmptyGroups();
                BeginGroupRename(created);   // name the new group right away, like Windows
                break;
            }

            case DropKind.IntoTile:
                if (s.TargetTile != null)
                {
                    var (folder, created) = _model.DropIntoTile(s.TargetTile, s.Payload);
                    if (created && folder?.App != null) BeginRename(folder.App);   // name the new folder right away
                }
                break;

            case DropKind.Unpin:
                if (s.Payload is TileModel unpinned) _model.Unpin(unpinned.AppId);
                break;

            case DropKind.Reorder:
                if (s.Payload is TileGroup moving)
                {
                    MoveGroupBefore(moving, s.SlotBefore);
                    _model.SaveTilesInternal();
                }
                break;
        }
    }

    private void PlaceInGroup(object payload, TileGroup group, int column, int row)
    {
        if (payload is TileModel tile) _model.MoveTile(tile, group, column, row);
        else if (payload is AppEntry app) _model.PinAt(app, group, int.MaxValue, column, row);
    }

    /// <summary>Moves <paramref name="group"/> so it sits directly before <paramref name="before"/> (null = last).</summary>
    private void MoveGroupBefore(TileGroup group, TileGroup? before)
    {
        int from = _model.Groups.IndexOf(group);
        if (from < 0) return;
        int to = before == null ? _model.Groups.Count - 1 : _model.Groups.IndexOf(before);
        if (to < 0) return;
        if (to > from && before != null) to--;
        if (to != from) _model.Groups.Move(from, to);
    }

    // ---------- helpers ----------

    private static ImageSource? Snapshot(FrameworkElement el)
    {
        double w = el.ActualWidth, h = el.ActualHeight;
        if (w < 1 || h < 1) return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(el);
            int pw = (int)Math.Ceiling(w * dpi.DpiScaleX), ph = (int)Math.Ceiling(h * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            // Draw the element 1:1. A default VisualBrush stretches the element's *content bounds* over the rectangle, which squashes
            // the picture when anything inside (a small tile's icon or label) extends past the tile's own size.
            var brush = new VisualBrush(el)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, w, h)
            };
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    private static void CollectChildren<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) into.Add(t);
            CollectChildren(c, into);
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static DependencyObject? FindAncestorWhere(DependencyObject start, Func<DependencyObject, bool> test)
    {
        for (DependencyObject? d = start; d != null; d = VisualTreeHelper.GetParent(d))
            if (test(d)) return d;
        return null;
    }
}
