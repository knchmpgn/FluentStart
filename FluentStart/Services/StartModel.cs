using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;

namespace FluentStart;

public sealed record TileData(string Id, TileSize Size, string? Color = null, string? Name = null, List<string>? Items = null, int? X = null, int? Y = null, bool Accent = false);
public sealed record GroupData(string Name, List<TileData> Tiles);
public sealed record UsageEntry(int Count, DateTime Last);
public sealed record Bundle(Dictionary<string, string> Renames, List<GroupData> Groups, List<string>? Hidden = null);

/// <summary>
/// Orders rows like the native list: "Most used"/"Recently added" first, then everything alphabetical with each folder
/// followed by its (expanded) children. While searching: best match first.
/// </summary>
internal sealed class RowComparer : IComparer
{
    private readonly bool _search;
    public RowComparer(bool search) { _search = search; }

    private static int Cmp(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);

    public int Compare(object? x, object? y)
    {
        var a = (ListRow)x!;
        var b = (ListRow)y!;

        if (_search)
        {
            int s = b.SearchScore.CompareTo(a.SearchScore);
            return s != 0 ? s : Cmp(a.SortName, b.SortName);
        }

        int rank = a.Rank.CompareTo(b.Rank);
        if (rank != 0) return rank;
        if (a.Rank < 2)
        {
            int order = a.SortOrder.CompareTo(b.SortOrder);
            return order != 0 ? order : Cmp(a.SortName, b.SortName);
        }

        var pa = a.SortChain;
        var pb = b.SortChain;
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            int c = Cmp(pa[i], pb[i]);
            if (c != 0) return c;
        }
        int len = pa.Length.CompareTo(pb.Length);   // a folder comes before its children
        return len != 0 ? len : string.CompareOrdinal(a.App.Id, b.App.Id);
    }
}

public sealed class StartModel : Observable
{
    public const string MostUsed = "Most used";
    public const string RecentlyAdded = "Recently added";

    private readonly Settings _settings;
    private readonly Dictionary<string, string> _renames;
    private readonly Dictionary<string, UsageEntry> _usage;
    private readonly Dictionary<string, DateTime> _firstSeen;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, AppEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ListRow> _mainRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ListRow> _folderRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ListRow> _sectionRows = new();
    private readonly List<ListRow> _structRows = new();   // folder + app rows (everything except the Most used / Recently added shortcuts)
    private bool _seeded;
    private readonly DispatcherTimer _usageSaveTimer;
    private bool _usageDirty;
    private string _search = "";
    private int _tileColumns = 6;
    private bool _useIconColors;

    /// <summary>Real apps only (folders are separate rows with synthetic entries).</summary>
    public ObservableCollection<AppEntry> Apps { get; } = new();
    public RangeObservableCollection<ListRow> Rows { get; } = new();
    public ObservableCollection<TileGroup> Groups { get; } = new();
    public ICollectionView AppsView { get; }

    /// <summary>Tile grid width in small cells (even, 6-16).</summary>
    public int TileColumns
    {
        get => _tileColumns;
        set => Set(ref _tileColumns, TileLayout.Normalize(value));
    }

    /// <summary>When true, tiles use <see cref="AppEntry.TileBrush"/> instead of the system accent.</summary>
    public bool UseIconColors
    {
        get => _useIconColors;
        set => Set(ref _useIconColors, value);
    }

    /// <summary>Show each app's name on its tile (off = icon only).</summary>
    public bool ShowTileNames
    {
        get => _showTileNames;
        set
        {
            if (!Set(ref _showTileNames, value)) return;
            TileModel.NamesVisible = value;
            foreach (var g in Groups) foreach (var tile in g.Tiles) tile.RefreshLabel();
        }
    }
    private bool _showTileNames = true;

    public bool IsSearching => !string.IsNullOrWhiteSpace(_search);

    /// <summary>True while searching with no matches (drives the empty-state text).</summary>
    public bool NoResults => IsSearching && AppsView.IsEmpty;

    public string SearchText
    {
        get => _search;
        set
        {
            string v = value ?? "";
            if (v == _search) return;
            bool was = IsSearching;
            _search = v;
            ApplySearchScores();
            if (IsSearching != was)
            {
                foreach (var r in Rows) r.FlatMode = IsSearching;
                ConfigureView(IsSearching);
            }
            else AppsView.Refresh();
            Raise(nameof(NoResults));
        }
    }

