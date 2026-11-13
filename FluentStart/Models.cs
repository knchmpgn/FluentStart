using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentStart.Native;

namespace FluentStart;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed class AppEntry : Observable
{
    private string? _custom;
    private bool _isEditing, _isPinned, _iconRequested;
    private string _editText = "";
    private ImageSource? _icon;
    private Brush? _tileBrush;
    private bool _isHidden;

    public AppEntry(string id, string name, bool isFolder = false) { Id = id; OriginalName = name; IsFolder = isFolder; }

    public string Id { get; }

    /// <summary>True for the synthetic entry behind a Start-menu folder row (never pinned, renamed or launched).</summary>
    public bool IsFolder { get; }

    /// <summary>Shortcut file to launch (null for Store/AppsFolder apps, which launch via shell:AppsFolder).</summary>
    public string? Path { get; internal set; }

    /// <summary>Start-menu folder path using display names ("Microsoft Office" or "Windows Tools\Sub"); null = top level.</summary>
    public string? FolderPath { get; internal set; }
    public string OriginalName { get; private set; }

    /// <summary>User-chosen display name. Never written back to the shortcut/package.</summary>
    public string? CustomName
    {
        get => _custom;
        set { if (Set(ref _custom, value)) { _group = null; Raise(nameof(DisplayName)); Raise(nameof(Group)); Raise(nameof(IsRenamed)); } }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_custom) ? OriginalName : _custom!;
    public bool IsRenamed => !string.IsNullOrWhiteSpace(_custom);

    private string? _group;

    /// <summary>Letter heading: A-Z, "#" for names starting with a digit, "&amp;" for other symbols (as in the native list).</summary>
    public string Group => _group ??= ComputeGroup();

    private string ComputeGroup()
    {
        string s = DisplayName.TrimStart();
        if (s.Length == 0) return "&";
        char c = char.ToUpperInvariant(s[0]);
        if (char.IsLetter(c)) return c.ToString();
        return char.IsDigit(c) ? "#" : "&";
    }

    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    public bool IsPinned
    {
        get => _isPinned;
        set { if (Set(ref _isPinned, value)) Raise(nameof(PinMenuText)); }
    }
    public string PinMenuText => _isPinned ? "Unpin from Start" : "Pin to Start";

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconRequested && !IsFolder) { _iconRequested = true; IconLoader.Enqueue(this); }
            return _icon;
        }
        set { _icon = value; Raise(); }
    }

    /// <summary>Hidden from the Start list/search (overlay only; the shortcut/package is untouched).</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set { if (Set(ref _isHidden, value)) Raise(nameof(HideMenuText)); }
    }
    public string HideMenuText => _isHidden ? "Show" : "Hide";

    /// <summary>Tile color derived from the icon (null for grayscale/unknown icons).</summary>
    public Brush? TileBrush
    {
        get => _tileBrush;
        internal set { if (Set(ref _tileBrush, value)) Raise(nameof(HasIconColor)); }
    }
    public bool HasIconColor => _tileBrush != null;

    internal void UpdateOriginal(string name)
    {
        if (OriginalName == name) return;
        OriginalName = name; _group = null; Raise(nameof(OriginalName)); Raise(nameof(DisplayName)); Raise(nameof(Group));
    }
}

/// <summary>
/// One row in the app list: an app (main row), a Start-menu folder, or a "Most used"/"Recently added" shortcut to an app.
/// App rows can have a <see cref="Parent"/> folder; folders can nest.
/// </summary>
public sealed class ListRow : Observable
{
    private bool _expanded;
    private bool _flat;

    public ListRow(AppEntry app, string? section, int sortOrder, ListRow? parent = null, bool isFolder = false)
    {
        App = app; Section = section; SortOrder = sortOrder; Parent = parent; IsFolder = isFolder;
        Depth = parent == null ? 0 : parent.Depth + 1;
        parent?.Children.Add(this);
        app.PropertyChanged += OnAppChanged;
    }

    public AppEntry App { get; }
    public string? Section { get; }
    public int SortOrder { get; }
    public ListRow? Parent { get; }
    public bool IsFolder { get; }
    public int Depth { get; }

    /// <summary>Rows directly inside this folder (empty for apps).</summary>
    public List<ListRow> Children { get; } = new();

    /// <summary>A normal app row (not a folder, not a section shortcut).</summary>
    public bool IsMain => Section == null && !IsFolder;

    /// <summary>0 = Most used, 1 = Recently added, 2 = alphabetical.</summary>
    public int Rank => Section == null ? 2 : (Section == StartModel.MostUsed ? 0 : 1);

    public ListRow Top
    {
        get { var r = this; while (r.Parent != null) r = r.Parent; return r; }
    }

    /// <summary>Letter header the row sits under; children share their top-level folder's letter.</summary>
    public string Group => Section ?? Top.App.Group;
    public string SortName => App.DisplayName;

    private string[]? _chain;

