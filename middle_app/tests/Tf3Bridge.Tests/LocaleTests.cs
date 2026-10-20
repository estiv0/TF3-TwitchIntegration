using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tf3Bridge.Chat;

namespace Tf3Bridge.Tests;

/// <summary>
/// Keeps src\Tf3Bridge\Locales\*.json in step with the code: every English text passed to Loc.T / Loc.F / Loc.P / Loc.N
/// as a literal must be in every locale file, nothing else may be there, and placeholders must match.
/// </summary>
public sealed partial class LocaleTests
{
    private static readonly string SourceDir = FindSource();

    private static string FindSource([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Tf3Bridge"));

    private sealed record Key(string Text, string Kind, string Where);

    private static readonly Lazy<(List<Key> Keys, List<string> Problems)> Scan = new(ScanSource);

    private static IEnumerable<string> LocaleFiles() => Directory.GetFiles(Path.Combine(SourceDir, "Locales"), "*.json");

    private static Dictionary<string, JsonElement> Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    public static TheoryData<string> Languages() => [.. LocaleFiles().Select(Path.GetFileNameWithoutExtension).Select(c => c!)];

    [Fact]
    public void The_source_has_keys_and_no_interpolated_ones()
    {
        var (keys, problems) = Scan.Value;
        Assert.True(keys.Count > 300, $"only {keys.Count} keys found in {SourceDir}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Locale_file_has_every_key_and_nothing_else(string language)
    {
        var file = Read(Path.Combine(SourceDir, "Locales", language + ".json"));
        var keys = Scan.Value.Keys;
        var used = keys.Select(k => k.Text).ToHashSet(StringComparer.Ordinal);

        var missing = keys.Where(k => !file.ContainsKey(k.Text)).DistinctBy(k => k.Text).Select(k => $"missing: {k.Where} {Json(k.Text)}");
        var orphans = file.Keys.Where(k => k is not (Loc.CommandsKey or Loc.ArgumentsKey) && !used.Contains(k)).Select(k => $"not in the code: {Json(k)}");
        var problems = missing.Concat(orphans).ToList();
        Assert.True(problems.Count == 0, $"{language}.json:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_the_placeholders_and_plural_forms(string language)
    {
        var file = Read(Path.Combine(SourceDir, "Locales", language + ".json"));
        var plurals = Scan.Value.Keys.Where(k => k.Kind == "P").Select(k => k.Text).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (key, value) in file)
        {
            if (key is Loc.CommandsKey or Loc.ArgumentsKey) continue;
            string[] forms = value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(v => v.GetString() ?? "")]
                : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [];
            var expected = plurals.Contains(key) ? Loc.PluralForms(language) : 1;
            if (forms.Length != expected || value.ValueKind == JsonValueKind.Array != plurals.Contains(key))
            {
                problems.Add($"{Json(key)}: expected {(expected == 1 && !plurals.Contains(key) ? "a string" : $"an array of {expected} forms")}");
                continue;
            }
            foreach (var t in forms.Where(t => t.Length > 0)) // "" = not translated yet: English is shown
            {
                if (!Placeholders(t).SetEquals(Placeholders(key)))
                    problems.Add($"{Json(key)}: placeholders of {Json(t)} differ");
                try { _ = string.Format(CultureInfo.InvariantCulture, t, new object[10]); }
                catch (FormatException) when (Placeholders(key).Any(p => char.IsDigit(p[1]))) { problems.Add($"{Json(t)}: not a valid format string"); }
                catch (FormatException) { } // plain text with a lone brace, never formatted
            }
        }
        Assert.True(problems.Count == 0, $"{language}.json:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Locale_file_is_embedded_with_known_commands_and_arguments(string language)
    {
        Assert.Contains(language, Loc.Languages);
        _ = CultureInfo.GetCultureInfo(language); // the code must be a culture: number formats and the native name come from it
        var file = Read(Path.Combine(SourceDir, "Locales", language + ".json"));
        if (file.TryGetValue(Loc.CommandsKey, out var commands))
            foreach (var c in commands.EnumerateObject())
            {
                Assert.Contains(c.Name, ChatCommandService.Names);
                Assert.All(c.Value.EnumerateArray(), a => Assert.Matches("^![^\\s!]+$", a.GetString()!));
            }
        if (file.TryGetValue(Loc.ArgumentsKey, out var arguments))
            foreach (var a in arguments.EnumerateObject())
            {
                Assert.Contains(a.Name, ChatAnswers.ArgumentWords);
                Assert.All(a.Value.EnumerateArray(), p => Assert.Matches("^\\S+$", p.GetString()!));
            }
    }

    [Fact]
    public void A_missing_or_broken_translation_falls_back_to_english()
    {
        using var _ = Loc.Use("pl");
        Assert.Equal("No such text anywhere", Loc.T("No such text anywhere"));
        Assert.Equal("a 1 b 2", Loc.F("a {0} b {1}", 1, 2));
        Assert.Equal("Zapisz", Loc.T("Save"));
    }

    // "{0}", "{1:0}" -> "{0}", "{1}"; "{months}" stays; "{{" / "}}" are not placeholders.
    private static HashSet<string> Placeholders(string s) =>
        [.. PlaceholderRegex().Matches(s).Where(m => m.Groups[1].Success).Select(m => "{" + m.Groups[1].Value + "}")];

    [GeneratedRegex(@"\{\{|\}\}|\{([A-Za-z0-9_]+)(?:,[^{}:]*)?(?::[^{}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    private static string Json(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [GeneratedRegex(@"\bLoc\.([TFNP])\(")]
    private static partial Regex CallRegex();

    private static (List<Key>, List<string>) ScanSource()
    {
        var keys = new List<Key>();
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(SourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var code = StripComments(File.ReadAllText(path));
            var name = Path.GetRelativePath(SourceDir, path);
            foreach (Match m in CallRegex().Matches(code))
            {
                var where = $"{name}:{code.AsSpan(0, m.Index).Count('\n') + 1}";
                var kind = m.Groups[1].Value;
                var i = m.Index + m.Length;
                if (kind == "P") i = code.IndexOf(',', i) + 1; // Loc.P(n, "one", "other"): the key is "one"
                var first = SkipSpace(code, i);
                if (code[first] is '$' or '@')
                {
                    problems.Add($"{where}: Loc.{kind} with an interpolated or verbatim string; use Loc.F(\"... {{0}} ...\", arg)");
                    continue;
                }
                if (ReadLiteral(code, first) is not { } text)
                {
                    if (kind == "N") problems.Add($"{where}: Loc.N needs a string literal");
                    continue; // e.g. Loc.T(d.Title): the key comes from a Loc.N elsewhere
                }
                if (text.Length == 0 || text.StartsWith('_')) problems.Add($"{where}: key {Json(text)} is empty or starts with _");
                keys.Add(new(text, kind, where));
            }
        }
        return (keys, problems);
    }

    private static int SkipSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    // "a" + "b" (regular literals only) -> "ab"; null when the argument is anything else.
    private static string? ReadLiteral(string s, int i)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            while (s[i] != '"')
            {
                if (s[i] != '\\') { sb.Append(s[i++]); continue; }
                var e = s[i + 1];
                i += 2;
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            var next = SkipSpace(s, i + 1);
            if (next < s.Length && s[next] == '+' && SkipSpace(s, next + 1) is var after && after < s.Length && s[after] == '"')
                i = after;
            else
                return next < s.Length && s[next] is ',' or ')' ? sb.ToString() : null;
        }
    }

    // Comments become spaces (doc comments show example calls); string and char literals are kept as they are.
    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length) i = Code(s, i, sb, closer: null);
        return sb.ToString();
    }

    // Copies code until `closer` at depth 0 (the end of an interpolation hole) or the end; returns the index after it.
    private static int Code(string s, int i, StringBuilder sb, char? closer)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] is '/' or '*')
            {
                var end = s[i + 1] == '/' ? s.IndexOf('\n', i) : s.IndexOf("*/", i, StringComparison.Ordinal) + 2;
                if (end <= 1) end = s.Length;
                for (; i < end; i++) sb.Append(s[i] == '\n' ? '\n' : ' ');
                continue;
            }
            if (c == '\'' )
            {
                var end = i + 1;
                while (s[end] != '\'') end += s[end] == '\\' ? 2 : 1;
                sb.Append(s, i, end + 1 - i);
                i = end + 1;
                continue;
            }
            if (c is '"' or '$' or '@' && LiteralStart(s, i) is { } start)
            {
                i = Literal(s, i, start, sb);
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}')
            {
                if (depth == 0 && c == closer) { sb.Append(c); return i + 1; }
                depth--;
            }
            sb.Append(c);
            i++;
        }
        return i;
    }

    // Index of the opening quote when s[i..] starts a string literal ($"", @"", $@"", """), else null.
    private static int? LiteralStart(string s, int i)
    {
        var j = i;
        while (j < s.Length && s[j] is '$' or '@') j++;
        return j < s.Length && s[j] == '"' && j - i <= 2 ? j : null;
    }

    private static int Literal(string s, int i, int quote, StringBuilder sb)
    {
        var interpolated = s.AsSpan(i, quote - i).Contains('$');
        var verbatim = s.AsSpan(i, quote - i).Contains('@');
        sb.Append(s, i, quote + 1 - i);
        if (s.AsSpan(quote).StartsWith("\"\"\""))
        {
            var end = s.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal) + 3;
            sb.Append(s, quote + 1, end - quote - 1);
            return end;
        }
        var j = quote + 1;
        while (true)
        {
            var c = s[j];
            if (c == '"' && verbatim && j + 1 < s.Length && s[j + 1] == '"') { sb.Append("\"\""); j += 2; continue; }
            if (c == '"') { sb.Append(c); return j + 1; }
            if (c == '\\' && !verbatim) { sb.Append(s, j, 2); j += 2; continue; }
            if (interpolated && c == '{')
            {
                if (s[j + 1] == '{') { sb.Append("{{"); j += 2; continue; }
                sb.Append(c);
                j = Code(s, j + 1, sb, closer: '}');
                continue;
            }
            sb.Append(c);
            j++;
        }
    }
}
