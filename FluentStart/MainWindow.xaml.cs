using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FluentStart.Native;

namespace FluentStart;

public partial class MainWindow : Window
{
    private const double RailWidth = TileLayout.Rail, ListWidth = TileLayout.ListPane, BaseMargin = 12, MinHeightDip = 480;

    private readonly StartModel _model;
    private readonly Settings _settings;
    private IntPtr _hwnd;
    private DateTime _lastHide = DateTime.MinValue;
    private bool _railExpanded;
    private bool _suppressHide;
    private bool _modalBusy;   // a native dialog / OLE drag is running: Start must not hide behind it
    private bool _isClosing;
    private long _closeStarted;
    private int _menuGeneration;
    private Point _dragStart;
    private object? _dragCandidate;
    private SearchResult? _searchDragCandidate;
    private uint _taskbarCreatedMsg;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _appListScrollTimer;
    private ScrollBar? _appListScrollBar;
    private ScrollViewer? _appListScrollViewer;
    private SmoothScroller? _listScroll, _tilesScroll, _resultsScroll;
    private const double WheelLinePx = 40;   // one wheel "line"; every scrollable surface uses the same step so scrolling feels identical everywhere

    // search expansion (window widens so the filter tabs are never clipped)
    private bool _searchExpanded;
    private double _baseWidthDip;
    private int _workRightPx, _marginPx;
    private bool _widthRunning;
    private long _widthStart;
    private double _widthFrom, _widthTo;
    private IEasingFunction _widthEase = Motion.Enter;
    private DateTime _showTime;
    private IntPtr _foregroundOnShow;
    private readonly SearchPageModel _search;
    private bool _searchVisible;
    private bool _showTiles;   // which pane is active when only one is shown

    public bool AllowClose { get; set; }
    public event Action? MenuShowing;
    public event Action? SettingsRequested;
    public event Action? TaskbarCreated;

    private static bool AnimationsEnabled => Motion.Enabled;
    private bool SinglePane => _settings.ViewMode != StartViewMode.Both;

    private double _maxWidthDip = double.MaxValue;   // work-area limits of the monitor the menu opened on (for resize snapping)
    private int _maxHeightPx = int.MaxValue;

