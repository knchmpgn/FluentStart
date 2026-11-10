using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace FluentStart;

/// <summary>A pre-configured folder that can be shown as a button on the Start menu's left rail.</summary>
public sealed record RailFolderDef(string Id, string Name, string Description, string ShellUri, string Group);

/// <summary>A rail folder button as the rail draws it.</summary>
public sealed class RailFolderItem
{
    public string Name { get; init; } = "";
    public string ShellUri { get; init; } = "";
    public ImageSource? Icon { get; init; }
    /// <summary>Fallback glyph (Segoe Fluent "Folder") when the shell has no icon for the folder; null when <see cref="Icon"/> is set.</summary>
    public string? Glyph { get; init; }
}

/// <summary>One row of the Navigation settings page.</summary>
public sealed class RailChoice : Observable
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Group { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string? Glyph { get; init; }

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}

/// <summary>
/// The common folders of the user profile. Icons are the real Windows folder icons (each known folder has its own: the Downloads
/// arrow, the Music note, ...), asked from the shell so they always match the system. Folders are opened through their "shell:"
/// names, which follow a folder if the user has moved it. Documents and Pictures keep their original dedicated settings.
/// </summary>
public static class RailFolderCatalog
{
    public const string FolderGlyph = "\uE8B7";   // Segoe Fluent "Folder"

    public static readonly RailFolderDef[] Extra =
    {
        new("downloads",  "Downloads",       "Files you have downloaded",                       "shell:Downloads",       "Folders"),
        new("music",      "Music",           "Your music library",                              "shell:My Music",        "Folders"),
        new("videos",     "Videos",          "Your videos and recordings",                      "shell:My Video",        "Folders"),
        new("desktop",    "Desktop",         "Everything on your desktop",                      "shell:Desktop",         "Folders"),
        new("profile",    "Personal Folder", "Your user profile folder with all the folders above", "shell:UsersFilesFolder", "Additional Folders"),
        new("onedrive",   "OneDrive",        "Your OneDrive folder (if OneDrive is set up)",    "shell:OneDrive",        "Additional Folders"),
    };

    private static readonly Dictionary<string, ImageSource?> Icons = new();

    /// <summary>The shell's icon for a "shell:" folder, cached. Null if the shell has none.</summary>
    internal static ImageSource? IconOf(string shellUri, int size = 64)
    {
        lock (Icons)
        {
            if (Icons.TryGetValue(shellUri, out var cached)) return cached;
            ImageSource? icon = null;
            try { icon = ShellIcons.GetByParsingName(shellUri, size); } catch { }
            Icons[shellUri] = icon;
            return icon;
        }
    }

    /// <summary>The enabled folder buttons, in catalog order.</summary>
    internal static List<RailFolderItem> Enabled(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var list = new List<RailFolderItem>();
        foreach (var def in Extra.Where(d => set.Contains(d.Id)))
        {
            var icon = IconOf(def.ShellUri);
            list.Add(new RailFolderItem { Name = def.Name, ShellUri = def.ShellUri, Icon = icon, Glyph = icon == null ? FolderGlyph : null });
        }
        return list;
    }
}
