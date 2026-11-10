using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentStart;

internal static class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Portable: ./data next to the exe; falls back to %AppData%\FluentStart if not writable.</summary>
    public static string DataDir { get; private set; } = ResolveDataDir();

    /// <summary>Test hook: redirect all persistence to another directory.</summary>
    internal static void UseDirectory(string path)
    {
        Directory.CreateDirectory(path);
        DataDir = path;
    }

    private static string ResolveDataDir()
    {
        try
        {
            string portable = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(portable);
            string probe = Path.Combine(portable, ".w");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            string fb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentStart");
            Directory.CreateDirectory(fb);
            return fb;
        }
    }

    public static T? Load<T>(string file) where T : class
    {
        try
        {
            string p = Path.Combine(DataDir, file);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p), Options) : null;
        }
        catch (Exception ex) { Log.Warn("Load failed: " + file, ex); return null; }
    }

    /// <summary>Reads a JSON file from an arbitrary path (used by import).</summary>
    public static T? LoadFrom<T>(string fullPath) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(fullPath), Options); }
        catch (Exception ex) { Log.Warn("LoadFrom failed: " + fullPath, ex); return null; }
    }

    /// <summary>Writes JSON to an arbitrary path (used by export).</summary>
    public static bool SaveTo<T>(string fullPath, T value)
    {
        try { File.WriteAllText(fullPath, JsonSerializer.Serialize(value, Options)); return true; }
        catch (Exception ex) { Log.Warn("SaveTo failed: " + fullPath, ex); return false; }
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            string p = Path.Combine(DataDir, file);
            string tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, p, true);
        }
        catch (Exception ex) { Log.Warn("Save failed: " + file, ex); }
    }
}
