using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FluentStart;

/// <summary>
/// Reads the real Start Menu folders (all-users + per-user "Programs") so the app list can mirror the native structure:
/// subfolders become expandable folders, shortcuts become apps. Same-named folders from the two roots are merged and a
/// per-user shortcut overrides an all-users shortcut at the same relative path.
/// Folder names use the shell's localized display names (desktop.ini), e.g. "Windows Tools".
/// </summary>
/// <summary>A shortcut found in a Start-menu folder. <paramref name="Target"/>/<paramref name="Aumid"/> are read from .lnk files and used to match it to its AppsFolder entry.</summary>
internal sealed record ShortcutInfo(string Name, string Path, string? Folder, string? Target, string? Aumid);

internal static class StartMenuScanner
{
    private static readonly string[] Extensions = { ".lnk", ".url", ".appref-ms" };

    private static bool IsShortcutExtension(string ext)
    {
        foreach (string e in Extensions)
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Reading a .lnk is a COM round trip per file on every refresh. A shortcut's target/AUMID only change when the file does, so
    // reuse the last read while (last-write time, size) are unchanged. Failed reads (null, null) are never cached.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, string? Target, string? Aumid)> LnkCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static (string? Target, string? Aumid) ReadLnkCached(FileInfo fi)
    {
        string path = fi.FullName;
        long ticks = fi.LastWriteTimeUtc.Ticks, length = fi.Length;
        if (LnkCache.TryGetValue(path, out var c) && c.Ticks == ticks && c.Length == length) return (c.Target, c.Aumid);

        var read = ShortcutReader.Read(path);
        if (read.Target != null || read.Aumid != null) LnkCache[path] = (ticks, length, read.Target, read.Aumid);
        return read;
    }

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

    private const uint SHGFI_DISPLAYNAME = 0x00000200;

    /// <summary>Must run on an STA thread (reads .lnk files through COM).</summary>
    public static List<ShortcutInfo> Scan()
    {
        var byRelativePath = new Dictionary<string, ShortcutInfo>(StringComparer.OrdinalIgnoreCase);
        var displayCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Order matters: the per-user root is scanned last and wins.
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try { Walk(root, root, byRelativePath, displayCache); }
            catch (Exception ex) { Log.Warn("Start menu scan failed: " + root, ex); }
        }
        return byRelativePath.Values.ToList();
    }

    private static void Walk(string root, string dir, Dictionary<string, ShortcutInfo> result, Dictionary<string, string> displayCache)
    {
        List<FileInfo> files;
        try { files = new DirectoryInfo(dir).EnumerateFiles().ToList(); }   // attributes + timestamps come with the enumeration: no per-file syscalls
        catch { return; }

        foreach (FileInfo fi in files)
        {
            string file = fi.FullName;
            try
            {
                if (!IsShortcutExtension(fi.Extension)) continue;
                if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;

                string rel = Path.GetRelativePath(root, file);
                string? folderRel = Path.GetDirectoryName(rel);
                string? folder = string.IsNullOrEmpty(folderRel) ? null : DisplayPath(root, folderRel, displayCache);
                string name = Path.GetFileNameWithoutExtension(file);
                var (target, aumid) = fi.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ReadLnkCached(fi)
                    : (null, null);
                result[rel] = new ShortcutInfo(name, file, folder, target, aumid);
            }
            catch (Exception ex) { Log.WarnOnce("scanfile", "Skipping shortcut " + file, ex); }
        }

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch { return; }

        foreach (string d in dirs)
        {
            try { if ((File.GetAttributes(d) & FileAttributes.Hidden) != 0) continue; }
            catch { continue; }
            Walk(root, d, result, displayCache);
        }
    }

    /// <summary>"A\B" on disk -> "Display A\Display B" using the shell's display names.</summary>
    private static string DisplayPath(string root, string folderRel, Dictionary<string, string> cache)
    {
        var parts = new List<string>();
        string current = root;
        foreach (string comp in folderRel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, comp);
            if (!cache.TryGetValue(current, out string? display))
            {
                display = ShellDisplayName(current) ?? comp;
                cache[current] = display;
            }
            parts.Add(display);
        }
        return string.Join("\\", parts);
    }

    private static string? ShellDisplayName(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
            return r != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szDisplayName) ? info.szDisplayName : null;
        }
        catch { return null; }
    }
}