    /// <summary>Names from the top-level folder down to this row (cached; the comparator runs O(n log n) times).</summary>
    public string[] SortChain => _chain ??= BuildChain();

    private string[] BuildChain()
    {
        var l = new List<string>(Depth + 1);
        for (var p = this; p != null; p = p.Parent) l.Add(p.SortName);
        l.Reverse();
        return l.ToArray();
    }

    /// <summary>Inline rename editor only shows on the main row (never duplicated in sections, never on folders).</summary>
    public bool ShowEditor => IsMain && App.IsEditing;

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) Raise(nameof(Chevron)); }
    }

    /// <summary>Segoe Fluent chevron: down when collapsed, up when expanded.</summary>
    public string Chevron => _expanded ? "\uE70E" : "\uE70D";

    /// <summary>While searching the list is flat, so indentation is dropped.</summary>
    public bool FlatMode
    {
        get => _flat;
        set { if (Set(ref _flat, value)) Raise(nameof(Indent)); }
    }

    public Thickness Indent => new(_flat ? 0 : Depth * 24, 0, 0, 0);

    /// <summary>True when every ancestor folder is expanded.</summary>
    public bool IsTreeVisible
    {
        get { for (var p = Parent; p != null; p = p.Parent) if (!p.IsExpanded) return false; return true; }
    }

    /// <summary>Set by StartModel before the view refreshes while searching.</summary>
    public int SearchScore { get; set; }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppEntry.DisplayName):
                _chain = null;
                Raise(nameof(SortName)); Raise(nameof(Group));
                break;
            case nameof(AppEntry.IsEditing):
                Raise(nameof(ShowEditor));
                break;
        }
    }
}

public enum TileSize { Small, Medium, Wide, Large }

public sealed class TileModel : Observable
{
    private AppEntry? _app;
    private TileSize _size;
    private int _column;
    private int _row;

    public TileModel(string appId, TileSize size) { AppId = appId; _size = size; }

    public string AppId { get; }

    /// <summary>Ids of the apps inside a folder tile (dropped tile-on-tile); null for an ordinary tile.</summary>
    public List<string>? ChildIds { get; set; }
    public bool IsFolderTile => ChildIds != null;
    public string FolderName { get; set; } = "Folder";

    public AppEntry? App
    {
        get => _app;
        set { if (Set(ref _app, value)) Raise(nameof(IsResolved)); }
    }
    public bool IsResolved => _app != null;

    private string? _customColor;
    private bool _useAccent;

    /// <summary>User-chosen tile background as "#RRGGBB", or null for the default (system accent / icon colour).</summary>
    public string? CustomColor
    {
        get => _customColor;
        set
        {
            if (!Set(ref _customColor, TileColors.Normalize(value))) return;
            if (_customColor != null) _useAccent = false;
            Raise(nameof(HasCustomColor)); Raise(nameof(TileBackground)); Raise(nameof(LabelBrush)); Raise(nameof(UseAccent));
        }
    }
    public bool HasCustomColor => _customColor != null;

    /// <summary>When true, the tile uses the live system accent colour as its background.</summary>
    public bool UseAccent
    {
        get => _useAccent;
        set
        {
            if (!Set(ref _useAccent, value)) return;
            if (value) { _customColor = null; Raise(nameof(HasCustomColor)); Raise(nameof(TileBackground)); }
            Raise(nameof(LabelBrush));
        }
    }
    public Brush? TileBackground => TileColors.BrushOf(_customColor);
    /// <summary>Label colour that stays readable on <see cref="TileBackground"/>.</summary>
    public Brush LabelBrush => TileColors.LabelOn(_customColor);

    public TileSize Size
    {
        get => _size;
        set
        {
            if (!Set(ref _size, value)) return;
            Raise(nameof(Width)); Raise(nameof(Height)); Raise(nameof(IconSize)); Raise(nameof(ShowLabel)); Raise(nameof(LabelHidden));
        }
    }

    /// <summary>Grid column (in small cells) where this tile is placed.</summary>
    public int Column
    {
        get => _column;
        set => Set(ref _column, value);
    }

    /// <summary>Grid row (in small cells) where this tile is placed.</summary>
    public int Row
    {
        get => _row;
        set => Set(ref _row, value);
    }

    /// <summary>The "Show app names on tiles" setting (set by <see cref="StartModel.ShowTileNames"/>).</summary>
    public static bool NamesVisible { get; set; } = true;

    /// <summary>Re-evaluates the label after <see cref="NamesVisible"/> changed.</summary>
    public void RefreshLabel() { Raise(nameof(ShowLabel)); Raise(nameof(LabelHidden)); }

    public double Width => _size switch { TileSize.Small => 48, TileSize.Medium => 100, _ => 204 };
    public double Height => _size switch { TileSize.Small => 48, TileSize.Large => 204, _ => 100 };
    public double IconSize => _size switch { TileSize.Small => 28, TileSize.Large => 72, _ => 44 };
    public bool ShowLabel => NamesVisible && _size != TileSize.Small;

