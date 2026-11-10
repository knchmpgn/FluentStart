using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FluentStart.Native;
using Microsoft.Win32;

namespace FluentStart;

/// <summary>An "Everything" (voidtools) installation or running instance.</summary>
public sealed record EverythingInfo(
    string? ExePath,
    Version? Version,
    string ProductVersion,
    bool IsPreRelease,
    string? WindowClass,
    IntPtr Hwnd,
    bool IsProcessRunning = false)
{
    /// <summary>True only when Everything's IPC window was found: that window is what queries are sent to.</summary>
    public bool Running => Hwnd != IntPtr.Zero;
    public bool IsLiveProcess => IsProcessRunning || Hwnd != IntPtr.Zero;

    public string Label =>
        $"Everything {(string.IsNullOrEmpty(ProductVersion) ? "(unknown version)" : ProductVersion)}" +
        (IsPreRelease ? " (alpha/beta)" : "") +
        (Running ? " - running" : IsProcessRunning ? " - running, but its IPC window was not found" : " - installed, not running");
}

/// <summary>
/// Finds Everything by its IPC window and by install locations (Program Files*, uninstall registry keys). The IPC window class is
/// "EVERYTHING_TASKBAR_NOTIFICATION" for the default instance (1.4) and "EVERYTHING_TASKBAR_NOTIFICATION_(&lt;instance&gt;)" for a named
/// instance such as the 1.5 alpha ("(1.5a)") - see voidtools' everything_ipc.h. When several exist the highest version wins, so a 1.5
/// alpha/beta is preferred over 1.4. NOT verified here: install folder names and the -startup switch; check with <c>--dump-everything</c>.
/// </summary>
public static class EverythingDetector
{
    private static readonly Regex Numeric = new(@"^\d+(\.\d+){1,3}", RegexOptions.Compiled);

    public const string IpcClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    public static bool IsIpcWindowClass(string cls) =>
        cls == IpcClass || (cls.StartsWith(IpcClass + "_(", StringComparison.Ordinal) && cls.EndsWith(")", StringComparison.Ordinal));

    /// <summary>Instance name of a named-instance IPC class ("1.5a"), or null for the default instance.</summary>
    public static string? InstanceOf(string? cls) =>
        cls != null && cls.Length > IpcClass.Length + 3 && cls.StartsWith(IpcClass + "_(", StringComparison.Ordinal) && cls.EndsWith(")", StringComparison.Ordinal)
            ? cls.Substring(IpcClass.Length + 2, cls.Length - IpcClass.Length - 3)
            : null;

