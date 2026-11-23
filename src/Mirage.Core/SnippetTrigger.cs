using System.Text;

namespace Mirage.Core;

/// <summary>
/// Tracks recently typed characters and reports when a snippet keyword was just completed.
/// Keywords fire when typed in full (e.g. ";sig"); longest keyword wins.
/// </summary>
public sealed class SnippetTrigger
{
    readonly StringBuilder _buffer = new();
    List<Snippet> _snippets = new();
    int _maxLen = 1;

    public void SetSnippets(IEnumerable<Snippet> snippets)
    {
        _snippets = snippets.Where(s => s.Keyword.Length >= 2).OrderByDescending(s => s.Keyword.Length).ToList();
        _maxLen = Math.Max(1, _snippets.Select(s => s.Keyword.Length).DefaultIfEmpty(1).Max());
        _buffer.Clear();
    }

    public void Reset() => _buffer.Clear();

    public void Backspace()
    {
        if (_buffer.Length > 0) _buffer.Length--;
    }

    /// <summary>Feed one typed character. Returns the snippet whose keyword was just completed.</summary>
    public Snippet? Feed(char c)
    {
        if (char.IsControl(c)) { _buffer.Clear(); return null; }
        _buffer.Append(c);
        if (_buffer.Length > _maxLen * 2) _buffer.Remove(0, _buffer.Length - _maxLen);
        var text = _buffer.ToString();
        foreach (var s in _snippets)
        {
            if (text.EndsWith(s.Keyword, StringComparison.Ordinal))
            {
                // Only fire at a word boundary so "a;date" inside a token doesn't trigger.
                var start = text.Length - s.Keyword.Length;
                if (start > 0 && char.IsLetterOrDigit(text[start - 1]) && char.IsLetterOrDigit(s.Keyword[0])) continue;
                _buffer.Clear();
                return s;
            }
        }
        return null;
    }
}