    /// <summary>True when the tile shows no name (small tile, or names turned off): the name is then offered as a tooltip.</summary>
    public bool LabelHidden => !ShowLabel;
}

public sealed class TileGroup : Observable
{
    private string _name;
    private bool _isEditing;
    private string _editText = "";

    public TileGroup(string name) { _name = name; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    private bool _isDropTarget;
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public ObservableCollection<TileModel> Tiles { get; } = new();
}

/// <summary>Runs icon work on a single STA thread (shell COM wants STA). Used for app icons and search-result icons.</summary>
internal static class IconLoader
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static Thread? _thread;
    private static readonly object Gate = new();

    public static void Enqueue(Action work)
    {
        lock (Gate)
        {
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "IconLoader" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        Queue.Add(work);
    }

    public static void Enqueue(AppEntry a) => Enqueue(() => LoadAppIcon(a));

    private static void Run()
    {
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Log.WarnOnce("icon", "Icon work failed", ex); }
        }
    }

    private static void LoadAppIcon(AppEntry a)
    {
        var src = IconCache.TryLoad(a.Id);
        if (src == null)
        {
            src = ShellIcons.GetForEntry(a, ShellIcons.PreferredSize);
            if (src != null) IconCache.Save(a.Id, src);
        }
        if (src == null) return;

        if (ColorExtractor.Dominant(src) is Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            a.TileBrush = brush;
        }
        a.Icon = src;
    }
}

internal static class IconCache
{
    private static string Dir => Path.Combine(Storage.DataDir, "icons");

    // Bump when icons are produced differently: older, lower-resolution cache files are then discarded once.
    private const string CacheVersion = "3";
    private static bool _versionChecked;

    private static string PathFor(string id)
    {
        EnsureVersion();
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()));
        return Path.Combine(Dir, Convert.ToHexString(h) + ".png");
    }

    private static void EnsureVersion()
    {
        if (_versionChecked) return;
        _versionChecked = true;
        try
        {
            string marker = Path.Combine(Dir, "version.txt");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == CacheVersion) return;
            if (Directory.Exists(Dir)) foreach (var f in Directory.GetFiles(Dir, "*.png")) File.Delete(f);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(marker, CacheVersion);
        }
        catch (Exception ex) { Log.WarnOnce("iconver", "Icon cache version check failed", ex); }
    }

    public static BitmapSource? TryLoad(string id)
    {
        try
        {
            string p = PathFor(id);
            if (!File.Exists(p)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(p);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static void Save(string id, BitmapSource src)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(PathFor(id));
            enc.Save(fs);
        }
        catch (Exception ex) { Log.WarnOnce("iconsave", "Icon cache write failed", ex); }
    }

    public static void Clear()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
        catch (Exception ex) { Log.Warn("Icon cache clear failed", ex); }
    }
}

internal static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    /// <summary>The icon the native Start shows: the AppsFolder item's own image (no shortcut-arrow overlay).</summary>
    public static BitmapSource? GetForEntry(AppEntry a, int size) =>
        GetByParsingName("shell:AppsFolder\\" + a.Id, size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static BitmapSource? GetFolderIcon(int size, bool open = false)
    {
        try
        {
            var shfi = new SHFILEINFO();
            const uint SHGFI_ICON = 0x100;
            const uint SHGFI_USEFILEATTRIBUTES = 0x10;
            const uint SHGFI_OPENICON = 0x2;
            const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES;
            if (open) flags |= SHGFI_OPENICON;
            IntPtr r = SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            try
            {
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return null; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static int _preferredSize;

    /// <summary>
    /// Pixel size to ask the shell for. The largest place an app icon is drawn is a Large tile (72 DIP), so ask for enough pixels
    /// for that at this display's scale (128, 192 or 256) instead of a fixed 64: a 64 px icon on a 72 DIP tile is upscaled and soft.
    /// </summary>
    public static int PreferredSize
    {
        get
        {
            if (_preferredSize != 0) return _preferredSize;
            double scale = 1.0;
            try { scale = GetDpiForSystem() / 96.0; } catch { }
            int need = (int)Math.Ceiling(72 * Math.Max(1.0, scale));
            return _preferredSize = need <= 128 ? 128 : need <= 192 ? 192 : 256;
        }
    }

    public static BitmapSource? GetByParsingName(string parsingName, int size)
    {
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero,
                typeof(IShellItemImageFactory).GUID, out var factory);
            // 0x4 = ICONONLY, 0x1 = BIGGERSIZEOK: the closest real icon at least this big, never a stretched smaller one.
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x5, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return DownscaleIfLarger(bs, size);
            }
            finally { NativeMethods.DeleteObject(hbm); }
        }
        catch { return null; }
    }

    /// <summary>The shell may hand back a 256 px icon for a smaller request; keep memory in check with one high-quality downscale.</summary>
    private static BitmapSource DownscaleIfLarger(BitmapSource src, int size)
    {
        if (src.PixelWidth <= size * 3 / 2) return src;
        try
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, size, size));
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch { return src; }
    }
}