    public static Version? ParseVersion(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return null;
        var m = Numeric.Match(productVersion.Trim());
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>Alpha/beta builds are recognisable by a letter suffix ("1.5.0.1383a") or a named instance.</summary>
    public static bool LooksPreRelease(string? productVersion, string? windowClass) =>
        (!string.IsNullOrWhiteSpace(productVersion) && Regex.IsMatch(productVersion.Trim(), @"\d[a-zA-Z]+\b")) ||
        (InstanceOf(windowClass) is { } inst && Regex.IsMatch(inst, @"^\d+(\.\d+)*[a-zA-Z]+"));

    /// <summary>Higher version first; running before not running; null versions last.</summary>
    public static EverythingInfo? PickBest(IEnumerable<EverythingInfo> candidates) =>
        candidates.OrderByDescending(c => c.Version ?? new Version(0, 0))
                  .ThenByDescending(c => c.Running)
                  .ThenByDescending(c => c.IsProcessRunning)
                  .FirstOrDefault();

    public static List<EverythingInfo> FindRunning()
    {
        var result = new List<EverythingInfo>();

        foreach (var proc in Process.GetProcessesByName("Everything"))
        {
            try
            {
                string? exe = proc.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe)) continue;
                result.Add(Describe(exe, null, IntPtr.Zero, true));
            }
            catch { }
            finally { proc.Dispose(); }
        }

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 && IsIpcWindowClass(sb.ToString()))
            {
                string cls = sb.ToString();
                string? exe = ProcessPathOf(hwnd);
                result.Add(Describe(exe, cls, hwnd));
            }
            return true;
        }, IntPtr.Zero);

        return result
            .GroupBy(i => i.ExePath ?? i.WindowClass ?? i.Hwnd.ToString())
            .Select(g => g.OrderByDescending(x => x.Running)   // the entry that has the IPC window wins over the bare process entry
                          .ThenByDescending(x => x.Version ?? new Version(0, 0))
                          .First())
            .ToList();
    }

    public static List<EverythingInfo> FindInstalled()
    {
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                 })
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "Everything*"))
                    foreach (var name in new[] { "Everything.exe", "Everything64.exe" })
                    {
                        string p = Path.Combine(dir, name);
                        if (File.Exists(p)) exes.Add(p);
                    }
            }
            catch { }
        }

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var sub in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                try
                {
                    using var key = hive.OpenSubKey(sub);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var k = key.OpenSubKey(name);
                        string? display = k?.GetValue("DisplayName") as string;
                        if (display == null || !display.StartsWith("Everything", StringComparison.OrdinalIgnoreCase)) continue;
                        string? loc = k!.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(loc)) continue;
                        foreach (var exe in new[] { "Everything.exe", "Everything64.exe" })
                        {
                            string p = Path.Combine(loc, exe);
                            if (File.Exists(p)) exes.Add(p);
                        }
                    }
                }
                catch { }
            }

        return exes.Select(p => Describe(p, null, IntPtr.Zero)).ToList();
    }

    /// <summary>
    /// Chooses the instance to query. Prefers a running IPC window (highest version); otherwise the highest installed version,
    /// started in the background with <c>-startup</c> when <paramref name="autoStart"/> is true.
    /// </summary>
    public static EverythingInfo? Resolve(bool autoStart)
    {
        var all = FindRunning();
        var running = PickBest(all.Where(i => i.Running));
        if (running != null) return running;
        if (all.Any(i => i.IsProcessRunning))
        {
            Log.Warn("Everything is running but no IPC window was found (is IPC disabled, or is it running elevated?)");
            return null;
        }
        if (!autoStart) return null;

        var installed = PickBest(FindInstalled());
        if (installed?.ExePath == null) return null;

        try
        {
            Process.Start(new ProcessStartInfo(installed.ExePath, "-startup") { UseShellExecute = false, CreateNoWindow = true });
            for (int i = 0; i < 40; i++)   // up to ~4 s for the IPC window to appear
            {
                Thread.Sleep(100);
                running = PickBest(FindRunning().Where(i => i.Running));
                if (running != null) return running;
            }
        }
        catch (Exception ex) { Log.Warn("Starting Everything failed", ex); }
        return null;
    }

    /// <summary>Human-readable detection summary for the settings window / diagnostics.</summary>
    public static string Describe()
    {
        var running = FindRunning();
        var installed = FindInstalled();
        if (running.Count == 0 && installed.Count == 0) return "Everything was not detected (not running, no install found).";
        var best = PickBest(running.Concat(installed));
        return "Detected: " + string.Join("; ", running.Concat(installed.Where(i => running.All(r => r.ExePath != i.ExePath)))
                                                     .Select(i => i.Label + (i.ExePath != null ? $" [{i.ExePath}]" : "")))
               + (best != null ? $"  ->  using: {best.Label}" : "");
    }

    private static EverythingInfo Describe(string? exe, string? cls, IntPtr hwnd, bool isProcessRunning = false)
    {
        string product = "";
        try { if (exe != null && File.Exists(exe)) product = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? ""; }
        catch { }
        if (string.IsNullOrWhiteSpace(product) && hwnd != IntPtr.Zero) product = IpcVersion(hwnd) ?? "";
        return new EverythingInfo(exe, ParseVersion(product), product, LooksPreRelease(product, cls), cls, hwnd, isProcessRunning);
    }

    /// <summary>Asks the IPC window for its version (WM_USER + 0..3 = major, minor, revision, build).</summary>
    private static string? IpcVersion(IntPtr hwnd)
    {
        try
        {
            var parts = new long[4];
            for (int i = 0; i < 4; i++)
            {
                if (NativeMethods.SendMessageTimeoutPtr(hwnd, 0x0400, (IntPtr)i, IntPtr.Zero, 0x2, 500, out IntPtr r) == IntPtr.Zero) return null;
                parts[i] = r.ToInt64();
            }
            return string.Join(".", parts);
        }
        catch { return null; }
    }

    private static string? ProcessPathOf(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }
}
