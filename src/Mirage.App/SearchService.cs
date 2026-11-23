using System.Diagnostics;
using System.IO;
using Mirage.Core;

namespace Mirage.App;

internal sealed class LauncherContext
{
    public IntPtr PreviousWindow { get; set; }
    /// <summary>Text grabbed from the app the user was in (selection hotkey).</summary>
    public string? Selection { get; set; }
}

/// <summary>Builds the result list for whatever is typed.</summary>
internal static class SearchService
{
    static Settings S => Services.Settings;

    static readonly (string Name, string Instruction, string Glyph)[] AiPresets =
    {
        ("Explain", "Explain this clearly and briefly:", ""),
        ("Summarize", "Summarize this in a few bullet points:", ""),
        ("Fix grammar and tone", "Fix the grammar and improve the tone of this text. Reply with only the corrected text:", ""),
        ("Translate to English", "Translate this to English:", ""),
        ("Translate to Arabic", "Translate this to Arabic:", ""),
        ("Reply to this", "Draft a short, friendly reply to this message:", ""),
        ("Improve this code", "Review this code and suggest improvements:", ""),
    };

    public static List<ResultItem> Build(string raw, LauncherContext ctx)
    {
        var q = QueryRouter.Parse(raw, S);
        List<ResultItem> list;
        try
        {
            list = ctx.Selection != null && q.Mode == QueryMode.Default
                ? SelectionResults(q.Text, ctx)
                : q.Mode switch
                {
                    QueryMode.Windows => WindowResults(q.Text),
                    QueryMode.Clipboard => ClipboardResults(q.Text, ctx),
                    QueryMode.Snippets => SnippetResults(q.Text, ctx),
                    QueryMode.NoteAdd => NoteAddResults(q.Text),
                    QueryMode.Notes => NotesResults(q.Text),
                    QueryMode.Workspaces => WorkspaceResults(q.Text),
                    QueryMode.Ai => AiResults(q.Text, ctx),
                    QueryMode.Shell => ShellResults(q.Text),
                    QueryMode.Calc => CalcResults(q.Text, true),
                    QueryMode.Command => CommandResults(q.Command!, q.Text),
                    QueryMode.Files => new List<ResultItem> { Info("Searching files…", "Type at least 2 letters of the file name", "") },
                    _ => DefaultResults(q.Text, ctx),
                };
        }
        catch (Exception ex)
        {
            Log.Error("search", ex);
            list = new List<ResultItem> { Info("Something went wrong", ex.Message, "") };
        }
        foreach (var r in list) r.IsPinned = r.PinKey != null && S.Pinned.Contains(r.PinKey);
        return list;
    }

    // ---------------- default ----------------

