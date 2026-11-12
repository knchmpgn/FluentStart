using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace FluentStart;

public partial class SettingsWindow : Window
{
    private readonly StartModel _model;
    private readonly Settings _s;
    private readonly Action _changed;
    private readonly Action _clearIcons;
    private readonly Action _resetRenames;
    private readonly Action _resetTiles;
    private readonly Func<string, bool> _export;
    private readonly Func<string, bool> _import;
    private bool _loading = true;
    private bool _dark;

    public SettingsWindow(StartModel model, Settings settings, Action changed, Action clearIcons, Action resetRenames, Action resetTiles,
                          Func<string, bool> export, Func<string, bool> import)
    {
        _model = model; _s = settings; _changed = changed; _clearIcons = clearIcons; _resetRenames = resetRenames; _resetTiles = resetTiles; _export = export; _import = import;
        InitializeComponent();
        ApplyFonts();

        ThemeBox.ItemsSource = System.Enum.GetValues<ThemeChoice>();
        BackdropBox.ItemsSource = System.Enum.GetValues<BackdropChoice>();
        AlignBox.ItemsSource = System.Enum.GetValues<AlignChoice>();
        ViewModeBox.ItemsSource = new[] { new ViewModeOption(StartViewMode.Both, "Both"), new ViewModeOption(StartViewMode.TilesOnly, "Tiles"), new ViewModeOption(StartViewMode.ListOnly, "List") };
        QuickBox.ItemsSource = new[] { 0, 1, 2, 3, 4, 5, 6 };
        // Font sizes: a plain list that always contains the current value, so the box shows it.
        List<double> SizesWith(double current)
        {
            var sizes = Enumerable.Range(8, 25).Select(i => (double)i).ToList();
            if (!sizes.Contains(current)) { sizes.Add(current); sizes.Sort(); }
            return sizes;
        }
        AppFontSizeBox.ItemsSource = SizesWith(_s.AppFontSize);
        UiFontSizeBox.ItemsSource = SizesWith(_s.UiFontSize);

        ViewModeBox.SelectedItem = ((IEnumerable<ViewModeOption>)ViewModeBox.ItemsSource).First(o => o.Mode == _s.ViewMode);
        ThemeBox.SelectedItem = _s.Theme;
        BackdropBox.SelectedItem = _s.Backdrop;
        AlignBox.SelectedItem = _s.Align;
        QuickBox.SelectedItem = Math.Clamp(_s.QuickItems, 0, 6);
        TileColorCheck.IsChecked = _s.TileColorsFromIcons;
        TileNamesCheck.IsChecked = _s.ShowTileNames;
        MostUsedCheck.IsChecked = _s.ShowMostUsed;
        RecentCheck.IsChecked = _s.ShowRecentlyAdded;
        WinKeyCheck.IsChecked = _s.EnableWinKey;
        StartBtnCheck.IsChecked = _s.EnableStartButton;
        StartupCheck.IsChecked = Startup.IsEnabled();
        SearchFilesCheck.IsChecked = _s.SearchFiles;
        EverythingCheck.IsChecked = _s.UseEverything;
        EverythingAutoStartCheck.IsChecked = _s.EverythingAutoStart;
        ShowHiddenCheck.IsChecked = _s.ShowHiddenApps;
        RefreshCustomButtonsList();
        BuildRailChoices();
        WebUrlBox.Text = _s.WebSearchUrl;
        AppFontSizeBox.SelectedItem = _s.AppFontSize;
        UiFontSizeBox.SelectedItem = _s.UiFontSize;
        RefreshHidden();
        RefreshEverythingStatus();
        _loading = false;

        // Same Mica / Alt / Acrylic backdrop, dark title bar and rounded corners as the Start menu.
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            _dark = (System.Windows.Application.Current is FluentStart.App app) ? app.GetDarkMode() : true;
            ApplyThemeDictionaries(_dark);
            ReapplyAppearance();
        };
    }

    /// <summary>Sync theme dictionaries with the app (dark/light).</summary>
    public void SyncTheme(bool dark)
    {
        if (_dark == dark) return;
        _dark = dark;
        ApplyThemeDictionaries(dark);
        ReapplyAppearance();
    }

    private void ApplyThemeDictionaries(bool dark)
    {
        var merged = Resources.MergedDictionaries;
        static bool Is(ResourceDictionary d, string file) => d.Source?.OriginalString.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true;

        foreach (var d in merged.Where(d => Is(d, "Themes/Dark.xaml") || Is(d, "Themes/Light.xaml")).ToList()) merged.Remove(d);

        var styles = merged.FirstOrDefault(d => Is(d, "Themes/Fluent.xaml"));
        if (styles != null) merged.Remove(styles);
        else styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Fluent.xaml") };

        merged.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") });
        merged.Add(styles);

        // Re-apply font resources since they may reference theme brushes
        ApplyFonts();
    }

    /// <summary>Called when theme/backdrop settings change while this window is open.</summary>
    public void ReapplyAppearance()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (Appearance.Apply(hwnd, out _)) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "Brush.WindowBg");
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SettingsTabs) || SettingsTabs.SelectedContent is not FrameworkElement content) return;

        Motion.Set(content, OpacityProperty, 1);
        if (!IsLoaded) return;
        Motion.FadeIn(content, from: 0.82);
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var activeScroll = SettingsTabs.SelectedContent as ScrollViewer ?? SettingsScroll;
        double scrollOffset = activeScroll.VerticalOffset;
        if (ViewModeBox.SelectedItem is ViewModeOption vm) _s.ViewMode = vm.Mode;
        if (ThemeBox.SelectedItem is ThemeChoice t) _s.Theme = t;
        if (BackdropBox.SelectedItem is BackdropChoice b) _s.Backdrop = b;
        if (AlignBox.SelectedItem is AlignChoice a) _s.Align = a;
        if (QuickBox.SelectedItem is int q) _s.QuickItems = q;
        _s.TileColorsFromIcons = TileColorCheck.IsChecked == true;
        _s.ShowTileNames = TileNamesCheck.IsChecked == true;
        _s.ShowMostUsed = MostUsedCheck.IsChecked == true;
        _s.ShowRecentlyAdded = RecentCheck.IsChecked == true;
        _s.SearchFiles = SearchFilesCheck.IsChecked == true;
        _s.UseEverything = EverythingCheck.IsChecked == true;
        _s.EverythingAutoStart = EverythingAutoStartCheck.IsChecked == true;
        _s.ShowHiddenApps = ShowHiddenCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(WebUrlBox.Text)) _s.WebSearchUrl = WebUrlBox.Text.Trim();
        if (AppFontSizeBox.SelectedItem is double appFs) _s.AppFontSize = appFs;
        if (UiFontSizeBox.SelectedItem is double uiFs) _s.UiFontSize = uiFs;
        _s.EnableWinKey = WinKeyCheck.IsChecked == true;
        _s.EnableStartButton = StartBtnCheck.IsChecked == true;
        _s.Save();
        _changed();
        Dispatcher.BeginInvoke(() => activeScroll.ScrollToVerticalOffset(scrollOffset));
    }

    // ---------- Navigation: which buttons the Start menu's left rail shows ----------

    private sealed record ViewModeOption(StartViewMode Mode, string Label) { public override string ToString() => Label; }

    private List<RailChoice> _railChoices = new();

    private void BuildRailChoices()
    {
        static ImageSource? Icon(string uri) => RailFolderCatalog.IconOf(uri);
        RailChoice Folder(string id, string name, string description, string group, string uri, bool on)
        {
            var icon = Icon(uri);
            return new RailChoice { Id = id, Name = name, Description = description, Group = group, Icon = icon, Glyph = icon == null ? RailFolderCatalog.FolderGlyph : null, IsEnabled = on };
        }

        _railChoices = new List<RailChoice>
        {
            new() { Id = "user", Name = "Account", Description = "Your picture and the lock, sign-out and switch-user menu", Group = "Account and System", Glyph = "\uE77B", IsEnabled = _s.ShowUserButton },
            new() { Id = "settings", Name = "Settings", Description = "Windows Settings (right-click for FluentStart settings)", Group = "Account and System", Glyph = "\uE713", IsEnabled = _s.ShowSettingsButton },
            Folder("docs", "Documents", "Your Documents folder", "Folders", "shell:Personal", _s.ShowDocumentsButton),
            Folder("pics", "Pictures", "Your Pictures folder", "Folders", "shell:My Pictures", _s.ShowPicturesButton),
        };
        foreach (var def in RailFolderCatalog.Extra)
            _railChoices.Add(Folder(def.Id, def.Name, def.Description, def.Group, def.ShellUri, _s.RailFolders.Contains(def.Id, StringComparer.OrdinalIgnoreCase)));

        foreach (var choice in _railChoices)
            choice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RailChoice.IsEnabled)) RailChoiceChanged(); };

        var view = new System.Windows.Data.ListCollectionView(_railChoices);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RailChoice.Group)));
        RailChoicesList.ItemsSource = view;
    }

    private void RailChoiceChanged()
    {
        if (_loading) return;
        bool On(string id) => _railChoices.First(c => c.Id == id).IsEnabled;
        _s.ShowUserButton = On("user");
        _s.ShowSettingsButton = On("settings");
        _s.ShowDocumentsButton = On("docs");
        _s.ShowPicturesButton = On("pics");
        _s.RailFolders = RailFolderCatalog.Extra.Where(d => On(d.Id)).Select(d => d.Id).ToList();
        _s.Save();
        _changed();
    }

    private void RefreshHidden() => HiddenList.ItemsSource = _model.HiddenApps.ToList();

    private void RefreshCustomButtonsList()
    {
        if (CustomButtonsList != null) CustomButtonsList.ItemsSource = null;
        if (CustomButtonsList != null) CustomButtonsList.ItemsSource = _s.CustomRailButtons.ToList();
    }

    private void AddCustomButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomButtonDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _s.CustomRailButtons.Add(new CustomRailButton { Name = dlg.ButtonName, Glyph = dlg.Glyph, Target = dlg.Target });
            _s.Save();
            RefreshCustomButtonsList();
            _changed();
        }
    }

    private void RemoveCustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomButtonsList.SelectedItem is CustomRailButton btn)
        {
            _s.CustomRailButtons.Remove(btn);
            _s.Save();
            RefreshCustomButtonsList();
            _changed();
        }
    }

    private async void RefreshEverythingStatus()
    {
        EverythingStatus.Text = "Detecting Everything...";
        string text = await Task.Run(EverythingDetector.Describe);
        EverythingStatus.Text = text;
    }

    private void EverythingChanged(object sender, RoutedEventArgs e)
    {
        Changed(sender, e);
        if (!_loading && EverythingCheck.IsChecked == true) RefreshEverythingStatus();
    }

    private void Detect_Click(object sender, RoutedEventArgs e) => RefreshEverythingStatus();

    private void Unhide_Click(object sender, RoutedEventArgs e)
    {
        if (HiddenList.SelectedItem is AppEntry app) { _model.SetHidden(app, false); RefreshHidden(); }
    }

    private void UnhideAll_Click(object sender, RoutedEventArgs e)
    {
        _model.ClearHidden();
        RefreshHidden();
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Startup.SetEnabled(StartupCheck.IsChecked == true);
    }

    private void InstallTask_Click(object sender, RoutedEventArgs e) => Startup.InstallElevatedTask();
    private void RemoveTask_Click(object sender, RoutedEventArgs e) => Startup.RemoveElevatedTask();

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Storage.DataDir + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open data folder failed", ex); }
    }

    private void ClearIcons_Click(object sender, RoutedEventArgs e)
    {
        _clearIcons();
        MessageBox.Show(this, "Icon cache cleared. Icons are re-read the next time FluentStart starts.", "FluentStart");
    }

    private void ResetRenames_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove all custom app names?", "FluentStart", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _resetRenames();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "FluentStart layout (*.json)|*.json", FileName = "fluentstart-layout.json" };
        if (dlg.ShowDialog(this) != true) return;
        MessageBox.Show(this, _export(dlg.FileName) ? "Exported." : "Export failed (see data/log.txt).", "FluentStart");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "FluentStart layout (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        MessageBox.Show(this, _import(dlg.FileName) ? "Imported. Renames were merged; the tile layout was replaced."
                                                      : "Import failed (see data/log.txt).", "FluentStart");
    }

    private void ResetTiles_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Unpin all tiles and remove all groups?", "FluentStart", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _resetTiles();
    }

    public void ApplyFonts()
    {
        try
        {
            const string fontFamily = "Segoe UI Variable Text, Segoe UI";
            Resources["Font.App"] = new System.Windows.Media.FontFamily(fontFamily);
            Resources["Font.Ui"] = new System.Windows.Media.FontFamily(fontFamily);
            Resources["FontSize.App"] = _s.AppFontSize;
            Resources["FontSize.Ui"] = _s.UiFontSize;
        }
        catch { }
    }
}

