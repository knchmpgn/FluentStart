using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentStart;

/// <summary>An entry the shell itself offers for an app (the verbs behind the native Start menu's "More" submenu and friends).</summary>
public sealed record NativeVerb(string Text, string? Verb, uint CommandOffset, bool HasSubmenu);

/// <summary>
/// Asks the shell for the AppsFolder item's own context menu (IContextMenu::QueryContextMenu), so entries such as "Pin to taskbar",
/// "Run as administrator", "Open file location" and "Uninstall" come from Windows (including anything shell extensions add) rather than
/// from a hard-coded list. Verbs/strings are read at runtime - nothing about their names is assumed beyond text matching in <see cref="StartMenus"/>.
/// UNVERIFIED on a real system: that the AppsFolder item exposes these through BHID_SFUIObject, and the text each entry has.
/// Run on the UI (STA) thread; dispose when the menu closes.
/// </summary>
internal sealed class ShellContextMenu : IDisposable
{
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName, uint cchMax);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize, fMask;
        public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW;
        public int ptInvokeX, ptInvokeY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    private const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
    private const uint MFT_SEPARATOR = 0x800, MFS_DISABLED = 0x3;
    private const uint GCS_VERBA = 0;
    private const uint FirstId = 1, LastId = 0x7FFF;

    private IContextMenu? _menu;
    private IntPtr _hmenu;

    public List<NativeVerb> Verbs { get; } = new();

    /// <summary>Returns null when the shell offers no menu for the item.</summary>
    public static ShellContextMenu? Create(string appId)
    {
        var m = new ShellContextMenu();
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            Guid bhidUiObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");   // BHID_SFUIObject
            Guid iidContextMenu = typeof(IContextMenu).GUID;
            if (item.BindToHandler(IntPtr.Zero, ref bhidUiObject, ref iidContextMenu, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
            m._menu = (IContextMenu)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);

            m._hmenu = CreatePopupMenu();
            if (m._menu.QueryContextMenu(m._hmenu, 0, FirstId, LastId, 0) < 0) return null;
            m.Read(m._hmenu, m.Verbs);
            return m;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("shellmenu", "Shell context menu unavailable", ex);
            m.Dispose();
            return null;
        }
    }

    private void Read(IntPtr hmenu, List<NativeVerb> into)
    {
        int count = GetMenuItemCount(hmenu);
        for (int i = 0; i < count; i++)
        {
            IntPtr buf = Marshal.AllocHGlobal(512 * 2);
            try
            {
                var info = new MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING | MIIM_FTYPE,
                    dwTypeData = buf,
                    cch = 511
                };
                if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info)) continue;
                if ((info.fType & MFT_SEPARATOR) != 0) continue;
                if ((info.fState & MFS_DISABLED) != 0) continue;

                string text = (Marshal.PtrToStringUni(info.dwTypeData) ?? "").Replace("&", "");
                if (text.Length == 0) continue;
                uint offset = info.wID - FirstId;
                var verb = new StringBuilder(128);
                string? v = null;
                if (info.hSubMenu == IntPtr.Zero && _menu!.GetCommandString((UIntPtr)offset, GCS_VERBA, IntPtr.Zero, verb, 128) == 0 && verb.Length > 0)
                    v = verb.ToString();
                into.Add(new NativeVerb(text, v, offset, info.hSubMenu != IntPtr.Zero));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>Runs one of the shell's own commands.</summary>
    public bool Invoke(NativeVerb verb)
    {
        if (_menu == null) return false;
        try
        {
            var ci = new CMINVOKECOMMANDINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = 0x4000,                       // CMIC_MASK_UNICODE
                lpVerb = (IntPtr)verb.CommandOffset,  // MAKEINTRESOURCE(offset)
                lpVerbW = (IntPtr)verb.CommandOffset,
                nShow = 1
            };
            return _menu.InvokeCommand(ref ci) >= 0;
        }
        catch (Exception ex) { Log.Warn("Shell verb failed: " + verb.Text, ex); return false; }
    }

    public void Dispose()
    {
        if (_hmenu != IntPtr.Zero) { DestroyMenu(_hmenu); _hmenu = IntPtr.Zero; }
        if (_menu != null) { Marshal.ReleaseComObject(_menu); _menu = null; }
    }
}
