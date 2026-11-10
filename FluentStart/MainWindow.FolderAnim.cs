using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FluentStart;

/// <summary>
/// Expanding / collapsing a Start-menu folder in the app list. The list is a filtered, grouped view that is refreshed on every
/// toggle (rows are re-created / recycled), so the animation is built around that instead of per-row state:
///  - expand: refresh, then the new rows fade/slide in while everything below them slides down from where it used to be;
///  - collapse: the rows fade/slide out and everything below slides up, and only then is the view refreshed.
/// Timings and curves come from <see cref="Motion"/>.
/// </summary>
public partial class MainWindow
{
    private const double RowPitch = 42;   // app row: 40 high + 1 DIP margin above and below (see RowHeight / RowMargin in MainWindow.xaml)
    private bool _folderAnimating;

    private static bool IsInside(ListRow row, ListRow folder)
    {
        for (var p = row.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, folder)) return true;
        return false;
    }

    /// <summary>The realised row containers, by row (grouped lists do not expose them through the generator).</summary>
    private Dictionary<ListRow, ListBoxItem> RealizedRows()
    {
        var found = new List<ListBoxItem>();
        CollectChildren(AppList, found);
        var map = new Dictionary<ListRow, ListBoxItem>();
        foreach (var c in found) if (c.DataContext is ListRow r) map[r] = c;
        return map;
    }

    /// <summary>
    /// Elements that have to move when rows are inserted/removed: the containers of the rows after <paramref name="firstIndex"/> in
    /// the folder's own letter group, and the whole block (header + rows) of each later group. Only realised rows are included.
    /// </summary>
    private List<UIElement> ElementsAfter(IList<object> items, Dictionary<ListRow, ListBoxItem> map, int firstIndex, ListRow folder)
    {
        map.TryGetValue(folder, out var folderContainer);
        var folderGroup = folderContainer == null ? null : FindAncestorWhere(folderContainer, d => d is GroupItem);

        var result = new List<UIElement>();
        var seen = new HashSet<UIElement>();
        for (int i = firstIndex; i < items.Count; i++)
        {
            if (items[i] is not ListRow row || !map.TryGetValue(row, out var container)) break;   // past the realised window
            UIElement target = container;
            if (FindAncestorWhere(container, d => d is GroupItem) is GroupItem group && !ReferenceEquals(group, folderGroup)) target = group;
            if (seen.Add(target)) result.Add(target);
        }
        return result;
    }

    private void ToggleFolderAnimated(ListRow folder)
    {
        if (!folder.IsFolder || _folderAnimating) return;
        if (!Motion.Enabled || !AppList.IsVisible || _model.IsSearching) { _model.ToggleFolder(folder); return; }

        var items = AppList.Items.Cast<object>().ToList();
        if (!items.Contains(folder)) { _model.ToggleFolder(folder); return; }

        if (folder.IsExpanded) CollapseFolder(folder, items);
        else ExpandFolder(folder);
    }

    private void ExpandFolder(ListRow folder)
    {
        _model.ToggleFolder(folder);
        AppList.UpdateLayout();   // realise the new rows now, so the animation starts before the first frame is drawn

        var items = AppList.Items.Cast<object>().ToList();
        int at = items.IndexOf(folder);
        if (at < 0) return;
        int kids = items.Skip(at + 1).TakeWhile(o => o is ListRow r && IsInside(r, folder)).Count();
        if (kids == 0) return;

        var map = RealizedRows();
        double shift = kids * RowPitch;

        for (int i = 0; i < kids; i++)
        {
            if (items[at + 1 + i] is not ListRow row || !map.TryGetValue(row, out var c)) continue;
            Motion.Run(c, OpacityProperty, 1, Motion.Fast, Motion.Enter, from: 0);
            Motion.Run(Motion.EnsureTranslate(c), TranslateTransform.YProperty, 0, Motion.Normal, Motion.Enter, from: -Motion.SlideItem);
        }
        foreach (var el in ElementsAfter(items, map, at + 1 + kids, folder))
            Motion.Run(Motion.EnsureTranslate(el), TranslateTransform.YProperty, 0, Motion.Normal, Motion.Enter, from: -shift);
    }

    private void CollapseFolder(ListRow folder, List<object> items)
    {
        int at = items.IndexOf(folder);
        int kids = items.Skip(at + 1).TakeWhile(o => o is ListRow r && IsInside(r, folder)).Count();
        var map = RealizedRows();
        if (kids == 0 || !map.ContainsKey(folder)) { _model.ToggleFolder(folder); return; }

        _folderAnimating = true;
        double shift = kids * RowPitch;
        var touched = new List<UIElement>();
        int pending = 1;   // released after the loops, so a callback can never finish the animation early

        void Done()
        {
            if (--pending > 0) return;
            foreach (var el in touched)
            {
                Motion.Set(el, OpacityProperty, 1);
                Motion.Set(Motion.EnsureTranslate(el), TranslateTransform.YProperty, 0);
            }
            _folderAnimating = false;
            _model.ToggleFolder(folder);   // the rows really go away now; everything is already where it will be
        }

        for (int i = 0; i < kids; i++)
        {
            if (items[at + 1 + i] is not ListRow row || !map.TryGetValue(row, out var c)) continue;
            touched.Add(c);
            Motion.Run(c, OpacityProperty, 0, Motion.Faster, null);
            pending++;
            Motion.Run(Motion.EnsureTranslate(c), TranslateTransform.YProperty, -Motion.SlideItem, Motion.Fast, Motion.Exit, done: Done);
        }
        foreach (var el in ElementsAfter(items, map, at + 1 + kids, folder))
        {
            touched.Add(el);
            pending++;
            Motion.Run(Motion.EnsureTranslate(el), TranslateTransform.YProperty, -shift, Motion.Fast, Motion.Enter, done: Done);
        }
        Done();
    }
}
