using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mirage.Core;

public sealed record TextTransform(string Id, string Name, string Glyph, Func<string, string> Apply);

/// <summary>Quick actions on selected or clipboard text.</summary>
public static class TextTools
{
    public static readonly IReadOnlyList<TextTransform> Transforms = new List<TextTransform>
    {
        new("upper", "UPPERCASE", "", s => s.ToUpperInvariant()),
        new("lower", "lowercase", "", s => s.ToLowerInvariant()),
        new("title", "Title Case", "", s => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())),
        new("sentence", "Sentence case", "", SentenceCase),
        new("trim", "Clean up spaces", "", CleanSpaces),
        new("oneline", "Join into one line", "", s => Regex.Replace(s, @"\s*\r?\n\s*", " ").Trim()),
        new("sort", "Sort lines", "", s => string.Join(Environment.NewLine, SplitLines(s).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))),
        new("dedupe", "Remove duplicate lines", "", s => string.Join(Environment.NewLine, SplitLines(s).Distinct())),
        new("bullets", "Make bullet list", "", s => string.Join(Environment.NewLine, SplitLines(s).Where(l => l.Trim().Length > 0).Select(l => "- " + l.Trim()))),
        new("json", "Format JSON", "", FormatJson),
        new("jsonmin", "Minify JSON", "", MinifyJson),
        new("urlenc", "URL encode", "", Uri.EscapeDataString),
        new("urldec", "URL decode", "", WebUtility.UrlDecode),
        new("b64enc", "Base64 encode", "", s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        new("b64dec", "Base64 decode", "", s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()))),
        new("slug", "Make slug", "", Slug),
        new("snake", "snake_case", "", s => string.Join("_", Words(s)).ToLowerInvariant()),
        new("camel", "camelCase", "", Camel),
        new("reverse", "Reverse text", "", s => new string(s.Reverse().ToArray())),
    };

    public static string Stats(string s)
    {
        var words = Regex.Matches(s, @"\S+").Count;
        var lines = SplitLines(s).Count;
        var minutes = Math.Max(1, (int)Math.Round(words / 230.0));
        return $"{words:N0} words · {s.Length:N0} characters · {lines:N0} lines · ~{minutes} min read";
    }

    public static List<string> SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n').ToList();

    static string SentenceCase(string s)
    {
        var lower = s.ToLowerInvariant();
        return Regex.Replace(lower, @"(^\s*\p{Ll})|([.!?]\s+\p{Ll})", m => m.Value.ToUpperInvariant());
    }

    static string CleanSpaces(string s)
    {
        var lines = SplitLines(s).Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim());
        return Regex.Replace(string.Join(Environment.NewLine, lines), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine).Trim();
    }

    static string FormatJson(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static string MinifyJson(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static IEnumerable<string> Words(string s) =>
        Regex.Matches(Regex.Replace(s, "([a-z])([A-Z])", "$1 $2"), @"[\p{L}\p{N}]+").Select(m => m.Value);

    static string Slug(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return string.Join("-", Words(sb.ToString())).ToLowerInvariant();
    }

    static string Camel(string s)
    {
        var w = Words(s).Select(x => x.ToLowerInvariant()).ToList();
        if (w.Count == 0) return "";
        return w[0] + string.Concat(w.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    /// <summary>Expand {date}, {time}, {datetime}, {clipboard}, {weekday}, {year}, {guid}.</summary>
    public static string ExpandPlaceholders(string text, DateTime now, Func<string?> clipboard)
    {
        return Regex.Replace(text, @"\{(date|time|datetime|clipboard|weekday|year|guid|isodate)\}", m => m.Groups[1].Value switch
        {
            "date" => now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            "isodate" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => now.ToString("HH:mm", CultureInfo.InvariantCulture),
            "datetime" => now.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture),
            "weekday" => now.ToString("dddd", CultureInfo.InvariantCulture),
            "year" => now.Year.ToString(CultureInfo.InvariantCulture),
            "guid" => Guid.NewGuid().ToString(),
            "clipboard" => clipboard() ?? "",
            _ => m.Value
        });
    }

    /// <summary>Fill a {query} template. URL templates get the query escaped.</summary>
    public static string FillTemplate(string template, string query, bool escape)
    {
        var raw = template.Replace("{query:raw}", query);
        return raw.Replace("{query}", escape ? Uri.EscapeDataString(query) : query);
    }

    public static bool LooksLikeUrl(string s)
    {
        s = s.Trim();
        if (s.Contains(' ')) return false;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return true;
        return Regex.IsMatch(s, @"^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|org|net|io|dev|ai|app|co|me|gov|edu|uk|ae|jo|de|fr|gg|tv|xyz|info|ly|so|sh)(/\S*)?$", RegexOptions.IgnoreCase);
    }

    public static string NormalizeUrl(string s)
    {
        s = s.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? s : "https://" + s;
    }
}
