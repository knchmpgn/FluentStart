using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FluentStart.Native;
using Microsoft.Win32;

namespace FluentStart;

public partial class App : Application
{
    // No dotted/black keyboard-focus rectangle on any control. Must run before the first control is created, and must never be
    // able to stop the app from starting: a failure here only means the default focus rectangle stays.
    static App()
    {
        try
        {
            FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(typeof(System.Windows.Controls.Control), new FrameworkPropertyMetadata((Style?)null));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not disable the keyboard focus rectangle globally", ex);
        }
    }

    private static readonly string[] HighContrastKeys =
        { "Brush.Text", "Brush.TextSecondary", "Brush.Hover", "Brush.Pressed", "Brush.Overlay", "Brush.Stroke", "Brush.WindowBg",
          "Brush.Flyout", "Brush.Control", "Brush.ControlHover", "Brush.ControlStroke", "Brush.ScrollThumb", "Brush.Divider" };

    private Mutex? _mutex;
    private Settings _settings = new();
    private StartModel _model = null!;
    private MainWindow _win = null!;
    private SettingsWindow? _settingsWin;
    private InputHooks? _hooks;
    private readonly StartButtonLocator _locator = new();
    private Tray? _tray;
    private DispatcherTimer? _locatorTimer, _watchTimer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private DateTime _lastCatalog = DateTime.MinValue;
    private bool _dark = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\FluentStart.Singleton", out bool created);
        if (!created) { Shutdown(); return; }
        base.OnStartup(e);

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Warn("Unhandled UI exception", a.Exception);
            a.Handled = true;
            if (_tray != null) return;   // started fine: keep running

            // Startup did not finish (no tray icon yet): say so instead of leaving an invisible, useless process behind.
            MessageBox.Show("FluentStart could not start:\n\n" + a.Exception.Message + "\n\nDetails were written to " +
                            Path.Combine(Storage.DataDir, "log.txt"), "FluentStart", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Warn("Unhandled exception: " + a.ExceptionObject);

        bool dumpTaskbar = e.Args.Contains("--dump-taskbar", StringComparer.OrdinalIgnoreCase);
        bool dumpApps = e.Args.Contains("--dump-apps", StringComparer.OrdinalIgnoreCase);
        bool dumpEverything = e.Args.Contains("--dump-everything", StringComparer.OrdinalIgnoreCase);
        string? menuArg = e.Args.FirstOrDefault(x => x.StartsWith("--dump-appmenu", StringComparison.OrdinalIgnoreCase));
        if (dumpTaskbar || dumpApps || dumpEverything || menuArg != null)
        {
            string? filter = menuArg != null && menuArg.Contains('=') ? menuArg[(menuArg.IndexOf('=') + 1)..] : null;
            _ = RunDiagnosticsAsync(dumpTaskbar, dumpApps, dumpEverything, menuArg != null, filter);
            return;
        }

        bool noHooks = e.Args.Contains("--no-hooks", StringComparer.OrdinalIgnoreCase);
        bool safe = e.Args.Contains("--safe", StringComparer.OrdinalIgnoreCase);
        Log.Info($"Starting (noHooks={noHooks}, safe={safe})");

        _settings = safe ? new Settings { Persist = false } : (Storage.Load<Settings>("settings.json") ?? new Settings());
        _model = new StartModel(_settings);
        ApplyTheme();

        _win = new MainWindow(_model, _settings);
        _win.PrepareHandle(_dark);
        _win.MenuShowing += () =>
        {
            // Native enumeration is cheap, so refresh sooner (catches Store-app installs/uninstalls the .lnk watcher can't see).
            _ = RefreshStartButtonAsync();
            const double maxAgeMinutes = 0.5;
            if ((DateTime.UtcNow - _lastCatalog).TotalMinutes > maxAgeMinutes) _ = RefreshCatalogAsync();
        };
        _win.SettingsRequested += OpenSettings;
        _win.TaskbarCreated += async () =>
        {
            await RefreshStartButtonAsync();
            await Task.Delay(2000);
            await RefreshStartButtonAsync();
        };

        var cache = AppCatalog.LoadCache();
        if (cache != null) _model.ApplyCatalog(cache);
        _ = RefreshCatalogAsync();
        WatchStartMenuFolders();

        _locator.Refresh().GetAwaiter().GetResult();   // ensure the Start-button rects are known before any hook can fire
        string? hookWarning = _settings.EnableStartButton && !_locator.Found
            ? "FluentStart could not verify the taskbar Start button. It will leave the native Start button working until the button can be located."
            : null;
        _locatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };   // events (show, TaskbarCreated, display change) cover most changes
        _locatorTimer.Tick += (_, _) => _ = RefreshStartButtonAsync();
        _locatorTimer.Start();

