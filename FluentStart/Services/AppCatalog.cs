using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluentStart;

internal static class AppCatalog
{
    /// <summary>
    /// One app exactly as the native Start lists it: <paramref name="Id"/> = AppsFolder AppID (AUMID, or "{KnownFolder}\\path.exe" for desktop apps),
    /// <paramref name="Name"/> = AppsFolder display name, <paramref name="Path"/> = the Start-menu shortcut (for "open file location"), null if none,
    /// <paramref name="Folder"/> = Start-menu folder (display names joined by '\\'), null = top level.
    /// </summary>
    public sealed record Item(string Id, string Name, string? Path = null, string? Folder = null);

    public static List<Item>? LoadCache() => Storage.Load<List<Item>>("apps-cache.json");

    /// <summary>
    /// The native Start's "All apps" list is the shell's AppsFolder (shell:AppsFolder). That set is used verbatim, so the app list is the
    /// same set of apps; the Start-menu folder structure is then layered on by matching each AppsFolder entry to its shortcut.
    /// </summary>
    public static async Task<List<Item>> QueryAsync(bool native)
    {
        var shortcutsTask = StaRunner.Run(StartMenuScanner.Scan);

        List<Item> apps;
        try { apps = await QueryAppsFolderAsync(native); }
        catch (Exception ex) { Log.Warn("AppsFolder enumeration failed", ex); apps = new(); }

        List<ShortcutInfo> shortcuts;
        try { shortcuts = await shortcutsTask; }
        catch (Exception ex) { Log.Warn("Start menu scan failed", ex); shortcuts = new(); }

        var list = Build(apps, shortcuts);
        if (list.Count > 0) Storage.Save("apps-cache.json", list);
        return list;
    }

    /// <summary>
    /// Returns every AppsFolder entry unchanged and attaches folder + shortcut path to the ones that have a shortcut.
    /// A shortcut matches an entry by (1) its explicit AppUserModelID, (2) its target exe equal to the entry's expanded implicit id,
    /// (3) the display name. Shortcuts that match nothing are NOT added (the native list is AppsFolder-driven).
    /// </summary>
    internal static List<Item> Build(IReadOnlyList<Item> apps, IReadOnlyList<ShortcutInfo> shortcuts)
    {
        var byId = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<Item>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var a in apps)
        {
            byId.TryAdd(a.Id, a);
            string? expanded = KnownFolders.ExpandAppId(a.Id);
            if (expanded != null) byPath.TryAdd(expanded, a.Id);
            if (!byName.TryGetValue(a.Name, out var list)) byName[a.Name] = list = new List<Item>();
            list.Add(a);
        }

        var matched = new Dictionary<string, ShortcutInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var sc in shortcuts)
        {
            string? id = null;
            if (sc.Aumid != null && byId.ContainsKey(sc.Aumid)) id = sc.Aumid;
            else if (sc.Target != null && byPath.TryGetValue(sc.Target, out var pid)) id = pid;
            else if (byName.TryGetValue(sc.Name, out var cands)) id = cands.FirstOrDefault(c => !matched.ContainsKey(c.Id))?.Id;

            if (id != null) matched.TryAdd(id, sc);   // first shortcut wins when several point at one entry
        }

        return apps.Select(a => matched.TryGetValue(a.Id, out var sc) ? a with { Path = sc.Path, Folder = sc.Folder } : a).ToList();
    }

    /// <summary>Native IShellItem enumeration of FOLDERID_AppsFolder; Get-StartApps (PowerShell) if that fails or is disabled.</summary>
    public static async Task<List<Item>> QueryAppsFolderAsync(bool native)
    {
        if (native)
        {
            try
            {
                var list = await NativeAppEnumerator.EnumerateAsync();
                if (list.Count > 0) return list;
                Log.Warn("Native enumeration returned no apps; falling back to PowerShell");
            }
            catch (Exception ex) { Log.Warn("Native enumeration failed; falling back to PowerShell", ex); }
        }
        return await QueryPowerShellAsync();
    }

    private static async Task<List<Item>> QueryPowerShellAsync()
    {
        var result = new List<Item>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return result;

            // Use a timeout with cancellation token to prevent ReadToEnd from blocking indefinitely
            var readTask = p.StandardOutput.ReadToEndAsync();
            var completed = Task.WaitAny(readTask, Task.Delay(20000)) == 0;
            if (!completed)
            {
                try { p.Kill(); } catch { }
                return result;
            }

            string json = await readTask;
            await p.WaitForExitAsync();
            if (string.IsNullOrWhiteSpace(json)) return result;

            using var doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> els = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in els)
            {
                string? name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
                string? id = e.TryGetProperty("AppID", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (seen.Add(id)) result.Add(new Item(id, name));
            }
        }
        catch (Exception ex) { Log.Warn("Get-StartApps failed", ex); }
        return result;
    }
}
