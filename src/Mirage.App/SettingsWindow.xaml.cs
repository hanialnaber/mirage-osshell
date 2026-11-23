using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mirage.Core;
using Mirage.Core.Agent;

namespace Mirage.App;

public partial class SettingsWindow : Window
{
    const string RepoUrl = "https://github.com/hanialnaber/mirage-launcher";
    bool _loading;
    bool _showToken;

    static Settings S => Services.Settings;

    internal SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyGlass();
        CloseButton.Click += (_, _) => Close();

        foreach (var nav in new[] { NavGeneral, NavLibrary, NavAgents, NavAbout })
            nav.Checked += (_, _) => ShowPage((string)nav.Tag);

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = "Version " + version;
        AboutVersion.Text = $"Version {version} · settings in {Paths.Root}";

        GlassCombo.ItemsSource = Enum.GetNames<GlassMode>();
        EngineCombo.ItemsSource = Settings.SearchEngines.Keys.ToList();

        HookHotkeyBox(HotkeyBox, v => S.Hotkey = v);
        HookHotkeyBox(SelectionHotkeyBox, v => S.SelectionHotkey = v);

        foreach (var (box, set) in new (CheckBox, Action<bool>)[]
        {
            (StartupCheck, v => S.LaunchAtStartup = v), (HideCheck, v => S.HideOnFocusLoss = v), (FilesCheck, v => S.FileSearch = v),
            (ClipCheck, v => S.ClipboardHistory = v), (PersistClipCheck, v => S.PersistClipboard = v), (ExpandCheck, v => S.SnippetAutoExpand = v),
            (BridgeCheck, v => S.Bridge.Enabled = v), (LogCheck, v => S.Bridge.LogActivity = v),
        })
        {
            box.Checked += (_, _) => { if (!_loading) { set(true); Save(); } };
            box.Unchecked += (_, _) => { if (!_loading) { set(false); Save(); } };
        }
        GlassCombo.SelectionChanged += (_, _) => { if (!_loading && GlassCombo.SelectedItem is string g) { S.Glass = Enum.Parse<GlassMode>(g); Save(); } };
        EngineCombo.SelectionChanged += (_, _) => { if (!_loading && EngineCombo.SelectedItem is string e) { S.SearchEngine = e; Save(); } };
        AiCombo.SelectionChanged += (_, _) => { if (!_loading && AiCombo.SelectedItem is string a) { S.DefaultAi = a; Save(); } };
        PortBox.LostFocus += (_, _) => CommitPort();
        PortBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitPort(); };

        ShowTokenButton.Click += (_, _) => { _showToken = !_showToken; RefreshAgents(); };
        CopyTokenButton.Click += (_, _) => Actions.Copy(S.Bridge.Token, toast: true);
        NewTokenButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Agents using the current token will stop working until you give them the new one. Restart stdio agents too.", "Make a new token?", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            S.Bridge.Token = Tokens.NewToken();
            Save();
        };
        CopyStdioButton.Click += (_, _) => Actions.Copy(StdioConfig(), toast: true);
        CopyHttpButton.Click += (_, _) => Actions.Copy(HttpConfig(reveal: true), toast: true);
        CopyRestButton.Click += (_, _) => Actions.Copy(RestExample(reveal: true), toast: true);
        ClearLogButton.Click += (_, _) => App.Instance.ClearActivity();

        EditJsonButton.Click += (_, _) => { Services.SaveSettings(); Actions.Open("notepad.exe", $"\"{Paths.Settings}\""); };
        ReloadButton.Click += (_, _) => ReloadFromDisk();
        SaveWsButton.Click += (_, _) =>
        {
            var name = $"Workspace {DateTime.Now:MMM d HH.mm}";
            var ws = Actions.SaveWindowsAsWorkspace(name);
            Toast.Show($"Saved \"{ws.Name}\"", $"{ws.Items.Count} apps. Rename it in settings.json.");
        };
        OpenDataButton.Click += (_, _) => Actions.Open(Paths.Root);
        OpenLogButton.Click += (_, _) => { if (File.Exists(Paths.Log)) Actions.Open("notepad.exe", $"\"{Paths.Log}\""); };
        RepoButton.Click += (_, _) => Actions.OpenUrl(RepoUrl);

        App.Instance.ActivityChanged += RefreshActivity;
        Closed += (_, _) => App.Instance.ActivityChanged -= RefreshActivity;
        Services.SettingsSaved += OnSaved;
        Closed += (_, _) => Services.SettingsSaved -= OnSaved;

        LoadValues();
    }

    void OnSaved() => Dispatcher.BeginInvoke(LoadValues);

    public void ApplyGlass()
    {
        var ok = Glass.Apply(this, S.Glass == GlassMode.Auto ? GlassMode.Mica : S.Glass);
        Root.Background = (Brush)FindResource(ok ? "GlassTint" : "SolidTint");
    }

    void ShowPage(string name)
    {
        PageGeneral.Visibility = name == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = name == "Library" ? Visibility.Visible : Visibility.Collapsed;
        PageAgents.Visibility = name == "Agents" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = name == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    void Save()
    {
        Services.SaveSettings();
    }

    void LoadValues()
    {
        _loading = true;
        try
        {
            HotkeyBox.Text = S.Hotkey;
            SelectionHotkeyBox.Text = S.SelectionHotkey;
            StartupCheck.IsChecked = S.LaunchAtStartup;
            HideCheck.IsChecked = S.HideOnFocusLoss;
            FilesCheck.IsChecked = S.FileSearch;
            ClipCheck.IsChecked = S.ClipboardHistory;
            PersistClipCheck.IsChecked = S.PersistClipboard;
            ExpandCheck.IsChecked = S.SnippetAutoExpand;
            GlassCombo.SelectedItem = S.Glass.ToString();
            EngineCombo.SelectedItem = S.SearchEngine;
            AiCombo.ItemsSource = S.AiTargets.Select(a => a.Name).ToList();
            AiCombo.SelectedItem = Actions.DefaultAi().Name;
            BridgeCheck.IsChecked = S.Bridge.Enabled;
            LogCheck.IsChecked = S.Bridge.LogActivity;
            PortBox.Text = S.Bridge.Port.ToString();
            RefreshAgents();
            RefreshLibrary();
            RefreshActivity();
            KeysText.Text =
                $"{S.Hotkey}   open or close Mirage\n{S.SelectionHotkey}   actions for selected text\n" +
                "↑ ↓   move     ↵   open     Tab   more actions     Esc   clear, then close\n" +
                "Alt+1…9   open that result     Ctrl+P   pin to home     Ctrl+,   settings\n" +
                "w  windows   f  files   c  clipboard   ;  snippets   ws  workspaces\n" +
                "?  ask AI   n  quick note   notes  search notes   >  command   =  calculator";
        }
        finally { _loading = false; }
    }

    void HookHotkeyBox(TextBox box, Action<string> set)
    {
        box.GotKeyboardFocus += (_, _) => { box.Tag = box.Text; box.Text = "Press keys…"; };
        box.LostKeyboardFocus += (_, _) => { if (box.Text == "Press keys…") box.Text = (string)box.Tag; };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (key == Key.Escape) { box.Text = (string)box.Tag; Keyboard.ClearFocus(); return; }
            var mods = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
            bool fkey = key >= Key.F1 && key <= Key.F24;
            if (mods == ModifierKeys.None && !fkey) { box.Text = "Add Ctrl, Alt, Shift or Win"; return; }
            var combo = MessageWindow.Format(mods, key);
            box.Text = combo;
            box.Tag = combo;
            set(combo);
            Save();
            Keyboard.ClearFocus();
        };
    }

    void CommitPort()
    {
        if (_loading) return;
        if (int.TryParse(PortBox.Text.Trim(), out var p) && p is >= 1024 and <= 65535)
        {
            if (p != S.Bridge.Port) { S.Bridge.Port = p; Save(); }
        }
        else PortBox.Text = S.Bridge.Port.ToString();
    }

    void ReloadFromDisk()
    {
        try
        {
            var fresh = Settings.Load(Paths.Settings);
            Services.Settings = fresh;
            Services.SaveSettings();
            Toast.Show("Settings reloaded", $"{fresh.Snippets.Count} snippets, {fresh.Workspaces.Count} workspaces, {fresh.Commands.Count} commands");
        }
        catch (Exception ex) { MessageBox.Show(this, "settings.json has a mistake, so nothing changed.\n\n" + ex.Message, "Mirage"); }
    }

    // ---------- agents page ----------

    string MaskedToken => _showToken ? S.Bridge.Token : S.Bridge.Token[..Math.Min(8, S.Bridge.Token.Length)] + new string('•', 20);

    static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string StdioConfig() =>
        "{\n  \"mcpServers\": {\n    \"mirage\": {\n      \"command\": \"" + JsonEscape(Paths.Exe) + "\",\n      \"args\": [\"--mcp\"]\n    }\n  }\n}";

    string HttpConfig(bool reveal) =>
        $"URL:     http://127.0.0.1:{S.Bridge.Port}/mcp\nHeader:  Authorization: Bearer {(reveal ? S.Bridge.Token : MaskedToken)}";

    string RestExample(bool reveal)
    {
        var t = reveal ? S.Bridge.Token : MaskedToken;
        return $"curl -H \"Authorization: Bearer {t}\" http://127.0.0.1:{S.Bridge.Port}/v1/tools\n" +
               $"curl -X POST -H \"Authorization: Bearer {t}\" -H \"Content-Type: application/json\" -d \"{{\\\"app\\\":\\\"Notepad\\\"}}\" http://127.0.0.1:{S.Bridge.Port}/v1/tools/launch_app";
    }

    void RefreshAgents()
    {
        TokenBox.Text = MaskedToken;
        ShowTokenButton.Content = _showToken ? "Hide" : "Show";
        StdioSnippet.Text = StdioConfig();
        HttpSnippet.Text = HttpConfig(false);
        RestSnippet.Text = RestExample(false);
        BridgeState.Text = !S.Bridge.Enabled ? "Off. Nothing is listening."
            : App.Instance.BridgeRunning ? $"On. Listening on 127.0.0.1:{S.Bridge.Port} (this PC only)."
            : App.Instance.BridgeError ?? "Starting…";

        ToolList.Items.Clear();
        foreach (var tool in App.Instance.Agent.Tools.OrderBy(t => t.Name))
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = tool.Name, Style = (Style)FindResource("Label"), FontFamily = new FontFamily("Cascadia Mono, Consolas") });
            text.Children.Add(new TextBlock { Text = tool.Description, Style = (Style)FindResource("Hint") });
            row.Children.Add(text);
            var combo = new ComboBox { Style = (Style)FindResource("GlassCombo"), MinWidth = 110, ItemsSource = Enum.GetNames<Permission>(), VerticalAlignment = VerticalAlignment.Center };
            combo.SelectedItem = App.Instance.Agent.EffectivePermission(tool.Name).ToString();
            var name = tool.Name;
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading || combo.SelectedItem is not string v) return;
                S.Bridge.Permissions[name] = Enum.Parse<Permission>(v);
                Save();
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            ToolList.Items.Add(row);
        }
    }

    void RefreshActivity()
    {
        ActivityList.Items.Clear();
        var log = App.Instance.ActivityLog;
        if (log.Count == 0)
        {
            ActivityList.Items.Add(new TextBlock { Text = S.Bridge.LogActivity ? "Nothing yet." : "The activity log is off.", Style = (Style)FindResource("Hint") });
            return;
        }
        foreach (var e in log.Take(60))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var outcome = new TextBlock { Text = e.Outcome, FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0), Foreground = (Brush)FindResource(e.Outcome.StartsWith("ok", StringComparison.OrdinalIgnoreCase) || e.Outcome.StartsWith("allowed", StringComparison.OrdinalIgnoreCase) ? "SubtleTextBrush" : "AccentBrush") };
            DockPanel.SetDock(outcome, Dock.Right);
            row.Children.Add(outcome);
            row.Children.Add(new TextBlock
            {
                Text = $"{e.Time:HH:mm:ss}  {e.Client} · {e.Tool}  {e.Summary}",
                Style = (Style)FindResource("Hint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            });
            ActivityList.Items.Add(row);
        }
    }

    // ---------- library page ----------

    void RefreshLibrary()
    {
        LibraryList.Items.Clear();
        void Section(string title, string hint, IEnumerable<(string Key, string Value)> rows)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("TextBrush"), FontSize = 14, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 8) });
            int n = 0;
            foreach (var (k, v) in rows)
            {
                n++;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var key = new TextBlock { Text = k, Width = 140, Foreground = (Brush)FindResource("AccentBrush"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
                row.Children.Add(key);
                row.Children.Add(new TextBlock { Text = v, Style = (Style)FindResource("Hint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                stack.Children.Add(row);
            }
            if (n == 0) stack.Children.Add(new TextBlock { Text = "None yet.", Style = (Style)FindResource("Hint") });
            LibraryList.Items.Add(new Border { Style = (Style)FindResource("Card"), Child = stack });
        }

        Section("Snippets", "Type ; in Mirage to insert one, or turn on expansion to type the keyword anywhere. Placeholders: {date} {time} {clipboard} {guid}.",
            S.Snippets.Select(s => (s.Keyword, string.IsNullOrEmpty(s.Name) ? Actions.Shorten(s.Text, 90) : s.Name)));
        Section("Workspaces", "Type ws and a name to open a set of apps, sites and folders at once.",
            S.Workspaces.Select(w => (w.Name, $"{w.Items.Count} items · " + string.Join(", ", w.Items.Take(4).Select(i => Path.GetFileNameWithoutExtension(i.Target))))));
        Section("Custom commands", "Type the keyword, a space and your text. {query} is replaced with what you type.",
            S.Commands.Select(c => (c.Keyword, $"{c.Name} · {c.Type} · {Actions.Shorten(c.Template, 70)}")));
        Section("AI targets", "Where ? questions and text actions go. {query} pre-fills the prompt where the site supports it.",
            S.AiTargets.Select(a => (a.Name, string.IsNullOrEmpty(a.App) ? a.Url : "app: " + a.App)));
    }
}
