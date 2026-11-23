using System.IO;
using System.Windows;
using Mirage.Core;
using Mirage.Core.Agent;

namespace Mirage.App;

/// <summary>The real Windows side of the agent bridge. Every call runs on the UI thread.</summary>
internal sealed class DesktopBridge : IDesktop, IConfirmer
{
    public Settings Settings => Services.Settings;

    static Task<T> Ui<T>(Func<T> f) => Application.Current.Dispatcher.InvokeAsync(f).Task;
    static Task Ui(Action f) => Application.Current.Dispatcher.InvokeAsync(f).Task;
    static async Task<T> UiAsync<T>(Func<Task<T>> f) => await await Application.Current.Dispatcher.InvokeAsync(f).Task;
    static async Task UiAsync(Func<Task> f) => await await Application.Current.Dispatcher.InvokeAsync(f).Task;

    public Task<IReadOnlyList<AppInfo>> ListAppsAsync(string? query, int limit) => Ui<IReadOnlyList<AppInfo>>(() =>
    {
        IEnumerable<AppEntry> apps = Services.Apps.Apps;
        if (!string.IsNullOrWhiteSpace(query))
            apps = apps.Select(a => (a, s: Fuzzy.Score(query, a.Name))).Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => x.a);
        else
            apps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        return apps.Take(Math.Clamp(limit, 1, 500)).Select(a => new AppInfo(a.Name, a.Name, a.Kind)).ToList();
    });

    public Task<string> LaunchAppAsync(string nameOrId, string? args) => Ui(() =>
    {
        var app = Actions.FindApp(nameOrId) ?? throw new InvalidOperationException($"No installed app matches \"{nameOrId}\". Call list_apps to see names.");
        Actions.Open(app.Target, args);
        Services.Usage.Record(app.Key);
        return $"Launched {app.Name}";
    });

    public Task OpenUrlAsync(string url) => Ui(() => Actions.OpenUrl(url));

    public Task OpenPathAsync(string path) => Ui(() =>
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException($"Not found: {path}");
        Actions.Open(path);
    });

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => Ui<IReadOnlyList<WindowInfo>>(() =>
    {
        var active = Native.GetForegroundWindow();
        return WindowService.List().Select(w => WindowService.ToInfo(w, active)).ToList();
    });

    public Task<WindowInfo?> GetActiveWindowAsync() => Ui(() =>
    {
        var active = Native.GetForegroundWindow();
        var w = WindowService.List().FirstOrDefault(x => x.Handle == active);
        return w == null ? null : WindowService.ToInfo(w, active);
    });

    public Task<bool> FocusWindowAsync(long handle) => Ui(() => WindowService.Focus(new IntPtr(handle)));
    public Task<bool> WindowActionAsync(long handle, string action) => Ui(() => WindowService.Act(new IntPtr(handle), action));

    public Task<string?> GetClipboardAsync() => Ui(InputService.GetClipboardText);

    public Task SetClipboardAsync(string text) => Ui(() =>
    {
        ClipboardSuppress.Begin();
        try { InputService.SetClipboardText(text); } finally { ClipboardSuppress.End(); }
    });

    public Task PasteTextAsync(string text) => UiAsync(() => InputService.PasteAsync(text, IntPtr.Zero, restoreClipboard: true));

    public Task SendKeysAsync(string combo) => Ui(() =>
    {
        if (!InputService.TryParseCombo(combo, out _, out _)) throw new ArgumentException($"Unknown key combo \"{combo}\". Use forms like Ctrl+S, Alt+Tab, Win+D, Enter.");
        InputService.ReleaseModifiers();
        InputService.SendCombo(combo);
    });

    public async Task<IReadOnlyList<string>> SearchFilesAsync(string query, int limit)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await FileSearchService.SearchAsync(query, Math.Clamp(limit, 1, 100), cts.Token);
    }

    public Task<string> RunWorkspaceAsync(string name) => Ui(() =>
    {
        var ws = Settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException($"No workspace named \"{name}\".");
        var n = Actions.RunWorkspace(ws);
        return $"Opened {n} of {ws.Items.Count} items from {ws.Name}";
    });

    public Task<string> RunCommandAsync(string keyword, string? query) => Ui(() =>
    {
        var c = Settings.Commands.FirstOrDefault(x => x.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No custom command with keyword \"{keyword}\".");
        return $"{c.Name}: {Actions.RunCustom(c, query ?? "")}";
    });

    public Task AddNoteAsync(string text) => Ui(() => Services.Notes.Add(text));
    public Task<string> ReadNotesAsync(int maxChars) => Ui(() => Services.Notes.Tail(Math.Clamp(maxChars, 100, 50_000)));
    public Task NotifyAsync(string title, string message) => Ui(() => Toast.Show(title, message));
    public Task LockAsync() => Ui(() => { Native.LockWorkStation(); });
    public Task ShowLauncherAsync(string? query) => Ui(() => Services.ShowLauncher?.Invoke(query));

    public Task<ConfirmResult> ConfirmAsync(string client, string tool, string summary) =>
        UiAsync(() => ConfirmWindow.AskAsync(client, tool, summary));
}