        if (!noHooks)
        {
            _hooks = new InputHooks(_locator)
            {
                EnableWinKey = _settings.EnableWinKey,
                EnableStartButton = _settings.EnableStartButton && _locator.Found
            };
            _hooks.Trigger += () => Dispatcher.BeginInvoke(new Action(_win.Toggle));   // runs on the hook thread
            _hooks.SearchTrigger += () => Dispatcher.BeginInvoke(new Action(() => { _win.SearchOnShow = true; _win.Toggle(); }));
            _hooks.GlobalMouseDown += (x, y) => Dispatcher.BeginInvoke(new Action(() => _win.HandleGlobalMouseDown(x, y)));
            if (!_hooks.Start())
            {
                Log.Warn("Input hooks are not active");
                _hooks.Dispose();
                _hooks = null;
                hookWarning = "FluentStart could not install its system input hooks. Windows key and taskbar Start interception are disabled, so the native Start menu remains available.";
            }
        }

        _tray = new Tray(_settings,
            settingsChanged: OnSettingsChanged,
            showStart: () => _win.Toggle(),
            openSettings: OpenSettings,
            refreshApps: () => _ = RefreshCatalogAsync(),
            clearIcons: IconCache.Clear,
            exit: ExitApp,
            status: () => (_hooks?.Installed == true ? "Hooks: on" : "Hooks: off")
                          + " | " + (_locator.Found ? "Start button: found" : "Start button: not found"));

