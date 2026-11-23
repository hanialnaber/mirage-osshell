using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Mirage.Core;
using Mirage.Core.Agent;

namespace Mirage.App;

public partial class App : Application
{
    internal static App Instance { get; private set; } = null!;

    MessageWindow _msg = null!;
    LauncherWindow _launcher = null!;
    SnippetExpander _expander = null!;
    BridgeServer _bridge = null!;
    SettingsWindow? _settingsWindow;
    PointerTriggers _pointer = null!;
    string? _registeredHotkeys;

    internal AgentHost Agent { get; private set; } = null!;
    internal List<ActivityEntry> ActivityLog { get; } = new();
    internal event Action? ActivityChanged;
    internal string? BridgeError { get; private set; }
    internal bool BridgeRunning => _bridge?.Running == true;

    internal void Boot(bool background)
    {
        Instance = this;
        DispatcherUnhandledException += (_, e) => { Log.Error("UI exception", e.Exception); Toast.Show("Something went wrong", e.Exception.Message); e.Handled = true; };

        Directory.CreateDirectory(Paths.Root);
        bool firstRun = !File.Exists(Paths.Settings);
        try { Services.Settings = Settings.Load(Paths.Settings); }
        catch (JsonException ex)
        {
            Log.Error("settings.json is not valid JSON; keeping a copy and starting from defaults", ex);
            try { File.Copy(Paths.Settings, Paths.Settings + ".broken", overwrite: true); } catch { }
            Services.Settings = Settings.CreateDefault();
            Services.Settings.Normalize();
        }
        if (firstRun && Paths.Portable) Services.Settings.LaunchAtStartup = false; // portable copies don't add themselves to startup
        try { Services.Settings.Save(Paths.Settings); } catch (Exception ex) { Log.Error("save settings", ex); }

        Services.Usage = new UsageStore(Paths.Usage);
        Services.Notes = new NotesStore(Paths.Notes);
        Services.Clipboard = new ClipboardService();
        Services.Apps = new AppIndex();
        Services.Apps.Refresh();

        Services.Tray = new TrayService();
        Services.Tray.OpenLauncher += () => _launcher.ShowLauncher(null);
        Services.Tray.OpenSettings += ShowSettings;
        Services.Tray.Quit += Quit;
        Services.Tray.ToggleBridge += () =>
        {
            Services.Settings.Bridge.Enabled = !Services.Settings.Bridge.Enabled;
            Services.SaveSettings();
            Toast.Show(Services.Settings.Bridge.Enabled ? "AI agent bridge is on" : "AI agent bridge is off",
                       Services.Settings.Bridge.Enabled ? $"Listening on 127.0.0.1:{Services.Settings.Bridge.Port}" : "");
        };

        _launcher = new LauncherWindow();
        Services.ShowLauncher = q => _launcher.ShowLauncher(q);
        Services.OpenSettings = ShowSettings;
        Services.SettingsSaved = ApplySettings;
        Services.ShowSection = sec => _launcher.ShowLauncher(null, null, sec);
        Services.ShowQuickMenu = () => RadialMenu.ShowAtCursor(QuickMenuItems());
        Services.ActOnSelection = ShowForSelection;

        _pointer = new PointerTriggers();
        _pointer.HotCorner += dragging =>
        {
            if (_launcher.IsVisible && _launcher.IsActive) return;
            if (dragging) _launcher.ShowForDrop(); else _launcher.ShowLauncher(null);
        };
        _pointer.QuickMenuButton += (x, y) => RadialMenu.ShowAt(x, y, QuickMenuItems());

        _msg = new MessageWindow();
        _msg.ClipboardChanged += Services.Clipboard.OnClipboardChanged;
        _expander = new SnippetExpander();

        var desktop = new DesktopBridge();
        Agent = new AgentHost(desktop, desktop);
        Agent.Activity += e => Dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Insert(0, e);
            if (ActivityLog.Count > 200) ActivityLog.RemoveAt(ActivityLog.Count - 1);
            ActivityChanged?.Invoke();
        });
        Agent.SettingsChanged += () => Dispatcher.BeginInvoke(Services.SaveSettings);
        _bridge = new BridgeServer(Agent, new McpHandler(Agent, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"), () => Services.Settings.Bridge.Token);

        ApplySettings();
        Log.Info($"Mirage started ({(background ? "background" : "interactive")})");

        if (firstRun)
            Services.Tray.Balloon("Mirage is ready", FirstRunHint());
        if (!background)
            Dispatcher.BeginInvoke(() => _launcher.ShowLauncher(null), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Re-applies everything that depends on settings. Safe to call often.</summary>
    internal void ApplySettings()
    {
        var s = Services.Settings;

        var hotkeys = s.Hotkey + "|" + s.SelectionHotkey + "|" + s.QuickMenuHotkey;
        if (hotkeys != _registeredHotkeys)
        {
            _msg.UnregisterAll();
            var problems = new List<string>();
            if (_msg.Register(s.Hotkey, () => _launcher.Toggle()) < 0) problems.Add(s.Hotkey);
            if (!string.IsNullOrWhiteSpace(s.SelectionHotkey) && _msg.Register(s.SelectionHotkey, ShowForSelection) < 0) problems.Add(s.SelectionHotkey);
            if (!string.IsNullOrWhiteSpace(s.QuickMenuHotkey) && _msg.Register(s.QuickMenuHotkey, () => RadialMenu.ShowAtCursor(QuickMenuItems())) < 0) problems.Add(s.QuickMenuHotkey);
            _registeredHotkeys = hotkeys;
            if (problems.Count > 0)
                Services.Tray.Balloon("Hotkey not available", $"{string.Join(" and ", problems)} is used by another app. Pick another in Mirage settings.");
            Services.Tray.SetTooltip($"Mirage ({s.Hotkey})");
        }

        _pointer.Configure(s.HotCorner, s.HotCornerDelayMs, s.QuickMenuButton);

        try { global::Mirage.App.Startup.Set(s.LaunchAtStartup); } catch (Exception ex) { Log.Error("startup", ex); }

        Services.Clipboard.Enabled = s.ClipboardHistory;
        Services.Clipboard.Limit = s.ClipboardLimit;
        Services.Clipboard.Persist = s.PersistClipboard;
        Services.Clipboard.Save();
        _msg.ListenClipboard(false);
        if (s.ClipboardHistory) _msg.ListenClipboard(true);

        _expander.Update(s.Snippets);
        if (s.SnippetAutoExpand) _expander.Start(); else _expander.Stop();

        if (s.Bridge.Enabled)
        {
            if (!_bridge.Running || _bridge.Port != s.Bridge.Port)
            {
                try { _bridge.Start(s.Bridge.Port); BridgeError = null; Log.Info($"Agent bridge listening on 127.0.0.1:{s.Bridge.Port}"); }
                catch (Exception ex)
                {
                    _bridge.Stop();
                    BridgeError = $"Port {s.Bridge.Port} is busy. Pick another port.";
                    Log.Error("bridge start", ex);
                    Services.Tray.Balloon("AI agent bridge couldn't start", BridgeError);
                }
            }
        }
        else if (_bridge.Running) { _bridge.Stop(); BridgeError = null; }
        Services.Tray.SetBridgeState(_bridge.Running);

        _launcher.ApplyGlass();
        _settingsWindow?.ApplyGlass();
    }

    async void ShowForSelection()
    {
        string? sel = null;
        try { sel = await InputService.CopySelectionAsync(); } catch (Exception ex) { Log.Error("copy selection", ex); }
        _launcher.ShowLauncher(null, sel);
        if (sel == null) Toast.Show("No text selected", "Select some text first, or just search");
    }

    internal void ShowLauncherFromSignal() => _launcher.Toggle();

    /// <summary>The radial menu's slices, clockwise from the top.</summary>
    List<RadialMenu.Item> QuickMenuItems() => new()
    {
        new("Search", "\uE721", "blue", () => _launcher.ShowLauncher(null, null, HomeSection.Apps)),
        new("Selected text", "\uE8C8", "violet", ShowForSelection),
        new("Ask AI", "\uE99A", "teal", () => _launcher.ShowLauncher(null, null, HomeSection.Ai)),
        new("Clipboard", "\uE77F", "amber", () => _launcher.ShowLauncher(null, null, HomeSection.Clipboard)),
        new("Quick note", "\uE70B", "rose", () => _launcher.ShowLauncher("n ")),
        new("Workspaces", "\uE8A9", "green", () => _launcher.ShowLauncher(null, null, HomeSection.Workspaces)),
        new("Windows", "\uE737", "slate", () => _launcher.ShowLauncher("w ")),
    };

    static string FirstRunHint()
    {
        var s = Services.Settings;
        var corner = s.HotCorner switch
        {
            Corner.TopLeft => "top-left", Corner.TopRight => "top-right",
            Corner.BottomLeft => "bottom-left", Corner.BottomRight => "bottom-right", _ => null,
        };
        return corner != null
            ? $"Push the pointer into the {corner} corner, or press {s.Hotkey}. Map a touchpad gesture to it in Mirage settings."
            : $"Press {s.Hotkey} to open it. Map a touchpad gesture to it in Mirage settings.";
    }

    void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    internal void ClearActivity() { ActivityLog.Clear(); ActivityChanged?.Invoke(); }

    internal void Quit()
    {
        try
        {
            _bridge?.Stop();
            _pointer?.Dispose();
            _expander?.Dispose();
            _msg?.Dispose();
            Services.Clipboard?.Save();
            Services.Tray?.Dispose();
        }
        catch { }
        Shutdown();
    }
}
