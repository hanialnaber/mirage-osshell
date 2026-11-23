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
            Services.Tray.Balloon("Mirage is ready", $"Press {Services.Settings.Hotkey} to open it. Select text anywhere and press {Services.Settings.SelectionHotkey} for quick actions.");
        if (!background)
            Dispatcher.BeginInvoke(() => _launcher.ShowLauncher(null), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Re-applies everything that depends on settings. Safe to call often.</summary>
    internal void ApplySettings()
    {
        var s = Services.Settings;

        var hotkeys = s.Hotkey + "|" + s.SelectionHotkey;
        if (hotkeys != _registeredHotkeys)
        {
            _msg.UnregisterAll();
            var problems = new List<string>();
            if (_msg.Register(s.Hotkey, () => _launcher.Toggle()) < 0) problems.Add(s.Hotkey);
            if (!string.IsNullOrWhiteSpace(s.SelectionHotkey) && _msg.Register(s.SelectionHotkey, ShowForSelection) < 0) problems.Add(s.SelectionHotkey);
            _registeredHotkeys = hotkeys;
            if (problems.Count > 0)
                Services.Tray.Balloon("Hotkey not available", $"{string.Join(" and ", problems)} is used by another app. Pick another in Mirage settings.");
            Services.Tray.SetTooltip($"Mirage ({s.Hotkey})");
        }

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
            _expander?.Dispose();
            _msg?.Dispose();
            Services.Clipboard?.Save();
            Services.Tray?.Dispose();
        }
        catch { }
        Shutdown();
    }
}