        if (hookWarning != null)
            Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(hookWarning, "FluentStart", MessageBoxButton.OK, MessageBoxImage.Warning)));

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        ApplyTheme();
        _win.ApplyAppearance(_dark);
        _win.ApplyFonts();
    }));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _ = RefreshStartButtonAsync();

    private async Task RefreshStartButtonAsync()
    {
        await _locator.Refresh();
        if (_hooks != null)
            _hooks.EnableStartButton = _settings.EnableStartButton && _locator.Found;
    }

    // ---------- diagnostics ----------

    private async Task RunDiagnosticsAsync(bool taskbar, bool apps, bool everything, bool appMenu, string? menuFilter)
    {
        try
        {
            if (taskbar) await Task.Run(Diagnostics.DumpTaskbar);
            if (apps) await Diagnostics.DumpAppsAsync();
            if (everything) await Diagnostics.DumpEverythingAsync();
            if (appMenu) await Diagnostics.DumpAppMenusAsync(menuFilter);
        }
        finally
        {
            MessageBox.Show("Diagnostics written to:\n" + Path.Combine(Storage.DataDir, "log.txt"), "FluentStart");
            Shutdown();
        }
    }

    // ---------- catalog ----------

    private async Task RefreshCatalogAsync()
    {
        try
        {
            var items = await AppCatalog.QueryAsync(true);
            if (items.Count > 0) { _model.ApplyCatalog(items); _lastCatalog = DateTime.UtcNow; }
        }
        catch (Exception ex) { Log.Warn("Catalog refresh failed", ex); }
    }

    /// <summary>Refresh the app list shortly after Start-menu shortcuts are added/removed (debounced).</summary>
    private void WatchStartMenuFolders()
    {
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); _ = RefreshCatalogAsync(); };

        void Kick() => Dispatcher.BeginInvoke(new Action(() => { _watchTimer.Stop(); _watchTimer.Start(); }));

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                 })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string filter in new[] { "*.lnk", "*.url", "*.appref-ms" })
                {
                    var w = new FileSystemWatcher(folder, filter) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                    w.Created += (_, _) => Kick();
                    w.Deleted += (_, _) => Kick();
                    w.Renamed += (_, _) => Kick();
                    _watchers.Add(w);
                }
            }
            catch (Exception ex) { Log.Warn("Watcher failed: " + folder, ex); }
        }
    }

    // ---------- settings / theme ----------

    private void OpenSettings()
    {
        if (_settingsWin != null) { _settingsWin.Activate(); return; }
        _settingsWin = new SettingsWindow(_model, _settings, OnSettingsChanged,
            clearIcons: IconCache.Clear,
            resetRenames: _model.ClearRenames,
            resetTiles: _model.ClearTiles,
            export: _model.Export,
            import: _model.Import);
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show();
    }

    private void OnSettingsChanged()
    {
        ApplyTheme();
        _model.TileColumns = _settings.TileColumns;
        _model.UseIconColors = _settings.TileColorsFromIcons && !SystemParameters.HighContrast;
        _model.UseFolders = _settings.ShowStartMenuFolders;
        _model.ShowTileNames = _settings.ShowTileNames;
        _win.ApplyAppearance(_dark);
        _settingsWin?.ReapplyAppearance();
        _settingsWin?.SyncTheme(_dark);
        _win.ApplyViewSettings();
        _win.ApplyFonts();
        _settingsWin?.ApplyFonts();
        _model.RefreshView();   // hidden-app visibility, sections
        if (_hooks != null)
        {
            _hooks.EnableWinKey = _settings.EnableWinKey;
            _hooks.EnableStartButton = _settings.EnableStartButton && _locator.Found;
        }
        _model.RefreshSections();
    }

    private void ApplyTheme()
    {
        _dark = _settings.Theme switch
        {
            ThemeChoice.Light => false,
            ThemeChoice.Dark => true,
            _ => SystemUsesDark()
        };
        Appearance.Dark = _dark;
        Appearance.Backdrop = _settings.Backdrop;

        ThemeMode = _dark ? ThemeMode.Dark : ThemeMode.Light;   // Fluent control styles (.NET 9, experimental)
        ApplyThemeDictionaries(_dark);

        foreach (var k in HighContrastKeys) Resources.Remove(k);

        // Every accent-colored element uses the system accent (and its WinUI shades), never a fixed color.
        var accent = AccentPalette.Read();
        var fill = AccentPalette.Fill(accent, _dark);          // controls: Light2 on dark, Dark1 on light (WinUI "AccentFillColorDefault")
        Resources["Brush.Accent"] = Frozen(fill);
        Resources["Brush.AccentHover"] = Frozen(Color.FromArgb(0xE6, fill.R, fill.G, fill.B));
        Resources["Brush.AccentPressed"] = Frozen(Color.FromArgb(0xCC, fill.R, fill.G, fill.B));
        Resources["Brush.OnAccent"] = Frozen(AccentPalette.On(fill));
        Resources["Brush.AccentTile"] = Frozen(accent.Accent);   // tiles use the raw accent, as the Windows 10 Start did

        if (SystemParameters.HighContrast)
        {
            Resources["Brush.Text"] = SystemColors.WindowTextBrush;
            Resources["Brush.TextSecondary"] = SystemColors.GrayTextBrush;
            Resources["Brush.Hover"] = SystemColors.ControlLightBrush;
            Resources["Brush.Pressed"] = SystemColors.ControlDarkBrush;
            Resources["Brush.Overlay"] = SystemColors.WindowBrush;
            Resources["Brush.Stroke"] = SystemColors.WindowTextBrush;
            Resources["Brush.WindowBg"] = SystemColors.WindowBrush;
            Resources["Brush.Flyout"] = SystemColors.WindowBrush;
            Resources["Brush.Control"] = SystemColors.ControlLightBrush;
            Resources["Brush.ControlHover"] = SystemColors.ControlBrush;
            Resources["Brush.ControlStroke"] = SystemColors.WindowTextBrush;
            Resources["Brush.ScrollThumb"] = SystemColors.WindowTextBrush;
            Resources["Brush.Divider"] = SystemColors.WindowTextBrush;
            Resources["Brush.Accent"] = SystemColors.HighlightBrush;
            Resources["Brush.AccentHover"] = SystemColors.HighlightBrush;
            Resources["Brush.AccentPressed"] = SystemColors.HighlightBrush;
            Resources["Brush.OnAccent"] = SystemColors.HighlightTextBrush;
            Resources["Brush.AccentTile"] = SystemColors.HighlightBrush;
        }

        // Tile labels: white on icon-derived (darkened) tiles, otherwise whatever reads on the accent tile.
        bool iconColors = _settings.TileColorsFromIcons && !SystemParameters.HighContrast;
        Resources["Brush.TileLabel"] = iconColors ? Brushes.White
            : SystemParameters.HighContrast ? SystemColors.HighlightTextBrush
            : Frozen(AccentPalette.On(accent.Accent));
    }

    /// <summary>
    /// Swaps the Dark/Light token dictionary. Found by source, never by index: setting <c>ThemeMode</c> makes WPF insert its own
    /// dictionaries into <c>MergedDictionaries</c>, which shifts indexes - replacing "index 0" then replaced the wrong dictionary and
    /// left the startup (dark) tokens active, giving light text on light surfaces. FluentStart's control styles are re-attached last so
    /// they win over any built-in theme styles.
    /// </summary>
    private void ApplyThemeDictionaries(bool dark)
    {
        var merged = Resources.MergedDictionaries;
        static bool Is(ResourceDictionary d, string file) =>
            d.Source?.OriginalString.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true;

        foreach (var d in merged.Where(d => Is(d, "Themes/Dark.xaml") || Is(d, "Themes/Light.xaml")).ToList()) merged.Remove(d);

        var styles = merged.FirstOrDefault(d => Is(d, "Themes/Fluent.xaml"));
        if (styles != null) merged.Remove(styles);
        else styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Fluent.xaml") };

        merged.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") });
        merged.Add(styles);

        // Self-check (written to data/log.txt): the active text token must contrast with the selected theme.
        if (TryFindResource("Brush.Text") is SolidColorBrush text)
        {
            double lum = AccentPalette.Luminance(text.Color);
            Log.Info($"Theme applied: dark={dark}, Brush.Text={text.Color}, dictionaries={merged.Count}");
            if (dark ? lum < 0.5 : lum > 0.5) Log.Warn("Theme tokens do not match the selected theme (text token has the wrong brightness)");
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// "Auto" follows the Windows (system) mode, which is what the taskbar and native Start use (SystemUsesLightTheme),
    /// falling back to the apps mode if that value is missing.
    /// </summary>
    private static bool SystemUsesDark()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        if (Registry.GetValue(key, "SystemUsesLightTheme", null) is int sys) return sys == 0;
        return Registry.GetValue(key, "AppsUseLightTheme", 1) is int apps && apps == 0;
    }

    // ---------- exit ----------

    private void ExitApp()
    {
        _model.FlushUsage();
        _settingsWin?.Close();
        _win.AllowClose = true;
        _win.Close();
        Shutdown();
    }

    /// <summary>Get current dark mode state for syncing settings window.</summary>
    public bool GetDarkMode() => _dark;

    protected override void OnExit(ExitEventArgs e)
    {
        _hooks?.Dispose();
        _hooks = null;
        _tray?.Dispose();
        _tray = null;
        _locatorTimer?.Stop();
        _watchTimer?.Stop();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
