using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirage.Core.Agent;

public sealed class ToolDef
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public Permission DefaultPermission { get; init; } = Permission.Allow;
    /// <summary>Short human summary shown in the confirmation prompt.</summary>
    public required Func<JsonElement, string> Summarize { get; init; }
    public required Func<JsonElement, Task<object?>> Handler { get; init; }
    public bool ReadOnly { get; init; }
}

public sealed record ToolCallResult(bool IsError, string Text);

public sealed record ActivityEntry(DateTime Time, string Client, string Tool, string Summary, string Outcome);

/// <summary>
/// The agent bridge's brain: a fixed allow-list of desktop tools, each gated by a
/// per-tool permission (Allow / Ask / Deny). Transport-agnostic (used by MCP and REST).
/// </summary>
public sealed class AgentHost
{
    readonly IDesktop _desktop;
    readonly IConfirmer _confirmer;
    readonly Dictionary<string, ToolDef> _tools;
    readonly SemaphoreSlim _confirmGate = new(1, 1);

    public event Action<ActivityEntry>? Activity;
    /// <summary>Raised when a user picks "Always allow" so the app can persist settings.</summary>
    public event Action? SettingsChanged;

    public AgentHost(IDesktop desktop, IConfirmer confirmer)
    {
        _desktop = desktop;
        _confirmer = confirmer;
        _tools = BuildTools().ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ToolDef> Tools => _tools.Values;

    public Permission EffectivePermission(string tool)
    {
        if (!_tools.TryGetValue(tool, out var def)) return Permission.Deny;
        return _desktop.Settings.Bridge.Permissions.TryGetValue(tool, out var p) ? p : def.DefaultPermission;
    }

    public async Task<ToolCallResult> CallAsync(string name, JsonElement args, string client)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return new ToolCallResult(true, $"Unknown tool '{name}'. Call tools/list to see what Mirage offers.");

        string summary;
        try { summary = tool.Summarize(args); }
        catch (Exception ex) { return new ToolCallResult(true, "Bad arguments: " + ex.Message); }

        var permission = EffectivePermission(name);
        if (permission == Permission.Deny)
        {
            Log(client, name, summary, "blocked");
            return new ToolCallResult(true, $"The user has turned off '{name}' for agents in Mirage settings.");
        }
        if (permission == Permission.Ask)
        {
            ConfirmResult answer;
            await _confirmGate.WaitAsync();
            try { answer = await _confirmer.ConfirmAsync(client, name, summary); }
            finally { _confirmGate.Release(); }

            if (answer == ConfirmResult.Deny)
            {
                Log(client, name, summary, "denied");
                return new ToolCallResult(true, "The user declined this action.");
            }
            if (answer == ConfirmResult.AlwaysAllow)
            {
                _desktop.Settings.Bridge.Permissions[name] = Permission.Allow;
                SettingsChanged?.Invoke();
            }
        }

        try
        {
            var result = await tool.Handler(args);
            Log(client, name, summary, "ok");
            var text = result switch
            {
                null => "done",
                string s => s,
                _ => JsonSerializer.Serialize(result, Settings.JsonOptions)
            };
            return new ToolCallResult(false, text);
        }
        catch (Exception ex)
        {
            Log(client, name, summary, "error: " + ex.Message);
            return new ToolCallResult(true, ex.Message);
        }
    }

    void Log(string client, string tool, string summary, string outcome)
    {
        if (_desktop.Settings.Bridge.LogActivity)
            Activity?.Invoke(new ActivityEntry(DateTime.Now, client, tool, summary, outcome));
    }

    // ---------- tool catalogue ----------

