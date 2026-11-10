using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FluentStart;

/// <summary>Reads a .lnk's target path and explicit AppUserModelID (to match the shortcut to its AppsFolder entry) and runs STA work.</summary>
internal static class ShortcutReader
{
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

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    private const ushort VT_LPWSTR = 31;
    private static readonly Guid AppUserModelIdFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");   // System.AppUserModel.ID, pid 5

    /// <summary>Must run on an STA thread. Returns (null, null) for anything unreadable.</summary>
    public static (string? Target, string? Aumid) Read(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLinkCoClass();
            ((IPersistFile)obj).Load(lnkPath, 0);

            var sb = new StringBuilder(1024);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string? target = sb.Length > 0 ? sb.ToString() : null;

            string? aumid = null;
            var key = new PROPERTYKEY { fmtid = AppUserModelIdFmt, pid = 5 };
            ((IPropertyStore)obj).GetValue(ref key, out var pv);
            try { if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero) aumid = Marshal.PtrToStringUni(pv.p); }
            finally { PropVariantClear(ref pv); }

            return (target, string.IsNullOrWhiteSpace(aumid) ? null : aumid);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("lnkread", "Reading shortcut failed: " + lnkPath, ex);
            return (null, null);
        }
        finally { if (obj != null) Marshal.ReleaseComObject(obj); }
    }
}

internal static class StaRunner
{
    /// <summary>Runs <paramref name="work"/> on a fresh STA thread (shell COM objects want one).</summary>
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new System.Threading.Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "StaWork" };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
