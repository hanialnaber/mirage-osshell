namespace Mirage.Core.Agent;

public sealed record AppInfo(string Name, string Id, string Kind);
public sealed record WindowInfo(long Handle, string Title, string Process, bool IsActive);

public enum ConfirmResult { Deny, AllowOnce, AlwaysAllow }

/// <summary>What the bridge can do on the desktop. Implemented by the Windows app; faked in tests.</summary>
public interface IDesktop
{
    Task<IReadOnlyList<AppInfo>> ListAppsAsync(string? query, int limit);
    Task<string> LaunchAppAsync(string nameOrId, string? args);
    Task OpenUrlAsync(string url);
    Task OpenPathAsync(string path);
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync();
    Task<WindowInfo?> GetActiveWindowAsync();
    Task<bool> FocusWindowAsync(long handle);
    Task<bool> WindowActionAsync(long handle, string action);
    Task<string?> GetClipboardAsync();
    Task SetClipboardAsync(string text);
    Task PasteTextAsync(string text);
    Task SendKeysAsync(string combo);
    Task<IReadOnlyList<string>> SearchFilesAsync(string query, int limit);
    Task<string> RunWorkspaceAsync(string name);
    Task<string> RunCommandAsync(string keyword, string? query);
    Task AddNoteAsync(string text);
    Task<string> ReadNotesAsync(int maxChars);
    Task NotifyAsync(string title, string message);
    Task LockAsync();
    Task ShowLauncherAsync(string? query);
    Settings Settings { get; }
}

public interface IConfirmer
{
    Task<ConfirmResult> ConfirmAsync(string client, string tool, string summary);
}
