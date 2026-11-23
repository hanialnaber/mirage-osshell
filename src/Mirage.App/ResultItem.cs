using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Mirage.App;

internal sealed class ResultAction
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public Func<Task> Run { get; init; } = () => Task.CompletedTask;
    /// <summary>Keep the launcher open after running.</summary>
    public bool KeepOpen { get; init; }
}

/// <summary>Files or text dropped onto the launcher.</summary>
internal sealed record DropData(string[]? Files, string? Text);

internal sealed class ResultItem : INotifyPropertyChanged
{
    string _title = "", _subtitle = "";
    ImageSource? _icon;
    bool _armed;

    public string Title { get => _title; set { _title = value; OnChanged(); } }
    public string Subtitle { get => _subtitle; set { _subtitle = value; OnChanged(); OnChanged(nameof(HasSubtitle)); } }
    public bool HasSubtitle => !string.IsNullOrEmpty(_subtitle);
    public string Badge { get; init; } = "";
    public string Glyph { get; init; } = "";
    /// <summary>Accent color key for the glyph tile: blue, violet, teal, amber, rose, green, slate.</summary>
    public string Tint { get; init; } = "violet";
    public ImageSource? Icon { get => _icon; set { _icon = value; OnChanged(); OnChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;
    public int Score { get; set; }
    public string? UsageKey { get; init; }
    public string? PinKey { get; init; }
    public bool IsPinned { get; set; }
    public bool KeepOpen { get; init; }
    public string? ConfirmText { get; init; }
    public bool Armed { get => _armed; set { _armed = value; OnChanged(); } }
    /// <summary>Text to put in the search box instead of running (e.g. a mode prefix).</summary>
    public string? Completion { get; init; }
    public Func<Task>? Primary { get; init; }
    public List<ResultAction> Actions { get; } = new();
    public bool IsFallback { get; init; }
    /// <summary>What happens when files or text are dropped onto this row or tile.</summary>
    public Func<DropData, Task>? OnDrop { get; init; }
    public bool AcceptsDrop => OnDrop != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Load the shell icon for a path or shell: target, updating the row when ready.</summary>
    public ResultItem WithIcon(string? target)
    {
        if (string.IsNullOrEmpty(target)) return this;
        var img = IconService.Get(target, i => Icon = i);
        if (img != null) _icon = img;
        return this;
    }
}
