using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FluentStart.Native;

namespace FluentStart;

/// <summary>Shared window appearance (Mica / Mica Alt / Acrylic, rounded corners, dark title) applied to the Start window, flyouts and settings.</summary>
internal static class Appearance
{
    public static BackdropChoice Backdrop { get; set; } = BackdropChoice.Mica;
    public static bool Dark { get; set; } = true;

    private static int BackdropType(BackdropChoice b) => b switch
    {
        BackdropChoice.Acrylic => 3,   // DWMSBT_TRANSIENTWINDOW
        BackdropChoice.MicaAlt => 4,   // DWMSBT_TABBEDWINDOW
        _ => 2                         // DWMSBT_MAINWINDOW (Mica)
    };

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCACTIVATE = 0x0086;

    /// <summary>
    /// Mica / Mica Alt are only drawn while the window counts as active; when another window (a context menu, the shell menu)
    /// takes activation, DWM swaps them for a flat fallback colour. Answering WM_NCACTIVATE(FALSE) as if it were TRUE (lParam -1 =
    /// "do not repaint the non-client area", the same approach Chromium/Electron use for Mica windows) keeps the backdrop on.
    /// WPF's Activated/Deactivated events come from WM_ACTIVATE and are unaffected, so hide-on-deactivate keeps working.
    /// </summary>
    public static void KeepBackdropActive(HwndSource? source)
    {
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_NCACTIVATE || wParam != IntPtr.Zero) return IntPtr.Zero;
            handled = true;
            return DefWindowProc(hwnd, (uint)msg, (IntPtr)1, (IntPtr)(-1));
        });
    }

    /// <summary>Applies the current appearance to a window handle (call again after theme/backdrop changes).</summary>
    public static void Apply(IntPtr hwnd)
    {
        Apply(hwnd, out _);
    }

    /// <summary>Applies the shared DWM attributes and reports whether the requested backdrop is available.</summary>
    public static bool Apply(IntPtr hwnd, out int result)
    {
        result = -1;
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int dark = Dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
            int corner = 2;
            NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
            int type = BackdropType(Backdrop);
            result = NativeMethods.DwmSetWindowAttribute(hwnd, 38, ref type, 4);

            var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int frameResult = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            bool applied = result >= 0 && frameResult >= 0;
            if (!applied)
                Log.WarnOnce("dwm-backdrop", $"DWM backdrop unavailable (backdrop=0x{result:X8}, frame=0x{frameResult:X8}); using a solid theme background");
            return applied;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.WarnOnce("dwm-backdrop", "DWM backdrop is unavailable; using the solid theme background", ex);
            return false;
        }
    }
}
