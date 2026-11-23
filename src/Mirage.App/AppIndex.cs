using System.IO;

namespace Mirage.App;

internal sealed record AppEntry(string Name, string Target, string Kind, string? FilePath, string Keywords = "")
{
    public string Key => "app:" + Name.ToLowerInvariant();
}

/// <summary>Indexes Start menu shortcuts, Store apps (shell:AppsFolder) and Windows settings pages.</summary>
internal sealed class AppIndex
{
    volatile List<AppEntry> _apps = new();
    readonly FileSystemWatcher[] _watchers;
    Timer? _debounce;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public event Action? Changed;

    static readonly string[] Roots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    };

    public AppIndex()
    {
        _watchers = Roots.Where(Directory.Exists).Select(r =>
        {
            var w = new FileSystemWatcher(r) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            w.Created += (_, _) => Schedule(); w.Deleted += (_, _) => Schedule(); w.Renamed += (_, _) => Schedule();
            return w;
        }).ToArray();
    }

    void Schedule()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Refresh(), null, 2000, Timeout.Infinite);
    }

    public void Refresh()
    {
        var t = new Thread(() =>
        {
            try
            {
                var list = new List<AppEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in Roots.Where(Directory.Exists))
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }); }
                    catch { continue; }
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                        var name = Path.GetFileNameWithoutExtension(f);
                        var lower = name.ToLowerInvariant();
                        if (lower.Contains("uninstall") || lower.Contains("readme") || lower.Contains("help") && lower.Length < 20 || lower.EndsWith(" website")) continue;
                        if (!seen.Add(name)) continue;
                        list.Add(new AppEntry(name, f, "App", f));
                    }
                }
                // Store / packaged apps and anything else in "All apps"
                try
                {
                    var shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic folder = shell.NameSpace("shell:AppsFolder");
                        dynamic items = folder.Items();
                        int count = items.Count;
                        for (int i = 0; i < count; i++)
                        {
                            dynamic item = items.Item(i);
                            string name = item.Name;
                            string path = item.Path;
                            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                            if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                            list.Add(new AppEntry(name, "shell:AppsFolder\\" + path, path.Contains('!') ? "Store app" : "App", null));
                        }
                    }
                }
                catch (Exception ex) { Log.Error("AppsFolder", ex); }

                list.AddRange(SettingsPages);
                _apps = list;
                Log.Info($"Indexed {list.Count} apps");
                Changed?.Invoke();
            }
            catch (Exception ex) { Log.Error("app index", ex); }
        }) { IsBackground = true, Name = "MirageAppIndex" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static readonly AppEntry[] SettingsPages =
    {
        S("Wi-Fi settings", "ms-settings:network-wifi", "wifi wireless internet network"),
        S("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth headphones mouse"),
        S("Display settings", "ms-settings:display", "screen resolution brightness monitor scale"),
        S("Sound settings", "ms-settings:sound", "audio volume speakers microphone"),
        S("Apps & features", "ms-settings:appsfeatures", "uninstall programs installed"),
        S("Default apps", "ms-settings:defaultapps", "browser default"),
        S("Windows Update", "ms-settings:windowsupdate", "update patch"),
        S("Personalization", "ms-settings:personalization", "wallpaper theme background colors"),
        S("Taskbar settings", "ms-settings:taskbar", "taskbar"),
        S("Power & battery", "ms-settings:powersleep", "battery sleep power"),
        S("Storage settings", "ms-settings:storagesense", "disk space storage cleanup"),
        S("Notifications", "ms-settings:notifications", "focus do not disturb"),
        S("Privacy & security", "ms-settings:privacy", "permissions camera location"),
        S("Date & time", "ms-settings:dateandtime", "clock timezone"),
        S("Keyboard & language", "ms-settings:regionlanguage", "language keyboard input arabic"),
        S("VPN settings", "ms-settings:network-vpn", "vpn"),
        S("Mouse settings", "ms-settings:mousetouchpad", "mouse pointer touchpad"),
        S("Night light", "ms-settings:nightlight", "blue light"),
        S("Startup apps", "ms-settings:startupapps", "startup boot"),
        S("Task Manager", "taskmgr.exe", "processes performance"),
        S("Control Panel", "control.exe", "control panel"),
        S("Environment variables", "rundll32.exe sysdm.cpl,EditEnvironmentVariables", "path env"),
    };

    static AppEntry S(string name, string target, string kw) => new(name, target, "Setting", null, kw);
}