    public StartModel(Settings settings)
    {
        _settings = settings;
        _renames = new(Storage.Load<Dictionary<string, string>>("renames.json") ?? new(), StringComparer.OrdinalIgnoreCase);
        _usage = new(Storage.Load<Dictionary<string, UsageEntry>>("usage.json") ?? new(), StringComparer.OrdinalIgnoreCase);
        _hidden = new(Storage.Load<List<string>>("hidden.json") ?? new(), StringComparer.OrdinalIgnoreCase);
        var fs = Storage.Load<Dictionary<string, DateTime>>("firstseen.json");
        _seeded = fs != null;
        _firstSeen = new(fs ?? new(), StringComparer.OrdinalIgnoreCase);

        AppsView = CollectionViewSource.GetDefaultView(Rows);
        ConfigureView(false);
        AppsView.Filter = Match;

        var data = Storage.Load<List<GroupData>>("tiles.json");
        if (data != null)
            foreach (var g in data)
            {
                var grp = new TileGroup(g.Name);
                foreach (var t in g.Tiles) grp.Tiles.Add(TileFromData(t));
                Groups.Add(grp);
            }
        if (Groups.Count == 0) Groups.Add(new TileGroup("Pinned"));

        TileColumns = settings.TileColumns;
        // Migrate tiles that were saved without explicit positions (older files) into a packed layout.
        foreach (var g in Groups)
        {
            if (!g.Tiles.Any(t => t.Column < 0 || t.Row < 0)) continue;
            var sizes = g.Tiles.Select(t => TilePacker.CellSize(t.Size)).ToList();
            var positions = TilePacker.Pack(sizes, TileColumns, out _);
            for (int i = 0; i < g.Tiles.Count && i < positions.Count; i++)
            {
                g.Tiles[i].Column = positions[i].X;
                g.Tiles[i].Row = positions[i].Y;
            }
            foreach (var t in g.Tiles) if (t.Column < 0) t.Column = 0;
            foreach (var t in g.Tiles) if (t.Row < 0) t.Row = 0;
        }
        UseIconColors = settings.TileColorsFromIcons && !System.Windows.SystemParameters.HighContrast;
        ShowTileNames = settings.ShowTileNames;

        _usageSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _usageSaveTimer.Tick += (_, _) =>
        {
            _usageSaveTimer.Stop();
            if (_usageDirty)
            {
                _usageDirty = false;
                Storage.Save("usage.json", _usage);
                RefreshSections();
            }
        };
    }

    // ---------- list view (sorting / grouping / search) ----------

