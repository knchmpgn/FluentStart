using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentStart;

/// <summary>One entry of an app's jump list (a recent/frequent file, or a task link).</summary>
public sealed record JumpItem(string Title, string? Path, string? Arguments, string? ParsingName, bool IsFrequent);

/// <summary>
/// Recent and frequent items of an app, as the native Start shows them in an app's context menu (jump list "Recent"/"Frequent").
/// Uses the documented IApplicationDocumentLists with the app's AppUserModelID (= the AppsFolder AppID). UNVERIFIED: CLSID/IID values below,
/// that implicit desktop AppIDs ("{KnownFolder}\\path.exe") work with SetAppID, and that pinned items / custom "Tasks" categories are NOT
/// available through this API (they live in the custom/automatic destination files and are not read here).
/// Must run on an STA thread (the UI thread is).
/// </summary>
internal static class JumpLists
{
    [ComImport, Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")]
    private class ApplicationDocumentListsClass { }

    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void GetList(int listType, uint cItemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000, SIGDN_FILESYSPATH = 0x80058000;
    private const int ADLT_RECENT = 0, ADLT_FREQUENT = 1;

    private static readonly Dictionary<string, (DateTime At, List<JumpItem> Items)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recent (then frequent) items, de-duplicated, at most <paramref name="max"/>. Cached for a few seconds.</summary>
    public static List<JumpItem> Get(string appId, int max = 6)
    {
        if (Cache.TryGetValue(appId, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < 5) return c.Items;

        var result = new List<JumpItem>();
        try
        {
            var lists = (IApplicationDocumentLists)new ApplicationDocumentListsClass();
            lists.SetAppID(appId);
            AddList(lists, ADLT_RECENT, max, result, frequent: false);
            if (result.Count < max) AddList(lists, ADLT_FREQUENT, max, result, frequent: true);
            Marshal.ReleaseComObject(lists);
        }
        catch (Exception ex) { Log.WarnOnce("jumplist", "Jump list read failed (first failure shown)", ex); }

        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        Cache[appId] = (DateTime.UtcNow, result);
        return result;
    }

    private static void AddList(IApplicationDocumentLists lists, int type, int max, List<JumpItem> into, bool frequent)
    {
        Guid iidArray = typeof(IObjectArray).GUID;
        lists.GetList(type, (uint)max, ref iidArray, out object o);
        var array = (IObjectArray)o;
        array.GetCount(out uint count);

        Guid iidItem = typeof(IShellItem).GUID, iidLink = typeof(IShellLinkW).GUID;
        for (uint i = 0; i < count && into.Count < max; i++)
        {
            try
            {
                JumpItem? item = null;
                try
                {
                    array.GetAt(i, ref iidItem, out object so);
                    var si = (IShellItem)so;
                    si.GetDisplayName(SIGDN_NORMALDISPLAY, out string title);
                    string? path = si.GetDisplayName(SIGDN_FILESYSPATH, out string p) == 0 ? p : null;
                    string? parsing = si.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out string pn) == 0 ? pn : null;
                    item = new JumpItem(title, path, null, parsing, frequent);
                    Marshal.ReleaseComObject(si);
                }
                catch (InvalidCastException)
                {
                    array.GetAt(i, ref iidLink, out object lo);
                    var link = (IShellLinkW)lo;
                    var d = new StringBuilder(512); var p = new StringBuilder(1024); var a = new StringBuilder(1024);
                    link.GetDescription(d, d.Capacity); link.GetPath(p, p.Capacity, IntPtr.Zero, 0); link.GetArguments(a, a.Capacity);
                    string title = d.Length > 0 ? d.ToString() : System.IO.Path.GetFileName(p.ToString());
                    item = new JumpItem(title, p.Length > 0 ? p.ToString() : null, a.Length > 0 ? a.ToString() : null, null, frequent);
                    Marshal.ReleaseComObject(link);
                }

                if (item != null && !string.IsNullOrWhiteSpace(item.Title) &&
                    !into.Exists(x => string.Equals(x.Path ?? x.ParsingName, item.Path ?? item.ParsingName, StringComparison.OrdinalIgnoreCase)))
                    into.Add(item);
            }
            catch (Exception ex) { Log.WarnOnce("jumpitem", "Jump list item skipped", ex); }
        }
        Marshal.ReleaseComObject(array);
    }

    public static void Open(JumpItem item)
    {
        try
        {
            string? target = item.Path ?? item.ParsingName;
            if (target == null) return;
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(item.Arguments)) psi.Arguments = item.Arguments;
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn("Open jump list item failed: " + item.Title, ex); }
    }
}
