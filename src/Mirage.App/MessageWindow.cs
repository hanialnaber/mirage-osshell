using System.Windows.Input;
using System.Windows.Interop;

namespace Mirage.App;

/// <summary>Hidden message-only window that receives global hotkeys and clipboard updates.</summary>
internal sealed class MessageWindow : IDisposable
{
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _hotkeys = new();
    int _nextId = 0xB000;

    public event Action? ClipboardChanged;
    public IntPtr Handle => _source.Handle;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("MirageMessages") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }; // HWND_MESSAGE
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _hotkeys.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { Log.Error("hotkey action", ex); }
        }
        else if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            try { ClipboardChanged?.Invoke(); } catch (Exception ex) { Log.Error("clipboard", ex); }
        }
        return IntPtr.Zero;
    }

    public bool ListenClipboard(bool on) =>
        on ? Native.AddClipboardFormatListener(Handle) : Native.RemoveClipboardFormatListener(Handle);

    /// <summary>Registers a hotkey like "Alt+Space" or "Ctrl+Shift+K". Returns the id or -1.</summary>
    public int Register(string combo, Action action)
    {
        if (!TryParse(combo, out var mods, out var vk)) return -1;
        var id = _nextId++;
        if (!Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            Log.Error($"RegisterHotKey failed for {combo} (code {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return -1;
        }
        _hotkeys[id] = action;
        return id;
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeys.Keys) Native.UnregisterHotKey(Handle, id);
        _hotkeys.Clear();
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (Enum.TryParse<Key>(name, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var k = key.ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1..];
        parts.Add(k);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        try { ListenClipboard(false); } catch { }
        _source.Dispose();
    }
}
