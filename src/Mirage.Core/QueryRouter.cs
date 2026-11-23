namespace Mirage.Core;

public enum QueryMode { Default, Windows, Files, Clipboard, Snippets, NoteAdd, Notes, Workspaces, Ai, Shell, Calc, Command }

public sealed record ParsedQuery(QueryMode Mode, string Text, string Raw, CustomCommand? Command = null);

public sealed record ModeHint(string Prefix, string Name, string Description, string Glyph);

/// <summary>Turns what the user typed into a mode plus the remaining text.</summary>
public static class QueryRouter
{
    public static readonly IReadOnlyList<ModeHint> Hints = new[]
    {
        new ModeHint("w ", "Windows", "Switch to an open window", ""),
        new ModeHint("f ", "Files", "Find files by name", ""),
        new ModeHint("c ", "Clipboard", "Clipboard history", ""),
        new ModeHint(";", "Snippets", "Insert a saved snippet", ""),
        new ModeHint("ws ", "Workspaces", "Open a set of apps, sites and folders", ""),
        new ModeHint("? ", "Ask AI", "Send a prompt to your AI", ""),
        new ModeHint("n ", "Quick note", "Jot a note", ""),
        new ModeHint("notes ", "Notes", "Search your notes", ""),
        new ModeHint("> ", "Terminal", "Run a command", ""),
        new ModeHint("= ", "Calculator", "Math and unit conversion", ""),
    };

    public static ParsedQuery Parse(string raw, Settings settings)
    {
        raw ??= "";
        var t = raw.TrimStart();

        static bool Pre(string s, string p) => s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        static string After(string s, int n) => s.Length > n ? s[n..].Trim() : "";

        if (Pre(t, "notes") && (t.Length == 5 || t[5] == ' ')) return new(QueryMode.Notes, After(t, 5), raw);
        if (Pre(t, "ws ")) return new(QueryMode.Workspaces, After(t, 3), raw);
        if (Pre(t, "w ")) return new(QueryMode.Windows, After(t, 2), raw);
        if (Pre(t, "f ")) return new(QueryMode.Files, After(t, 2), raw);
        if (Pre(t, "c ") || Pre(t, "clip ")) return new(QueryMode.Clipboard, After(t, t.IndexOf(' ') + 1), raw);
        if (Pre(t, "n ")) return new(QueryMode.NoteAdd, After(t, 2), raw);
        if (Pre(t, "ai ")) return new(QueryMode.Ai, After(t, 3), raw);
        if (t.StartsWith('?')) return new(QueryMode.Ai, After(t, 1), raw);
        if (t.StartsWith('>')) return new(QueryMode.Shell, After(t, 1), raw);
        if (t.StartsWith('=')) return new(QueryMode.Calc, After(t, 1), raw);
        if (t.StartsWith(';')) return new(QueryMode.Snippets, t, raw);

        // Exact mode names typed alone open the mode ("windows", "clipboard"...)
        switch (t.Trim().ToLowerInvariant())
        {
            case "clipboard": return new(QueryMode.Clipboard, "", raw);
            case "windows": return new(QueryMode.Windows, "", raw);
            case "workspaces": return new(QueryMode.Workspaces, "", raw);
        }

        var space = t.IndexOf(' ');
        if (space > 0)
        {
            var kw = t[..space];
            var cmd = settings.Commands.FirstOrDefault(c => c.Keyword.Equals(kw, StringComparison.OrdinalIgnoreCase));
            if (cmd != null) return new(QueryMode.Command, After(t, space + 1), raw, cmd);
        }

        return new(QueryMode.Default, t.Trim(), raw);
    }
}
