using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirage.Core;

public enum GlassMode { Auto, Acrylic, Mica, Solid }
public enum Permission { Allow, Ask, Deny }

public sealed class Snippet
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class WorkspaceItem
{
    /// <summary>app | url | folder | file | command</summary>
    public string Type { get; set; } = "app";
    /// <summary>Path, URL, app name or command line.</summary>
    public string Target { get; set; } = "";
    public string? Args { get; set; }
}

public sealed class Workspace
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<WorkspaceItem> Items { get; set; } = new();
}

/// <summary>A user command. Keyword triggers it; {query} is replaced with what follows.</summary>
public sealed class CustomCommand
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>url | shell | open</summary>
    public string Type { get; set; } = "url";
    /// <summary>URL template, command line, or path. {query} and {query:raw} are replaced.</summary>
    public string Template { get; set; } = "";
    /// <summary>For shell commands: keep the terminal window open.</summary>
    public bool KeepOpen { get; set; } = true;
}

public sealed class AiTarget
{
    public string Name { get; set; } = "";
    /// <summary>URL template with {query}; if it has no {query} the prompt is copied to the clipboard first.</summary>
    public string Url { get; set; } = "";
    /// <summary>Optional desktop app to open instead of the URL (path or app name).</summary>
    public string? App { get; set; }
}

public sealed class BridgeSettings
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 47821;
    public string Token { get; set; } = "";
    public Dictionary<string, Permission> Permissions { get; set; } = new();
    public bool LogActivity { get; set; } = true;
}

public sealed class Settings
{
    public string Hotkey { get; set; } = "Alt+Space";
    public string SelectionHotkey { get; set; } = "Ctrl+Alt+Space";
    public GlassMode Glass { get; set; } = GlassMode.Auto;
    public string Accent { get; set; } = "#7C8CFF";
    public bool LaunchAtStartup { get; set; } = true;
    public bool HideOnFocusLoss { get; set; } = true;
    public string SearchEngine { get; set; } = "Google";
    public string DefaultAi { get; set; } = "ChatGPT";
    public bool ClipboardHistory { get; set; } = true;
    public int ClipboardLimit { get; set; } = 100;
    public bool PersistClipboard { get; set; } = false;
    public bool SnippetAutoExpand { get; set; } = false;
    public bool FileSearch { get; set; } = true;
    public int MaxResults { get; set; } = 9;
    public List<string> Pinned { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();
    public List<Workspace> Workspaces { get; set; } = new();
    public List<CustomCommand> Commands { get; set; } = new();
    public List<AiTarget> AiTargets { get; set; } = new();
    public BridgeSettings Bridge { get; set; } = new();

    public static readonly Dictionary<string, string> SearchEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google"] = "https://www.google.com/search?q={query}",
        ["Bing"] = "https://www.bing.com/search?q={query}",
        ["DuckDuckGo"] = "https://duckduckgo.com/?q={query}",
        ["Brave"] = "https://search.brave.com/search?q={query}",
        ["Perplexity"] = "https://www.perplexity.ai/search?q={query}",
    };

    public static Settings CreateDefault()
    {
        var s = new Settings();
        s.Snippets.AddRange(new[]
        {
            new Snippet { Keyword = ";date", Name = "Today's date", Text = "{date}" },
            new Snippet { Keyword = ";now", Name = "Date and time", Text = "{datetime}" },
            new Snippet { Keyword = ";shrug", Name = "Shrug", Text = "¯\\_(ツ)_/¯" },
            new Snippet { Keyword = ";thanks", Name = "Thanks reply", Text = "Thanks for getting back to me. I'll take a look and reply shortly." },
            new Snippet { Keyword = ";meet", Name = "Meeting times", Text = "Would any of these times work for a quick call?\n- \n- \n- " },
        });
        s.Workspaces.Add(new Workspace
        {
            Name = "Focus",
            Description = "Example: edit me in Settings > Library",
            Items =
            {
                new WorkspaceItem { Type = "url", Target = "https://calendar.google.com" },
                new WorkspaceItem { Type = "url", Target = "https://mail.google.com" },
                new WorkspaceItem { Type = "folder", Target = "%USERPROFILE%\\Documents" },
            }
        });
        s.Commands.AddRange(new[]
        {
            new CustomCommand { Keyword = "gh", Name = "Search GitHub", Type = "url", Template = "https://github.com/search?q={query}" },
            new CustomCommand { Keyword = "yt", Name = "Search YouTube", Type = "url", Template = "https://www.youtube.com/results?search_query={query}" },
            new CustomCommand { Keyword = "maps", Name = "Google Maps", Type = "url", Template = "https://www.google.com/maps/search/{query}" },
            new CustomCommand { Keyword = "tr", Name = "Translate", Type = "url", Template = "https://translate.google.com/?sl=auto&tl=en&text={query}" },
            new CustomCommand { Keyword = "ip", Name = "Network info (ipconfig)", Type = "shell", Template = "ipconfig /all", KeepOpen = true },
        });
        s.AiTargets.AddRange(DefaultAiTargets());
        s.Bridge.Token = Tokens.NewToken();
        return s;
    }

    public static IEnumerable<AiTarget> DefaultAiTargets() => new[]
    {
        new AiTarget { Name = "ChatGPT", Url = "https://chatgpt.com/?q={query}" },
        new AiTarget { Name = "Claude", Url = "https://claude.ai/new?q={query}" },
        new AiTarget { Name = "Perplexity", Url = "https://www.perplexity.ai/search?q={query}" },
        new AiTarget { Name = "Grok", Url = "https://grok.com/?q={query}" },
        new AiTarget { Name = "Copilot", Url = "https://copilot.microsoft.com/?q={query}" },
        new AiTarget { Name = "Gemini", Url = "https://gemini.google.com/app" },
    };

    /// <summary>Fill gaps after loading an older or hand-edited file.</summary>
    public void Normalize()
    {
        Pinned ??= new(); Snippets ??= new(); Workspaces ??= new(); Commands ??= new();
        AiTargets ??= new(); Bridge ??= new();
        Bridge.Permissions ??= new();
        if (AiTargets.Count == 0) AiTargets.AddRange(DefaultAiTargets());
        if (string.IsNullOrWhiteSpace(Bridge.Token) || Bridge.Token.Length < 24) Bridge.Token = Tokens.NewToken();
        if (Bridge.Port is < 1024 or > 65535) Bridge.Port = 47821;
        if (MaxResults is < 3 or > 20) MaxResults = 9;
        if (ClipboardLimit is < 10 or > 1000) ClipboardLimit = 100;
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Alt+Space";
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch (JsonException) { /* fall through to defaults; caller keeps a backup */ throw; }
        var d = CreateDefault();
        d.Normalize();
        return d;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

public static class Tokens
{
    public static string NewToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return "mrg_" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        var x = System.Text.Encoding.UTF8.GetBytes(a ?? "");
        var y = System.Text.Encoding.UTF8.GetBytes(b ?? "");
        return x.Length == y.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }
}
