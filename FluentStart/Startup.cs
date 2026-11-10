using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FluentStart;

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "FluentStart";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("FluentStart") != null;
    }

    public static void SetEnabled(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) k.SetValue("FluentStart", "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue("FluentStart", false);
    }

    /// <summary>Scheduled task that starts FluentStart at logon with highest privileges (UAC prompt). Lets the hooks see elevated windows.</summary>
    public static void InstallElevatedTask() =>
        RunSchtasks($"/Create /TN {TaskName} /TR \"\\\"{Environment.ProcessPath}\\\"\" /SC ONLOGON /RL HIGHEST /F");

    public static void RemoveElevatedTask() => RunSchtasks($"/Delete /TN {TaskName} /F");

    private static void RunSchtasks(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex) { Log.Warn("schtasks failed", ex); }
    }
}
