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

    public Task Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                var found = new List<Box>();
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) AddBox(found, tray);

                // Secondary-monitor taskbars. Whether they host a Start button at all is unverified.
                IntPtr sec = IntPtr.Zero;
                while ((sec = NativeMethods.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                    AddBox(found, sec);

                _boxes = found.ToArray();
            }
            catch (Exception ex) { _boxes = Array.Empty<Box>(); Log.WarnOnce("locator", "Start button lookup failed", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private static void AddBox(List<Box> list, IntPtr taskbarHwnd)
    {
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
        if (!r.IsEmpty) list.Add(new Box((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));
    }
}