    static List<ResultItem> DefaultResults(string text, LauncherContext ctx)
    {
        var results = new List<ResultItem>();
        if (text.Length == 0)
        {
            // Pinned first, then what you use most, then the mode cheat sheet
            foreach (var key in S.Pinned)
            {
                var item = FromKey(key);
                if (item != null) { item.Score = 2000; results.Add(item); }
            }
            foreach (var (key, _, _) in Services.Usage.Top(12))
            {
                if (S.Pinned.Contains(key) || results.Count >= S.MaxResults) continue;
                var item = FromKey(key);
                if (item != null) results.Add(item);
            }
            if (results.Count < 4)
                results.AddRange(QueryRouter.Hints.Take(S.MaxResults - results.Count).Select(h => new ResultItem
                {
                    Title = h.Name, Subtitle = $"Type \"{h.Prefix.Trim()}\" then your text · {h.Description}", Glyph = h.Glyph, Tint = "slate",
                    Badge = h.Prefix.Trim(), Completion = h.Prefix, KeepOpen = true,
                }));
            return results;
        }

        // Instant answers
        if (Calculator.LooksLikeExpression(text)) results.AddRange(CalcResults(text, false));
        if (Units.TryConvert(text, out var conv, out var convValue))
            results.Add(new ResultItem
            {
                Title = conv, Subtitle = $"{text} · Enter to copy", Glyph = "", Tint = "amber", Badge = "Convert", Score = 5000,
                Primary = () => { Actions.Copy(Calculator.Format(convValue)); return Task.CompletedTask; },
            });
        if (TextTools.LooksLikeUrl(text))
        {
            var url = TextTools.NormalizeUrl(text);
            results.Add(new ResultItem
            {
                Title = $"Open {text}", Subtitle = url, Glyph = "", Tint = "blue", Badge = "Link", Score = 4000,
                Primary = () => { Actions.OpenUrl(url); return Task.CompletedTask; },
            });
        }
        if (Directory.Exists(text) || File.Exists(text))
            results.Add(PathItem(text, 4500));

        // Apps & settings
        foreach (var app in Services.Apps.Apps)
        {
            var score = Math.Max(Fuzzy.Score(text, app.Name), app.Keywords.Length > 0 ? Fuzzy.Score(text, app.Keywords) - 150 : -1);
            if (score <= 0) continue;
            var item = AppItem(app);
            item.Score = score + Services.Usage.Boost(app.Key) + (S.Pinned.Contains(app.Key) ? 120 : 0) + (app.Kind == "Setting" ? -40 : 0);
            results.Add(item);
        }

        // Built-in commands
        foreach (var cmd in SystemCommands())
        {
            var score = Math.Max(Fuzzy.Score(text, cmd.Title), Fuzzy.Score(text, cmd.Badge) - 50);
            if (score <= 0) continue;
            cmd.Score = score - 30 + (cmd.UsageKey != null ? Services.Usage.Boost(cmd.UsageKey) : 0);
            results.Add(cmd);
        }

        // Workspaces, snippets, custom commands, windows
        foreach (var ws in S.Workspaces)
        {
            var score = Fuzzy.Score(text, ws.Name);
            if (score > 0) { var i = WorkspaceItem(ws); i.Score = score + 20 + Services.Usage.Boost(i.UsageKey!); results.Add(i); }
        }
        foreach (var sn in S.Snippets)
        {
            var score = Math.Max(Fuzzy.Score(text, sn.Name), Fuzzy.Score(text, sn.Keyword.TrimStart(';')));
            if (score > 450) { var i = SnippetItem(sn, ctx); i.Score = score - 120; results.Add(i); }
        }
        foreach (var c in S.Commands)
        {
            var score = Math.Max(Fuzzy.Score(text, c.Name), text.Equals(c.Keyword, StringComparison.OrdinalIgnoreCase) ? 950 : -1);
            if (score > 450)
                results.Add(new ResultItem
                {
                    Title = c.Name, Subtitle = $"Type \"{c.Keyword} \" then your text", Glyph = c.Type == "shell" ? "" : "", Tint = "teal",
                    Badge = c.Keyword, Completion = c.Keyword + " ", KeepOpen = true, Score = score - 60,
                });
        }
        if (text.Length >= 2)
            foreach (var w in WindowService.List())
            {
                var score = Math.Max(Fuzzy.Score(text, w.Title), Fuzzy.Score(text, w.ProcessName));
                if (score > 500) { var i = WindowItem(w); i.Score = score - 200; results.Add(i); }
            }

        var top = results.OrderByDescending(r => r.Score).Take(S.MaxResults).ToList();

        // Fallbacks always available at the bottom
        var ai = Actions.DefaultAi();
        var engine = Settings.SearchEngines.TryGetValue(S.SearchEngine, out var e) ? e : Settings.SearchEngines["Google"];
        top.Add(new ResultItem
        {
            Title = $"Search {S.SearchEngine} for \"{text}\"", Glyph = "", Tint = "blue", Badge = "Web", IsFallback = true,
            Primary = () => { Actions.OpenUrl(TextTools.FillTemplate(engine, text, true)); return Task.CompletedTask; },
        });
        top.Add(new ResultItem
        {
            Title = $"Ask {ai.Name}: {text}", Glyph = "", Tint = "violet", Badge = "AI", IsFallback = true,
            Primary = () => { Actions.AskAi(ai, text); return Task.CompletedTask; },
        });
        if (S.FileSearch && text.Length >= 2)
            top.Add(new ResultItem
            {
                Title = $"Find files named \"{text}\"", Glyph = "", Tint = "amber", Badge = "Files", IsFallback = true,
                Completion = "f " + text, KeepOpen = true,
            });
        return top;
    }

