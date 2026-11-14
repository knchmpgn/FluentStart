using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentStart.Native;

namespace FluentStart;

public partial class MainWindow
{
    private bool ScrollMode => _settings.ViewMode == StartViewMode.Scroll;
    private bool _appliedScrollMode;
    private bool _scrollModeTiles = true;
    private double _scrollModeTilesOffset, _scrollModeListOffset;
    private bool _sectionTransition;
    private int _sectionTransitionId;
    private bool _pointerDrivenSectionSwitch;
    private DateTime _lastSectionWheel = DateTime.MinValue;
    private double _sectionWheelDelta;
    private bool _blockSectionWheel;
    private int _blockedWheelDirection;
    private int? _sectionTouchId;
    private Point _sectionTouchStart;
    private bool _sectionTouchTiles;

    private void InitializeScrollModeInput()
    {
        PreviewMouseWheel += ScrollMode_MouseWheel;
        AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(ScrollMode_TouchDown), true);
        AddHandler(PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(ScrollMode_TouchUp), true);
    }

    private void ConfigureScrollModePanning()
    {
        if (ScrollMode)
        {
            ScrollViewer.SetPanningMode(TilesScroll, PanningMode.VerticalOnly);
            ScrollViewer.SetPanningMode(AppList, PanningMode.VerticalOnly);
            if (_appListScrollViewer != null) ScrollViewer.SetPanningMode(_appListScrollViewer, PanningMode.VerticalOnly);
        }
        else
        {
            TilesScroll.ClearValue(ScrollViewer.PanningModeProperty);
            AppList.ClearValue(ScrollViewer.PanningModeProperty);
            _appListScrollViewer?.ClearValue(ScrollViewer.PanningModeProperty);
        }
    }

    private bool CanNavigateSections =>
        ScrollMode && IsVisible && !_isClosing && !_searchVisible &&
        JumpOverlay.Visibility != Visibility.Visible &&
        FolderOverlay.Visibility != Visibility.Visible && _drag == null && Flyout.Root == null;

    private bool IsSectionInput(DependencyObject? source, bool wheel = false)
    {
        if (source == null) return false;
        if (wheel && IsDescendantOf(source, SectionButton)) return true;
        FrameworkElement host = _showTiles ? TilesScroll : AppList;
        if (!IsDescendantOf(source, host)) return false;
        for (var element = source; element != null && !ReferenceEquals(element, host); element = ParentOf(element))
        {
            if (element is TextBox || (!wheel && element is ScrollBar)) return false;
            if (element is ScrollViewer viewer && !ReferenceEquals(viewer, CurrentAppListViewer())) return false;
        }
        return true;
    }

    private ScrollViewer? ActiveSectionViewer => _showTiles ? TilesScroll : CurrentAppListViewer();

    private bool AtSectionBoundary(ScrollViewer viewer) =>
        _showTiles ? viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5 : viewer.VerticalOffset <= 0.5;

    private void ScrollMode_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanNavigateSections ||
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            SystemParameters.WheelScrollLines == 0) return;

        var source = e.OriginalSource as DependencyObject;
        if (_sectionTransition && source != null &&
            (IsDescendantOf(source, AppList) || IsDescendantOf(source, TilesScroll) ||
             IsDescendantOf(source, SectionButton)))
        {
            DateTime transitionWheelTime = DateTime.UtcNow;
            if ((transitionWheelTime - _lastSectionWheel).TotalMilliseconds >= 220) _sectionWheelDelta = 0;
            _lastSectionWheel = transitionWheelTime;
            e.Handled = true;
            if (!_showTiles && e.Delta > 0 && IsSectionInput(source, wheel: true) &&
                CurrentAppListViewer() is { IsVisible: true } list && AtSectionBoundary(list))
            {
                _sectionWheelDelta += e.Delta;
                if (_sectionWheelDelta >= 40)
                {
                    _sectionWheelDelta = 0;
                    _blockSectionWheel = true;
                    _blockedWheelDirection = 1;
                    SwitchScrollSection(tiles: true, pointerDriven: true);
                }
            }
            else _sectionWheelDelta = 0;
            return;
        }
        if (!IsSectionInput(source, wheel: true)) return;

        DateTime now = DateTime.UtcNow;
        bool newGesture = (now - _lastSectionWheel).TotalMilliseconds >= 220;
        _lastSectionWheel = now;
        if (newGesture) { _blockSectionWheel = false; _sectionWheelDelta = 0; }
        if (_blockSectionWheel && Math.Sign(e.Delta) != _blockedWheelDirection)
            _blockSectionWheel = false;
        if (_blockSectionWheel || _sectionTransition)
        {
            e.Handled = true;
            return;
        }

        var viewer = ActiveSectionViewer;
        bool towardOtherSection = _showTiles ? e.Delta < 0 : e.Delta > 0;
        if (viewer == null || !towardOtherSection || !AtSectionBoundary(viewer))
        {
            _sectionWheelDelta = 0;
            return;
        }

        _sectionWheelDelta += Math.Abs(e.Delta);
        e.Handled = true;
        if (_sectionWheelDelta < (_showTiles ? 120 : 40)) return;
        _sectionWheelDelta = 0;
        _blockSectionWheel = true;
        _blockedWheelDirection = Math.Sign(e.Delta);
        SwitchScrollSection(!_showTiles, pointerDriven: true);
    }

    private void ScrollMode_TouchDown(object? sender, TouchEventArgs e)
    {
        _sectionTouchId = null;
        if (!CanNavigateSections || _sectionTransition || !IsSectionInput(e.OriginalSource as DependencyObject)) return;
        var viewer = ActiveSectionViewer;
        if (viewer == null || !AtSectionBoundary(viewer)) return;
        _sectionTouchId = e.TouchDevice.Id;
        _sectionTouchStart = e.GetTouchPoint(this).Position;
        _sectionTouchTiles = _showTiles;
    }

    private void ScrollMode_TouchUp(object? sender, TouchEventArgs e)
    {
        if (_sectionTouchId != e.TouchDevice.Id) return;
        _sectionTouchId = null;
        if (!CanNavigateSections || _sectionTransition || _showTiles != _sectionTouchTiles) return;
        var viewer = ActiveSectionViewer;
        if (viewer == null || !AtSectionBoundary(viewer)) return;
        Point end = e.GetTouchPoint(this).Position;
        double dy = end.Y - _sectionTouchStart.Y;
        if (Math.Abs(dy) < 48 || Math.Abs(dy) <= Math.Abs(end.X - _sectionTouchStart.X) ||
            (_showTiles ? dy >= 0 : dy <= 0)) return;
        e.Handled = true;
        SwitchScrollSection(!_showTiles, pointerDriven: true);
    }

    private void SectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateSections) return;
        SwitchScrollSection(!_showTiles);
        if (SectionButton != null && SectionButton.IsMouseOver)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.ClearFocus()));
        }
    }

    private void ResetScrollModeOnOpen()
    {
        if (!ScrollMode) return;
        _scrollModeTiles = _showTiles = true;
        _scrollModeTilesOffset = 0;
        _sectionWheelDelta = 0;
        _blockSectionWheel = false;
        _lastSectionWheel = DateTime.MinValue;
        TilesScroll.ScrollToVerticalOffset(0);
    }

    private void SwitchScrollSection(bool tiles, bool pointerDriven = false)
    {
        if (!ScrollMode || tiles == _showTiles) return;
        _pointerDrivenSectionSwitch = pointerDriven;
        SaveScrollModeOffset();
        _scrollModeTiles = _showTiles = tiles;
        ShowScrollModePane(tiles, animate: true);
        UpdateSectionButton();
        _pointerDrivenSectionSwitch = false;
    }

    private void SaveScrollModeOffset()
    {
        var viewer = ActiveSectionViewer;
        if (viewer == null) return;
        if (_showTiles) _scrollModeTilesOffset = viewer.VerticalOffset;
        else _scrollModeListOffset = viewer.VerticalOffset;
    }

    private void RestoreScrollModeOffset()
    {
        bool tiles = _showTiles;
        int generation = _menuGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ScrollMode || !IsVisible || _isClosing || _showTiles != tiles || generation != _menuGeneration) return;
            var viewer = ActiveSectionViewer;
            if (viewer == null) return;
            double offset = tiles ? _scrollModeTilesOffset : _scrollModeListOffset;
            viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        }));
    }

    private void CancelScrollModeTransition()
    {
        _sectionTransitionId++;
        _sectionTransition = false;
        _sectionTouchId = null;
        foreach (FrameworkElement pane in new FrameworkElement[] { AppList, TilesScroll })
        {
            Motion.Set(pane, OpacityProperty, 1);
            if (pane.RenderTransform is TranslateTransform transform)
                Motion.Set(transform, TranslateTransform.YProperty, 0);
        }
    }

    private void ShowScrollModePane(bool tiles, bool animate)
    {
        CancelAllScroll();
        CancelScrollModeTransition();
        if (_searchVisible) SetPanesOpacity(0);
        FrameworkElement show = tiles ? TilesScroll : AppList;
        FrameworkElement hide = tiles ? AppList : TilesScroll;
        if (hide.IsKeyboardFocusWithin)
        {
            if (_pointerDrivenSectionSwitch) Keyboard.ClearFocus();
            else SectionButton.Focus();
        }
        if (!animate || !AnimationsEnabled || !IsVisible || !hide.IsVisible)
        {
            hide.Visibility = Visibility.Collapsed;
            show.Visibility = Visibility.Visible;
            RestoreScrollModeOffset();
            return;
        }

        _sectionTransition = true;
        int id = _sectionTransitionId;
        Motion.FadeOut(hide, () =>
        {
            if (id != _sectionTransitionId) return;
            hide.Visibility = Visibility.Collapsed;
            Motion.Set(hide, OpacityProperty, 1);
            show.Visibility = Visibility.Visible;
            RestoreScrollModeOffset();
            var transform = Motion.EnsureTranslate(show);
            Motion.Run(show, OpacityProperty, 1, Motion.Fast, Motion.Enter, from: 0);
            Motion.Run(transform, TranslateTransform.YProperty, 0, Motion.Normal, Motion.Enter,
                from: tiles ? -Motion.SlidePage : Motion.SlidePage,
                done: () => { if (id == _sectionTransitionId) _sectionTransition = false; });
        });
    }

    private void UpdateSectionButton()
    {
        if (SectionButton == null) return;
        bool visible = ScrollMode && !_searchVisible && JumpOverlay.Visibility != Visibility.Visible;
        SectionButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(SectionButton, _showTiles ? 2 : 0);
        SectionButton.Margin = new Thickness(0, _showTiles ? 0 : 8, 0, 0);
        string label = _showTiles ? "Apps" : "Tiles";
        SectionLabel.Text = label;
        SectionGlyph.Text = _showTiles ? "\uE70D" : "\uE70E";
        AutomationProperties.SetName(SectionButton, "Show " + label);
        AutomationProperties.SetHelpText(SectionButton, _showTiles
            ? "Scroll down or activate to show all apps."
            : "Scroll up at the top of the list or activate to show pinned tiles.");
         RefreshSectionButtonHover();
    }
    private void RefreshSectionButtonHover()
    {
        if (SectionButton == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (SectionButton == null) return;
            try { Mouse.Synchronize(); } catch { }
            if (!SectionButton.IsMouseOver) return;
            try
            {
                NativeMethods.GetCursorPos(out var pt);
                Point cursor = PointFromScreen(new Point(pt.X, pt.Y));
                var hit = VisualTreeHelper.HitTest(this, cursor);
                bool actuallyOver = hit?.VisualHit != null && IsDescendantOf(hit.VisualHit, SectionButton);
                if (actuallyOver) return;
                SectionButton.IsHitTestVisible = false;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (SectionButton != null) SectionButton.IsHitTestVisible = true;
                }));
            }
            catch { }
        }));
    }
}