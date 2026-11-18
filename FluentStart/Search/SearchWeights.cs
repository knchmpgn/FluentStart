using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentStart;

/// <summary>
/// Learns what you actually activate from search and turns it into a score boost.
/// Two signals, both decayed exponentially (half-life 60 days) so old habits fade:
///  - global frecency: how often the item is activated at all;
///  - query affinity: how often it was activated after typing a query that starts with what you typed now
///    (activating "Disk Cleanup" after typing "disk" a few times makes "d" and "di" rank it first as well).
/// Boosts only re-rank results that already match the query; they never add results. Persisted to searchweights.json.
/// </summary>
internal sealed class SearchWeights
{
    private const string FileName = "searchweights.json";
    private const double HalfLifeDays = 60;
    private const double MaxGlobalBoost = 12;   // below a name-prefix -> exact-match gap (80 -> 100): habit alone never beats an exact name
    private const double MaxQueryBoost = 30;
    private const int MaxEntries = 400, MaxQueriesPerEntry = 24, MaxQueryLength = 40;

    public sealed class Entry
    {
        public double Count { get; set; }
        public DateTime Last { get; set; }
        public Dictionary<string, double> Queries { get; set; } = new();
    }

    private Dictionary<string, Entry> _data;

    public SearchWeights()
    {
        var loaded = Storage.Load<Dictionary<string, Entry>>(FileName);
        _data = new Dictionary<string, Entry>(loaded ?? new(), StringComparer.OrdinalIgnoreCase);

        // One-time import of the old favorites.json counters (id -> launch count) so existing history is not lost.
        if (loaded == null)
        {
            var old = Storage.Load<Dictionary<string, int>>("favorites.json");
            if (old != null && old.Count > 0)
            {
                foreach (var kv in old) _data[kv.Key] = new Entry { Count = kv.Value, Last = DateTime.UtcNow };
                Storage.Save(FileName, _data);
            }
        }
    }

    private static string Normalize(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "";
        string n = string.Join(' ', q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();
        return n.Length > MaxQueryLength ? n[..MaxQueryLength] : n;
    }

    private static double Decay(DateTime last, DateTime now) =>
        Math.Pow(0.5, Math.Max(0, (now - last).TotalDays) / HalfLifeDays);

    /// <summary>Score boost (0 .. ~42) for <paramref name="key"/> when the current query is <paramref name="query"/>.</summary>
    public double Boost(string key, string query)
    {
        if (!_data.TryGetValue(key, out var e)) return 0;
        double d = Decay(e.Last, DateTime.UtcNow);
        double boost = Math.Min(MaxGlobalBoost, 4 * Math.Log(1 + e.Count * d));

        string q = Normalize(query);
        if (q.Length > 0 && e.Queries.Count > 0)
        {
            double affinity = 0;
            foreach (var kv in e.Queries)
            {
                if (kv.Key.StartsWith(q, StringComparison.Ordinal)) affinity += kv.Value;            // typed "d", used after "disk"
                else if (q.StartsWith(kv.Key, StringComparison.Ordinal)) affinity += kv.Value * 0.5; // typed "disk cl", used after "disk"
            }
            boost += Math.Min(MaxQueryBoost, 10 * Math.Log(1 + affinity * d));
        }
        return boost;
    }

    /// <summary>Records one activation of <paramref name="key"/> after the user typed <paramref name="query"/> (may be empty).</summary>
    public void Record(string key, string query)
    {
        var now = DateTime.UtcNow;
        if (!_data.TryGetValue(key, out var e)) _data[key] = e = new Entry();

        // Bring the stored numbers up to date before adding the new use, so old habits keep fading.
        double f = Decay(e.Last == default ? now : e.Last, now);
        e.Count *= f;
        foreach (var k in e.Queries.Keys.ToList())
        {
            e.Queries[k] *= f;
            if (e.Queries[k] < 0.05) e.Queries.Remove(k);
        }
        e.Count += 1;
        e.Last = now;

        string q = Normalize(query);
        if (q.Length > 0)
        {
            e.Queries[q] = (e.Queries.TryGetValue(q, out double c) ? c : 0) + 1;
            if (e.Queries.Count > MaxQueriesPerEntry)
                e.Queries.Remove(e.Queries.OrderBy(kv => kv.Value).First().Key);
        }

        if (_data.Count > MaxEntries)
            foreach (var stale in _data.OrderBy(kv => kv.Value.Count * Decay(kv.Value.Last, now)).Take(_data.Count - MaxEntries).Select(kv => kv.Key).ToList())
                _data.Remove(stale);

        Storage.Save(FileName, _data);
    }
}