    public MainWindow(StartModel model, Settings settings)
    {
        _model = model;
        _settings = settings;
        _search = new SearchPageModel(model, settings);
        InitializeComponent();
        ApplyFonts();
        DataContext = model;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _hideTimer.Tick += (_, _) => CheckHideOnOutsideClick();
        _hideTimer.Start();
        _appListScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _appListScrollTimer.Tick += (_, _) =>
        {
            _appListScrollTimer.Stop();
            if (_appListScrollBar != null) _appListScrollBar.Tag = false;
        };
        _search.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SearchPageModel.IsBusy) && !_search.IsBusy && _pendingEnter)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FlushPendingEnter));
        };
        PreviewMouseLeftButtonUp += (_, _) => ReleaseTilePress(instant: false);
        Deactivated += (_, _) => ReleaseTilePress(instant: true);
        _listScroll = new SmoothScroller(AppList, WheelLinePx);
        _tilesScroll = new SmoothScroller(TilesScroll, WheelLinePx);
        _resultsScroll = new SmoothScroller(ResultsList, WheelLinePx);
        SearchPage.DataContext = _search;
        UserLabel.Text = Environment.UserName;
        var pic = AccountPicture.TryLoad();
        if (pic != null)
        {
            UserPic.Fill = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
            UserPic.Visibility = Visibility.Visible;
            UserGlyph.Visibility = Visibility.Collapsed;
        }
        CustomRailButtons.ItemsSource = _settings.CustomRailButtons;
    }

    /// <summary>Creates the HWND off-screen once so DWM attributes are applied before first real show.</summary>
    public void PrepareHandle(bool dark)
    {
        ShowActivated = false;
        Left = -20000; Top = -20000;
        Show();
        Hide();
        ShowActivated = true;

        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd);
        if (src != null)
        {
            if (src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            src.AddHook(WndProc);
            Appearance.KeepBackdropActive(src);
        }
        ApplyAppearance(dark);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg) TaskbarCreated?.Invoke();
        else if ((uint)msg == NativeMethods.WM_SIZING) { OnSizing(wParam.ToInt32(), lParam); handled = true; return (IntPtr)1; }
        else if ((uint)msg == NativeMethods.WM_EXITSIZEMOVE) OnSizeEnded();
        return IntPtr.Zero;
    }

    public void ApplyFonts()
    {
        try
        {
            const string fontFamily = "Segoe UI Variable Text, Segoe UI";
            Resources["Font.App"] = new System.Windows.Media.FontFamily(fontFamily);
            Resources["Font.Ui"] = new System.Windows.Media.FontFamily(fontFamily);
            Resources["FontSize.App"] = _settings.AppFontSize;
            Resources["FontSize.Ui"] = _settings.UiFontSize;
        }
        catch { }
    }

    /// <summary>Live resize: the width snaps to the nearest size that shows whole tiles (even column counts); the height is free within limits.</summary>
    private void OnSizing(int edge, IntPtr lParam)
    {
        var rc = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;

        if (_searchExpanded)   // the search layout owns the width; keep the window exactly as it is
        {
            if (NativeMethods.GetWindowRect(_hwnd, out var cur)) Marshal.StructureToPtr(cur, lParam, false);
            return;
        }

        int cols = TileLayout.Nearest((rc.Right - rc.Left) / s, SinglePane, _maxWidthDip);
        int targetW = (int)Math.Round(TileLayout.WidthFor(cols, SinglePane) * s);
        bool leftEdge = edge is 1 or 4 or 7;                 // WMSZ_LEFT, TOPLEFT, BOTTOMLEFT
        if (leftEdge) rc.Left = rc.Right - targetW; else rc.Right = rc.Left + targetW;

        int minH = (int)(MinHeightDip * s);
        int h = Math.Clamp(rc.Bottom - rc.Top, minH, Math.Max(minH, _maxHeightPx));
        bool topEdge = edge is 3 or 4 or 5;                  // WMSZ_TOP, TOPLEFT, TOPRIGHT
        if (topEdge) rc.Top = rc.Bottom - h; else rc.Bottom = rc.Top + h;

        Marshal.StructureToPtr(rc, lParam, false);
        if (cols != _model.TileColumns) _model.TileColumns = cols;   // tiles reflow while dragging
    }

    private void OnSizeEnded()
    {
        _settings.TileColumns = _model.TileColumns;
        _settings.WindowHeight = Math.Round(ActualHeight);
        _settings.Save();
    }

    public bool SearchOnShow { get; set; }

    public void ApplyAppearance(bool dark)
    {
        Appearance.Dark = dark;
        Appearance.Backdrop = _settings.Backdrop;   // flyouts read the same values, so menus follow the Start menu's appearance
        if (Appearance.Apply(_hwnd, out _)) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "Brush.WindowBg");
    }

    // ---------- show / hide ----------

    public void Toggle()
    {
        if (IsVisible) HideMenu();
        else if ((DateTime.UtcNow - _lastHide).TotalMilliseconds > 200) ShowMenu();
    }

    private void ShowMenu()
    {
        var sw = Stopwatch.StartNew();
        _menuGeneration++;
        CancelAllScroll();
        _isClosing = false;
        _folderAnimating = false;
        _suppressHide = false;
        ResetSearchUi();
        JumpOverlay.Visibility = Visibility.Collapsed;
        SetRailExpanded(false, animate: false);
        _showTiles = _settings.ViewMode == StartViewMode.TilesOnly;
        ApplyViewMode(animate: false);
        UpdateRailButtonVisibility();
        PositionWindow();
        _showTime = DateTime.UtcNow;
        _foregroundOnShow = NativeMethods.GetForegroundWindow();

        // Reapply appearance before showing to ensure Mica/backdrop is consistent
        ApplyAppearance(Appearance.Dark);

        Show();
        NativeMethods.ForceForeground(_hwnd);
        Activate();
        // The search box always owns the keyboard when the menu opens (Start button, Windows key or search hotkey), so typing
        // goes straight into it. Without a focused element the first keystrokes had nowhere reliable to land.
        bool selectAll = SearchOnShow;
        SearchOnShow = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            if (selectAll) SearchBox.SelectAll();
            else SearchBox.CaretIndex = SearchBox.Text.Length;
        }));

        _model.CollapseAll();   // like the native list, folders start collapsed each time
        var first = _model.AppsView.Cast<ListRow>().FirstOrDefault();
        if (first != null) AppList.ScrollIntoView(first);

        // One entrance for the whole surface: fade (Fast) + slide up (Normal), both Fast Out / Slow In.
        Motion.Run(RootGrid, OpacityProperty, 1, Motion.Fast, Motion.Enter, from: 0);
        Motion.Run(Slide, TranslateTransform.YProperty, 0, Motion.Normal, Motion.Enter, from: Motion.SlideWindow);
        MenuShowing?.Invoke();
        if (sw.ElapsedMilliseconds > 100) Log.Info($"ShowMenu took {sw.ElapsedMilliseconds} ms");
    }

    public void HideMenu()
    {
        if (_isClosing) return;
        bool wasVisible = IsVisible;
        _isClosing = wasVisible;
        _closeStarted = Stopwatch.GetTimestamp();
        _lastHide = DateTime.UtcNow;
        bool cleanupFailed = false;
        void Cleanup(string stage, Action action)
        {
            try { action(); }
            catch (Exception ex) { cleanupFailed = true; Log.Warn("Start menu dismissal cleanup failed: " + stage, ex); }
        }

        Cleanup("drag", AbortDrag);
        Cleanup("tile press", () => ReleaseTilePress(instant: true));
        Cleanup("folder overlay", () => CloseFolderOverlay(instant: true));
        Cleanup("scrolling", CancelAllScroll);
        _suppressHide = false;
        Cleanup("edits", _model.CancelEdits);
        Cleanup("jump overlay", () => JumpOverlay.Visibility = Visibility.Collapsed);
        Cleanup("panes", () => EnforcePaneVisibility());
        Cleanup("rail", () => SetRailExpanded(false, animate: false));
        Cleanup("search", ResetSearchUi);
        Cleanup("flyouts", Flyout.CloseAll);
        Cleanup("shell menu", DisposeShellMenu);
        if (!wasVisible || !_isClosing) return;
        if (cleanupFailed) { CompleteHide(); return; }
        try
        {
            int generation = _menuGeneration;
            Motion.Run(RootGrid, OpacityProperty, 0, Motion.Fast, null);
            Motion.Run(Slide, TranslateTransform.YProperty, Motion.SlideWindow, Motion.Fast, Motion.Exit,
                done: () => { if (generation == _menuGeneration) CompleteHide(); });
        }
        catch (Exception ex)
        {
            Log.Warn("Start menu exit animation failed", ex);
            CompleteHide();
        }
    }

    private void CompleteHide()
    {
        if (!_isClosing) return;
        try
        {
            Hide();
        }
        catch (Exception ex)
        {
            Log.Warn("Start menu could not be hidden", ex);
        }
        finally
        {
            _isClosing = false;
            _suppressHide = false;
        }

        try
        {
            Motion.Set(RootGrid, OpacityProperty, 1);
            Motion.Set(Slide, TranslateTransform.YProperty, 0);
        }
        catch (Exception ex) { Log.Warn("Start menu animation reset failed", ex); }
    }

    private void PositionWindow()
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr mon = NativeMethods.MonitorFromPoint(pt, 2 /* NEAREST */);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(mon, ref mi);

        uint dpi = 96;
        try { NativeMethods.GetDpiForMonitor(mon, 0, out dpi, out _); } catch { }
        double s = dpi / 96.0;

        int margin = (int)(BaseMargin * s);
        var work = mi.rcWork;
        _maxWidthDip = (work.Right - work.Left - 2 * margin) / s;
        _maxHeightPx = work.Bottom - work.Top - 2 * margin;

        // Saved size, reduced to the widest whole-tile width that fits this monitor (never a width that would cut a tile).
        _model.TileColumns = _settings.TileColumns;
        _model.TileColumns = TileLayout.Fit(_model.TileColumns, SinglePane, _maxWidthDip);
        _baseWidthDip = TileLayout.WidthFor(_model.TileColumns, SinglePane);
        _workRightPx = work.Right;
        _marginPx = margin;
        int w = (int)Math.Round(_baseWidthDip * s);
        int h = Math.Clamp((int)Math.Round(_settings.WindowHeight * s), (int)(MinHeightDip * s), Math.Max((int)(MinHeightDip * s), _maxHeightPx));

        int x = _settings.Align == AlignChoice.Center
            ? (mi.rcMonitor.Left + mi.rcMonitor.Right - w) / 2
            : work.Left + margin;
        x = Math.Max(work.Left + margin, Math.Min(x, work.Right - w - margin));

        int y = work.Top > mi.rcMonitor.Top ? work.Top + margin : work.Bottom - h - margin;

        NativeMethods.SetWindowPos(_hwnd, new IntPtr(-1), x, y, w, h, NativeMethods.SWP_NOACTIVATE);   // HWND_TOPMOST: always on top
    }

    // ---------- window events ----------

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressHide && Flyout.Root == null && _drag == null && !_modalBusy) _suppressHide = false;
        if (_suppressHide) return;   // a context menu or drag is in progress

        // Additional safety: if rail is expanded, collapse it and close
        if (_railExpanded) SetRailExpanded(false, animate: false);
        HideMenu();
    }

    private void CheckHideOnOutsideClick()
    {
        if (_isClosing)
        {
            if (Stopwatch.GetElapsedTime(_closeStarted) > TimeSpan.FromSeconds(1))
            {
                Log.Warn("Start menu exit animation did not complete; forcing dismissal");
                CompleteHide();
            }
            return;
        }
        if (!IsVisible) return;
        // Self-heal: _suppressHide only has a reason to be set while a menu, drag or dialog is actually open. If one was
        // abandoned without clearing it, Start could never be closed by clicking outside.
        if (_suppressHide && Flyout.Root == null && _drag == null && !_modalBusy) _suppressHide = false;
        if (_suppressHide) return;
        if (Flyout.Root != null) return;   // a context menu is open

        // Detect a mouse button held/pressed outside the window.
        if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0)   // VK_LBUTTON
        {
            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.GetWindowRect(_hwnd, out var rc);
            bool over = pt.X >= rc.Left && pt.X <= rc.Right && pt.Y >= rc.Top && pt.Y <= rc.Bottom;
            if (!over) { HideMenu(); return; }
        }

        if (IsMouseOverWindow()) return;
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == _hwnd) return;
        if (fg == _foregroundOnShow) return;   // no outside activation yet
        HideMenu();
    }

    private bool IsMouseOverWindow()
    {
        try
        {
            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.GetWindowRect(_hwnd, out var rc);
            return pt.X >= rc.Left && pt.X <= rc.Right && pt.Y >= rc.Top && pt.Y <= rc.Bottom;
        }
        catch { return true; } // if we can't check, don't hide
    }

    public void HandleGlobalMouseDown(int x, int y)
    {
        if (!IsVisible) return;
        if (_suppressHide && Flyout.Root == null && _drag == null && !_modalBusy) _suppressHide = false;
        if (_suppressHide) return;
        if (Flyout.Root != null) return;   // let the flyout handle its own dismissal
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        if (x >= rc.Left && x <= rc.Right && y >= rc.Top && y <= rc.Bottom) return;
        HideMenu();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose)
        {
            _hideTimer?.Stop();
            return;
        }
        e.Cancel = true;   // e.g. Alt+F4 must only hide the menu
        HideMenu();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_railExpanded && !IsDescendantOf(e.OriginalSource as DependencyObject, RailGrid))
            SetRailExpanded(false, animate: true);

        if (JumpOverlay.Visibility == Visibility.Visible)
        {
            var src = e.OriginalSource as DependencyObject;
            if (src == null || !IsDescendantOf(src, JumpOverlay))
            {
                CloseJumpOverlay();
                e.Handled = true;
                return;
            }
        }

        if (e.RightButton == MouseButtonState.Pressed)
        {
            var src = e.OriginalSource as DependencyObject;
            if (src == null) return;
            bool inListArea = IsDescendantOf(src, AppList) || IsDescendantOf(src, TilesScroll);
            for (var element = src; element != null; element = ParentOf(element))
                if (element is Border { Tag: "TileGroupHeader" }) return;
            bool isItem = src is FrameworkElement fe2 && fe2.DataContext is ListRow or TileModel or TileGroup or AppEntry;
            if (inListArea && isItem) return;
            if (!inListArea && src is Button) return;
            if (inListArea && src is Button) return;
            e.Handled = true;
            ShowFlyoutAtCursor(BuildBackgroundMenu());
        }
    }

    private System.Collections.Generic.List<FlyoutItem> BuildBackgroundMenu()
    {
        var items = new System.Collections.Generic.List<FlyoutItem>
        {
            new() { Text = "Settings", Glyph = "\uE713", Click = () => { HideMenu(); SettingsRequested?.Invoke(); } }
        };
        return items;
    }




    private ListRow? FocusedRow() =>
        Keyboard.FocusedElement is ListBoxItem { DataContext: ListRow r } ? r
        : AppList.IsKeyboardFocusWithin ? AppList.SelectedItem as ListRow
        : null;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FolderOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            CloseFolderOverlay();
            e.Handled = true;
            return;
        }
        if (_drag != null)   // a tile/app/group drag owns the keyboard: Esc cancels, everything else is ignored
        {
            if (e.Key == Key.Escape) CancelDrag();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox tb && !ReferenceEquals(tb, SearchBox)) return; // rename boxes handle their own keys

        if (_searchVisible)
        {
            if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
            {
                var target = _search.Selected ?? ResultsList.Items.Cast<SearchResult>().FirstOrDefault();
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ShowResultMenu(target);          // Shift+Enter: the right-click menu
                else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) RunResultAsAdmin(target); // Ctrl+Enter: run as administrator
                else EnterOnResult();
                e.Handled = true;
                return;
            }
            if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.Modifiers == ModifierKeys.None && SwitchResultColumn(e.Key == Key.Right))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && ReferenceEquals(Keyboard.FocusedElement, SearchBox))
            {
                if (ResultsList.Items.Count > 0)
                {
                    if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
                    ResultsList.Focus();
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.Up && ResultsList.IsKeyboardFocusWithin && ResultsList.SelectedIndex <= 0)
            {
                SearchBox.Focus();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_railExpanded) SetRailExpanded(false, animate: true);
                else if (JumpOverlay.Visibility == Visibility.Visible) { CloseJumpOverlay(); }
                else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else HideMenu();
                e.Handled = true;
                break;

            case Key.Enter:
            {
                var focused = FocusedRow();
                if (focused is { IsFolder: true }) { ToggleFolderAnimated(focused); e.Handled = true; break; }
                AppEntry? target = focused?.App;
                if (target == null && SearchBox.Text.Length > 0)
                    target = _model.AppsView.Cast<ListRow>().FirstOrDefault(r => r.IsMain)?.App;
                if (target != null) { Launch(target); e.Handled = true; }
                break;
            }

            case Key.F2:
            {
                var row = FocusedRow();
                if (row is { IsFolder: false }) { BeginRename(row.App); e.Handled = true; }
                break;
            }

            case Key.Down when ReferenceEquals(Keyboard.FocusedElement, SearchBox):
                if (FocusList()) e.Handled = true;
                break;

            case Key.Right when AppList.IsKeyboardFocusWithin:
                if (FocusFirstTile()) e.Handled = true;
                break;

            case Key.Left when Keyboard.FocusedElement is Button { DataContext: TileModel } leftTile:
                if (leftTile.PredictFocus(FocusNavigationDirection.Left) == null && FocusList()) e.Handled = true;
                break;

            case Key.Delete when Keyboard.FocusedElement is Button { DataContext: TileModel } delTile:
                if (delTile.DataContext is TileModel dt) { _model.Unpin(dt.AppId); e.Handled = true; }
                break;

            case Key.Apps:
            case Key.F10 when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                if (OpenContextMenuForFocus()) e.Handled = true;
                break;
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private void AppList_Loaded(object sender, RoutedEventArgs e)
    {
        if (_appListScrollViewer != null) _appListScrollViewer.ScrollChanged -= AppList_ScrollChanged;
        _appListScrollViewer = FindChild<ScrollViewer>(AppList);
        if (_appListScrollViewer != null) _appListScrollViewer.ScrollChanged += AppList_ScrollChanged;
        _appListScrollBar = FindChild<ScrollBar>(AppList);
        if (_appListScrollBar != null) _appListScrollBar.Tag = false;
    }

    private void AppList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        _appListScrollBar ??= FindChild<ScrollBar>(AppList);
        if (_appListScrollBar == null || _appListScrollTimer == null) return;
        _appListScrollBar.Tag = true;
        _appListScrollTimer.Stop();
        _appListScrollTimer.Start();
    }

    private void CancelAllScroll()
    {
        _listScroll?.Cancel();
        _tilesScroll?.Cancel();
        _resultsScroll?.Cancel();
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null)
        {
            if (parent is T match) return match;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static T? FindFirst<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && predicate(t)) return t;
            var r = FindFirst(c, predicate);
            if (r != null) return r;
        }
        return null;
    }

    private bool FocusList()
    {
        if (AppList.Items.Count == 0) return false;
        AppList.SelectedIndex = 0;
        AppList.Focus();
        return true;
    }

    private bool FocusFirstTile()
    {
        var btn = FindFirst<Button>(TilesScroll, b => b.DataContext is TileModel { IsResolved: true });
        if (btn == null) return false;
        btn.Focus();
        return true;
    }


    // ---------- launching ----------

    private void Launch(AppEntry a) { _model.Launch(a); HideMenu(); }

    private static AppEntry? ResolveApp(object? dc) => dc switch
    {
        AppEntry a => a,
        ListRow r => r.App,
        TileModel t => t.App,
        _ => null
    };

    private void AppItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ListRow r }) return;
        if (r.IsFolder)
        {
            ToggleFolderAnimated(r);
            // The click selected + focused this row, and the rebuilt (recycled) list would keep painting it as hovered.
            // A mouse click should leave no row highlighted; keyboard navigation re-selects on the next arrow key.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!AppList.IsMouseOver) return;
                AppList.UnselectAll();
                AppList.Focus();
            }));
            return;
        }
        if (!r.App.IsEditing) { Launch(r.App); HideMenu(); }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TileModel { App: { } a } tile || a.IsEditing) return;
        if (tile.IsFolderTile) OpenFolderOverlay(tile, (FrameworkElement)sender);   // folder tile: show what is inside
        else { Launch(a); HideMenu(); }
    }

    private void Result_DragDown(object sender, MouseButtonEventArgs e)
    {
        _searchDragCandidate = (sender as FrameworkElement)?.DataContext as SearchResult;
    }

    private void Result_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _searchDragCandidate?.Path == null) return;
        if (!ExceededDragDistance(e)) return;
        var candidate = _searchDragCandidate;
        _searchDragCandidate = null;
        _suppressHide = true;
        _modalBusy = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { candidate.Path! });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        finally { _modalBusy = false; _suppressHide = false; }
    }


    // ---------- context menu actions ----------




    private void BeginRename(AppEntry app)
    {
        // Defer until any context menu has fully closed, otherwise it steals focus back and cancels the edit.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            app.EditText = app.DisplayName;
            app.IsEditing = true;
        }));
    }



    // ---------- inline rename (apps) ----------

    private static AppEntry? BoxApp(object sender) =>
        sender is TextBox tb ? ResolveApp(tb.Tag ?? tb.DataContext) : null;

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox tb)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
                tb.SelectAll();
            }));
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        var app = BoxApp(sender);
        if (app == null) return;
        if (e.Key == Key.Enter) { CommitRename(app, ((TextBox)sender).Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { app.IsEditing = false; e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var app = BoxApp(sender);
        if (app is { IsEditing: true }) CommitRename(app, ((TextBox)sender).Text);
    }

    private void CommitRename(AppEntry app, string text)
    {
        app.IsEditing = false;
        _model.Rename(app, text);
    }

    // ---------- groups: rename / remove ----------

    private void GroupHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TileGroup grh)
        {
            if (e.ClickCount == 2) { BeginGroupRename(grh); e.Handled = true; return; }
            if (e.ClickCount == 1 && !grh.IsEditing)
            {
                _dragStart = e.GetPosition(null);
                _dragCandidate = grh;
            }
        }
    }



    private void BeginGroupRename(TileGroup g)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            g.EditText = g.Name;
            g.IsEditing = true;
        }));
    }

    private void GroupBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TileGroup g } tb) return;
        if (e.Key == Key.Enter) { g.IsEditing = false; _model.RenameGroup(g, tb.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { g.IsEditing = false; e.Handled = true; }
    }

    private void GroupBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TileGroup { IsEditing: true } g } tb)
        {
            g.IsEditing = false;
            _model.RenameGroup(g, tb.Text);
        }
    }

    // ---------- drag & drop (tiles + apps -> groups) ----------

    private void Tile_ResetKeyboardFocus(object sender, MouseButtonEventArgs e)
    {
        // WPF otherwise keeps a keyboard focus adorner when the already-focused tile is clicked.
        if (sender is Button { IsKeyboardFocused: true }) Keyboard.ClearFocus();
    }

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = (sender as FrameworkElement)?.DataContext;
        if (sender is Button { DataContext: TileModel } tileButton) TilePress(tileButton, e);
    }

    private bool ExceededDragDistance(MouseEventArgs e)
    {
        var p = e.GetPosition(null);
        return Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    // ---------- A-Z jump ----------

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is string s && (s == StartModel.MostUsed || s == StartModel.RecentlyAdded)) return;

        var present = _model.AppsView.Groups?.OfType<CollectionViewGroup>()
                          .Select(g => g.Name?.ToString() ?? "").ToHashSet() ?? new();
        JumpGrid.Children.Clear();
        var keys = new[] { "&", "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()));
        foreach (var key in keys)
        {
            var b = new Button
            {
                Content = key,
                Tag = key,
                Style = (Style)FindResource("JumpButton"),
                IsEnabled = present.Contains(key)
            };
            b.Click += Jump_Click;
            JumpGrid.Children.Add(b);
        }
        AppList.Visibility = Visibility.Collapsed;
        Motion.Set(JumpOverlay, OpacityProperty, 0);
        JumpOverlay.Visibility = Visibility.Visible;
        Motion.FadeIn(JumpOverlay);
        e.Handled = true;
    }

    /// <summary>Closes the letter grid with the standard exit (short fade) and brings the app list back with the standard fade in.</summary>
    private void CloseJumpOverlay()
    {
        if (JumpOverlay.Visibility != Visibility.Visible) return;
        Motion.Set(AppList, OpacityProperty, 0);
        EnforcePaneVisibility(jumpOpen: false);
        Motion.FadeIn(AppList);
        Motion.FadeOut(JumpOverlay, () =>
        {
            JumpOverlay.Visibility = Visibility.Collapsed;
            Motion.Set(JumpOverlay, OpacityProperty, 1);
        });
    }

    private static void CreateShortcut(AppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.Path)) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = System.IO.Path.Combine(desktop, System.IO.Path.GetFileNameWithoutExtension(app.Path) + ".lnk");
            
            var shell32 = Type.GetTypeFromProgID("Shell.Application");
            dynamic shellApp = Activator.CreateInstance(shell32);
            dynamic folder = shellApp.NameSpace(desktop);
            dynamic link = folder.Items().Item(System.IO.Path.GetFileName(shortcutPath));
            if (link == null)
            {
                dynamic newLink = shellApp.CreateShortcut(shortcutPath);
                newLink.TargetPath = app.Path;
                newLink.Save();
            }
        }
        catch (Exception ex) { Log.Warn("Create shortcut failed", ex); }
    }

    public static void CreateShortcutFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = System.IO.Path.Combine(desktop, System.IO.Path.GetFileNameWithoutExtension(path) + ".lnk");
            var shell32 = Type.GetTypeFromProgID("Shell.Application");
            dynamic shellApp = Activator.CreateInstance(shell32);
            dynamic newLink = shellApp.CreateShortcut(shortcutPath);
            newLink.TargetPath = path;
            newLink.Save();
        }
        catch (Exception ex) { Log.Warn("Create shortcut failed", ex); }
    }

    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        var g = _model.AppsView.Groups?.OfType<CollectionViewGroup>().FirstOrDefault(x => (x.Name?.ToString() ?? "") == key);
        CloseJumpOverlay();
        if (g is { Items.Count: > 0 })
        {
            var firstItem = g.Items[0];
            _listScroll?.Cancel();
            AppList.ScrollIntoView(firstItem);

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var container = AppList.ItemContainerGenerator.ContainerFromItem(firstItem) as ListBoxItem;
                if (container != null)
                {
                    var scrollViewer = FindChild<ScrollViewer>(AppList);
                    if (scrollViewer != null)
                    {
                        scrollViewer.UpdateLayout();
                        var groupContainer = FindAncestor<GroupItem>(container);
                        double groupTop = groupContainer != null
                            ? groupContainer.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y
                            : container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y - 42;
                        scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset + groupTop));
                    }
                }
            }));
        }
        e.Handled = true;
    }

    private void Jump_Close(object sender, MouseButtonEventArgs e)
    {
        CloseJumpOverlay();
        e.Handled = true;
    }

    private void JumpBack_Click(object sender, RoutedEventArgs e)
    {
        CloseJumpOverlay();
        e.Handled = true;
    }

    // ---------- left rail ----------

    private void SetRailExpanded(bool on, bool animate)
    {
        _railExpanded = on;
        if (on) RailGrid.SetResourceReference(Panel.BackgroundProperty, "Brush.Overlay");
        else { RailGrid.ClearValue(Panel.BackgroundProperty); RailGrid.Background = Brushes.Transparent; }
        double to = on ? 220 : RailWidth;
        if (animate) Motion.Run(RailGrid, WidthProperty, to, Motion.Fast, on ? Motion.Enter : Motion.Exit);
        else Motion.Set(RailGrid, WidthProperty, to);
    }

    private void Rail_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        switch (b.Tag as string)
        {
            case "top": SetRailExpanded(!_railExpanded, animate: true); break;
            case "viewtoggle":
                if (_searchVisible) SearchBox.Text = "";
                _showTiles = !_showTiles;
                ShowPane(_showTiles, animate: true);
                UpdateToggleLabel();
                break;
            case "docs": Open("explorer.exe", "shell:Personal"); break;
            case "pics": Open("explorer.exe", "shell:My Pictures"); break;
            case "settings": Open("ms-settings:"); break;
            case "user": ShowFlyoutBeside(b, AccountMenu()); break;
            case "power": ShowFlyoutBeside(b, PowerMenu()); break;
        }
    }

    private void FolderRailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RailFolderItem folder)
        {
            HideMenu();
            try { Process.Start(new ProcessStartInfo(folder.ShellUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Folder rail button failed: " + folder.ShellUri, ex); }
        }
    }

    private void CustomRailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CustomRailButton btn)
        {
            HideMenu();
            try { Process.Start(new ProcessStartInfo(btn.Target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Custom rail button failed: " + btn.Target, ex); }
        }
    }




    // ---------- view mode: list + tiles, list only, or tiles only (rail button switches in single-pane modes) ----------

    /// <summary>Re-applies the view-mode setting (called when settings change while the menu is open).</summary>
    public void ApplyViewSettings()
    {
        _showTiles = _settings.ViewMode == StartViewMode.TilesOnly;
        ApplyViewMode(animate: false);
        UpdateRailButtonVisibility();
        if (IsVisible)
        {
            PositionWindow();
            if (_searchVisible) { _searchExpanded = false; SetSearchExpanded(true); }
        }
    }

    private void UpdateRailButtonVisibility()
    {
        if (UserButton != null) UserButton.Visibility = _settings.ShowUserButton ? Visibility.Visible : Visibility.Collapsed;
        if (DocsButton != null) DocsButton.Visibility = _settings.ShowDocumentsButton ? Visibility.Visible : Visibility.Collapsed;
        if (PicsButton != null) PicsButton.Visibility = _settings.ShowPicturesButton ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsButton != null) SettingsButton.Visibility = _settings.ShowSettingsButton ? Visibility.Visible : Visibility.Collapsed;
        if (FolderRailButtons != null) FolderRailButtons.ItemsSource = RailFolderCatalog.Enabled(_settings.RailFolders);
    }

    private void ApplyViewMode(bool animate)
    {
        if (!SinglePane)
        {
            ListColumn.Width = new GridLength(ListWidth);
            TilesColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(TilesScroll, 2);
            DetailColumn.Width = new GridLength(270);
            ViewToggleButton.Visibility = Visibility.Collapsed;
            Motion.Cancel(AppList, OpacityProperty);
            Motion.Cancel(TilesScroll, OpacityProperty);
            AppList.Visibility = Visibility.Visible;
            TilesScroll.Visibility = Visibility.Visible;
            if (!_searchVisible) { AppList.Opacity = 1; TilesScroll.Opacity = 1; }
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            TilesColumn.Width = new GridLength(0);
            Grid.SetColumn(TilesScroll, 1);
            DetailColumn.Width = new GridLength(0);
            ViewToggleButton.Visibility = Visibility.Visible;
            ShowPane(_showTiles, animate);
        }
        UpdateToggleLabel();
    }

    private void UpdateToggleLabel()
    {
        // The button names the pane you would switch to.
        ViewToggleGlyph.Text = _showTiles ? "\uE71D" : "\uE8A9";
        ViewToggleLabel.Text = _showTiles ? "All apps" : "Tiles";
    }

    /// <summary>
    /// Sets pane visibility from the view mode alone: "Both" shows both, "List" only the app list, "Tiles" only the tiles.
    /// Called wherever a transition could have been interrupted, so a pane can never be left visible underneath the other
    /// (the tiles drawn over the app list in Tiles-only mode).
    /// </summary>
    private void EnforcePaneVisibility(bool jumpOpen = true)
    {
        bool letterGridOpen = jumpOpen && JumpOverlay.Visibility == Visibility.Visible;
        if (!SinglePane)
        {
            AppList.Visibility = letterGridOpen ? Visibility.Collapsed : Visibility.Visible;
            TilesScroll.Visibility = Visibility.Visible;
            return;
        }
        AppList.Visibility = !_showTiles && !letterGridOpen ? Visibility.Visible : Visibility.Collapsed;
        TilesScroll.Visibility = _showTiles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPane(bool tiles, bool animate)
    {
        CancelAllScroll();
        FrameworkElement show = tiles ? TilesScroll : AppList;
        FrameworkElement hide = tiles ? AppList : TilesScroll;
        Motion.Cancel(show, OpacityProperty);
        Motion.Cancel(hide, OpacityProperty);

        if (!animate || !AnimationsEnabled || !IsVisible)
        {
            hide.Visibility = Visibility.Collapsed;
            show.Visibility = Visibility.Visible;
            if (!_searchVisible) { show.Opacity = 1; hide.Opacity = 1; }
            return;
        }

        // Outgoing pane leaves with the standard exit; the incoming pane then enters sliding in from the side.
        Motion.FadeOut(hide, () =>
        {
            hide.Visibility = Visibility.Collapsed;
            Motion.Set(hide, OpacityProperty, 1);
            Motion.Set(show, OpacityProperty, 0);
            show.Visibility = Visibility.Visible;
            Motion.EnterX(show);
            if (_searchVisible) Motion.Set(show, OpacityProperty, 0);
            EnforcePaneVisibility();
        });
    }

    // ---------- search page ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pendingEnter = false;
        bool any = !string.IsNullOrWhiteSpace(SearchBox.Text);
        UpdateSearchChrome();
        _search.QueryChanged(SearchBox.Text);
        SetSearchPageVisible(any);
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchChrome();

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchChrome();

    private void UpdateSearchChrome()
    {
        bool hasText = SearchBox.Text.Length > 0;
        if (SearchClearButton != null)
            SearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (SearchGlyph != null)
            SearchGlyph.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
        SearchBox.CaretIndex = 0;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<SearchFilter>(tag, out var filter))
        {
            _search.Filter = filter;
            if (_searchVisible) Motion.FadeIn(ResultsList, from: 0.6);
        }
    }

    private void SetPanesOpacity(double value)
    {
        Motion.Set(AppList, OpacityProperty, value);
        Motion.Set(TilesScroll, OpacityProperty, value);
    }

    private void FadePanes(double to)
    {
        if (to >= 1)
        {
            Motion.FadeIn(AppList, AppList.Opacity);
            Motion.FadeIn(TilesScroll, TilesScroll.Opacity);
        }
        else
        {
            Motion.FadeOut(AppList);
            Motion.FadeOut(TilesScroll);
        }
    }

    private void SetPanesHitTest(bool on)
    {
        AppList.IsHitTestVisible = on;
        TilesScroll.IsHitTestVisible = on;
    }

    /// <summary>Cross-fades between the Start content and the search page (fade + slight slide), like the native Start search.</summary>
    private void SetSearchPageVisible(bool show)
    {
        if (show == _searchVisible) return;
        if (show) CancelAllScroll();
        _searchVisible = show;

        SetSearchExpanded(show);
        EnforcePaneVisibility();
        if (show) CloseFolderOverlay(instant: true);

        if (show)
        {
            Motion.Cancel(SearchPage, OpacityProperty);
            SearchPage.Visibility = Visibility.Visible;
            SetPanesHitTest(false);
            if (AnimationsEnabled)
            {
                Motion.EnterY(SearchPage, Motion.SlidePage);   // SearchPage's RenderTransform is SearchSlide
                FadePanes(0);
            }
            else
            {
                Motion.Set(SearchPage, OpacityProperty, 1);
                Motion.Set(SearchSlide, TranslateTransform.YProperty, 0);
                SetPanesOpacity(0);
            }
        }
        else
        {
            SetPanesHitTest(true);
            if (AnimationsEnabled && IsVisible)
            {
                FadePanes(1);
                Motion.FadeOut(SearchPage, () =>
                {
                    if (_searchVisible) return;
                    SearchPage.Visibility = Visibility.Collapsed;
                });
            }
            else
            {
                Motion.Set(SearchPage, OpacityProperty, 0);
                SearchPage.Visibility = Visibility.Collapsed;
                SetPanesOpacity(1);
            }
        }
    }

    // ---------- search width ----------

    /// <summary>
    /// Widens the window while searching so every filter tab ("All, Apps, Documents, ...") fits on one row, and returns it to the
    /// normal width afterwards. The width never shrinks below the normal layout, and never runs off the work area.
    /// </summary>
    private void SetSearchExpanded(bool expand)
    {
        if (_searchExpanded == expand) return;
        _searchExpanded = expand;
        if (_hwnd == IntPtr.Zero || !IsVisible || !NativeMethods.GetWindowRect(_hwnd, out var rc)) return;

        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double to = _baseWidthDip;
        if (expand)
        {
            FilterChips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double needed = RailWidth + 12 + FilterChips.DesiredSize.Width + 12;   // 12 = the chip row's side margins
            double room = (_workRightPx - _marginPx - rc.Left) / s;
            to = Math.Max(_baseWidthDip, Math.Min(needed, room));
        }
        AnimateWidth((rc.Right - rc.Left) / s, to, expand);
    }

    private void AnimateWidth(double fromDip, double toDip, bool entering)
    {
        if (Math.Abs(fromDip - toDip) < 0.5) { StopWidthAnimation(); return; }
        if (!Motion.Enabled) { StopWidthAnimation(); SetWindowWidthDip(toDip); return; }

        _widthFrom = fromDip;
        _widthTo = toDip;
        _widthEase = entering ? Motion.Enter : Motion.Exit;
        _widthStart = Stopwatch.GetTimestamp();
        if (!_widthRunning)
        {
            _widthRunning = true;
            CompositionTarget.Rendering += OnWidthFrame;
        }
    }

    private void OnWidthFrame(object? sender, EventArgs e)
    {
        double t = Stopwatch.GetElapsedTime(_widthStart).TotalMilliseconds / Motion.Normal.TimeSpan.TotalMilliseconds;
        if (t >= 1 || !IsVisible)
        {
            if (IsVisible) SetWindowWidthDip(_widthTo);
            StopWidthAnimation();
            return;
        }
        SetWindowWidthDip(_widthFrom + (_widthTo - _widthFrom) * _widthEase.Ease(t));
    }

    private void StopWidthAnimation()
    {
        if (!_widthRunning) return;
        CompositionTarget.Rendering -= OnWidthFrame;
        _widthRunning = false;
    }

    /// <summary>Resizes the native window to a width in DIPs, keeping its left edge, top and height.</summary>
    private void SetWindowWidthDip(double dip)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rc)) return;
        int w = (int)Math.Round(dip * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (w == rc.Right - rc.Left) return;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, rc.Left, rc.Top, w, rc.Bottom - rc.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Instantly returns to the normal Start content (no animation) and clears the query.</summary>
    private void ResetSearchUi()
    {
        _searchVisible = false;
        _searchExpanded = false;   // the window is re-sized to its normal width by PositionWindow on the next show
        StopWidthAnimation();
        Motion.Set(SearchSlide, TranslateTransform.YProperty, 0);
        Motion.Set(SearchPage, OpacityProperty, 0);
        SearchPage.Visibility = Visibility.Collapsed;
        SetPanesOpacity(1);
        SetPanesHitTest(true);
        EnforcePaneVisibility();
        SearchBox.Text = "";
        UpdateSearchChrome();
        _search.QueryChanged("");
    }

    // ---------- search keyboard extras (modelled on the Windhawk "start-everything" mod) ----------

    private bool _pendingEnter;
    private DispatcherTimer? _pendingEnterTimer;

    /// <summary>
    /// Enter: opens the selected (or first) result. Pressed while the file search for what you typed is still running, it waits
    /// and opens the first result as soon as the results are in (at most 1.5 s), so a fast typist never opens a stale top hit.
    /// </summary>
    private void EnterOnResult()
    {
        if (!_search.IsBusy) { OpenResult(_search.Selected ?? ResultsList.Items.Cast<SearchResult>().FirstOrDefault()); return; }

        _pendingEnter = true;
        _pendingEnterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pendingEnterTimer.Tick -= PendingEnterTimeout;
        _pendingEnterTimer.Tick += PendingEnterTimeout;
        _pendingEnterTimer.Stop();
        _pendingEnterTimer.Start();
    }

    private void PendingEnterTimeout(object? sender, EventArgs e) => FlushPendingEnter();

    private void FlushPendingEnter()
    {
        _pendingEnterTimer?.Stop();
        if (!_pendingEnter) return;
        _pendingEnter = false;
        OpenResult(_search.Selected ?? ResultsList.Items.Cast<SearchResult>().FirstOrDefault());
    }

    /// <summary>Ctrl+Enter: runs the selected result elevated, using the result's own "Run as administrator" action.</summary>
    private void RunResultAsAdmin(SearchResult? r)
    {
        var action = r?.Actions.FirstOrDefault(a => a.Label.StartsWith("Run as administrator", StringComparison.OrdinalIgnoreCase));
        if (action == null) return;
        try { action.Execute(); }
        catch (Exception ex) { Log.Warn("Run as administrator failed", ex); }
        HideMenu();
    }

    /// <summary>Shift+Enter: the same menu as right-clicking the result.</summary>
    private void ShowResultMenu(SearchResult? r)
    {
        if (r == null || (r.App == null && r.Actions.Count == 0)) return;
        if (ResultsList.ItemContainerGenerator.ContainerFromItem(r) is not FrameworkElement anchor) return;
        ShowFlyoutBeside(anchor, ResultMenuItems(r));
    }

    /// <summary>
    /// Left / Right: jump between the app-like results (apps, settings) and the file results (documents, folders, files).
    /// Right only acts with the caret at the end of the query and Left at the start, so the arrows still edit the text.
    /// </summary>
    private bool SwitchResultColumn(bool toFiles)
    {
        bool inBox = ReferenceEquals(Keyboard.FocusedElement, SearchBox);
        if (inBox && (SearchBox.SelectionLength > 0 || (toFiles ? SearchBox.CaretIndex != SearchBox.Text.Length : SearchBox.CaretIndex != 0))) return false;
        if (!inBox && !ResultsList.IsKeyboardFocusWithin) return false;

        static bool IsFile(SearchResult r) => r.Kind is SearchKind.Document or SearchKind.Folder or SearchKind.File;
        static bool IsApp(SearchResult r) => r.Kind is SearchKind.App or SearchKind.Setting;
        var target = ResultsList.Items.Cast<SearchResult>().FirstOrDefault(toFiles ? IsFile : IsApp);
        if (target == null) return false;

        ResultsList.SelectedItem = target;
        ResultsList.ScrollIntoView(target);
        return true;
    }

    private void OpenResult(SearchResult? r)
    {
        if (r == null) return;
        try { r.Open(); }
        catch (Exception ex) { Log.Warn("Open result failed", ex); }
        HideMenu();
    }

    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SearchResult r }) OpenResult(r);
    }

    private void ResultAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SearchAction action) return;
        try { action.Execute(); }
        catch (Exception ex) { Log.Warn("Result action failed: " + action.Label, ex); }
        HideMenu();
    }

}
