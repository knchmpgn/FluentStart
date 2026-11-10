using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace FluentStart;

public enum SearchKind { App, Setting, Document, Folder, File, Web, Calculator }
public enum SearchFilter { All, Apps, Documents, Folders, Settings, Web }

/// <summary>A command shown for a result (detail pane buttons / context menu).</summary>
public sealed class SearchAction
{
    public SearchAction(string label, string glyph, Action execute) { Label = label; Glyph = glyph; Execute = execute; }
    public string Label { get; }
    public string Glyph { get; }
    public Action Execute { get; }
}

public sealed class SearchResult : Observable
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public SearchResult(SearchKind kind, string title)
    {
        Kind = kind;
        Title = title;
        Glyph = kind switch
        {
            SearchKind.Setting => "\uE713",
            SearchKind.Folder => "\uE8B7",
            SearchKind.Document => "\uE8A5",
            SearchKind.File => "\uE7C3",
            SearchKind.Web => "\uE774",
            SearchKind.Calculator => "\uE8EF",
            _ => "\uE71D"
        };
    }

    public SearchKind Kind { get; }
    public string Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Path { get; set; }
    private string? _previewText;
    public string? PreviewText { get => _previewText; set { if (Set(ref _previewText, value)) Raise(nameof(PreviewText)); } }
    private ImageSource? _previewImage;
    public ImageSource? PreviewImage { get => _previewImage; set { if (Set(ref _previewImage, value)) Raise(nameof(PreviewImage)); } }
    private bool _isLoadingPreview;
    public bool IsLoadingPreview { get => _isLoadingPreview; set { if (Set(ref _isLoadingPreview, value)) Raise(nameof(IsLoadingPreview)); } }
    public MediaElement? PreviewMedia { get; set; }
    public WebView2? PreviewWebView { get; set; }
    public string Glyph { get; set; }
    public double Score { get; set; }
    public AppEntry? App { get; set; }

    /// <summary>"Best match", "Apps", "Settings", "Documents", "Folders", "Files", "Web".</summary>
    public string Section { get; set; } = "";
    public int SectionOrder { get; set; }

    public Action Open { get; set; } = () => { };
    public List<SearchAction> Actions { get; } = new();

    /// <summary>Kind label shown in the detail pane ("App", "Document", "Setting", ...).</summary>
    public string KindLabel => Kind switch
    {
        SearchKind.App => "App",
        SearchKind.Setting => "Setting",
        SearchKind.Document => "Document",
        SearchKind.Folder => "Folder",
        SearchKind.File => "File",
        SearchKind.Web => "Web search",
        SearchKind.Calculator => "Calculator",
        _ => ""
    };

    /// <summary>Real icon when one exists (apps, files, folders); otherwise the UI shows <see cref="Glyph"/>.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (App != null) return App.Icon;
            if (_icon == null && !_iconRequested && !string.IsNullOrEmpty(Path) &&
                Kind is SearchKind.Document or SearchKind.Folder or SearchKind.File)
            {
                _iconRequested = true;
                string path = Path!;
                IconLoader.Enqueue(() =>
                {
                    var src = ShellIcons.GetByParsingName(path, 96);
                    if (src == null) return;
                    _icon = src;
                    Raise(nameof(Icon));
                    Raise(nameof(HasIcon));
                });
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon != null;
}
