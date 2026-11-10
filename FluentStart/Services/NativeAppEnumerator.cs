using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentStart;

/// <summary>
/// Enumerates FOLDERID_AppsFolder through IShellItem/IEnumShellItems (no PowerShell).
/// UNVERIFIED: that SIGDN_PARENTRELATIVEPARSING yields the same IDs as Get-StartApps. Compare via data/log.txt before relying on it
/// (renames/tiles are keyed by ID).
/// </summary>
internal static class NativeAppEnumerator
{
    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems ppenum);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetKnownFolderItem(ref Guid rfid, uint flags, IntPtr hToken, ref Guid riid, out IntPtr ppv);

    public static Task<List<AppCatalog.Item>> EnumerateAsync()
    {
        var tcs = new TaskCompletionSource<List<AppCatalog.Item>>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "AppsFolderEnum" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static List<AppCatalog.Item> Enumerate()
    {
        var result = new List<AppCatalog.Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Guid appsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
        Guid iidItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        Guid bhidEnum = new("94f60519-2850-4924-aa5a-d15e84868039");
        Guid iidEnum = new("70629033-e363-4a28-a567-0db78006e6d7");

        SHGetKnownFolderItem(ref appsFolder, 0, IntPtr.Zero, ref iidItem, out IntPtr pItem);
        var folder = (IShellItem)Marshal.GetObjectForIUnknown(pItem);
        Marshal.Release(pItem);

        int hr = folder.BindToHandler(IntPtr.Zero, ref bhidEnum, ref iidEnum, out IntPtr pEnum);
        if (hr != 0 || pEnum == IntPtr.Zero) throw new COMException("BindToHandler(EnumItems) failed", hr);
        var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
        Marshal.Release(pEnum);

        while (en.Next(1, out IShellItem child, out uint fetched) == 0 && fetched == 1)
        {
            try
            {
                if (child.GetDisplayName(SIGDN_NORMALDISPLAY, out string name) == 0 &&
                    child.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out string id) == 0 &&
                    !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    result.Add(new AppCatalog.Item(id, name));
            }
            finally { Marshal.ReleaseComObject(child); }
        }
        Marshal.ReleaseComObject(en);
        Marshal.ReleaseComObject(folder);
        return result;
    }
}
