using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mirage.Core;

namespace Mirage.App;

public partial class LauncherWindow : Window
{
    readonly ObservableCollection<ResultItem> _items = new();
    readonly LauncherContext _ctx = new();
    readonly DispatcherTimer _fileTimer;
    CancellationTokenSource? _fileCts;
    int _refreshVersion;
    bool _running;
    bool _glassApplied;

    internal LauncherWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        Query.TextChanged += (_, _) => Refresh();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (Services.Settings.HideOnFocusLoss && !_running && IsVisible && !ConfirmWindow.IsOpen) HideLauncher(); };
        Results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem li && li.DataContext is ResultItem r)
            {
                Results.SelectedItem = r;
                _ = RunAsync(r, null);
            }
        };
        ActionList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(ActionList, (DependencyObject)e.OriginalSource) is ListBoxItem li && li.DataContext is ResultAction a && Selected is { } r)
                _ = RunAsync(r, a);
        };
        Results.SelectionChanged += (_, _) => { CloseActions(); UpdateFooter(); };
        _fileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _fileTimer.Tick += async (_, _) => { _fileTimer.Stop(); await SearchFilesAsync(); };
        SourceInitialized += (_, _) => ApplyGlass();
    }

    ResultItem? Selected => Results.SelectedItem as ResultItem;

    public void ApplyGlass()
    {
        _glassApplied = Glass.Apply(this, Services.Settings.Glass);
        Root.Background = (System.Windows.Media.Brush)FindResource(_glassApplied ? "GlassTint" : "SolidTint");
    }

    // ---------- show / hide ----------

    internal void ShowLauncher(string? query, string? selection = null)
    {
        var fg = Native.GetForegroundWindow();
        var me = new WindowInteropHelper(this).EnsureHandle();
        if (fg != me) _ctx.PreviousWindow = fg;
        _ctx.Selection = string.IsNullOrWhiteSpace(selection) ? null : selection;

        SelectionBox.Visibility = _ctx.Selection != null ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = _ctx.Selection != null ? Actions.Shorten(_ctx.Selection, 240) : "";
        Placeholder.Text = _ctx.Selection != null ? "What should I do with the selected text?" : "Search apps, files, or type ? to ask AI";

        Query.Text = query ?? "";
        Query.CaretIndex = Query.Text.Length;
        Refresh();

        Monitors.PlaceOnCursorMonitor(this, 0.2);
        Show();
        Activate();
        Native.SetForegroundWindow(me);
        Query.Focus();
        Keyboard.Focus(Query);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    internal void Toggle(string? query = null)
    {
        if (IsVisible && IsActive && query == null) HideLauncher();
        else ShowLauncher(query);
    }

    internal void HideLauncher()
    {
        _fileCts?.Cancel();
        CloseActions();
        Hide();
        _ctx.Selection = null;
    }

    // ---------- results ----------

    void Refresh(bool keepSelection = false)
    {
        var raw = Query.Text;
        Placeholder.Visibility = raw.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var parsed = QueryRouter.Parse(raw, Services.Settings);
        UpdateModeChip(parsed);

        int oldIndex = Results.SelectedIndex;
        var list = SearchService.Build(raw, _ctx);
        _refreshVersion++;
        _items.Clear();
        foreach (var r in list) _items.Add(r);
        Results.SelectedIndex = _items.Count == 0 ? -1 : keepSelection ? Math.Clamp(oldIndex, 0, _items.Count - 1) : 0;
        if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
        Status.Text = "";
        UpdateFooter();

        _fileTimer.Stop();
        _fileCts?.Cancel();
        bool wantFiles = Services.Settings.FileSearch && _ctx.Selection == null &&
                         ((parsed.Mode == QueryMode.Files && parsed.Text.Trim().Length >= 2) ||
                          (parsed.Mode == QueryMode.Default && parsed.Text.Trim().Length >= 3 && !TextTools.LooksLikeUrl(parsed.Text) && !Calculator.LooksLikeExpression(parsed.Text)));
        if (wantFiles) _fileTimer.Start();
    }

    async Task SearchFilesAsync()
    {
        var parsed = QueryRouter.Parse(Query.Text, Services.Settings);
        var version = _refreshVersion;
        _fileCts?.Cancel();
        var cts = _fileCts = new CancellationTokenSource();
        bool filesMode = parsed.Mode == QueryMode.Files;
        if (filesMode) Status.Text = "Searching…";
        List<string> paths;
        try { paths = await FileSearchService.SearchAsync(parsed.Text, filesMode ? 30 : 4, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Error("file search", ex); paths = new(); }
        if (cts.IsCancellationRequested || version != _refreshVersion) return;
        Status.Text = "";
        var found = SearchService.FileResults(paths);
        if (filesMode)
        {
            _items.Clear();
            foreach (var r in found) _items.Add(r);
            if (_items.Count == 0)
                _items.Add(new ResultItem { Title = "No matching files", Subtitle = "Mirage searches your user folders by file name", Glyph = "", Tint = "slate", KeepOpen = true });
            Results.SelectedIndex = 0;
        }
        else if (found.Count > 0)
        {
            int at = _items.ToList().FindIndex(i => i.IsFallback);
            if (at < 0) at = _items.Count;
            foreach (var r in found.Where(f => !_items.Any(i => i.Subtitle == f.Subtitle && i.Title == f.Title)))
                _items.Insert(at++, r);
            if (Results.SelectedIndex < 0) Results.SelectedIndex = 0;
        }
        UpdateFooter();
    }

    static readonly Dictionary<QueryMode, (string Name, string Glyph)> ModeInfo = new()
    {
        [QueryMode.Windows] = ("Windows", ""),
        [QueryMode.Files] = ("Files", ""),
        [QueryMode.Clipboard] = ("Clipboard", ""),
        [QueryMode.Snippets] = ("Snippets", ""),
        [QueryMode.NoteAdd] = ("New note", ""),
        [QueryMode.Notes] = ("Notes", ""),
        [QueryMode.Workspaces] = ("Workspaces", ""),
        [QueryMode.Ai] = ("Ask AI", ""),
        [QueryMode.Shell] = ("Command line", ""),
        [QueryMode.Calc] = ("Calculator", ""),
    };

    void UpdateModeChip(ParsedQuery q)
    {
        string? name = null; string glyph = "";
        if (q.Mode == QueryMode.Command && q.Command != null) { name = q.Command.Name; glyph = ""; }
        else if (ModeInfo.TryGetValue(q.Mode, out var m)) { name = m.Name; glyph = m.Glyph; }
        else if (_ctx.Selection != null) { name = "Selected text"; glyph = ""; }
        ModeChip.Visibility = name != null ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Text = name ?? "";
        ModeGlyph.Text = glyph;
    }

    void UpdateFooter()
    {
        var r = Selected;
        if (r == null) { Hints.Text = "Esc Close"; return; }
        if (r.Armed && r.ConfirmText != null) { Hints.Text = "↵ Confirm    Esc Cancel"; return; }
        var verb = r.Primary == null && r.Completion != null ? "↵ Select" : "↵ Open";
        Hints.Text = r.Actions.Count > 0 ? $"{verb}    Tab Actions ({r.Actions.Count})    Esc Close" : $"{verb}    Esc Close";
    }

    // ---------- running ----------

    async Task RunAsync(ResultItem item, ResultAction? action)
    {
        if (_running) return;
        if (action == null && item.Primary == null && item.Completion != null)
        {
            Query.Text = item.Completion;
            Query.CaretIndex = Query.Text.Length;
            return;
        }
        if (action == null && item.ConfirmText != null && !item.Armed)
        {
            foreach (var other in _items) other.Armed = false;
            item.Armed = true;
            Status.Text = item.ConfirmText + " Press Enter again to confirm.";
            UpdateFooter();
            return;
        }
        var run = action?.Run ?? item.Primary;
        if (run == null) return;
        bool keepOpen = action?.KeepOpen ?? item.KeepOpen;
        if (item.UsageKey != null) Services.Usage.Record(item.UsageKey);

        _running = true;
        try
        {
            if (!keepOpen) HideLauncher();
            await run();
        }
        catch (Exception ex)
        {
            Log.Error($"run {item.Title}", ex);
            Toast.Show("Couldn't do that", ex.Message);
        }
        finally { _running = false; }
        if (keepOpen && IsVisible) { CloseActions(); Refresh(keepSelection: true); }
    }

    // ---------- actions panel ----------

    bool ActionsOpen => ActionPanel.Visibility == Visibility.Visible;

    void OpenActions()
    {
        var r = Selected;
        if (r == null || r.Actions.Count == 0) return;
        ActionTitle.Text = r.Title;
        ActionList.ItemsSource = r.Actions;
        ActionList.SelectedIndex = 0;
        ActionPanel.Visibility = Visibility.Visible;
        ActionPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    void CloseActions()
    {
        if (ActionPanel == null) return;
        ActionPanel.Visibility = Visibility.Collapsed;
        ActionList.ItemsSource = null;
    }

    // ---------- keyboard ----------

    static string ComboText(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key switch { Key.Return => "Enter", Key.Delete => "Del", _ => key.ToString() });
        return string.Join("+", parts);
    }

    void Move(int delta)
    {
        var list = ActionsOpen ? (ListBox)ActionList : Results;
        int n = list.Items.Count;
        if (n == 0) return;
        int i = list.SelectedIndex < 0 ? 0 : list.SelectedIndex + delta;
        if (Math.Abs(delta) == 1) i = (i + n) % n; else i = Math.Clamp(i, 0, n - 1);
        list.SelectedIndex = i;
        list.ScrollIntoView(list.SelectedItem);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                if (ActionsOpen) CloseActions();
                else if (Selected is { Armed: true } armed) { armed.Armed = false; Status.Text = ""; UpdateFooter(); }
                else if (Query.Text.Length > 0) Query.Text = "";
                else HideLauncher();
                return;
            case Key.Down: Move(1); e.Handled = true; return;
            case Key.Up: Move(-1); e.Handled = true; return;
            case Key.PageDown: Move(6); e.Handled = true; return;
            case Key.PageUp: Move(-6); e.Handled = true; return;
            case Key.N when mods == ModifierKeys.Control: Move(1); e.Handled = true; return;
            case Key.P when mods == ModifierKeys.Control:
                // Ctrl+P pins or unpins the selected result
                e.Handled = true;
                if (Selected?.PinKey is { } pin)
                {
                    var s = Services.Settings;
                    bool nowPinned = !s.Pinned.Remove(pin);
                    if (nowPinned) s.Pinned.Add(pin);
                    Services.SaveSettings();
                    Status.Text = nowPinned ? "Pinned to the home list" : "Unpinned";
                    Refresh(keepSelection: true);
                }
                return;
            case Key.OemComma when mods == ModifierKeys.Control:
                e.Handled = true;
                HideLauncher();
                Services.OpenSettings?.Invoke();
                return;
            case Key.Tab:
                e.Handled = true;
                if (ActionsOpen) CloseActions();
                else if (Selected is { } t)
                {
                    if (t.Actions.Count > 0) OpenActions();
                    else if (t.Completion != null) { Query.Text = t.Completion; Query.CaretIndex = Query.Text.Length; }
                }
                return;
            case Key.Back when Query.Text.Length == 0 && _ctx.Selection != null:
                e.Handled = true;
                _ctx.Selection = null;
                SelectionBox.Visibility = Visibility.Collapsed;
                Placeholder.Text = "Search apps, files, or type ? to ask AI";
                Refresh();
                return;
        }

        // Alt+1..9 runs that row directly
        if (mods == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D9)
        {
            int idx = key - Key.D1;
            if (idx < _items.Count) { e.Handled = true; _ = RunAsync(_items[idx], null); }
            return;
        }

        if (key == Key.Return && mods == ModifierKeys.None)
        {
            e.Handled = true;
            if (ActionsOpen && ActionList.SelectedItem is ResultAction a && Selected is { } r1) _ = RunAsync(r1, a);
            else if (Selected is { } r2) _ = RunAsync(r2, null);
            return;
        }

        // Any other combo: match a shortcut on the selected result's actions
        if (mods != ModifierKeys.None && Selected is { } sel && key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt))
        {
            // Let Ctrl+C copy text that is selected in the search box
            if (key == Key.C && mods == ModifierKeys.Control && Query.SelectionLength > 0) return;
            var combo = ComboText(mods, key);
            var match = sel.Actions.FirstOrDefault(a => a.Shortcut.Equals(combo, StringComparison.OrdinalIgnoreCase));
            if (match == null && combo == "Shift+Enter" && sel.Actions.Count > 0) match = sel.Actions[0];
            if (match != null) { e.Handled = true; _ = RunAsync(sel, match); }
        }
    }
}
