using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using FluentStart.Native;

namespace FluentStart;

/// <summary>
/// One-shot diagnostics (run with --dump-taskbar and/or --dump-apps). Output goes to data/log.txt.
/// They exist to settle assumptions A2 (Start button AutomationId) and A13 (native vs PowerShell app IDs) on a real machine.
/// </summary>
internal static class Diagnostics
{
    public static void DumpTaskbar()
    {
        Log.Info($"[diag] OS {Environment.OSVersion} 64bit={Environment.Is64BitOperatingSystem}");
        foreach (string cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                Log.Info($"[taskbar] window {cls} 0x{h.ToInt64():X}");
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    int n = 0;
                    foreach (AutomationElement el in all)
                    {
                        if (++n > 300) { Log.Info("[taskbar]   ... truncated at 300 elements"); break; }
                        var c = el.Current;
                        Log.Info($"[taskbar]   id='{c.AutomationId}' name='{c.Name}' type={c.ControlType.ProgrammaticName} class='{c.ClassName}' rect={c.BoundingRectangle}");
                    }
                }
                catch (Exception ex) { Log.Warn("[taskbar] dump failed", ex); }
            }
        }
    }

    /// <summary>
    /// Settles "is the list exactly the native list?": logs the AppsFolder set (native enumerator vs PowerShell), how many entries got a Start-menu
    /// folder, shortcuts that matched nothing, and entries without a shortcut.
    /// </summary>
    public static async Task DumpAppsAsync()
    {
        try
        {
            var native = new List<AppCatalog.Item>();
            try { native = await NativeAppEnumerator.EnumerateAsync(); }
            catch (Exception ex) { Log.Warn("[apps] native enumeration failed", ex); }
            var ps = await AppCatalog.QueryAppsFolderAsync(false);

            var nIds = new HashSet<string>(native.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            var pIds = new HashSet<string>(ps.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            var onlyNative = nIds.Except(pIds, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyPs = pIds.Except(nIds, StringComparer.OrdinalIgnoreCase).ToList();
            Log.Info($"[apps] AppsFolder: native={native.Count} PowerShell(Get-StartApps)={ps.Count} onlyNative={onlyNative.Count} onlyPowerShell={onlyPs.Count}");
            foreach (var id in onlyNative.Take(15)) Log.Info("[apps]   only native:     " + id);
            foreach (var id in onlyPs.Take(15)) Log.Info("[apps]   only PowerShell: " + id);

            var apps = native.Count > 0 ? native : ps;
            var shortcuts = await StaRunner.Run(StartMenuScanner.Scan);
            var list = AppCatalog.Build(apps, shortcuts);
            var withFolder = list.Where(i => i.Folder != null).ToList();
            var withShortcut = list.Where(i => i.Path != null).ToList();
            var usedPaths = new HashSet<string>(withShortcut.Select(i => i.Path!), StringComparer.OrdinalIgnoreCase);
            var unmatched = shortcuts.Where(sc => !usedPaths.Contains(sc.Path)).ToList();

            Log.Info($"[apps] shortcuts scanned={shortcuts.Count}; apps with a shortcut={withShortcut.Count}; in a folder={withFolder.Count}; shortcuts matching no app={unmatched.Count}");
            foreach (var sc in unmatched.Take(20))
                Log.Info($"[apps]   unmatched shortcut: '{sc.Name}' target='{sc.Target}' aumid='{sc.Aumid}' folder='{sc.Folder}'");
            foreach (var a in list.Where(i => i.Path == null).Take(20)) Log.Info($"[apps]   app without shortcut: '{a.Name}' id='{a.Id}'");
            foreach (var f in withFolder.Select(i => i.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).Take(30))
                Log.Info($"[apps]   folder: {f} ({withFolder.Count(i => i.Folder == f)} apps)");
            Log.Info("[apps] verdict: " + (native.Count > 0 && onlyNative.Count == 0 && onlyPs.Count == 0
                ? "native and PowerShell agree on the app set"
                : "the two enumerations differ - compare against the real Start menu before trusting either"));
        }
        catch (Exception ex) { Log.Warn("[apps] dump failed", ex); }
    }

    /// <summary>
    /// Logs the shell's own context-menu entries and the jump-list (recent/frequent) items for apps whose name contains <paramref name="filter"/>
    /// (first few apps if empty). Use it to compare with what the native Start shows when you right-click the same app.
    /// </summary>
    public static async Task DumpAppMenusAsync(string? filter)
    {
        try
        {
            var items = await AppCatalog.QueryAppsFolderAsync(true);
            var picks = string.IsNullOrWhiteSpace(filter)
                ? items.Take(5).ToList()
                : items.Where(i => i.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToList();
            foreach (var app in picks)
            {
                Log.Info($"[appmenu] '{app.Name}' id='{app.Id}'");
                using var menu = ShellContextMenu.Create(app.Id);
                if (menu == null) Log.Info("[appmenu]   shell context menu: unavailable");
                else foreach (var v in menu.Verbs) Log.Info($"[appmenu]   shell entry: '{v.Text}' verb='{v.Verb}' offset={v.CommandOffset} submenu={v.HasSubmenu}");
                var jumps = JumpLists.Get(app.Id, 10);
                if (jumps.Count == 0) Log.Info("[appmenu]   jump list: none returned");
                foreach (var j in jumps) Log.Info($"[appmenu]   jump list ({(j.IsFrequent ? "frequent" : "recent")}): '{j.Title}' path='{j.Path}' args='{j.Arguments}'");
            }
        }
        catch (Exception ex) { Log.Warn("[appmenu] dump failed", ex); }
    }

    /// <summary>Logs every running/installed Everything, which one would be used, and a small test query through the IPC.</summary>
    public static async Task DumpEverythingAsync()
    {
        try
        {
            var running = await Task.Run(EverythingDetector.FindRunning);
            var installed = await Task.Run(EverythingDetector.FindInstalled);
            foreach (var r in running)
                Log.Info($"[everything] running: class='{r.WindowClass}' hwnd=0x{r.Hwnd.ToInt64():X} exe='{r.ExePath}' product='{r.ProductVersion}' prerelease={r.IsPreRelease}");
            foreach (var i in installed)
                Log.Info($"[everything] installed: exe='{i.ExePath}' product='{i.ProductVersion}' prerelease={i.IsPreRelease}");
            Log.Info("[everything] " + await Task.Run(EverythingDetector.Describe));

            var best = EverythingDetector.PickBest(running);
            if (best == null) { Log.Info("[everything] no running instance - IPC test skipped"); return; }

            using var client = new EverythingClient();
            var hits = await client.QueryAsync(best.Hwnd, "a", 5, System.Threading.CancellationToken.None);
            Log.Info($"[everything] IPC test query 'a' returned {hits.Count} hit(s)" + (hits.Count == 0 ? " - IPC layout assumptions may be wrong (A40-A42)" : ""));
            foreach (var h in hits) Log.Info($"[everything]   {(h.IsFolder ? "[dir] " : "")}{h.FullPath}");
        }
        catch (Exception ex) { Log.Warn("[everything] dump failed", ex); }
    }
}
