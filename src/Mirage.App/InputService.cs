using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Clipboard = System.Windows.Clipboard;

namespace Mirage.App;

/// <summary>Synthesized keyboard input: paste into apps, press shortcuts, grab the current selection.</summary>
internal static class InputService
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    static readonly int[] ModifierVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

    static Native.INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
        if (vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x21 or 0x22 or 0x23 or 0x24 or 0x5B or 0x5C or 0xA3 or 0xA5)
            flags |= Native.KEYEVENTF_EXTENDEDKEY;
        return new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    static void Send(params Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());

    /// <summary>Let go of modifiers the user may still be holding from the hotkey.</summary>
    public static void ReleaseModifiers()
    {
        var ups = new List<Native.INPUT>();
        foreach (var vk in ModifierVks)
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) ups.Add(Key((ushort)vk, true));
        if (ups.Count > 0) Send(ups.ToArray());
    }

    public static bool TryParseCombo(string combo, out List<ushort> mods, out ushort key)
    {
        mods = new(); key = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods.Add(0x11); break;
                case "alt": mods.Add(0x12); break;
                case "shift": mods.Add(0x10); break;
                case "win": case "windows": mods.Add(0x5B); break;
                case "enter": case "return": key = 0x0D; break;
                case "esc": case "escape": key = 0x1B; break;
                case "del": key = 0x2E; break;
                case "pgup": key = 0x21; break;
                case "pgdn": key = 0x22; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<System.Windows.Input.Key>(name, true, out var k)) return false;
                    key = (ushort)KeyInterop.VirtualKeyFromKey(k);
                    break;
            }
        }
        return key != 0 || mods.Count > 0;
    }

    public static void SendCombo(string combo)
    {
        if (!TryParseCombo(combo, out var mods, out var key)) throw new ArgumentException($"Unknown keys '{combo}'");
        var seq = new List<Native.INPUT>();
        foreach (var m in mods) seq.Add(Key(m, false));
        if (key != 0) { seq.Add(Key(key, false)); seq.Add(Key(key, true)); }
        for (int i = mods.Count - 1; i >= 0; i--) seq.Add(Key(mods[i], true));
        Send(seq.ToArray());
    }

    public static string? GetClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(30); }
        }
        return null;
    }

    public static bool SetClipboardText(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (COMException) { Thread.Sleep(40); }
        }
        return false;
    }

    /// <summary>Paste text into the target (or current) window. Must run on the UI thread.</summary>
    public static async Task PasteAsync(string text, IntPtr target, bool restoreClipboard)
    {
        var previous = restoreClipboard ? GetClipboardText() : null;
        ClipboardSuppress.Begin();
        try
        {
            if (!SetClipboardText(text)) throw new InvalidOperationException("Clipboard is busy");
            if (target != IntPtr.Zero) WindowService.Focus(target);
            await Task.Delay(70);
            ReleaseModifiers();
            SendCombo("Ctrl+V");
            if (restoreClipboard && previous != null)
            {
                await Task.Delay(450);
                SetClipboardText(previous);
            }
        }
        finally { ClipboardSuppress.End(); }
    }

    /// <summary>Copy whatever is selected in the foreground app. Returns null if nothing changed.</summary>
    public static async Task<string?> CopySelectionAsync()
    {
        var before = GetClipboardSequenceNumber();
        var previous = GetClipboardText();
        ClipboardSuppress.Begin();
        try
        {
            ReleaseModifiers();
            await Task.Delay(30);
            SendCombo("Ctrl+C");
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(25);
                if (GetClipboardSequenceNumber() != before) break;
            }
            if (GetClipboardSequenceNumber() == before) return null;
            var text = GetClipboardText();
            if (previous != null) SetClipboardText(previous); // leave the user's clipboard as it was
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally { ClipboardSuppress.End(); }
    }
}

/// <summary>While active, clipboard changes we cause are not recorded in history.</summary>
internal static class ClipboardSuppress
{
    static int _depth;
    static DateTime _until;
    public static void Begin() { Interlocked.Increment(ref _depth); }
    public static void End() { Interlocked.Decrement(ref _depth); _until = DateTime.UtcNow.AddMilliseconds(300); }
    public static bool Active => Volatile.Read(ref _depth) > 0 || DateTime.UtcNow < _until;
}
