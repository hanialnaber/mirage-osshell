namespace Mirage.Core;

/// <summary>
/// Fast fuzzy matcher tuned for launcher-style queries.
/// Exact &gt; prefix &gt; word-start acronym &gt; substring &gt; subsequence.
/// Returns -1 when the query does not match.
/// </summary>
public static class Fuzzy
{
    public static int Score(string query, string text)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        if (string.IsNullOrEmpty(text)) return -1;

        var q = query.Trim();
        if (q.Length == 0) return 1;

        if (text.Equals(q, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return 900 - Math.Min(100, text.Length - q.Length);

        // Every query word must match a word start ("vs code" -> "Visual Studio Code")
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => HasWordStartingWith(text, w)))
            return 780 - Math.Min(80, text.Length / 2);

        var acronym = Acronym(text);
        if (acronym.StartsWith(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            return 760 - Math.Min(60, acronym.Length - q.Length);

        if (HasWordStartingWith(text, q))
            return 720 - Math.Min(80, text.Length / 2);

        var idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return 600 - Math.Min(150, idx * 4) - Math.Min(50, text.Length / 4);

        return Subsequence(q.Replace(" ", ""), text);
    }

    static bool HasWordStartingWith(string text, string word)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsWordStart(text, i) &&
                string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                i + word.Length <= text.Length)
                return true;
        }
        return false;
    }

    static bool IsWordStart(string text, int i)
    {
        if (i == 0) return true;
        char prev = text[i - 1], c = text[i];
        if (!char.IsLetterOrDigit(prev)) return char.IsLetterOrDigit(c);
        if (char.IsLower(prev) && char.IsUpper(c)) return true;   // camelCase
        if (char.IsLetter(prev) && char.IsDigit(c)) return true;
        return false;
    }

    public static string Acronym(string text)
    {
        var chars = new List<char>();
        for (int i = 0; i < text.Length; i++)
            if (IsWordStart(text, i)) chars.Add(char.ToLowerInvariant(text[i]));
        return new string(chars.ToArray());
    }

    static int Subsequence(string q, string text)
    {
        int ti = 0, gaps = 0, wordStarts = 0, lastMatch = -1;
        foreach (var qc in q)
        {
            bool found = false;
            while (ti < text.Length)
            {
                if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(qc))
                {
                    if (lastMatch >= 0 && ti - lastMatch > 1) gaps += ti - lastMatch - 1;
                    if (IsWordStart(text, ti)) wordStarts++;
                    lastMatch = ti;
                    ti++;
                    found = true;
                    break;
                }
                ti++;
            }
            if (!found) return -1;
        }
        var score = 400 + wordStarts * 25 - gaps * 6 - Math.Min(60, text.Length / 3);
        return Math.Clamp(score, 1, 499);
    }
}
