using System.Diagnostics;
using System.IO;
using System.Windows;
using Mirage.Core;

namespace Mirage.App;

/// <summary>Shared app state, created once at startup.</summary>
internal static class Services
{
    public static Settings Settings = new();
    public static ClipboardService Clipboard = null!;
    public static UsageStore Usage = null!;
    public static NotesStore Notes = null!;
    public static AppIndex Apps = null!;
    public static TrayService Tray = null!;
    public static Action<string?>? ShowLauncher;
    public static Action? OpenSettings;
    public static Action? SettingsSaved;

    public static void SaveSettings()
    {
        try { Settings.Save(Paths.Settings); SettingsSaved?.Invoke(); }
        catch (Exception ex) { Log.Error("save settings", ex); Notify("Couldn't save settings", ex.Message); }
    }

    public static void Notify(string title, string message) => Tray?.Balloon(title, message);
}

/// <summary>Things results do: open, paste, run, ask AI.</summary>
internal static class Actions
{
    public static void Open(string target, string? args = null, bool admin = false, string? workingDir = null)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim());
        // "rundll32.exe sysdm.cpl,..." style targets carry their own args
        if (args == null && !File.Exists(target) && !Directory.Exists(target))
        {
            var exeIdx = target.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0) { args = target[(exeIdx + 5)..]; target = target[..(exeIdx + 4)]; }
        }
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        if (admin) psi.Verb = "runas";
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        else if (Path.IsPathRooted(target) && File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
        try { Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* user cancelled UAC */ }
    }

    public static void OpenUrl(string url) => Open(url);

    public static void ShowInFolder(string path)
    {
        if (Directory.Exists(path)) Open(path);
        else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void Copy(string text, bool toast = true)
    {
        ClipboardSuppress.Begin();
        try { InputService.SetClipboardText(text); }
        finally { ClipboardSuppress.End(); }
        Services.Clipboard.Add(text);
        if (toast) Toast.Show("Copied", Shorten(text, 60));
    }

    public static Task Paste(string text, IntPtr target) => InputService.PasteAsync(text, target, restoreClipboard: false);

    public static void RunShell(string command, bool keepOpen, bool admin = false)
    {
        var psi = new ProcessStartInfo("cmd.exe", (keepOpen ? "/k " : "/c ") + command) { UseShellExecute = true };
        psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (admin) psi.Verb = "runas";
        try { Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
    }

    public static async Task<string> RunShellCaptureAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return "(timed out)"; }
        var output = (await outTask) + (await errTask);
        return output.TrimEnd();
    }

    public static string RunCustom(CustomCommand c, string query)
    {
        switch (c.Type.ToLowerInvariant())
        {
            case "url":
                OpenUrl(TextTools.FillTemplate(c.Template, query, escape: true));
                return "opened";
            case "shell":
                RunShell(TextTools.FillTemplate(c.Template, query, escape: false), c.KeepOpen);
                return "started";
            default:
                Open(TextTools.FillTemplate(c.Template, query, escape: false));
                return "opened";
        }
    }

    public static int RunWorkspace(Workspace ws)
    {
        int opened = 0;
        foreach (var item in ws.Items)
        {
            try
            {
                switch (item.Type.ToLowerInvariant())
                {
                    case "url": OpenUrl(TextTools.NormalizeUrl(item.Target)); break;
                    case "command": RunShell(item.Target, keepOpen: true); break;
                    case "app":
                        var app = FindApp(item.Target);
                        if (app != null) Open(app.Target, item.Args);
                        else Open(item.Target, item.Args);
                        break;
                    default: Open(item.Target, item.Args); break;
                }
                opened++;
            }
            catch (Exception ex) { Log.Error($"workspace item {item.Target}", ex); }
        }
        return opened;
    }

    public static AppEntry? FindApp(string nameOrTarget)
    {
        var apps = Services.Apps.Apps;
        var exact = apps.FirstOrDefault(a => a.Name.Equals(nameOrTarget, StringComparison.OrdinalIgnoreCase) || a.Target.Equals(nameOrTarget, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return apps.Select(a => (a, s: Fuzzy.Score(nameOrTarget, a.Name))).Where(x => x.s >= 600).OrderByDescending(x => x.s).Select(x => x.a).FirstOrDefault();
    }

    /// <summary>Snapshot the apps in open windows into a reusable workspace.</summary>
    public static Workspace SaveWindowsAsWorkspace(string name)
    {
        var ws = new Workspace { Name = name, Description = $"Saved {DateTime.Now:d MMM yyyy}" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in WindowService.List())
        {
            if (w.ExePath == null || !seen.Add(w.ExePath)) continue;
            if (w.ExePath.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (w.ExePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
            {
                // Store apps can't be started from their exe; match them by window title instead
                var app = FindApp(w.ProcessName);
                if (app != null) ws.Items.Add(new WorkspaceItem { Type = "app", Target = app.Name });
                continue;
            }
            ws.Items.Add(new WorkspaceItem { Type = "file", Target = w.ExePath });
        }
        Services.Settings.Workspaces.RemoveAll(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Services.Settings.Workspaces.Add(ws);
        Services.SaveSettings();
        return ws;
    }

    public static AiTarget DefaultAi() =>
        Services.Settings.AiTargets.FirstOrDefault(a => a.Name.Equals(Services.Settings.DefaultAi, StringComparison.OrdinalIgnoreCase))
        ?? Services.Settings.AiTargets.FirstOrDefault()
        ?? Settings.DefaultAiTargets().First();

    /// <summary>Open an AI chat with the prompt filled in where the site supports it; otherwise copy it.</summary>
    public static void AskAi(AiTarget ai, string prompt)
    {
        prompt = prompt.Trim();
        bool canPrefill = ai.Url.Contains("{query}") && Uri.EscapeDataString(prompt).Length < 6000 && string.IsNullOrEmpty(ai.App);
        if (!canPrefill && prompt.Length > 0)
        {
            Copy(prompt, toast: false);
            Toast.Show($"Prompt copied for {ai.Name}", "Paste it with Ctrl+V");
        }
        if (!string.IsNullOrEmpty(ai.App))
        {
            var app = FindApp(ai.App);
            Open(app?.Target ?? ai.App);
            return;
        }
        var url = canPrefill ? TextTools.FillTemplate(ai.Url, prompt, escape: true) : ai.Url.Replace("?q={query}", "").Replace("{query}", "");
        OpenUrl(url);
    }

    public static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= n ? s : s[..n] + "…";
    }
}
