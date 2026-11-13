using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace FluentStart;

/// <summary>Every context menu of the Start window (apps, tiles, groups, search results, rail). They are <see cref="Flyout"/> windows so they follow the Mica/Alt/Acrylic setting.</summary>
public partial class MainWindow
{
    private ShellContextMenu? _shellMenu;

    private void ShowFlyoutAtCursor(IReadOnlyList<FlyoutItem> items)
    {
        _suppressHide = true;               // opening a flyout deactivates this window; do not hide Start while it is open
        Flyout.ShowAtCursor(items, OnFlyoutClosed);
        if (Flyout.Root == null) _suppressHide = false;   // nothing opened: never leave Start unable to close
    }

    private void ShowFlyoutBeside(FrameworkElement anchor, IReadOnlyList<FlyoutItem> items)
    {
        _suppressHide = true;
        Flyout.ShowBeside(anchor, items, OnFlyoutClosed);
        if (Flyout.Root == null) _suppressHide = false;
    }

    private void OnFlyoutClosed()
    {
        _suppressHide = false;
        bool commandChosen = (DateTime.UtcNow - Flyout.LastActionUtc).TotalMilliseconds < 1500;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsVisible || _suppressHide || IsActive) return;
            // A command that works inside Start (rename, hide, pin, resize...) must not close it: take the focus back.
            // Otherwise focus went elsewhere while the menu was open (a click on the desktop): hide, as usual.
            if (commandChosen) Activate();
            else HideMenu();
        }));
    }

    private void DisposeShellMenu()
    {
        _shellMenu?.Dispose();
        _shellMenu = null;
    }

    // ---------- app / tile menu (mirrors the native Start menu's entries) ----------

    /// <summary>
    /// Order: Pin to Start, More (Pin to taskbar / Run as administrator / Open file location / anything else the shell offers), Uninstall,
    /// then FluentStart-only commands (resize, rename, hide) after a separator, and finally the app's jump-list items (Recent / Frequent).
    /// </summary>
    private List<FlyoutItem> BuildAppMenu(AppEntry app, TileModel? tile, string? section = null, bool inSearch = false)
    {
        var items = new List<FlyoutItem>();

        items.Add(new FlyoutItem
        {
            Text = app.IsPinned ? "Unpin from Start" : "Pin to Start",
            Glyph = app.IsPinned ? "\uE77A" : "\uE718",
            Click = () => { if (app.IsPinned) _model.Unpin(app.Id); else _model.Pin(app); }
        });

        // "More" + "Uninstall" come from the shell's own menu for this app when it provides them.
        DisposeShellMenu();
        _shellMenu = ShellContextMenu.Create(app.Id);
        var native = _shellMenu?.Verbs ?? new List<NativeVerb>();
        var shell = _shellMenu;

        FlyoutItem? Native(Func<string, bool> match, string glyph, bool closeStart = true)
        {
            var v = native.FirstOrDefault(n => !n.HasSubmenu && match(n.Text));
            if (v == null || shell == null) return null;
            native.Remove(v);
            return new FlyoutItem { Text = v.Text, Glyph = glyph, Click = () => { shell.Invoke(v); if (closeStart) HideMenu(); } };
        }

        // Pinning in the real Start layout is not what this app does; drop the shell's own pin-to-Start entries.
        native.RemoveAll(n => n.Text.Contains("Start", StringComparison.OrdinalIgnoreCase) && n.Text.Contains("pin", StringComparison.OrdinalIgnoreCase));
        native.RemoveAll(n => n.Text.Equals("Open", StringComparison.OrdinalIgnoreCase));
        native.RemoveAll(n => n.Text.Contains("new window", StringComparison.OrdinalIgnoreCase));
        native.RemoveAll(n => n.Text.Contains("create shortcut", StringComparison.OrdinalIgnoreCase) || n.Text.Contains("create link", StringComparison.OrdinalIgnoreCase));

        var taskbar = Native(t => t.Contains("taskbar", StringComparison.OrdinalIgnoreCase), "\uE718");
        var admin = Native(t => t.Contains("administrator", StringComparison.OrdinalIgnoreCase), "\uE7EF")
                    ?? (!string.IsNullOrEmpty(app.Path)
                        ? new FlyoutItem { Text = "Run as administrator", Glyph = "\uE7EF", Click = () => { RunElevated(app.Path!); HideMenu(); } }
                        : null);
        var location = Native(t => t.Contains("location", StringComparison.OrdinalIgnoreCase), "\uE838")
                       ?? (!string.IsNullOrEmpty(app.Path)
                           ? new FlyoutItem { Text = "Open file location", Glyph = "\uE838", Click = () => { Reveal(app.Path!); HideMenu(); } }
                           : null);
        var uninstall = Native(t => t.Contains("uninstall", StringComparison.OrdinalIgnoreCase), "\uE74D")
                        ?? new FlyoutItem { Text = "Uninstall", Glyph = "\uE74D", Click = () => { Open("ms-settings:appsfeatures"); } };

        var more = new List<FlyoutItem>();
        foreach (var f in new[] { taskbar, admin, location }) if (f != null) more.Add(f);
        foreach (var v in native.Where(n => !n.HasSubmenu).ToList())
        {
            var captured = v;
            more.Add(new FlyoutItem { Text = v.Text, Click = () => { shell?.Invoke(captured); HideMenu(); } });
        }
        if (more.Count > 0) items.Add(new FlyoutItem { Text = "More", Glyph = "\uE712", Children = more });
        items.Add(uninstall);

        // FluentStart's own commands (overlay only; the real shortcut/package is never touched).
        items.Add(FlyoutItem.Separator());
        if (tile != null)
            items.Add(new FlyoutItem
            {
                Text = "Resize", Glyph = "\uE740",
                Children = Enum.GetValues<TileSize>().Select(size => new FlyoutItem
                {
                    Text = size.ToString(), IsChecked = tile.Size == size, Click = () => _model.Resize(tile, size)
                }).ToList()
            });
        if (tile != null) items.Add(BuildColorMenu(tile));
        if (!inSearch) items.Add(new FlyoutItem { Text = "Rename", Glyph = "\uE8AC", Click = () => BeginRename(app) });   // the rename box lives in the list / on the tile
        if (app.IsRenamed) items.Add(new FlyoutItem { Text = "Reset name", Glyph = "\uE777", Click = () => _model.Rename(app, "") });
        // "Hide from list" belongs to the app list only; a tile is never hidden from here.
        if (tile == null)
            items.Add(new FlyoutItem { Text = app.IsHidden ? "Show in list" : "Hide from list", Glyph = "\uED1A", Click = () => _model.ToggleHidden(app) });

        if (!string.IsNullOrWhiteSpace(app.Path))
            more.Add(new FlyoutItem { Text = "Create shortcut", Glyph = "\uE718", Click = () => { CreateShortcut(app); HideMenu(); } });

        // Clear the "Most used" / "Recently added" list when invoked from an entry in that section (Windows 10-style).
        if (section == StartModel.MostUsed)
        {
            items.Add(FlyoutItem.Separator());
            items.Add(new FlyoutItem { Text = "Clear most used", Glyph = "\uE74D", Click = () => _model.ClearMostUsed() });
        }
        else if (section == StartModel.RecentlyAdded)
        {
            items.Add(FlyoutItem.Separator());
            items.Add(new FlyoutItem { Text = "Clear recently added", Glyph = "\uE74D", Click = () => _model.ClearRecentlyAdded() });
        }

        // Jump list: recent / frequent items of the app (e.g. Word's recent documents). They come last, after every command
        // FluentStart itself provides.
        var jumps = JumpLists.Get(app.Id);
        if (jumps.Count > 0)
        {
            items.Add(FlyoutItem.Separator());
            items.Add(FlyoutItem.Header(jumps.Any(j => !j.IsFrequent) ? "Recent" : "Frequent"));
            foreach (var j in jumps)
            {
                var captured = j;
                items.Add(new FlyoutItem { Text = j.Title, Glyph = "\uE8A5", Click = () => { JumpLists.Open(captured); HideMenu(); } });
            }
        }
        return items;
    }

    /// <summary>"Color" submenu of a tile: the Windows colour swatches in a grid, "Custom" (Windows colour dialog) and, when a colour is set, "Default".</summary>
    private FlyoutItem BuildColorMenu(TileModel tile)
    {
        var swatches = TileColors.Palette.Select(h => TileColors.TryParse(h)!.Value).ToList();
        var children = new List<FlyoutItem>
        {
            new() { Swatches = swatches, SelectedColor = TileColors.TryParse(tile.CustomColor), SwatchChosen = c => _model.SetTileColor(tile, TileColors.ToHex(c)) },
            FlyoutItem.Separator(),
            new() { Text = "Accent", Glyph = "\uEC34", IsChecked = tile.UseAccent, Click = () => _model.SetTileAccent(tile) },
            new() { Text = "Custom", Glyph = "\uEF3C", Click = () => PickCustomTileColor(tile) }
        };
        if (tile.HasCustomColor)
            children.Add(new FlyoutItem { Text = "Default", Glyph = "\uE777", Click = () => _model.SetTileColor(tile, null) });
        return new FlyoutItem { Text = "Color", Glyph = "\uE790", Children = children };
    }

    private static int[]? _customDialogColors;

    private sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }

    /// <summary>Opens the standard Windows colour dialog (full "Define Custom Colors" view) and applies the chosen colour to the tile.</summary>
    private void PickCustomTileColor(TileModel tile)
    {
        var start = TileColors.TryParse(tile.CustomColor)
                    ?? (TryFindResource("Brush.AccentTile") as System.Windows.Media.SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD7);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
            CustomColors = _customDialogColors ?? Array.Empty<int>()
        };

        _suppressHide = true;   // the dialog takes focus; Start must stay open behind it
        _modalBusy = true;
        try
        {
            var owner = new Win32Owner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                _model.SetTileColor(tile, TileColors.ToHex(System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
            _customDialogColors = dialog.CustomColors;
        }
        finally
        {
            _modalBusy = false;
            _suppressHide = false;
            if (IsVisible) Activate();
        }
    }

    private void Open(string target, string? args = null)
    {
        HideMenu();
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open failed: " + target, ex); }
    }

    private static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Warn("Run failed: " + exe, ex); }
    }

    private static void RunElevated(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); }
        catch (Exception ex) { Log.Warn("Run as administrator failed: " + path, ex); }   // also thrown when the UAC prompt is declined
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Reveal failed: " + path, ex); }
    }

    // ---------- right-click entry points ----------

    private void ListItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ListRow { IsFolder: false } row) return;
        e.Handled = true;
        ShowFlyoutAtCursor(BuildAppMenu(row.App, null, row.Section));
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TileModel { App: { } app } tile) return;
        e.Handled = true;
        ShowFlyoutAtCursor(tile.IsFolderTile ? BuildFolderTileMenu(tile) : BuildAppMenu(app, tile));
    }

    private void GroupHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TileGroup tg })
        {
            e.Handled = true;
            var items = new List<FlyoutItem>
            {
                new() { Text = "Rename", Glyph = "\uE8AC", Click = () => BeginGroupRename(tg) }
            };
            if (!ReferenceEquals(tg, _model.Groups.FirstOrDefault()))
            {
                items.Add(new FlyoutItem { Text = "Remove group", Glyph = "\uE74D", Click = () => _model.RemoveGroup(tg) });
            }
            items.Add(FlyoutItem.Separator());
            items.Add(new() { Text = "Settings", Glyph = "\uE713", Click = () => { HideMenu(); SettingsRequested?.Invoke(); } });
            ShowFlyoutAtCursor(items);
            return;
        }

        if (sender is not FrameworkElement { DataContext: CollectionViewGroup group }) return;
        string name = group.Name?.ToString() ?? "";
        if (name != StartModel.MostUsed && name != StartModel.RecentlyAdded) return;

        e.Handled = true;
        ShowFlyoutAtCursor(new List<FlyoutItem>
        {
            new() { Text = $"Clear \"{name}\"", Glyph = "\uE74D", Click = () =>
            {
                if (name == StartModel.MostUsed) _model.ClearMostUsed();
                else _model.ClearRecentlyAdded();
                HideMenu();
            } }
        });
    }

    private void Result_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SearchResult result) return;
        var menu = ResultMenuItems(result);
        if (menu.Count == 0) return;
        e.Handled = true;
        ShowFlyoutAtCursor(menu);
    }

    /// <summary>
    /// Menu of a search result. An app gets exactly the menu it has in the app list (Pin to Start, More, Uninstall, Hide, recent files...);
    /// everything else gets the commands of its own result.
    /// </summary>
    private List<FlyoutItem> ResultMenuItems(SearchResult result) => result.App != null && result.Kind == SearchKind.App
        ? BuildAppMenu(result.App, null, inSearch: true)
        : result.Actions.Select(a => new FlyoutItem
    {
        Text = a.Label, Glyph = a.Glyph,
        Click = () =>
        {
            try { a.Execute(); } catch (Exception ex) { Log.Warn("Result action failed: " + a.Label, ex); }
            HideMenu();
        }
    }).ToList();

    private void Rail_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: "settings" } b) return;
        e.Handled = true;
        ShowFlyoutBeside(b, new List<FlyoutItem> { new() { Text = "FluentStart settings", Glyph = "\uE713", Click = () => { HideMenu(); SettingsRequested?.Invoke(); } } });
    }

    /// <summary>Menu key / Shift+F10 on the focused app, tile or result.</summary>
    private bool OpenContextMenuForFocus()
    {
        switch (Keyboard.FocusedElement)
        {
            case Button { DataContext: TileModel { App: { } app } tile }:
                ShowFlyoutAtCursor(BuildAppMenu(app, tile));
                return true;
            case ListBoxItem { DataContext: ListRow { IsFolder: false } row }:
                ShowFlyoutAtCursor(BuildAppMenu(row.App, null, row.Section));
                return true;
        }
        return false;
    }

    // ---------- rail menus (left click) ----------

    private List<FlyoutItem> AccountMenu() => new()
    {
        new() { Text = "Change account settings", Glyph = "\uE77B", Click = () => Open("ms-settings:yourinfo") },
        new() { Text = "Lock", Glyph = "\uE72E", Click = () => { HideMenu(); Run("rundll32.exe", "user32.dll,LockWorkStation"); } },
        new() { Text = "Sign out", Glyph = "\uE748", Click = () => { HideMenu(); Run("shutdown.exe", "/l"); } }
    };

    private List<FlyoutItem> PowerMenu() => new()
    {
        new() { Text = "Sleep", Glyph = "\uE708", Click = () => { HideMenu(); Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); } },
        new() { Text = "Shut down", Glyph = "\uE7E8", Click = () => { HideMenu(); Run("shutdown.exe", "/s /t 0"); } },
        new() { Text = "Restart", Glyph = "\uE777", Click = () => { HideMenu(); Run("shutdown.exe", "/r /t 0"); } }
    };
}