    // ---------------- items ----------------

    public static ResultItem AppItem(AppEntry app)
    {
        var item = new ResultItem
        {
            Title = app.Name,
            Subtitle = app.Kind == "Setting" ? "Windows setting" : app.Kind == "Store app" ? "Microsoft Store app" : (app.FilePath != null ? Path.GetDirectoryName(app.FilePath)?.Split('\\').LastOrDefault() ?? "" : ""),
            Badge = app.Kind == "Setting" ? "Setting" : "App",
            Glyph = app.Kind == "Setting" ? "" : "",
            Tint = app.Kind == "Setting" ? "slate" : "blue",
            UsageKey = app.Key, PinKey = app.Key,
            Primary = () => { Actions.Open(app.Target); return Task.CompletedTask; },
        };
        if (app.Kind != "Setting") item.WithIcon(app.Target);
        if (app.Kind != "Setting" && !app.Target.StartsWith("shell:"))
        {
            item.Actions.Add(new ResultAction { Name = "Run as administrator", Glyph = "", Shortcut = "Ctrl+Shift+Enter", Run = () => { Actions.Open(app.Target, admin: true); return Task.CompletedTask; } });
            item.Actions.Add(new ResultAction { Name = "Open file location", Glyph = "", Shortcut = "Shift+Enter", Run = () => { Actions.ShowInFolder(app.FilePath ?? app.Target); return Task.CompletedTask; } });
        }
        item.Actions.Add(new ResultAction { Name = "Copy name", Glyph = "", Run = () => { Actions.Copy(app.Name); return Task.CompletedTask; } });
        return item;
    }

