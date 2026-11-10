using System;
using System.IO;
using System.Security.Principal;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FluentStart;

/// <summary>
/// Best-effort lookup of the signed-in user's account picture.
/// UNVERIFIED: the HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{SID} key and its ImageNN values.
/// Returns null (the rail then shows the person glyph) if anything is missing.
/// </summary>
internal static class AccountPicture
{
    public static BitmapSource? TryLoad()
    {
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            if (key == null) return null;

            foreach (string name in new[] { "Image96", "Image64", "Image48", "Image40", "Image448" })
            {
                if (key.GetValue(name) is string path && File.Exists(path))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
        }
        catch (Exception ex) { Log.WarnOnce("acctpic", "Account picture lookup failed", ex); }
        return null;
    }
}