    private void ConfigureView(bool searching)
    {
        using (AppsView.DeferRefresh())
        {
            AppsView.GroupDescriptions.Clear();
            if (AppsView is ListCollectionView lcv) lcv.CustomSort = new RowComparer(searching);
            if (!searching) AppsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ListRow.Group)));
        }
    }

    private bool Match(object o)
    {
        if (o is not ListRow r) return false;
        bool showHidden = _settings.ShowHiddenApps;
        if (IsSearching) return r.IsMain && r.SearchScore > 0 && (showHidden || !r.App.IsHidden);   // flat, ranked, folders hidden
        if (!r.IsTreeVisible) return false;                                                         // collapsed folders hide their children
        if (r.IsFolder) return showHidden || HasVisibleApp(r);                                      // folders whose apps are all hidden disappear
        return showHidden || !r.App.IsHidden;
    }

    private static bool HasVisibleApp(ListRow folder) =>
        folder.Children.Any(c => c.IsFolder ? HasVisibleApp(c) : !c.App.IsHidden);

    private void ApplySearchScores()
    {
        string q = _search.Trim();
        foreach (var r in Rows)
            r.SearchScore = r.IsMain && q.Length > 0 ? SearchScorer.Score(q, r.App.DisplayName, r.App.OriginalName) : 0;
    }

    // ---------- folders ----------

    public void ToggleFolder(ListRow folder)
    {
        if (!folder.IsFolder) return;
        folder.IsExpanded = !folder.IsExpanded;
        if (folder.App.IsFolder) folder.App.Icon = ShellIcons.GetFolderIcon(28, folder.IsExpanded);
        AppsView.Refresh();
    }

    public void CollapseAll()
    {
        bool any = false;
        foreach (var f in _folderRows.Values)
            if (f.IsExpanded) { f.IsExpanded = false; any = true; }
        if (any) AppsView.Refresh();
    }

    /// <summary>Recreates the folder + app rows from <see cref="Apps"/> (keeps which folders were expanded) and republishes the list once.</summary>
    private void RebuildRows()
    {
        var expanded = new HashSet<string>(_folderRows.Where(kv => kv.Value.IsExpanded).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var old in _structRows) old.Detach();
        _structRows.Clear();
        _mainRows.Clear();
        _folderRows.Clear();

        foreach (var app in Apps)
        {
            ListRow? parent = !string.IsNullOrEmpty(app.FolderPath) ? EnsureFolder(app.FolderPath!) : null;   // Start-menu folders are always shown as expandable rows
            var row = new ListRow(app, null, 0, parent) { FlatMode = IsSearching };
            _mainRows[app.Id] = row;
            _structRows.Add(row);
        }
        foreach (var kv in _folderRows)
            if (expanded.Contains(kv.Key)) kv.Value.IsExpanded = true;

        BuildSections();
        PublishRows();
    }

    /// <summary>One Reset notification instead of hundreds of Add/Remove calls.</summary>
    private void PublishRows()
    {
        Rows.ReplaceAll(_sectionRows.Concat(_structRows));
        if (IsSearching) ApplySearchScores();
        Raise(nameof(NoResults));
    }

    private ListRow EnsureFolder(string path)
    {
        if (_folderRows.TryGetValue(path, out var existing)) return existing;

        int cut = path.LastIndexOf('\\');
        ListRow? parent = cut > 0 ? EnsureFolder(path[..cut]) : null;
        string name = cut >= 0 ? path[(cut + 1)..] : path;

        var entry = new AppEntry("folder:" + path, name, isFolder: true);
        entry.Icon = ShellIcons.GetFolderIcon(28);
        var row = new ListRow(entry, null, 0, parent, isFolder: true) { FlatMode = IsSearching };
        _folderRows[path] = row;
        _structRows.Add(row);
        return row;
    }

    // ---------- catalog ----------

    private int _lastSignature;

    internal void ApplyCatalog(IEnumerable<AppCatalog.Item> items)
    {
        var list = items.ToList();

        // The catalog is re-read often (every time the menu opens after 30 s); skip all work when nothing changed.
        var hash = new HashCode();
        foreach (var it in list) { hash.Add(it.Id); hash.Add(it.Name); hash.Add(it.Path); hash.Add(it.Folder); }
        int signature = hash.ToHashCode();
        if (signature == _lastSignature && Apps.Count > 0) return;
        _lastSignature = signature;

        bool seed = !_seeded && list.Count > 0;
        bool firstSeenChanged = false;
        bool structure = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in list)
        {
            seen.Add(it.Id);
            if (_byId.TryGetValue(it.Id, out var existing))
            {
                existing.UpdateOriginal(it.Name);
                existing.Path = it.Path;
                if (!string.Equals(existing.FolderPath, it.Folder, StringComparison.Ordinal))
                {
                    existing.FolderPath = it.Folder;
                    structure = true;
                }
            }
            else
            {
                var n = new AppEntry(it.Id, it.Name) { Path = it.Path, FolderPath = it.Folder, IsHidden = _hidden.Contains(it.Id) };
                if (_renames.TryGetValue(it.Id, out var c)) n.CustomName = c;
                Apps.Add(n);
                _byId[it.Id] = n;
                structure = true;
            }
            if (!_firstSeen.ContainsKey(it.Id))
            {
                _firstSeen[it.Id] = seed ? DateTime.UtcNow.AddYears(-1) : DateTime.UtcNow;
                firstSeenChanged = true;
            }
        }
        if (seed) _seeded = true;

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            var a = Apps[i];
            if (seen.Contains(a.Id)) continue;
            Apps.RemoveAt(i);
            _byId.Remove(a.Id);
            structure = true;
        }

        if (structure) RebuildRows();
        ResolveTiles();

        if (firstSeenChanged) Storage.Save("firstseen.json", _firstSeen);
        if (!structure) RefreshSections();
    }

    /// <summary>Links tiles to their apps (null if not installed) and refreshes IsPinned flags.</summary>
    private void ResolveTiles()
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Groups.SelectMany(g => g.Tiles))
        {
            if (t.IsFolderTile)
            {
                foreach (var id in t.ChildIds!) pinned.Add(id);
                EnsureFolderApp(t);
                continue;
            }
            t.App = _byId.TryGetValue(t.AppId, out var a) ? a : null;
            pinned.Add(t.AppId);
        }
        foreach (var a in Apps) a.IsPinned = pinned.Contains(a.Id);
    }

    // ---------- folder tiles ----------

    private readonly HashSet<string> _hookedFolderIcons = new();

    public AppEntry? FindApp(string id) => _byId.TryGetValue(id, out var a) ? a : null;

    /// <summary>The folder tile that contains the app, if any.</summary>
    public TileModel? FolderOf(string appId) =>
        Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => t.IsFolderTile && t.ChildIds!.Contains(appId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Gives a folder tile its stand-in app (name + icon) and keeps the icon in step with its apps' icons.</summary>
    private void EnsureFolderApp(TileModel tile)
    {
        tile.App ??= new AppEntry(tile.AppId, string.IsNullOrWhiteSpace(tile.FolderName) ? "Folder" : tile.FolderName, isFolder: true);
        foreach (var id in tile.ChildIds!)
        {
            if (!_byId.TryGetValue(id, out var child) || !_hookedFolderIcons.Add(tile.AppId + "|" + id)) continue;
            var captured = tile;
            child.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppEntry.Icon))
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => RefreshFolderIcon(captured)));
            };
        }
        RefreshFolderIcon(tile);
    }

    public void RefreshFolderIcon(TileModel tile)
    {
        if (tile.App == null || tile.ChildIds == null) return;
        var icons = new List<System.Windows.Media.ImageSource?>();
        foreach (var id in tile.ChildIds)
            if (_byId.TryGetValue(id, out var child)) { icons.Add(child.Icon); if (icons.Count == 9) break; }   // reading Icon starts loading it
        tile.App.Icon = FolderIcons.Compose(icons);
    }

    private TileGroup? GroupOf(TileModel tile) => Groups.FirstOrDefault(g => g.Tiles.Contains(tile));

    /// <summary>
    /// Drops a tile or an app onto a tile: onto an ordinary tile it creates a folder of the two (in the target's place), onto a folder
    /// tile it joins it. Folders do not nest. Returns the folder and whether it was just created.
    /// </summary>
    public (TileModel? Folder, bool Created) DropIntoTile(TileModel target, object payload)
    {
        TileModel? sourceTile = payload as TileModel;
        string? sourceId = sourceTile?.AppId ?? (payload as AppEntry)?.Id;
        if (sourceId == null || (sourceTile != null && (sourceTile.IsFolderTile || ReferenceEquals(sourceTile, target)))) return (null, false);
        if (payload is AppEntry { IsFolder: true }) return (null, false);
        var group = GroupOf(target);
        if (group == null) return (null, false);
        if (!target.IsFolderTile && string.Equals(target.AppId, sourceId, StringComparison.OrdinalIgnoreCase)) return (null, false);

        // An app that is already a tile elsewhere moves into the folder instead of being pinned twice.
        var duplicate = Groups.SelectMany(g => g.Tiles).FirstOrDefault(t =>
            !t.IsFolderTile && !ReferenceEquals(t, sourceTile) && !ReferenceEquals(t, target) && string.Equals(t.AppId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (duplicate != null) GroupOf(duplicate)?.Tiles.Remove(duplicate);

        var currentFolder = FolderOf(sourceId);
        if (currentFolder != null && !ReferenceEquals(currentFolder, target)) RemoveFromFolder(currentFolder, sourceId, keepAsTile: false);

        TileModel folder;
        bool created = false;
        if (target.IsFolderTile)
        {
            folder = target;
            if (!folder.ChildIds!.Contains(sourceId, StringComparer.OrdinalIgnoreCase)) folder.ChildIds.Add(sourceId);
            if (sourceTile != null) GroupOf(sourceTile)?.Tiles.Remove(sourceTile);
        }
        else
        {
            if (sourceTile != null) GroupOf(sourceTile)?.Tiles.Remove(sourceTile);
            int index = group.Tiles.IndexOf(target);
            if (index < 0) return (null, false);
            folder = new TileModel("folder:" + Guid.NewGuid().ToString("N"), target.Size)
            {
                ChildIds = new List<string> { target.AppId, sourceId },
                FolderName = "Folder",
                CustomColor = target.CustomColor,
                Column = target.Column,
                Row = target.Row
            };
            group.Tiles[index] = folder;
            created = true;
        }
        ResolveTiles();
        PruneEmptyGroups();
        SaveTilesInternal();
        return (folder, created);
    }

    /// <summary>Takes an app out of a folder tile (it becomes its own tile next to the folder). A folder left with fewer than two apps dissolves.</summary>
    public void RemoveFromFolder(TileModel folder, string appId, bool keepAsTile = true)
    {
        var group = GroupOf(folder);
        if (group == null || folder.ChildIds == null) return;
        folder.ChildIds.RemoveAll(id => string.Equals(id, appId, StringComparison.OrdinalIgnoreCase));

        int index = group.Tiles.IndexOf(folder);
        if (keepAsTile && _byId.TryGetValue(appId, out var app))
        {
            var occupied = group.Tiles.Where(t => !ReferenceEquals(t, folder)).Select(t => (t.Column, t.Row, TilePacker.CellSize(t.Size).W, TilePacker.CellSize(t.Size).H)).ToList();
            var (cx, cy) = TilePacker.FindPosition(occupied, TilePacker.CellSize(TileSize.Medium).W, TilePacker.CellSize(TileSize.Medium).H, TileColumns);
            group.Tiles.Insert(index + 1, new TileModel(appId, TileSize.Medium) { App = app, Column = cx, Row = cy });
        }

        if (folder.ChildIds.Count < 2)
        {
            index = group.Tiles.IndexOf(folder);
            var (fc, fr) = (folder.Column, folder.Row);
            group.Tiles.RemoveAt(index);
            if (folder.ChildIds.Count == 1 && _byId.TryGetValue(folder.ChildIds[0], out var last))
            {
                var occupied = group.Tiles.Select(t => (t.Column, t.Row, TilePacker.CellSize(t.Size).W, TilePacker.CellSize(t.Size).H)).ToList();
                var (cx, cy) = TilePacker.FindPosition(occupied, TilePacker.CellSize(folder.Size).W, TilePacker.CellSize(folder.Size).H, TileColumns);
                // prefer original spot if free
                if (TilePacker.Fits(occupied, fc, fr, TilePacker.CellSize(folder.Size).W, TilePacker.CellSize(folder.Size).H, TileColumns)) { cx = fc; cy = fr; }
                group.Tiles.Insert(index, new TileModel(last.Id, folder.Size) { App = last, CustomColor = folder.CustomColor, Column = cx, Row = cy });
            }
        }
        ResolveTiles();
        SaveTilesInternal();
    }

    /// <summary>Replaces a folder tile with one tile per app.</summary>
    public void Ungroup(TileModel folder)
    {
        var group = GroupOf(folder);
        if (group == null || folder.ChildIds == null) return;
        int index = group.Tiles.IndexOf(folder);
        var (fc, fr) = (folder.Column, folder.Row);
        group.Tiles.RemoveAt(index);
        int i = 0;
        foreach (var id in folder.ChildIds)
            if (_byId.TryGetValue(id, out var app))
            {
                var occupied = group.Tiles.Select(t => (t.Column, t.Row, TilePacker.CellSize(t.Size).W, TilePacker.CellSize(t.Size).H)).ToList();
                var (cx, cy) = TilePacker.FindPosition(occupied, TilePacker.CellSize(TileSize.Medium).W, TilePacker.CellSize(TileSize.Medium).H, TileColumns);
                if (i == 0 && TilePacker.Fits(occupied, fc, fr, 2, 2, TileColumns)) { cx = fc; cy = fr; }
                group.Tiles.Insert(index + i, new TileModel(id, TileSize.Medium) { App = app, Column = cx, Row = cy });
                i++;
            }
        ResolveTiles();
        SaveTilesInternal();
    }

    /// <summary>Removes a folder tile and unpins every app in it.</summary>
    public void UnpinFolder(TileModel folder)
    {
        GroupOf(folder)?.Tiles.Remove(folder);
        PruneEmptyGroups();
        ResolveTiles();
        SaveTilesInternal();
    }

    /// <summary>Rebuilds the "Most used" / "Recently added" rows from usage + first-seen data and current settings.</summary>
    public void RefreshSections()
    {
        BuildSections();
        PublishRows();
    }

    private void BuildSections()
    {
        foreach (var old in _sectionRows) old.Detach();
        _sectionRows.Clear();

        int n = Math.Max(0, _settings.QuickItems);
        if (n == 0) return;

        bool Listed(string id) => _byId.TryGetValue(id, out var a) && (_settings.ShowHiddenApps || !a.IsHidden);

        if (_settings.ShowMostUsed)
        {
            int i = 0;
            foreach (var kv in _usage.Where(k => k.Value.Count > 0 && Listed(k.Key))
                                     .OrderByDescending(k => k.Value.Count).ThenByDescending(k => k.Value.Last).Take(n))
                AddSection(_byId[kv.Key], MostUsed, i++);
        }

        if (_settings.ShowRecentlyAdded)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            int i = 0;
            foreach (var kv in _firstSeen.Where(k => k.Value > cutoff && Listed(k.Key))
                                         .OrderByDescending(k => k.Value).Take(n))
                AddSection(_byId[kv.Key], RecentlyAdded, i++);
        }
    }

    private void AddSection(AppEntry app, string section, int order)
    {
        _sectionRows.Add(new ListRow(app, section, order) { FlatMode = IsSearching });
    }

    // ---------- hide (overlay only; the shortcut/package is untouched) ----------

    public IEnumerable<AppEntry> HiddenApps => Apps.Where(a => a.IsHidden).OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    public void SetHidden(AppEntry app, bool hidden)
    {
        if (app.IsFolder) return;
        if (hidden) _hidden.Add(app.Id); else _hidden.Remove(app.Id);
        app.IsHidden = hidden;
        Storage.Save("hidden.json", _hidden.ToList());
        RefreshView();
    }

    public void ToggleHidden(AppEntry app) => SetHidden(app, !app.IsHidden);

    public void ClearHidden()
    {
        foreach (var a in Apps) a.IsHidden = false;
        _hidden.Clear();
        Storage.Save("hidden.json", _hidden.ToList());
        RefreshView();
    }

    /// <summary>Re-applies filters/sections after a settings change (e.g. "show hidden apps").</summary>
    public void RefreshView() => RefreshSections();   // republishing rows re-runs the filter, so hidden-app visibility updates too

    // ---------- rename ----------

    public void Rename(AppEntry app, string text)
    {
        if (app.IsFolder)
        {
            // a folder tile's stand-in app: the name is stored with the tile
            var folder = Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => ReferenceEquals(t.App, app));
            if (folder == null) return;
            text = (text ?? "").Trim();
            folder.FolderName = text.Length == 0 ? "Folder" : text;
            app.CustomName = text.Length == 0 ? null : text;
            SaveTilesInternal();
            return;
        }
        text = (text ?? "").Trim();
        if (text.Length == 0 || text == app.OriginalName)
        {
            _renames.Remove(app.Id);
            app.CustomName = null;
        }
        else
        {
            _renames[app.Id] = text;
            app.CustomName = text;
        }
        Storage.Save("renames.json", _renames);
        AppsView.Refresh();
    }

    public void ClearRenames()
    {
        _renames.Clear();
        foreach (var a in Apps) a.CustomName = null;
        Storage.Save("renames.json", _renames);
        AppsView.Refresh();
    }

    public void ClearMostUsed()
    {
        _usage.Clear();
        Storage.Save("usage.json", _usage);
        RefreshSections();
    }

    public void ClearRecentlyAdded()
    {
        _firstSeen.Clear();
        Storage.Save("firstseen.json", _firstSeen);
        RefreshSections();
    }

    // ---------- tiles ----------

    public void Pin(AppEntry app) => PinAt(app, Groups[0], int.MaxValue);

    public void PinAt(AppEntry app, TileGroup group, int index, int column = -1, int row = -1)
    {
        if (app.IsFolder) return;
        if (FolderOf(app.Id) is { } holder) RemoveFromFolder(holder, app.Id, keepAsTile: false);   // leaving its folder to become a tile
        if (app.IsPinned)
        {
            var existing = Groups.SelectMany(g => g.Tiles)
                                 .FirstOrDefault(t => string.Equals(t.AppId, app.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var occ = group.Tiles.Where(t => !ReferenceEquals(t, existing))
                                     .Select(t => (t.Column, t.Row, TilePacker.CellSize(t.Size).W, TilePacker.CellSize(t.Size).H)).ToList();
                var (cx, cy) = (column >= 0 && row >= 0)
                    ? TilePacker.FindPositionNear(occ, TilePacker.CellSize(existing.Size).W, TilePacker.CellSize(existing.Size).H, TileColumns, column, row)
                    : TilePacker.FindPosition(occ, TilePacker.CellSize(existing.Size).W, TilePacker.CellSize(existing.Size).H, TileColumns);
                MoveTile(existing, group, cx, cy);
                return;
            }
        }
        index = Math.Clamp(index, 0, group.Tiles.Count);
        var occupied = group.Tiles.Select(t => (t.Column, t.Row, TilePacker.CellSize(t.Size).W, TilePacker.CellSize(t.Size).H)).ToList();
        var (x, y) = (column >= 0 && row >= 0)
            ? TilePacker.FindPositionNear(occupied, TilePacker.CellSize(TileSize.Small).W, TilePacker.CellSize(TileSize.Small).H, TileColumns, column, row)
            : TilePacker.FindPosition(occupied, TilePacker.CellSize(TileSize.Small).W, TilePacker.CellSize(TileSize.Small).H, TileColumns);
        var tile = new TileModel(app.Id, TileSize.Small) { App = app, Column = x, Row = y };
        group.Tiles.Insert(index, tile);
        app.IsPinned = true;
        SaveTilesInternal();
    }

    public void MoveTile(TileModel tile, TileGroup target, int column, int row)
    {
        var src = Groups.FirstOrDefault(g => g.Tiles.Contains(tile));
        if (src == null) return;
        var (w, h) = TilePacker.CellSize(tile.Size);
        column = Math.Clamp(column, 0, Math.Max(0, TileColumns - w));
        row = Math.Max(0, row);

        if (!ReferenceEquals(src, target))
        {
            src.Tiles.Remove(tile);
            target.Tiles.Add(tile);
        }
        tile.Column = column;
        tile.Row = row;
        PruneEmptyGroups();
        SaveTilesInternal();
    }

    public TileGroup AddGroup(string name)
    {
        var g = new TileGroup(name);
        Groups.Add(g);
        return g;
    }

    public void RenameGroup(TileGroup group, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        group.Name = name;
        SaveTilesInternal();
    }

    public void SaveTiles() => Storage.Save("tiles.json", SnapshotGroups());

    public void RemoveGroup(TileGroup group)
    {
        foreach (var t in group.Tiles.ToList())
        {
            if (t.App != null) t.App.IsPinned = false;
            group.Tiles.Remove(t);
        }
        if (Groups.Count == 1) Groups.Add(new TileGroup("Pinned"));
        Groups.Remove(group);
        ResolveTiles();
        SaveTilesInternal();
    }

    /// <summary>Removes empty groups except the first (which is the default pin target).</summary>
    public void PruneEmptyGroups()
    {
        for (int i = Groups.Count - 1; i >= 1; i--)
            if (Groups[i].Tiles.Count == 0) Groups.RemoveAt(i);
    }

    public void Unpin(string id)
    {
        foreach (var g in Groups)
            for (int i = g.Tiles.Count - 1; i >= 0; i--)
                if (string.Equals(g.Tiles[i].AppId, id, StringComparison.OrdinalIgnoreCase)) g.Tiles.RemoveAt(i);
        var inFolder = FolderOf(id);
        if (inFolder != null) RemoveFromFolder(inFolder, id, keepAsTile: false);
        if (_byId.TryGetValue(id, out var a)) a.IsPinned = false;
        PruneEmptyGroups();
        SaveTilesInternal();
    }

    public void Resize(TileModel tile, TileSize size) { tile.Size = size; SaveTilesInternal(); }

    /// <summary>Sets the tile's background colour ("#RRGGBB"), or null to go back to the default.</summary>
    public void SetTileColor(TileModel tile, string? hex) { tile.UseAccent = false; tile.CustomColor = hex; SaveTilesInternal(); }

    public void SetTileAccent(TileModel tile) { tile.CustomColor = null; tile.UseAccent = true; SaveTilesInternal(); }

    public void ClearTiles()
    {
        foreach (var a in Apps) a.IsPinned = false;
        Groups.Clear();
        Groups.Add(new TileGroup("Pinned"));
        SaveTilesInternal();
    }

    public void CancelEdits()
    {
        foreach (var a in Apps) a.IsEditing = false;
        foreach (var g in Groups) g.IsEditing = false;
    }

    private List<GroupData> SnapshotGroups() =>
        Groups.Select(g => new GroupData(g.Name, g.Tiles.Select(TileToData).ToList())).ToList();

    private static TileData TileToData(TileModel t) =>
        new(t.AppId, t.Size, t.CustomColor, t.IsFolderTile ? t.FolderName : null, t.IsFolderTile ? new List<string>(t.ChildIds!) : null, t.Column, t.Row, t.UseAccent);

    private static TileModel TileFromData(TileData t)
    {
        var tile = t.Items == null
            ? new TileModel(t.Id, t.Size) { CustomColor = t.Color }
            : new TileModel(t.Id, t.Size) { CustomColor = t.Color, ChildIds = new List<string>(t.Items), FolderName = string.IsNullOrWhiteSpace(t.Name) ? "Folder" : t.Name! };
        tile.Column = t.X ?? -1;
        tile.Row = t.Y ?? -1;
        tile.UseAccent = t.Accent;
        return tile;
    }

    public void SaveTilesInternal() => Storage.Save("tiles.json", SnapshotGroups());

    // ---------- export / import (renames + tile layout) ----------

    public bool Export(string path) =>
        Storage.SaveTo(path, new Bundle(new Dictionary<string, string>(_renames), SnapshotGroups(), _hidden.ToList()));

    /// <summary>Merges renames from the file and replaces the tile layout with the file's.</summary>
    public bool Import(string path)
    {
        var b = Storage.LoadFrom<Bundle>(path);
        if (b == null) return false;

        if (b.Renames != null)
            foreach (var kv in b.Renames) _renames[kv.Key] = kv.Value;
        foreach (var a in Apps) a.CustomName = _renames.TryGetValue(a.Id, out var c) ? c : null;
        Storage.Save("renames.json", _renames);

        if (b.Hidden != null)
        {
            foreach (var id in b.Hidden) _hidden.Add(id);
            foreach (var a in Apps) a.IsHidden = _hidden.Contains(a.Id);
            Storage.Save("hidden.json", _hidden.ToList());
        }

        Groups.Clear();
        foreach (var g in b.Groups ?? new List<GroupData>())
        {
            var grp = new TileGroup(g.Name);
            foreach (var t in g.Tiles ?? new List<TileData>()) grp.Tiles.Add(TileFromData(t));
            Groups.Add(grp);
        }
        if (Groups.Count == 0) Groups.Add(new TileGroup("Pinned"));

        ResolveTiles();
        SaveTilesInternal();
        AppsView.Refresh();
        return true;
    }

    // ---------- launch ----------

    public void Launch(AppEntry a)
    {
        if (a.IsFolder) return;
        RecordUsage(a);
        try
        {
            // The same activation path the native Start uses: the AppsFolder item (works for Store apps, desktop apps and AutoGenerated entries).
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + a.Id) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Launch failed: " + a.Id, ex); }
    }

    public int UsageCount(string id) => _usage.TryGetValue(id, out var u) ? u.Count : 0;

    internal void RecordUsage(AppEntry a)
    {
        var cur = _usage.TryGetValue(a.Id, out var u) ? u : new UsageEntry(0, DateTime.MinValue);
        _usage[a.Id] = new UsageEntry(cur.Count + 1, DateTime.UtcNow);
        _usageDirty = true;
        _usageSaveTimer.Stop();
        _usageSaveTimer.Start();
    }

    /// <summary>Flush pending usage saves immediately (call on shutdown).</summary>
    public void FlushUsage()
    {
        _usageSaveTimer.Stop();
        if (_usageDirty)
        {
            _usageDirty = false;
            Storage.Save("usage.json", _usage);
            RefreshSections();
        }
    }
}