    static JsonObject Schema(params (string name, string type, string desc, bool required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, type, desc, required) in props)
        {
            p[name] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (required) req.Add(name);
        }
        var o = new JsonObject { ["type"] = "object", ["properties"] = p };
        if (req.Count > 0) o["required"] = req;
        return o;
    }

    static string Str(JsonElement a, string name, bool required = true)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            if (required && s.Length == 0) throw new ArgumentException($"'{name}' is empty");
            return s;
        }
        if (required) throw new ArgumentException($"'{name}' is required");
        return "";
    }

    static int Int(JsonElement a, string name, int fallback, int min, int max)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return Math.Clamp(i, min, max);
        return fallback;
    }

    static long Handle(JsonElement a)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("handle", out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out l)) return l;
        }
        throw new ArgumentException("'handle' is required (from list_windows)");
    }

    static string Clip(string s, int n = 80) => s.Length <= n ? s : s[..n] + "…";

    IEnumerable<ToolDef> BuildTools()
    {
        var d = _desktop;

        yield return new ToolDef
        {
            Name = "list_apps", ReadOnly = true,
            Description = "List installed apps from the Start menu. Optional fuzzy 'query' filter.",
            InputSchema = Schema(("query", "string", "Filter, e.g. 'code' or 'excel'", false), ("limit", "integer", "Max results (default 25)", false)),
            Summarize = a => "List apps" + (Str(a, "query", false) is { Length: > 0 } q ? $" matching '{q}'" : ""),
            Handler = async a => await d.ListAppsAsync(Str(a, "query", false), Int(a, "limit", 25, 1, 200)),
        };
        yield return new ToolDef
        {
            Name = "launch_app",
            Description = "Launch an installed app by name (fuzzy) or id from list_apps.",
            InputSchema = Schema(("app", "string", "App name or id", true), ("args", "string", "Optional command-line arguments", false)),
            Summarize = a => $"Open {Str(a, "app")}",
            Handler = async a => await d.LaunchAppAsync(Str(a, "app"), Str(a, "args", false) is { Length: > 0 } x ? x : null),
        };
        yield return new ToolDef
        {
            Name = "open_url",
            Description = "Open an http(s) URL in the default browser.",
            InputSchema = Schema(("url", "string", "http or https URL", true)),
            Summarize = a => $"Open {Clip(Str(a, "url"))}",
            Handler = async a =>
            {
                var url = Str(a, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                    throw new ArgumentException("Only http and https URLs are allowed");
                await d.OpenUrlAsync(u.ToString());
                return "opened";
            },
        };
        yield return new ToolDef
        {
            Name = "open_path", DefaultPermission = Permission.Ask,
            Description = "Open a file or folder with its default app (folders open in Explorer).",
            InputSchema = Schema(("path", "string", "Full path to a file or folder", true)),
            Summarize = a => $"Open {Clip(Str(a, "path"))}",
            Handler = async a => { await d.OpenPathAsync(Str(a, "path")); return "opened"; },
        };
        yield return new ToolDef
        {
            Name = "list_windows", ReadOnly = true,
            Description = "List open top-level windows with handle, title and process name.",
            InputSchema = Schema(),
            Summarize = _ => "List open windows",
            Handler = async _ => await d.ListWindowsAsync(),
        };
        yield return new ToolDef
        {
            Name = "get_active_window", ReadOnly = true,
            Description = "Get the window the user is currently working in.",
            InputSchema = Schema(),
            Summarize = _ => "Read the active window's title",
            Handler = async _ => await d.GetActiveWindowAsync(),
        };
        yield return new ToolDef
        {
            Name = "focus_window",
            Description = "Bring a window to the front by handle (from list_windows).",
            InputSchema = Schema(("handle", "integer", "Window handle", true)),
            Summarize = a => $"Switch to window {Handle(a)}",
            Handler = async a => await d.FocusWindowAsync(Handle(a)) ? "focused" : throw new InvalidOperationException("Window not found"),
        };
        yield return new ToolDef
        {
            Name = "window_action", DefaultPermission = Permission.Ask,
            Description = "minimize, maximize, restore or close a window by handle. Close asks the app to close (it may prompt to save).",
            InputSchema = Schema(("handle", "integer", "Window handle", true), ("action", "string", "minimize | maximize | restore | close", true)),
            Summarize = a => $"{Str(a, "action")} window {Handle(a)}",
            Handler = async a =>
            {
                var act = Str(a, "action").ToLowerInvariant();
                if (act is not ("minimize" or "maximize" or "restore" or "close")) throw new ArgumentException("action must be minimize, maximize, restore or close");
                return await d.WindowActionAsync(Handle(a), act) ? "done" : throw new InvalidOperationException("Window not found");
            },
        };
        yield return new ToolDef
        {
            Name = "clipboard_read", ReadOnly = true, DefaultPermission = Permission.Ask,
            Description = "Read the text currently on the clipboard.",
            InputSchema = Schema(),
            Summarize = _ => "Read your clipboard",
            Handler = async _ => await d.GetClipboardAsync() ?? "",
        };
        yield return new ToolDef
        {
            Name = "clipboard_write",
            Description = "Put text on the clipboard.",
            InputSchema = Schema(("text", "string", "Text to copy", true)),
            Summarize = a => $"Copy to clipboard: \"{Clip(Str(a, "text"), 60)}\"",
            Handler = async a => { await d.SetClipboardAsync(Str(a, "text")); return "copied"; },
        };
        yield return new ToolDef
        {
            Name = "paste_text", DefaultPermission = Permission.Ask,
            Description = "Paste text into the active window at the cursor (uses the clipboard, then restores it).",
            InputSchema = Schema(("text", "string", "Text to paste", true)),
            Summarize = a => $"Paste into the active window: \"{Clip(Str(a, "text"), 60)}\"",
            Handler = async a => { await d.PasteTextAsync(Str(a, "text")); return "pasted"; },
        };
        yield return new ToolDef
        {
            Name = "send_keys", DefaultPermission = Permission.Ask,
            Description = "Press a key combination in the active window, e.g. 'Ctrl+S', 'Alt+Tab', 'Enter', 'Ctrl+Shift+T'.",
            InputSchema = Schema(("keys", "string", "Key combination", true)),
            Summarize = a => $"Press {Str(a, "keys")}",
            Handler = async a => { await d.SendKeysAsync(Str(a, "keys")); return "sent"; },
        };
        yield return new ToolDef
        {
            Name = "search_files", ReadOnly = true, DefaultPermission = Permission.Ask,
            Description = "Search the user's files by name (Windows Search index). Returns full paths.",
            InputSchema = Schema(("query", "string", "Part of the file name", true), ("limit", "integer", "Max results (default 20)", false)),
            Summarize = a => $"Search your files for '{Str(a, "query")}'",
            Handler = async a => await d.SearchFilesAsync(Str(a, "query"), Int(a, "limit", 20, 1, 100)),
        };
        yield return new ToolDef
        {
            Name = "list_workspaces", ReadOnly = true,
            Description = "List saved workspaces (sets of apps, sites and folders that open together).",
            InputSchema = Schema(),
            Summarize = _ => "List workspaces",
            Handler = _ => Task.FromResult<object?>(d.Settings.Workspaces.Select(w => new { w.Name, w.Description, items = w.Items.Count }).ToList()),
        };
        yield return new ToolDef
        {
            Name = "run_workspace",
            Description = "Open everything in a saved workspace by name.",
            InputSchema = Schema(("name", "string", "Workspace name", true)),
            Summarize = a => $"Open workspace '{Str(a, "name")}'",
            Handler = async a => await d.RunWorkspaceAsync(Str(a, "name")),
        };
        yield return new ToolDef
        {
            Name = "list_commands", ReadOnly = true,
            Description = "List the user's custom commands (keyword, name, type).",
            InputSchema = Schema(),
            Summarize = _ => "List custom commands",
            Handler = _ => Task.FromResult<object?>(d.Settings.Commands.Select(c => new { c.Keyword, c.Name, c.Type }).ToList()),
        };
        yield return new ToolDef
        {
            Name = "run_command", DefaultPermission = Permission.Ask,
            Description = "Run one of the user's own custom commands by keyword, with optional input. Only commands the user defined can run.",
            InputSchema = Schema(("keyword", "string", "Command keyword from list_commands", true), ("input", "string", "Text passed as {query}", false)),
            Summarize = a => $"Run command '{Str(a, "keyword")}'" + (Str(a, "input", false) is { Length: > 0 } q ? $" with \"{Clip(q, 50)}\"" : ""),
            Handler = async a => await d.RunCommandAsync(Str(a, "keyword"), Str(a, "input", false)),
        };
        yield return new ToolDef
        {
            Name = "add_note",
            Description = "Append a quick note to the user's Mirage notes.",
            InputSchema = Schema(("text", "string", "Note text", true)),
            Summarize = a => $"Add note: \"{Clip(Str(a, "text"), 60)}\"",
            Handler = async a => { await d.AddNoteAsync(Str(a, "text")); return "saved"; },
        };
        yield return new ToolDef
        {
            Name = "read_notes", ReadOnly = true, DefaultPermission = Permission.Ask,
            Description = "Read the user's Mirage notes (most recent last).",
            InputSchema = Schema(("max_chars", "integer", "Max characters from the end (default 4000)", false)),
            Summarize = _ => "Read your notes",
            Handler = async a => await d.ReadNotesAsync(Int(a, "max_chars", 4000, 100, 100_000)),
        };
        yield return new ToolDef
        {
            Name = "notify",
            Description = "Show the user a small notification from the agent.",
            InputSchema = Schema(("title", "string", "Title", true), ("message", "string", "Message", true)),
            Summarize = a => $"Show notification \"{Clip(Str(a, "title"), 40)}\"",
            Handler = async a => { await d.NotifyAsync(Clip(Str(a, "title"), 64), Clip(Str(a, "message"), 300)); return "shown"; },
        };
        yield return new ToolDef
        {
            Name = "show_launcher",
            Description = "Open the Mirage launcher, optionally with a query typed in.",
            InputSchema = Schema(("query", "string", "Optional text to put in the search box", false)),
            Summarize = _ => "Open the Mirage launcher",
            Handler = async a => { await d.ShowLauncherAsync(Str(a, "query", false)); return "shown"; },
        };
        yield return new ToolDef
        {
            Name = "lock_pc", DefaultPermission = Permission.Ask,
            Description = "Lock the PC.",
            InputSchema = Schema(),
            Summarize = _ => "Lock your PC",
            Handler = async _ => { await d.LockAsync(); return "locked"; },
        };
    }
}