internal sealed class CustomButtonDialog : Window
{
    public string ButtonName { get; private set; } = "";
    public string Glyph { get; private set; } = "\uE712";
    public string Target { get; private set; } = "";

    public CustomButtonDialog()
    {
        Title = "Add custom rail button";
        Width = 420;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)TryFindResource("Brush.WindowBg") ?? System.Windows.Media.Brushes.White;
        Foreground = (Brush)TryFindResource("Brush.Text") ?? System.Windows.Media.Brushes.Black;
        FontFamily = (FontFamily)TryFindResource("Font.Ui") ?? new FontFamily("Segoe UI");
        FontSize = 14;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(nameBox);

        panel.Children.Add(new TextBlock { Text = "Glyph (Segoe Fluent Icons code, e.g. \\uE712)", Margin = new Thickness(0, 0, 0, 4) });
        var glyphBox = new TextBox { Text = "\uE712", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(glyphBox);

        panel.Children.Add(new TextBlock { Text = "Target (path, URL, ms-settings:, etc.)", Margin = new Thickness(0, 0, 0, 4) });
        var targetBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(targetBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var okBtn = new Button { Content = "OK", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        okBtn.Click += (_, _) => { Name = nameBox.Text.Trim(); Glyph = glyphBox.Text.Trim(); Target = targetBox.Text.Trim(); if (!string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(Target)) { DialogResult = true; Close(); } else { MessageBox.Show(this, "Name and target are required.", "Missing info"); } };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        cancelBtn.Click += (_, _) => Close();
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        Content = panel;
    }
}
