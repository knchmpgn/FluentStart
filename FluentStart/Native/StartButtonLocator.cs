using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace FluentStart.Native;

/// <summary>
/// Finds the screen rectangles (physical pixels) of Start buttons on the primary and secondary taskbars via UI Automation
/// and caches them, so the mouse hook only needs cheap rectangle tests.
/// </summary>
internal sealed class StartButtonLocator
{
    public sealed record Box(int L, int T, int R, int B);

    private volatile Box[] _boxes = Array.Empty<Box>();
    private int _busy;

    public Box[] Current => _boxes;
    public bool Found => _boxes.Length > 0;

    // The Start button's UIA element per taskbar window. Searching the whole taskbar tree (a cross-process UIA walk) every 5 s
    // was the most expensive thing the app did while idle; re-reading one property of a known element is far cheaper.
    // Only touched inside Refresh, which never runs concurrently (_busy).
    private readonly Dictionary<IntPtr, AutomationElement> _elements = new();
    private int _cheapRefreshes;
    private const int FullSearchEvery = 6;   // even the periodic poll re-searches every ~30 s, so a re-created button is never trusted for long

    /// <param name="full">True (default): always search the taskbar tree. False: reuse the known button element and only re-read its rectangle.</param>
    public Task Refresh(bool full = true)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                if (full || ++_cheapRefreshes >= FullSearchEvery) { _elements.Clear(); _cheapRefreshes = 0; }

                var found = new List<Box>();
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) AddBox(found, tray);

                // Secondary-monitor taskbars. Whether they host a Start button at all is unverified.
                IntPtr sec = IntPtr.Zero;
                while ((sec = NativeMethods.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                    AddBox(found, sec);

                _boxes = found.ToArray();
            }
            catch (Exception ex) { _boxes = Array.Empty<Box>(); _elements.Clear(); Log.WarnOnce("locator", "Start button lookup failed", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private void AddBox(List<Box> list, IntPtr taskbarHwnd)
    {
        if (_elements.TryGetValue(taskbarHwnd, out var known))
        {
            try
            {
                var kr = known.Current.BoundingRectangle;
                if (!kr.IsEmpty && kr.Width > 0 && kr.Height > 0)
                {
                    list.Add(new Box((int)kr.Left, (int)kr.Top, (int)kr.Right, (int)kr.Bottom));
                    return;
                }
            }
            catch (ElementNotAvailableException) { }   // taskbar rebuilt / Explorer restarted: fall through to a full search
            catch (Exception ex) { Log.WarnOnce("locator-cache", "Cached Start button element failed", ex); }
            _elements.Remove(taskbarHwnd);
        }

        var root = AutomationElement.FromHandle(taskbarHwnd);
        // Primary: AutomationId. Fallback: localized Name "Start" (English only). Neither is verified on a real system.
        var el = root.FindFirst(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"))
                 ?? root.FindFirst(TreeScope.Descendants,
                     new AndCondition(
                         new PropertyCondition(AutomationElement.NameProperty, "Start"),
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (el == null) return;
        var r = el.Current.BoundingRectangle;
        if (!r.IsEmpty)
        {
            list.Add(new Box((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));
            _elements[taskbarHwnd] = el;
        }
    }
}
