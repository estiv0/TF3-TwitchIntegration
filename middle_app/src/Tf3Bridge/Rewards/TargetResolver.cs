using System.Globalization;
using System.Text;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Rewards;

/// <summary>
/// Finds the object a viewer typed in the reward input. Comparison ignores case, Polish diacritics,
/// extra spaces and the " | owner" part of game names. Exact match wins, then a unique prefix,
/// then a unique substring; anything else is rejected (the viewer gets the points back).
/// </summary>
public static class TargetResolver
{
    public sealed record Result(GameObject? Target, string? Error);

    public static Result Resolve(IEnumerable<GameObject> objects, GameObjectKind kind, string input)
    {
        var query = Normalize(input);
        if (query.Length == 0) return new(null, Loc.T("no name given"));
        var candidates = objects.Where(o => o.Kind == kind).Select(o => (Object: o, Key: Normalize(o.Name))).ToList();
        if (candidates.Count == 0) return new(null, Loc.T("no game data (is TF3 running?)"));

        foreach (var match in new Func<string, bool>[] { k => k == query, k => k.StartsWith(query, StringComparison.Ordinal), k => k.Contains(query, StringComparison.Ordinal) })
        {
            var found = candidates.Where(c => match(c.Key)).Select(c => c.Object).DistinctBy(o => o.Entity).ToList();
            if (found.Count == 1) return new(found[0], null);
            if (found.Count > 1)
            {
                var list = string.Join(", ", found.Take(5).Select(o => o.Name)) + (found.Count > 5 ? "…" : "");
                return new(null, Loc.F("\"{0}\" matches several: {1}", input.Trim(), list));
            }
        }
        return new(null, Loc.F("\"{0}\" not found", input.Trim()));
    }

    /// <summary>Lower case, no diacritics (ł → l too), single spaces.</summary>
    internal static string Normalize(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch switch { 'ł' => 'l', 'Ł' => 'l', _ => char.ToLowerInvariant(ch) };
            if (char.IsWhiteSpace(c) || c == '|')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
