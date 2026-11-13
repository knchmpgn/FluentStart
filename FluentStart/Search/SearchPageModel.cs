using System;

using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;


using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;


using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;

namespace FluentStart;

internal sealed class ResultComparer : IComparer
{
    public int Compare(object? x, object? y)
    {
        var a = (SearchResult)x!;
        var b = (SearchResult)y!;
        int s = a.SectionOrder.CompareTo(b.SectionOrder);
        if (s != 0) return s;
        int sc = b.Score.CompareTo(a.Score);
        return sc != 0 ? sc : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>
/// Drives the Windows 11-style search page: debounced query -> instant local results (apps, settings, calculator, path, web)
/// -> asynchronous file results (Everything when enabled and found, otherwise the Windows Search index).
/// </summary>
public sealed class SearchPageModel : Observable
{
    private readonly StartModel _model;
    private readonly Settings _settings;
    private readonly IFileSearchProvider _windowsSearch = new WindowsSearchProvider();
    private readonly EverythingSearchProvider _everything;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private string _query = "";
    private SearchFilter _filter = SearchFilter.All;
    private SearchResult? _selected;
    private bool _busy;
    private string _sourceNote = "";

    // Favorites
    private Dictionary<string, int> _favorites = new(StringComparer.OrdinalIgnoreCase);

    // Custom commands
    private List<CustomCommand> _customCommands = new();

    public SearchPageModel(StartModel model, Settings settings)
    {
        _model = model;
        _settings = settings;
        _everything = new EverythingSearchProvider(() => _settings.EverythingAutoStart);
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchResult.Section)));
        if (ResultsView is ListCollectionView lcv) lcv.CustomSort = new ResultComparer();
        LoadFavorites();
        LoadCustomCommands();
    }

    public RangeObservableCollection<SearchResult> Results { get; } = new();
    public ICollectionView ResultsView { get; }

    public bool IsActive => !string.IsNullOrWhiteSpace(_query);

    public SearchFilter Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) QueryChanged(_query); }
    }

    public SearchResult? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            _ = LoadPreviewAsync(value, _previewCts.Token);
        }
    }

    private async Task LoadPreviewAsync(SearchResult? r, CancellationToken cancellationToken = default)
    {
        if (r == null) return;
        if (string.IsNullOrEmpty(r.Path) || !File.Exists(r.Path)) { r.PreviewText = null; r.PreviewImage = null; r.PreviewMedia = null; r.PreviewWebView = null; return; }
        var ext = Path.GetExtension(r.Path).ToLowerInvariant();
        r.IsLoadingPreview = true;
        try
        {
            if (ext is ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" or ".cs" or ".xaml" or ".html" or ".htm")
            {
                using var sr = new StreamReader(r.Path);
                var buf = new char[1200];
                int n = await sr.ReadAsync(buf.AsMemory(0, buf.Length), cancellationToken);
                r.PreviewText = new string(buf, 0, n);
            }
            else if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tiff" or ".tif" or ".webp" or ".svg" or ".ico")
            {
                r.PreviewImage = await Task.Run(() => LoadImagePreview(r.Path!), cancellationToken);
            }
            else if (ext is ".pdf")
            {
                r.PreviewImage = await Task.Run(() => LoadPdfPreview(r.Path!), cancellationToken);
            }
            else if (ext is ".mp4" or ".webm" or ".avi" or ".mkv" or ".mov" or ".mp3" or ".wav" or ".flac" or ".m4a")
            {
                r.PreviewMedia = new MediaElement
                {
                    Source = new Uri(r.Path),
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    ScrubbingEnabled = true,
                    Volume = 0.5
                };
                r.PreviewMedia.Play();
            }
        }
        catch (OperationCanceledException) { }
        catch { r.PreviewText = null; r.PreviewImage = null; r.PreviewMedia = null; }
        finally { r.IsLoadingPreview = false; }
    }

    private static ImageSource? LoadImagePreview(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".svg")
            {
                var drawingImage = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return drawingImage;
            }
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 320;
            bitmap.DecodePixelHeight = 240;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static ImageSource? LoadPdfPreview(string path)
    {
        try
        {
            var drawingGroup = new DrawingGroup();
            var typeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var formattedText = new FormattedText("\uE8A5", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 80, Brushes.Orange, 1.0);
            drawingGroup.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 320, 240))));
            drawingGroup.Children.Add(new GeometryDrawing(Brushes.Orange, null, formattedText.BuildGeometry(new Point(120, 80))));
            var drawingImage = new DrawingImage(drawingGroup);
            drawingImage.Freeze();
            return drawingImage;
        }
        catch { return null; }
    }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(IsEmpty)); } }
    public bool IsEmpty => IsActive && !_busy && Results.Count == 0;
    public string EmptyText => $"No results for \u201C{_query.Trim()}\u201D";

    /// <summary>"Files: Everything 1.5.0.1383a (alpha/beta) - running" / "Files: Windows Search" - shown under the filter chips.</summary>
    public string SourceNote { get => _sourceNote; private set => Set(ref _sourceNote, value); }

    public async void QueryChanged(string? text)
    {
        _cts?.Cancel();
        _query = text ?? "";
        Raise(nameof(IsActive));
        Raise(nameof(EmptyText));

        if (!IsActive)
        {
            var suggestions = BuildSuggestions();
            Apply(suggestions);
            IsBusy = false;
            SourceNote = "";
            Raise(nameof(IsEmpty));
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        try
        {
            await RunAsync(_query.Trim(), cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Search failed", ex); IsBusy = false; }
    }

    private async Task RunAsync(string q, CancellationToken ct)
    {
        var local = BuildLocal(q, _filter);
        Apply(local);

        bool wantFiles = _settings.SearchFiles && q.Length >= 2 &&
                         _filter is SearchFilter.All or SearchFilter.Documents or SearchFilter.Folders;
        if (!wantFiles) { IsBusy = false; SourceNote = ""; return; }

        IsBusy = true;
        List<FileHit> hits = new();
        string source = _windowsSearch.Name;
        try
        {
            // App/settings results above are already on screen; only the file search waits for typing to settle
            // (Everything answers in a few ms, Windows Search needs more breathing room).
            await Task.Delay(_settings.UseEverything ? 40 : 120, ct);
            if (_settings.UseEverything)
            {
                hits = await _everything.SearchAsync(q, _filter, 30, ct);
                if (_everything.Available) source = _everything.Name;
                else hits = await _windowsSearch.SearchAsync(q, _filter, 30, ct);   // Everything missing -> fall back
            }
            else hits = await _windowsSearch.SearchAsync(q, _filter, 30, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("File search failed", ex); }

        ct.ThrowIfCancellationRequested();
        SourceNote = "Files: " + source + (_settings.UseEverything && !_everything.Available ? " (Everything not found)" : "");
        Apply(local.Concat(hits.Select((h, i) => FromHit(h, i))).ToList());
        IsBusy = false;
    }

    // ---------- local providers ----------

    internal List<SearchResult> BuildLocal(string q, SearchFilter filter)
    {
        var list = new List<SearchResult>();
        bool all = filter == SearchFilter.All;

        // Calculator
        if (all && Calculator.TryEvaluate(q, out string value))
        {
            var calc = new SearchResult(SearchKind.Calculator, "= " + value) { Subtitle = $"{q.Trim().TrimEnd('=')} = {value}  \u00B7  Enter copies the result", Score = 1000 };
            calc.Open = () => TryCopy(value);
            calc.Actions.Add(new SearchAction("Copy result", "\uE8C8", calc.Open));
            list.Add(calc);
        }

        // Unit converter
        if (all && UnitConverter.TryConvert(q, out var conversions))
        {
            var uc = new SearchResult(SearchKind.Calculator, string.Join("  \u00B7  ", conversions)) { Subtitle = "Unit conversion  \u00B7  Enter copies first", Score = 900 };
            uc.Open = () => TryCopy(conversions.FirstOrDefault() ?? "");
            uc.Actions.Add(new SearchAction("Copy", "\uE8C8", uc.Open));
            foreach (var c in conversions) uc.Actions.Add(new SearchAction(c, "\uE8C8", () => TryCopy(c)));
            list.Add(uc);
        }

        // Path
        if (all && (Directory.Exists(q) || File.Exists(q)))
        {
            bool dir = Directory.Exists(q);
            var path = new SearchResult(dir ? SearchKind.Folder : SearchKind.File, Path.GetFileName(q.TrimEnd('\\')) is { Length: > 0 } n ? n : q)
            { Subtitle = q, Path = q, Score = 150 };
            AddFileActions(path);
            list.Add(path);
        }

        // Apps
        if (all || filter == SearchFilter.Apps)
            foreach (var r in AppResults(q).Take(12)) list.Add(r);

        // Custom commands
        if (all || filter == SearchFilter.Apps)
            foreach (var r in CustomCommandResults(q).Take(12)) list.Add(r);

        // Settings
        if (all || filter == SearchFilter.Settings)
            foreach (var match in SettingsCatalog.All
                         .Select(entry => (entry, score: SettingsCatalog.Score(q, entry))).Where(m => m.score > 0)
                         .OrderByDescending(m => m.score).Take(8))
            {
                var r = new SearchResult(SearchKind.Setting, match.entry.Title)
                    { Subtitle = "Settings \u203A " + match.entry.Category, Score = match.score * 0.9 };
                string uri = match.entry.Uri;
                r.Open = () => ShellOpen(uri);
                r.Actions.Add(new SearchAction("Open", "\uE8A7", r.Open));
                list.Add(r);
            }

        // Web search
        if ((all || filter == SearchFilter.Web) && !string.IsNullOrWhiteSpace(_settings.WebSearchUrl))
        {
            var web = new SearchResult(SearchKind.Web, $"Search the web for \u201C{q}\u201D") { Subtitle = "Opens your default browser", Score = 5 };
            string url = _settings.WebSearchUrl.Replace("{0}", Uri.EscapeDataString(q));
            web.Open = () => ShellOpen(url);
            web.Actions.Add(new SearchAction("Open in browser", "\uE774", web.Open));
            web.Actions.Add(new SearchAction("Open in WebView", "\uE774", () => OpenWebView(url)));
            list.Add(web);
        }

        // Quick Tools
        if (all)
        {
            var tools = BuildQuickTools(q);
            list.AddRange(tools);
        }

        return list;
    }

    internal List<SearchResult> BuildSuggestions()
    {
        var list = new List<SearchResult>();
        foreach (var app in _model.Apps
            .Where(a => _settings.ShowHiddenApps || !a.IsHidden)
            .OrderByDescending(a => _model.UsageCount(a.Id))
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(12))
        {
            var r = new SearchResult(SearchKind.App, app.DisplayName)
            {
                App = app,
                Score = 50,
                Subtitle = app.FolderPath != null ? "App \u00B7 " + app.FolderPath : "App",
                Path = app.Path
            };
            var captured = app;
            r.Open = () => _model.Launch(captured);
            r.Actions.Add(new SearchAction("Open", "\uE8A7", r.Open));
            if (!string.IsNullOrEmpty(app.Path))
            {
                string path = app.Path!;
                r.Actions.Add(new SearchAction("Open file location", "\uE838", () => Reveal(path)));
            }
            list.Add(r);
        }
        // Add custom commands to suggestions
        foreach (var cmd in _customCommands.Take(8))
        {
            var r = new SearchResult(SearchKind.App, cmd.Name) { Subtitle = "Custom command", Score = 50 };
            var captured = cmd;
            r.Open = () => ExecuteCustomCommand(captured);
            r.Actions.Add(new SearchAction("Run", "\uE718", r.Open));
            r.Actions.Add(new SearchAction("Run as administrator", "\uE7EF", () => ExecuteCustomCommand(captured, true)));
            list.Add(r);
        }
        return list;
    }

    private IEnumerable<SearchResult> AppResults(string q)
    {
        foreach (var app in _model.Apps)
        {
            if (app.IsHidden && !_settings.ShowHiddenApps) continue;
            double score = SearchScorer.Score(q, app.DisplayName, app.OriginalName);
            if (score <= 0) continue;
            if (app.IsPinned) score += 0.5;
            // Boost favorites
            if (_favorites.TryGetValue(app.Id, out int fav)) score += fav * 0.1;

            var r = new SearchResult(SearchKind.App, app.DisplayName)
            {
                App = app,
                Score = score,
                Subtitle = app.FolderPath != null ? "App \u00B7 " + app.FolderPath : "App",
                Path = app.Path
            };
            var captured = app;
            r.Open = () => { _model.Launch(captured); RecordFavorite(captured.Id); };
            r.Actions.Add(new SearchAction("Open", "\uE8A7", r.Open));
            if (!string.IsNullOrEmpty(app.Path))
            {
                string path = app.Path!;
                r.Actions.Add(new SearchAction("Run as administrator", "\uE7EF", () => RunElevated(path)));
                r.Actions.Add(new SearchAction("Open file location", "\uE838", () => Reveal(path)));
            }
            r.Actions.Add(new SearchAction(app.IsPinned ? "Unpin from Start" : "Pin to Start", "\uE718",
                () => { if (captured.IsPinned) _model.Unpin(captured.Id); else _model.Pin(captured); }));
            yield return r;
        }
    }

    private IEnumerable<SearchResult> CustomCommandResults(string q)
    {
        foreach (var cmd in _customCommands)
        {
            double score = SearchScorer.Score(q, cmd.Name, cmd.Name);
            if (score <= 0) continue;
            if (_favorites.TryGetValue("cmd:" + cmd.Name, out int fav)) score += fav * 0.1;

            var r = new SearchResult(SearchKind.App, cmd.Name) { Subtitle = "Custom command", Score = score };
            var captured = cmd;
            r.Open = () => { ExecuteCustomCommand(captured); RecordFavorite("cmd:" + captured.Name); };
            r.Actions.Add(new SearchAction("Run", "\uE718", r.Open));
            r.Actions.Add(new SearchAction("Run as administrator", "\uE7EF", () => ExecuteCustomCommand(captured, true)));
            yield return r;
        }
    }

    private SearchResult FromHit(FileHit h, int index)
    {
        string name = Path.GetFileName(h.FullPath.TrimEnd('\\'));
        if (name.Length == 0) name = h.FullPath;
        var kind = h.IsFolder ? SearchKind.Folder : IsDocument(h.FullPath) ? SearchKind.Document : SearchKind.File;
        var r = new SearchResult(kind, name)
        {
            Path = h.FullPath,
            Subtitle = Path.GetDirectoryName(h.FullPath.TrimEnd('\\')) ?? h.FullPath,
            Score = 40 - index * 0.5
        };
        AddFileActions(r);
        return r;
    }

    private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp", ".csv" };

    internal static bool IsDocument(string path) => DocExt.Contains(Path.GetExtension(path));

    private static void AddFileActions(SearchResult r)
    {
        string path = r.Path!;
        bool isDir = Directory.Exists(path);
        r.Open = () => ShellOpen(path);
        r.Actions.Add(new SearchAction("Open", "\uE8A7", r.Open));
        if (!isDir && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
            r.Actions.Add(new SearchAction("Run as administrator", "\uE7EF", () => RunElevated(path)));
        r.Actions.Add(new SearchAction("Open file location", "\uE838", () => Reveal(path)));
        if (isDir)
            r.Actions.Add(new SearchAction("Open in terminal", "\uE756", () => OpenTerminal(path)));
        r.Actions.Add(new SearchAction("Copy name", "\uE8C8", () => TryCopy(Path.GetFileName(path.TrimEnd('\\')))));
        r.Actions.Add(new SearchAction("Create shortcut", "\uE718", () => { try { MainWindow.CreateShortcutFromPath(path); } catch { } }));
        r.Actions.Add(new SearchAction("Copy file", "\uE8C8", () => TryCopyFile(path)));
        r.Actions.Add(new SearchAction("Properties", "\uE946", () => ShowProperties(path)));
    }

    // ---------- result list ----------

    private static string SectionFor(SearchKind k) => k switch
    {
        SearchKind.Calculator => "Calculator",
        SearchKind.App => "Apps",
        SearchKind.Setting => "Settings",
        SearchKind.Document => "Documents",
        SearchKind.Folder => "Folders",
        SearchKind.File => "Files",
        _ => "Search the web"
    };

    private static int OrderFor(string section) => section switch
    {
        "Best match" => 0, "Calculator" => 1, "Apps" => 2, "Settings" => 3,
        "Documents" => 4, "Folders" => 5, "Files" => 6, _ => 7
    };

    private void Apply(List<SearchResult> items)
    {
        var ordered = items.OrderByDescending(r => r.Score).ToList();
        SearchResult? best = _filter == SearchFilter.All
            ? ordered.FirstOrDefault(r => r.Kind != SearchKind.Web && r.Score >= 30)
            : null;

        foreach (var r in ordered)
        {
            r.Section = ReferenceEquals(r, best) ? "Best match" : SectionFor(r.Kind);
            r.SectionOrder = OrderFor(r.Section);
        }

        string? keepTitle = _selected?.Title, keepPath = _selected?.Path;
        Results.ReplaceAll(ordered);

        Selected = ordered.FirstOrDefault(r => r.Title == keepTitle && r.Path == keepPath)
                   ?? ResultsView.Cast<SearchResult>().FirstOrDefault();
        Raise(nameof(IsEmpty));
    }

    // ---------- actions ----------

    internal static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open failed: " + target, ex); }
    }

    private static void OpenTerminal(string path)
    {
        try
        {
            string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt, "-d \"" + path + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("cmd.exe", "/k cd /d \"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Open terminal failed", ex); }
    }

    private static void TryCopyFile(string path)
    {
        try
        {
            var c = new StringCollection();
            c.Add(path);
            Clipboard.SetFileDropList(c);
        }
        catch (Exception ex) { Log.Warn("Clipboard file copy failed", ex); }
    }

    private static void ShowProperties(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { Verb = "properties", UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Properties failed", ex); }
    }

    private static void RunElevated(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); }
        catch (Exception ex) { Log.Warn("Run as administrator failed: " + path, ex); }
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Reveal failed: " + path, ex); }
    }

    private static void TryCopy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn("Clipboard failed", ex); }
    }

    // ---------- Quick Tools ----------

    private List<SearchResult> BuildQuickTools(string q)
    {
        var list = new List<SearchResult>();

        // /ip: a local lookup of this PC's network adapters (no network request is made).
        if (q.StartsWith("/ip", StringComparison.OrdinalIgnoreCase) && (q.Length == 3 || char.IsWhiteSpace(q[3])))
            list.AddRange(BuildIpResults());

        return list;
    }

    /// <summary>One result per active adapter (Wi-Fi, Ethernet, VPN...): IP address, mask, gateway and adapter description. Enter copies the IP.</summary>
    private static List<SearchResult> BuildIpResults()
    {
        var results = new List<SearchResult>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var v6 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                string? ip = (v4 ?? v6)?.Address.ToString();
                if (ip == null) continue;

                var parts = new List<string> { nic.Name };
                if (v4?.IPv4Mask != null) parts.Add("mask " + v4.IPv4Mask);
                var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
                if (gateway != null) parts.Add("gateway " + gateway);
                if (!string.IsNullOrWhiteSpace(nic.Description)) parts.Add(nic.Description);

                var r = new SearchResult(SearchKind.Calculator, ip) { Subtitle = string.Join("  \u00B7  ", parts) + "  \u00B7  Enter copies the IP", Score = 800 };
                string copy = ip;
                r.Open = () => TryCopy(copy);
                r.Actions.Add(new SearchAction("Copy IP address", "\uE8C8", r.Open));
                if (v4?.IPv4Mask != null) r.Actions.Add(new SearchAction("Copy subnet mask", "\uE8C8", () => TryCopy(v4.IPv4Mask.ToString())));
                if (gateway != null) r.Actions.Add(new SearchAction("Copy gateway", "\uE8C8", () => TryCopy(gateway.ToString())));
                results.Add(r);
            }
        }
        catch (Exception ex) { Log.Warn("Network interface lookup failed", ex); }

        if (results.Count == 0)
            results.Add(new SearchResult(SearchKind.Calculator, "No active network connection") { Subtitle = "No Wi-Fi, Ethernet or VPN adapter is up", Score = 800 });
        return results;
    }

    private void OpenWebView(string url)
    {
        var window = new Window
        {
            Title = "Web Search",
            Width = 1000,
            Height = 700,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = System.Windows.Media.Brushes.Transparent
        };
        var grid = new Grid();
        var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
        grid.Children.Add(webView);
        window.Content = grid;
        window.Show();
        _ = InitializeWebView(webView, url);
    }

    private async Task InitializeWebView(Microsoft.Web.WebView2.Wpf.WebView2 webView, string url)
    {
        await webView.EnsureCoreWebView2Async(null);
        webView.CoreWebView2.Navigate(url);
    }

    // ---------- Custom Commands ----------

    public void AddCustomCommand(CustomCommand cmd)
    {
        _customCommands.Add(cmd);
        SaveCustomCommands();
    }

    public void RemoveCustomCommand(string name)
    {
        _customCommands.RemoveAll(c => c.Name == name);
        SaveCustomCommands();
    }

    private void ExecuteCustomCommand(CustomCommand cmd, bool elevated = false)
    {
        try
        {
            string shellExe = cmd.Shell switch
            {
                "cmd" => "cmd.exe",
                "powershell" => "powershell.exe",
                "pwsh" => "pwsh.exe",
                _ => cmd.CustomShell ?? "cmd.exe"
            };
            string args = cmd.Shell switch
            {
                "cmd" => "/c " + cmd.Command,
                "powershell" => "-Command " + cmd.Command,
                "pwsh" => "-Command " + cmd.Command,
                _ => cmd.ShellArgs?.Replace("{command}", cmd.Command) ?? cmd.Command
            };
            var psi = new ProcessStartInfo(shellExe, args)
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : null
            };
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn("Custom command failed", ex); }
    }

    private void LoadFavorites()
    {
        try
        {
            var data = Storage.Load<Dictionary<string, int>>("favorites.json");
            if (data != null) _favorites = data;
        }
        catch { }
    }

    private void SaveFavorites()
    {
        try { Storage.Save("favorites.json", _favorites); } catch { }
    }

    private void RecordFavorite(string id)
    {
        if (!_settings.LearnFavorites) return;
        if (_favorites.TryGetValue(id, out int count)) _favorites[id] = count + 1;
        else _favorites[id] = 1;
        SaveFavorites();
    }

    private void LoadCustomCommands()
    {
        try
        {
            var data = Storage.Load<List<CustomCommand>>("custom_commands.json");
            if (data != null) _customCommands = data;
        }
        catch { }
    }

    private void SaveCustomCommands()
    {
        try { Storage.Save("custom_commands.json", _customCommands); } catch { }
    }
}

// Supporting types
public sealed class CustomCommand
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "\uE712";
    public string Shell { get; set; } = "cmd"; // cmd, powershell, pwsh, custom
    public string? CustomShell { get; set; }
    public string? ShellArgs { get; set; }
    public string Command { get; set; } = "";
    public bool HideTerminal { get; set; } = false;
}