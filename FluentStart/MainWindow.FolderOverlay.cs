using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FluentStart;

/// <summary>
/// The panel that opens over the Start menu when a folder tile is clicked: the folder's name and its apps in a grid.
/// It lives inside the Start window (not a separate window), so it never disturbs focus or the click-outside-to-close logic.
/// Click an app to launch it; right-click it for its menu plus "Remove from folder"; click outside the panel or press Esc to close.
/// </summary>
public partial class MainWindow
{
    private TileModel? _openFolder;

    private void OpenFolderOverlay(TileModel folder, FrameworkElement anchor)
    {
        var apps = (folder.ChildIds ?? new List<string>()).Select(id => _model.FindApp(id)).OfType<AppEntry>().ToList();
        if (apps.Count == 0) return;

        _openFolder = folder;
        FolderTitle.Text = folder.App?.DisplayName ?? "Folder";
        FolderItems.ItemsSource = apps;

        Motion.Set(FolderOverlay, OpacityProperty, 1);
        FolderOverlay.Visibility = Visibility.Visible;

        // Under the tile if it fits, otherwise above it; always inside the window.
        FolderPanel.Measure(new Size(RootGrid.ActualWidth - 16, RootGrid.ActualHeight - 16));
        Size size = FolderPanel.DesiredSize;
        Point bottom = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, anchor.ActualHeight), RootGrid);
        Point top = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), RootGrid);
        double left = System.Math.Clamp(bottom.X - size.Width / 2, 8, System.Math.Max(8, RootGrid.ActualWidth - size.Width - 8));
        double y = bottom.Y + 8;
        if (y + size.Height > RootGrid.ActualHeight - 8) y = System.Math.Max(8, top.Y - size.Height - 8);
        FolderPanel.Margin = new Thickness(left, y, 0, 0);

        Motion.EnterY(FolderPanel, Motion.SlideItem);
    }

    private void CloseFolderOverlay(bool instant = false)
    {
        _openFolder = null;
        if (FolderOverlay.Visibility != Visibility.Visible) return;
        if (instant || !Motion.Enabled)
        {
            FolderOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        Motion.FadeOut(FolderOverlay, () =>
        {
            if (_openFolder != null) return;   // reopened meanwhile
            FolderOverlay.Visibility = Visibility.Collapsed;
            Motion.Set(FolderOverlay, OpacityProperty, 1);
        });
    }

    private void FolderOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseFolderOverlay();
        e.Handled = true;
    }

    private void FolderPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;   // clicks on the panel itself do not close it

    private void FolderApp_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppEntry app) return;
        CloseFolderOverlay(instant: true);
        Launch(app);
    }

    private void FolderApp_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppEntry app || _openFolder is not { } folder) return;
        e.Handled = true;

        var items = BuildAppMenu(app, null);
        items.Insert(0, new FlyoutItem
        {
            Text = "Remove from folder", Glyph = "\uE711",
            Click = () =>
            {
                _model.RemoveFromFolder(folder, app.Id);
                bool stillAFolder = folder.IsFolderTile && folder.ChildIds!.Count >= 2 && _model.Groups.Any(g => g.Tiles.Contains(folder));
                if (stillAFolder && _openFolder != null) OpenFolderOverlayAgain(folder);
                else CloseFolderOverlay(instant: true);
            }
        });
        items.Insert(1, FlyoutItem.Separator());
        ShowFlyoutAtCursor(items);
    }

    private void OpenFolderOverlayAgain(TileModel folder)
    {
        var apps = (folder.ChildIds ?? new List<string>()).Select(id => _model.FindApp(id)).OfType<AppEntry>().ToList();
        FolderItems.ItemsSource = apps;
    }

    private List<FlyoutItem> BuildFolderTileMenu(TileModel tile)
    {
        var app = tile.App!;
        return new List<FlyoutItem>
        {
            new()
            {
                Text = "Resize", Glyph = "\uE740",
                Children = System.Enum.GetValues<TileSize>().Select(size => new FlyoutItem
                {
                    Text = size.ToString(), IsChecked = tile.Size == size, Click = () => _model.Resize(tile, size)
                }).ToList()
            },
            BuildColorMenu(tile),
            new() { Text = "Rename", Glyph = "\uE8AC", Click = () => BeginRename(app) },
            FlyoutItem.Separator(),
            new() { Text = "Ungroup", Glyph = "\uE711", Click = () => _model.Ungroup(tile) },
            new() { Text = "Unpin folder", Glyph = "\uE77A", Click = () => _model.UnpinFolder(tile) }
        };
    }
}
