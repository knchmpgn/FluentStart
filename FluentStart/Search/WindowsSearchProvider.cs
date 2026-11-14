using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentStart;

/// <summary>File results from the Windows Search index (ADODB + Search.CollatorDSO, late bound).</summary>
public interface IFileSearchProvider
{
    string Name { get; }
    Task<List<FileHit>> SearchAsync(string query, SearchFilter filter, int max, CancellationToken ct);
}

public sealed record FileHit(string FullPath, bool IsFolder);

internal sealed class WindowsSearchProvider : IFileSearchProvider
{
    public string Name => "Windows Search";

    public Task<List<FileHit>> SearchAsync(string query, SearchFilter filter, int max, CancellationToken ct) =>
        Task.Run(() => Query(query, filter, max, ct), ct);

    internal static string BuildSql(string query, SearchFilter filter, int max)
    {
        string q = EscapeLike(query);
        if (filter == SearchFilter.All)
        {
            return $"SELECT TOP {max} System.ItemPathDisplay, System.Kind FROM SystemIndex " +
                   $"WHERE System.FileName LIKE '%{q}%' AND SCOPE='file:' ORDER BY System.DateModified DESC";
        }
        string kind = filter switch
        {
            SearchFilter.Documents => " AND System.Kind = 'document'",
            SearchFilter.Folders => " AND System.Kind = 'folder'",
            _ => ""
        };
        // UNVERIFIED: column names / query shape against a real index; adjust from errors in data/log.txt.
        return $"SELECT TOP {max} System.ItemPathDisplay, System.Kind FROM SystemIndex " +
               $"WHERE System.FileName LIKE '%{q}%' AND SCOPE='file:'{kind} ORDER BY System.DateModified DESC";
    }

    /// <summary>Escapes a user string for a LIKE pattern inside single quotes.</summary>
    internal static string EscapeLike(string s) =>
        s.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static List<FileHit> Query(string query, SearchFilter filter, int max, CancellationToken ct)
    {
        var hits = new List<FileHit>();
        dynamic? conn = null, rs = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) return hits;
            conn = Activator.CreateInstance(type)!;
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            rs = conn.Execute(BuildSql(query, filter, max));

            while (!rs.EOF && !ct.IsCancellationRequested)
            {
                string? path = rs.Fields["System.ItemPathDisplay"].Value as string;
                object? kind = rs.Fields["System.Kind"].Value;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    bool folder = kind is object[] arr ? arr.Any(k => string.Equals(k as string, "folder", StringComparison.OrdinalIgnoreCase))
                                                        : string.Equals(kind as string, "folder", StringComparison.OrdinalIgnoreCase);
                    hits.Add(new FileHit(path, folder || Directory.Exists(path)));
                }
                rs.MoveNext();
            }
        }
        catch (Exception ex) { Log.WarnOnce("winsearch", "Windows Search query failed", ex); }
        finally
        {
            try { rs?.Close(); } catch { }
            try { conn?.Close(); } catch { }
        }
        return hits;
    }
}
