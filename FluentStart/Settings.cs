using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentStart;

public enum ThemeChoice { System, Light, Dark }
public enum BackdropChoice { Mica, MicaAlt, Acrylic }
public enum AlignChoice { Left, Center }
public enum StartViewMode { Both, ListOnly, TilesOnly }

public sealed class Settings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public BackdropChoice Backdrop { get; set; } = BackdropChoice.Mica;
    public AlignChoice Align { get; set; } = AlignChoice.Left;
    public double AppFontSize { get; set; } = 14;
    public double UiFontSize { get; set; } = 14;

    /// <summary>Tile grid width in small cells (even, 6-16: 6 = 3 medium tiles). Set by resizing the Start menu, which snaps to these widths.</summary>
    public int TileColumns { get; set; } = 6;

    /// <summary>Start menu height in DIPs (resizable; width is derived from <see cref="TileColumns"/>).</summary>
    public double WindowHeight { get; set; } = 720;

    public bool ShowMostUsed { get; set; } = true;
    public bool ShowRecentlyAdded { get; set; } = true;
    /// <summary>Max rows per quick section (0 hides both).</summary>
    public int QuickItems { get; set; } = 3;

    /// <summary>Tile fill: false = system accent, true = color derived from each app's icon.</summary>
    /// <summary>Mirror the Start Menu folders (expandable folder rows) instead of one flat alphabetical list.</summary>
    /// <summary>Show the app list and the tiles together, or only one of them (a rail button then switches between the two).</summary>
    public StartViewMode ViewMode { get; set; } = StartViewMode.Both;

    /// <summary>Hidden apps (hidden.json) stay out of the list/search unless this is on; they then show dimmed with an "unhide" command.</summary>
    public bool ShowHiddenApps { get; set; } = false;

    /// <summary>Include files/folders from the Windows Search index (or Everything) in search results.</summary>
    public bool SearchFiles { get; set; } = true;

    /// <summary>Use Everything (voidtools) for file results when it is installed/running; falls back to Windows Search.</summary>
    public bool UseEverything { get; set; } = false;

    /// <summary>If Everything is installed but not running, start it in the background (-startup) when searching.</summary>
    public bool EverythingAutoStart { get; set; } = true;

    /// <summary>Web search link; {0} is replaced by the escaped query.</summary>
    public string WebSearchUrl { get; set; } = "https://www.bing.com/search?q={0}";

    public bool ShowStartMenuFolders { get; set; } = true;

    public bool TileColorsFromIcons { get; set; } = false;

    /// <summary>Show each app's name on its tile. Off = icon only (the name still appears as a tooltip).</summary>
    public bool ShowTileNames { get; set; } = true;

    /// <summary>Learn favorite apps from usage and boost them in search results.</summary>
    public bool LearnFavorites { get; set; } = true;

    /// <summary>Enable network-based quick tools (weather, currency, crypto, speed test, IP lookup) in search. Off by default for privacy.</summary>

    /// <summary>Which rail buttons to show above the power button.</summary>
    public bool ShowUserButton { get; set; } = true;
    public bool ShowDocumentsButton { get; set; } = false;
    public bool ShowPicturesButton { get; set; } = false;
    public bool ShowSettingsButton { get; set; } = true;

    /// <summary>Ids (see <see cref="RailFolderCatalog"/>) of the extra folder buttons shown on the rail.</summary>
    public List<string> RailFolders { get; set; } = new();

    /// <summary>Custom rail buttons (name + glyph + target path/URI).</summary>
    public List<CustomRailButton> CustomRailButtons { get; set; } = new();

    public bool EnableWinKey { get; set; } = true;
    public bool EnableStartButton { get; set; } = true;

    /// <summary>False in --safe mode: nothing is written back to settings.json.</summary>
    [JsonIgnore] public bool Persist { get; set; } = true;

    public void Save() { if (Persist) Storage.Save("settings.json", this); }
}

public sealed class CustomRailButton
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "\uE712";
    public string Target { get; set; } = "";
}