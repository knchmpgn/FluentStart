using System;
using System.Linq;

namespace FluentStart;

/// <summary>Ranks how well a query matches an app name. 0 = no match.</summary>
public static class SearchScorer
{
    private static readonly char[] Separators = { ' ', '-', '_', '.', '(', ')', '[', ']', '+' };

    public static int Score(string query, params string[] names)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return 1;
        int best = 0;
        foreach (var n in names) best = Math.Max(best, ScoreOne(query, n));
        return best;
    }

    private static int ScoreOne(string q, string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        const StringComparison cmp = StringComparison.CurrentCultureIgnoreCase;

        if (name.Equals(q, cmp)) return 100;
        if (name.StartsWith(q, cmp)) return 80;

        var words = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, cmp))) return 60;

        string initials = new string(words.Select(w => w[0]).ToArray());
        if (initials.StartsWith(q, cmp)) return 50;      // "vsc" -> Visual Studio Code

        return name.Contains(q, cmp) ? 30 : 0;
    }
}
