using System;
using System.Collections.Generic;
using System.IO;

namespace FluentStart;

/// <summary>Tiny size-capped file logger (data/log.txt). Never throws.</summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly HashSet<string> Once = new();
    private static string? _path;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    /// <summary>Logs a warning only the first time <paramref name="key"/> is seen (use inside hot paths).</summary>
    public static void WarnOnce(string key, string message, Exception? ex = null)
    {
        lock (Gate) { if (!Once.Add(key)) return; }
        Write("WARN", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                _path ??= Path.Combine(Storage.DataDir, "log.txt");
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(_path, _path + ".old", true);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += " :: " + ex;
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