    static ResultItem PathItem(string path, int score)
    {
        var isDir = Directory.Exists(path);
        var item = new ResultItem
        {
            Title = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
            Subtitle = path, Badge = isDir ? "Folder" : "File", Glyph = isDir ? "" : "", Tint = "amber",
            Score = score, UsageKey = "path:" + path, PinKey = "path:" + path,
            Primary = () => { Actions.Open(path); return Task.CompletedTask; },
        }.WithIcon(path);
        item.Actions.Add(new ResultAction { Name = "Show in folder", Glyph = "", Shortcut = "Shift+Enter", Run = () => { Actions.ShowInFolder(path); return Task.CompletedTask; } });
        item.Actions.Add(new ResultAction { Name = "Copy path", Glyph = "", Shortcut = "Ctrl+C", Run = () => { Actions.Copy(path); return Task.CompletedTask; } });
        item.Actions.Add(new ResultAction { Name = "Open in terminal", Glyph = "", Run = () => { Actions.RunShell($"cd /d \"{(isDir ? path : Path.GetDirectoryName(path))}\"", true); return Task.CompletedTask; } });
        if (!isDir)
        {
            item.Actions.Add(new ResultAction { Name = "Open with…", Glyph = "", Run = () => { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")); return Task.CompletedTask; } });
            item.Actions.Add(new ResultAction { Name = "Copy file to clipboard", Glyph = "", Run = () =>
            {
                var files = new System.Collections.Specialized.StringCollection { path };
                System.Windows.Clipboard.SetFileDropList(files);
                Toast.Show("File copied", "Paste it into any folder or chat");
                return Task.CompletedTask;
            } });
        }
        return item;
    }

    public static List<ResultItem> FileResults(IEnumerable<string> paths) => paths.Select((p, i) => PathItem(p, 1000 - i)).ToList();

    static ResultItem WindowItem(OpenWindow w)
    {
        var item = new ResultItem
        {
            Title = w.Title, Subtitle = w.ProcessName, Badge = "Window", Glyph = "", Tint = "teal",
            Primary = () => { WindowService.Focus(w.Handle); return Task.CompletedTask; },
        }.WithIcon(w.ExePath);
        item.Actions.Add(new ResultAction { Name = "Close window", Glyph = "", Shortcut = "Ctrl+W", Run = () => { WindowService.Act(w.Handle, "close"); return Task.CompletedTask; } });
        item.Actions.Add(new ResultAction { Name = "Minimize", Glyph = "", Run = () => { WindowService.Act(w.Handle, "minimize"); return Task.CompletedTask; } });
        item.Actions.Add(new ResultAction { Name = "Maximize", Glyph = "", Run = () => { WindowService.Act(w.Handle, "maximize"); WindowService.Focus(w.Handle); return Task.CompletedTask; } });
        return item;
    }

    static List<ResultItem> WindowResults(string text)
    {
        var all = WindowService.List();
        var list = all.Select(w => (w, s: text.Length == 0 ? 1 : Math.Max(Fuzzy.Score(text, w.Title), Fuzzy.Score(text, w.ProcessName))))
            .Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => WindowItem(x.w)).ToList();
        if (list.Count == 0) list.Add(Info("No matching windows", "Open windows from every desktop show here", ""));
        return list;
    }

    static List<ResultItem> ClipboardResults(string text, LauncherContext ctx)
    {
        if (!S.ClipboardHistory)
            return new() { Info("Clipboard history is off", "Turn it on in Settings > General", "") };
        var items = Services.Clipboard.Items
            .Where(c => text.Length == 0 || c.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .Select(c =>
            {
                var item = new ResultItem
                {
                    Title = Actions.Shorten(c.Text, 90), Subtitle = $"{Ago(c.Time)} · {c.Text.Length:N0} characters · Enter pastes",
                    Badge = "Clip", Glyph = "", Tint = "violet",
                    Primary = () => Actions.Paste(c.Text, ctx.PreviousWindow),
                };
                item.Actions.Add(new ResultAction { Name = "Copy", Glyph = "", Shortcut = "Ctrl+C", Run = () => { Actions.Copy(c.Text); return Task.CompletedTask; } });
                item.Actions.Add(new ResultAction { Name = $"Ask {Actions.DefaultAi().Name} about this", Glyph = "", Run = () => { Actions.AskAi(Actions.DefaultAi(), c.Text); return Task.CompletedTask; } });
                item.Actions.Add(new ResultAction { Name = "Save as note", Glyph = "", Run = () => { Services.Notes.Add(c.Text); Toast.Show("Saved to notes", Actions.Shorten(c.Text, 50)); return Task.CompletedTask; } });
                foreach (var t in TextTools.Transforms.Take(6))
                    item.Actions.Add(new ResultAction { Name = $"Paste as {t.Name}", Glyph = t.Glyph, Run = () => Actions.Paste(t.Apply(c.Text), ctx.PreviousWindow) });
                item.Actions.Add(new ResultAction { Name = "Delete from history", Glyph = "", Shortcut = "Ctrl+Del", KeepOpen = true, Run = () => { Services.Clipboard.Remove(c); return Task.CompletedTask; } });
                return item;
            }).ToList();
        if (items.Count == 0) items.Add(Info(text.Length == 0 ? "Nothing copied yet" : "No matches", "Text you copy shows up here", ""));
        else items.Add(new ResultItem
        {
            Title = "Clear clipboard history", Glyph = "", Tint = "rose", Badge = "Clear", IsFallback = true, ConfirmText = "Press Enter again to clear everything",
            Primary = () => { Services.Clipboard.Clear(); Toast.Show("Clipboard history cleared", ""); return Task.CompletedTask; },
        });
        return items;
    }

    static ResultItem SnippetItem(Snippet sn, LauncherContext ctx)
    {
        string Expand() => TextTools.ExpandPlaceholders(sn.Text, DateTime.Now, InputService.GetClipboardText);
        var item = new ResultItem
        {
            Title = sn.Name.Length > 0 ? sn.Name : sn.Keyword, Subtitle = $"{sn.Keyword} · {Actions.Shorten(sn.Text, 70)}",
            Badge = "Snippet", Glyph = "", Tint = "green", PinKey = "snippet:" + sn.Keyword,
            UsageKey = "snippet:" + sn.Keyword,
            Primary = () => Actions.Paste(Expand(), ctx.PreviousWindow),
        };
        item.Actions.Add(new ResultAction { Name = "Copy", Glyph = "", Shortcut = "Ctrl+C", Run = () => { Actions.Copy(Expand()); return Task.CompletedTask; } });
        return item;
    }

    static List<ResultItem> SnippetResults(string text, LauncherContext ctx)
    {
        var t = text.TrimStart(';');
        var list = S.Snippets
            .Select(s => (s, score: t.Length == 0 ? 1 : Math.Max(Fuzzy.Score(t, s.Keyword.TrimStart(';')), Fuzzy.Score(t, s.Name))))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => SnippetItem(x.s, ctx)).ToList();
        list.Add(new ResultItem
        {
            Title = "Add or edit snippets", Subtitle = "Settings > Library", Glyph = "", Tint = "slate", IsFallback = true,
            Primary = () => { Services.OpenSettings?.Invoke(); return Task.CompletedTask; },
        });
        return list;
    }

    static List<ResultItem> NoteAddResults(string text)
    {
        if (text.Length == 0) return new() { Info("Type your note", "Enter saves it to your Mirage notes", "") };
        return new()
        {
            new ResultItem
            {
                Title = $"Save note: {text}", Subtitle = "Enter to save · \"notes\" to search them later", Glyph = "", Tint = "amber", Badge = "Note",
                Primary = () => { Services.Notes.Add(text); Toast.Show("Note saved", Actions.Shorten(text, 60)); return Task.CompletedTask; },
            }
        };
    }

    static List<ResultItem> NotesResults(string text)
    {
        var list = Services.Notes.All().Reverse()
            .Where(n => text.Length == 0 || n.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .Select(n => new ResultItem
            {
                Title = n.Text, Subtitle = n.When == DateTime.MinValue ? "" : Ago(n.When) + " · Enter copies", Glyph = "", Tint = "amber", Badge = "Note",
                Primary = () => { Actions.Copy(n.Text); return Task.CompletedTask; },
            }).ToList();
        list.Add(new ResultItem
        {
            Title = "Open notes file", Subtitle = Services.Notes.FilePath, Glyph = "", Tint = "slate", IsFallback = true,
            Primary = () => { if (!File.Exists(Services.Notes.FilePath)) Services.Notes.Add("Notes start here"); Actions.Open(Services.Notes.FilePath); return Task.CompletedTask; },
        });
        return list;
    }

    static ResultItem WorkspaceItem(Workspace ws)
    {
        var item = new ResultItem
        {
            Title = ws.Name, Subtitle = $"{ws.Items.Count} items · " + string.Join(", ", ws.Items.Take(3).Select(i => Actions.Shorten(Path.GetFileNameWithoutExtension(i.Target) is { Length: > 0 } n && i.Type is "file" or "app" ? n : i.Target, 24))),
            Badge = "Workspace", Glyph = "", Tint = "teal", UsageKey = "ws:" + ws.Name, PinKey = "ws:" + ws.Name,
            Primary = () => { var n = Actions.RunWorkspace(ws); Toast.Show($"Opening {ws.Name}", $"{n} items"); return Task.CompletedTask; },
        };
        item.Actions.Add(new ResultAction { Name = "Edit workspaces", Glyph = "", Run = () => { Services.OpenSettings?.Invoke(); return Task.CompletedTask; } });
        return item;
    }

    static List<ResultItem> WorkspaceResults(string text)
    {
        var list = S.Workspaces.Select(w => (w, s: text.Length == 0 ? 1 : Fuzzy.Score(text, w.Name)))
            .Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => WorkspaceItem(x.w)).ToList();
        var name = text.Length > 0 ? text : $"Workspace {DateTime.Now:MMM d HH.mm}";
        list.Add(new ResultItem
        {
            Title = $"Save open windows as \"{name}\"", Subtitle = "Captures the apps you have open now, so one command reopens them",
            Glyph = "", Tint = "green", Badge = "Save", IsFallback = true,
            Primary = () => { var ws = Actions.SaveWindowsAsWorkspace(name); Toast.Show($"Saved \"{ws.Name}\"", $"{ws.Items.Count} apps · open it with ws {ws.Name}"); return Task.CompletedTask; },
        });
        return list;
    }

    static List<ResultItem> AiResults(string text, LauncherContext ctx)
    {
        var list = new List<ResultItem>();
        var def = Actions.DefaultAi();
        foreach (var ai in S.AiTargets.OrderByDescending(a => a == def))
        {
            var item = new ResultItem
            {
                Title = text.Length > 0 ? $"Ask {ai.Name}: {text}" : $"Open {ai.Name}",
                Subtitle = ai.Url.Contains("{query}") ? "Opens with your prompt filled in" : "Copies your prompt, then opens it",
                Glyph = "", Tint = "violet", Badge = "AI", UsageKey = "ai:" + ai.Name,
                Primary = () => { Actions.AskAi(ai, text); return Task.CompletedTask; },
            };
            var clip = InputService.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
                item.Actions.Add(new ResultAction { Name = $"Ask {ai.Name} with clipboard text included", Glyph = "", Shortcut = "Shift+Enter", Run = () => { Actions.AskAi(ai, (text + "\n\n" + clip).Trim()); return Task.CompletedTask; } });
            item.Actions.Add(new ResultAction { Name = "Make default AI", Glyph = "", Run = () => { S.DefaultAi = ai.Name; Services.SaveSettings(); Toast.Show($"{ai.Name} is now your default AI", ""); return Task.CompletedTask; } });
            list.Add(item);
        }
        // Installed AI desktop apps
        foreach (var app in Services.Apps.Apps.Where(a => AiAppNames.Any(n => a.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).Take(5))
        {
            var it = AppItem(app); it.Subtitle = "AI app on this PC"; list.Add(it);
        }
        return list;
    }

    static readonly string[] AiAppNames = { "ChatGPT", "Claude", "Copilot", "Perplexity", "Gemini", "Grok", "Ollama", "LM Studio", "Cursor", "Windsurf" };

    static List<ResultItem> ShellResults(string text)
    {
        if (text.Length == 0) return new() { Info("Type a command", "Runs in Command Prompt, e.g. > ipconfig", "") };
        var item = new ResultItem
        {
            Title = $"Run: {text}", Subtitle = "Enter opens a terminal · Tab for more", Glyph = "", Tint = "slate", Badge = "Terminal",
            Primary = () => { Actions.RunShell(text, true); return Task.CompletedTask; },
        };
        item.Actions.Add(new ResultAction { Name = "Run and copy the output", Glyph = "", Shortcut = "Shift+Enter", Run = async () =>
        {
            Toast.Show("Running…", text);
            var output = await Actions.RunShellCaptureAsync(text, TimeSpan.FromSeconds(30));
            Actions.Copy(output, toast: false);
            Toast.Show("Output copied", Actions.Shorten(output, 80));
        } });
        item.Actions.Add(new ResultAction { Name = "Run as administrator", Glyph = "", Shortcut = "Ctrl+Shift+Enter", Run = () => { Actions.RunShell(text, true, admin: true); return Task.CompletedTask; } });
        item.Actions.Add(new ResultAction { Name = "Run in PowerShell", Glyph = "", Run = () => { Actions.Open("powershell.exe", $"-NoExit -Command \"{text.Replace("\"", "\\\"")}\""); return Task.CompletedTask; } });
        return new() { item };
    }

    static List<ResultItem> CalcResults(string text, bool explicitMode)
    {
        if (Units.TryConvert(text, out var conv, out var v2))
            return new() { new ResultItem { Title = conv, Subtitle = "Enter to copy", Glyph = "", Tint = "amber", Badge = "Convert", Score = 5000, Primary = () => { Actions.Copy(Calculator.Format(v2)); return Task.CompletedTask; } } };
        if (Calculator.TryEvaluate(text, out var v, out var err))
        {
            var pretty = Calculator.Format(v, true);
            return new() { new ResultItem { Title = $"= {pretty}", Subtitle = $"{text} · Enter to copy", Glyph = "", Tint = "amber", Badge = "Math", Score = 5000, Primary = () => { Actions.Copy(Calculator.Format(v)); return Task.CompletedTask; } } };
        }
        return explicitMode
            ? new() { Info(text.Length == 0 ? "Type a calculation" : "Not a valid calculation", text.Length == 0 ? "e.g. 18% * 2400, sqrt(2), 10 km to mi" : err ?? "", "") }
            : new();
    }

    static List<ResultItem> CommandResults(CustomCommand c, string text) => new()
    {
        new ResultItem
        {
            Title = text.Length > 0 ? $"{c.Name}: {text}" : c.Name, Subtitle = c.Type == "url" ? TextTools.FillTemplate(c.Template, text, true) : c.Template,
            Glyph = c.Type == "shell" ? "" : "", Tint = "teal", Badge = c.Keyword, UsageKey = "cmd:" + c.Keyword,
            Primary = () => { Actions.RunCustom(c, text); return Task.CompletedTask; },
        }
    };

    // ---------------- selection (text actions) ----------------

    static List<ResultItem> SelectionResults(string filter, LauncherContext ctx)
    {
        var sel = ctx.Selection!;
        var list = new List<ResultItem>();
        var ai = Actions.DefaultAi();
        foreach (var (name, instruction, glyph) in AiPresets)
            list.Add(new ResultItem
            {
                Title = $"{name} with {ai.Name}", Subtitle = "AI", Glyph = glyph, Tint = "violet", Badge = "AI",
                Primary = () => { Actions.AskAi(ai, instruction + "\n\n" + sel); return Task.CompletedTask; },
            });
        list.Add(new ResultItem
        {
            Title = $"Ask {ai.Name} about it…", Subtitle = "Type your question first, then Enter", Glyph = "", Tint = "violet", Badge = "AI",
            Primary = () => { Actions.AskAi(ai, (filter.Length > 0 ? filter + "\n\n" : "") + sel); return Task.CompletedTask; },
            Score = filter.Length > 0 ? 10_000 : 0,
        });
        if (TextTools.LooksLikeUrl(sel))
            list.Add(new ResultItem { Title = "Open link", Subtitle = sel.Trim(), Glyph = "", Tint = "blue", Badge = "Link", Primary = () => { Actions.OpenUrl(TextTools.NormalizeUrl(sel)); return Task.CompletedTask; } });
        list.Add(new ResultItem
        {
            Title = $"Search {S.SearchEngine}", Glyph = "", Tint = "blue", Badge = "Web",
            Primary = () => { Actions.OpenUrl(TextTools.FillTemplate(Settings.SearchEngines.GetValueOrDefault(S.SearchEngine, Settings.SearchEngines["Google"]), Actions.Shorten(sel, 300), true)); return Task.CompletedTask; },
        });
        list.Add(new ResultItem { Title = "Save as note", Glyph = "", Tint = "amber", Badge = "Note", Primary = () => { Services.Notes.Add(sel); Toast.Show("Saved to notes", Actions.Shorten(sel, 50)); return Task.CompletedTask; } });
        list.Add(new ResultItem
        {
            Title = "Save as snippet", Subtitle = "Keyword ;s1, ;s2… (rename in Settings)", Glyph = "", Tint = "green", Badge = "Snippet",
            Primary = () =>
            {
                var n = 1; while (S.Snippets.Any(s => s.Keyword == ";s" + n)) n++;
                S.Snippets.Add(new Snippet { Keyword = ";s" + n, Name = Actions.Shorten(sel, 30), Text = sel });
                Services.SaveSettings();
                Toast.Show($"Snippet ;s{n} saved", Actions.Shorten(sel, 50));
                return Task.CompletedTask;
            },
        });
        list.Add(new ResultItem { Title = "Copy word count", Subtitle = TextTools.Stats(sel), Glyph = "", Tint = "slate", Badge = "Stats", Primary = () => { Actions.Copy(TextTools.Stats(sel)); return Task.CompletedTask; } });
        foreach (var t in TextTools.Transforms)
            list.Add(new ResultItem
            {
                Title = t.Name, Subtitle = "Replace the selection", Glyph = t.Glyph, Tint = "teal", Badge = "Text",
                Primary = () =>
                {
                    string result;
                    try { result = t.Apply(sel); }
                    catch (Exception ex) { Toast.Show($"Couldn't {t.Name.ToLowerInvariant()}", ex.Message); return Task.CompletedTask; }
                    return Actions.Paste(result, ctx.PreviousWindow);
                },
            });
        if (filter.Length == 0) return list;
        return list.Select(r => { r.Score += Math.Max(Fuzzy.Score(filter, r.Title), 0); return r; })
            .Where(r => r.Score > 0).OrderByDescending(r => r.Score).ToList();
    }

    // ---------------- system ----------------

    static IEnumerable<ResultItem> SystemCommands()
    {
        ResultItem Cmd(string title, string badge, string glyph, string tint, Action run, string? confirm = null, string? subtitle = null) => new()
        {
            Title = title, Badge = badge, Glyph = glyph, Tint = tint, ConfirmText = confirm, Subtitle = subtitle ?? "",
            UsageKey = "sys:" + title, PinKey = "sys:" + title,
            Primary = () => { run(); return Task.CompletedTask; },
        };
        yield return Cmd("Lock PC", "System", "", "slate", () => Native.LockWorkStation());
        yield return Cmd("Sleep", "System", "", "slate", () => Native.SetSuspendState(false, false, false), "Press Enter again to sleep");
        yield return Cmd("Restart", "System", "", "rose", () => Actions.Open("shutdown.exe", "/r /t 0"), "Press Enter again to restart now");
        yield return Cmd("Shut down", "System", "", "rose", () => Actions.Open("shutdown.exe", "/s /t 0"), "Press Enter again to shut down now");
        yield return Cmd("Sign out", "System", "", "rose", () => Actions.Open("shutdown.exe", "/l"), "Press Enter again to sign out");
        yield return Cmd("Empty Recycle Bin", "System", "", "rose", () => Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOSOUND), "Press Enter again to empty it");
        yield return Cmd("Show desktop", "System", "", "slate", () => InputService.SendCombo("Win+D"));
        yield return Cmd("Screenshot (Snipping Tool)", "System", "", "slate", () => InputService.SendCombo("Win+Shift+S"));
        yield return Cmd("Emoji panel", "System", "", "slate", () => InputService.SendCombo("Win+OemPeriod"));
        yield return Cmd("Mirage settings", "Mirage", "", "violet", () => Services.OpenSettings?.Invoke());
        yield return Cmd("Clipboard history", "Mirage", "", "violet", () => Services.ShowLauncher?.Invoke("c "), subtitle: "or type c then space");
        yield return Cmd("Open Mirage notes", "Mirage", "", "amber", () => { if (!File.Exists(Services.Notes.FilePath)) Services.Notes.Add("Notes start here"); Actions.Open(Services.Notes.FilePath); });
        yield return Cmd("Reload app list", "Mirage", "", "violet", () => { Services.Apps.Refresh(); Toast.Show("Refreshing apps", ""); });
        yield return Cmd("Quit Mirage", "Mirage", "", "rose", () => System.Windows.Application.Current.Shutdown());
    }

    /// <summary>Rebuild a result from a pin/usage key.</summary>
    static ResultItem? FromKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon < 0) return null;
        var kind = key[..colon];
        var rest = key[(colon + 1)..];
        switch (kind)
        {
            case "app":
                var app = Services.Apps.Apps.FirstOrDefault(a => a.Key == key);
                return app == null ? null : AppItem(app);
            case "path":
                return File.Exists(rest) || Directory.Exists(rest) ? PathItem(rest, 0) : null;
            case "ws":
                var ws = S.Workspaces.FirstOrDefault(w => ("ws:" + w.Name) == key);
                return ws == null ? null : WorkspaceItem(ws);
            case "sys":
                return SystemCommands().FirstOrDefault(c => c.UsageKey == key);
            case "snippet":
                var sn = S.Snippets.FirstOrDefault(s => s.Keyword == rest);
                return sn == null ? null : SnippetItem(sn, new LauncherContext());
            default:
                return null;
        }
    }

    static ResultItem Info(string title, string subtitle, string glyph) =>
        new() { Title = title, Subtitle = subtitle, Glyph = glyph, Tint = "slate", KeepOpen = true };

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return t.ToString("d MMM");
    }
}
