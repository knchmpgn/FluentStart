using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace FluentStart;

/// <summary>
/// Tray icon with a modern flyout. The icon is a WinForms NotifyIcon (WPF has none) but the menu is a <see cref="Flyout"/>: a rounded window
/// with the same Mica / Mica Alt / Acrylic backdrop, light/dark mode, system accent and animations as the Start menu.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Settings _s;
    private readonly Action _changed, _showStart, _openSettings, _refreshApps, _clearIcons, _exit;
    private readonly Func<string> _status;

    public Tray(Settings s, Action settingsChanged, Action showStart, Action openSettings,
                Action refreshApps, Action clearIcons, Action exit, Func<string> status)
    {
        _s = s;
        _changed = () => { s.Save(); settingsChanged(); };
        _showStart = showStart; _openSettings = openSettings; _refreshApps = refreshApps; _clearIcons = clearIcons; _exit = exit; _status = status;

        _icon = new WinForms.NotifyIcon { Icon = LoadIcon(), Text = "Fluent Start", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) ShowMenu();
            else if (e.Button == WinForms.MouseButtons.Left) _showStart();
        };
    }

    private void ShowMenu()
    {
        var items = new List<FlyoutItem>
        {
            FlyoutItem.Header(_status()),
            FlyoutItem.Separator(),
            new() { Text = "Show Start", Glyph = "\uE8FC", Click = _showStart },
            new() { Text = "Settings", Glyph = "\uE713", Click = _openSettings },
            FlyoutItem.Separator(),
            Choice("Theme", "\uE790", Enum.GetValues<ThemeChoice>(), () => _s.Theme, v => _s.Theme = v),
            Choice("Backdrop", "\uE7F4", Enum.GetValues<BackdropChoice>(), () => _s.Backdrop, v => _s.Backdrop = v),
            Choice("Alignment", "\uE8E4", Enum.GetValues<AlignChoice>(), () => _s.Align, v => _s.Align = v),
            Choice("View", "\uE80A", Enum.GetValues<StartViewMode>(), () => _s.ViewMode, v => _s.ViewMode = v),
            new() { Text = "Start with Windows", Glyph = "\uE7E8", IsChecked = Startup.IsEnabled(), Click = () => Startup.SetEnabled(!Startup.IsEnabled()) },
            FlyoutItem.Separator(),
            new() { Text = "Refresh app list", Glyph = "\uE72C", Click = _refreshApps },
            new() { Text = "Clear icon cache", Glyph = "\uE74D", Click = _clearIcons },
            new() { Text = "Open data folder", Glyph = "\uE838", Click = OpenDataFolder },
            FlyoutItem.Separator(),
            new() { Text = "Exit", Glyph = "\uE711", Click = _exit }
        };
        Flyout.ShowAtCursor(items);
    }

    private FlyoutItem Choice<T>(string title, string glyph, T[] values, Func<T> get, Action<T> set) where T : struct, Enum =>
        new()
        {
            Text = title,
            Glyph = glyph,
            Children = values.Select(v => new FlyoutItem
            {
                Text = v.ToString(),
                IsChecked = Equals(v, get()),
                Click = () => { set(v); _changed(); }
            }).ToList()
        };

    private static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Storage.DataDir + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open data folder failed", ex); }
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var i = Environment.ProcessPath != null ? Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null;
            if (i != null) return i;
        }
        catch (Exception ex) { Log.WarnOnce("trayicon", "Tray icon load failed", ex); }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        Flyout.CloseAll();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
