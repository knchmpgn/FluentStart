using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FluentStart;

/// <summary>
/// Desktop apps without an explicit AppUserModelID are identified as "{KnownFolderGuid}\relative\path.exe" (as Get-StartApps / the AppsFolder show them).
/// This expands such an id to the real exe path so a Start-menu shortcut can be matched to its AppsFolder entry.
/// </summary>
internal static class KnownFolders
{
    private static readonly Regex AppIdPath = new(@"^\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}\\(.+)$", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<Guid, string?> Cache = new();

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string? ExpandAppId(string appId)
    {
        var m = AppIdPath.Match(appId);
        if (!m.Success) return null;
        var guid = Guid.Parse(m.Groups[1].Value);
        string? root = Cache.GetOrAdd(guid, g =>
        {
            try
            {
                var id = g;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return null; }
        });
        return root == null ? null : Path.Combine(root, m.Groups[2].Value);
    }
}
