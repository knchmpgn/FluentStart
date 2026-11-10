using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentStart;

/// <summary>
/// Frame-synchronised mouse-wheel smooth scrolling for a <see cref="ScrollViewer"/> (or a control that contains one).
///
/// Design: wheel input only moves a *target* offset (notches accumulate, so spinning the wheel glides instead of restarting).
/// A <see cref="CompositionTarget.Rendering"/> callback - which fires once per displayed frame, not on a free-running timer -
/// moves the real offset toward the target with frame-rate-independent exponential smoothing
/// (offset += (target - offset) * (1 - e^(-dt / tau))). It runs only while a glide is in progress.
/// Precision-touchpad deltas (smaller than one notch) and "Animation effects: off" scroll 1:1 without easing.
/// Anything else that moves the scroll position (scroll-bar drag, keyboard, ScrollIntoView, jump-to-letter) cancels the glide.
/// Requires pixel scrolling: ListBoxes need VirtualizingPanel.ScrollUnit="Pixel" or ScrollViewer.CanContentScroll="False".
/// </summary>
internal sealed class SmoothScroller
{
    private const double TauMs = 48;            // smoothing time constant: ~90 % of a notch in ~110 ms, settled in ~250 ms
    private const double SettleEpsilon = 0.35;  // px
    private const double OwnWriteTolerance = 2; // px

    private readonly FrameworkElement _host;
    private readonly double _linePx;
    private ScrollViewer? _viewer;
    private double _pos, _target;
    private double _w1 = double.NaN, _w2 = double.NaN, _w3 = double.NaN;   // the last offsets this class wrote
    private TimeSpan _lastRender;
    private bool _running;

    /// <param name="host">The ScrollViewer itself, or a control whose template contains one (ListBox).</param>
    /// <param name="linePx">Pixels per "line"; one wheel notch scrolls SystemParameters.WheelScrollLines lines.</param>
    public SmoothScroller(FrameworkElement host, double linePx)
    {
        _host = host;
        _linePx = linePx;
        host.PreviewMouseWheel += OnWheel;
        host.Unloaded += (_, _) => Cancel();
        host.IsVisibleChanged += (_, _) => { if (!_host.IsVisible) Cancel(); };
    }

    public void Cancel()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnRendering;
        _running = false;
    }

    private ScrollViewer? Resolve()
    {
        var found = _host as ScrollViewer ?? FindChild<ScrollViewer>(_host);
        if (!ReferenceEquals(found, _viewer))
        {
            if (_viewer != null) _viewer.ScrollChanged -= OnScrollChanged;
            _viewer = found;
            if (_viewer != null) _viewer.ScrollChanged += OnScrollChanged;
        }
        return _viewer;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;   // zoom / horizontal conventions

        var sv = Resolve();
        if (sv == null || !sv.IsVisible || sv.ScrollableHeight <= 0.5) return;

        int lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        double notchPx = lines < 0 ? sv.ViewportHeight * 0.9 : lines * _linePx;   // lines < 0 means "one page per notch"
        double delta = -e.Delta / 120.0 * notchPx;

        double current = sv.VerticalOffset;
        double basis = _running ? _target : current;
        double next = Math.Clamp(basis + delta, 0, sv.ScrollableHeight);
        if (Math.Abs(next - basis) < 0.01 && Math.Abs(next - current) < 0.5) return;   // already at the edge
        e.Handled = true;

        if (!Motion.Enabled || Math.Abs(e.Delta) < 120)
        {
            Cancel();
            Write(sv, next);
            return;
        }

        _target = next;
        if (!_running)
        {
            _pos = current;
            _lastRender = TimeSpan.Zero;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var sv = _viewer;
        if (sv == null || !sv.IsVisible) { Cancel(); return; }

        TimeSpan now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (now != TimeSpan.Zero && now == _lastRender) return;   // WPF can raise Rendering twice for one frame
        double dt = _lastRender == TimeSpan.Zero || now == TimeSpan.Zero ? 16.7 : (now - _lastRender).TotalMilliseconds;
        _lastRender = now;
        dt = Math.Clamp(dt, 1, 64);

        _target = Math.Clamp(_target, 0, sv.ScrollableHeight);   // the content can shrink under us (search, folder collapse)
        double next = _pos + (_target - _pos) * (1 - Math.Exp(-dt / TauMs));
        if (Math.Abs(_target - next) < SettleEpsilon)
        {
            Write(sv, _target);
            Cancel();
            return;
        }
        Write(sv, next);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_running || e.VerticalChange == 0) return;
        double o = e.VerticalOffset;
        if (Near(o, _w1) || Near(o, _w2) || Near(o, _w3)) return;   // our own write
        Cancel();                                                    // someone else moved the list: stop fighting them
    }

    private static bool Near(double a, double b) => !double.IsNaN(b) && Math.Abs(a - b) <= OwnWriteTolerance;

    private void Write(ScrollViewer sv, double offset)
    {
        _w3 = _w2; _w2 = _w1; _w1 = offset;
        _pos = offset;
        sv.ScrollToVerticalOffset(offset);
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
}
