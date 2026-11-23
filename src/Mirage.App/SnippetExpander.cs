using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Mirage.Core;

namespace Mirage.App;

/// <summary>
/// Opt-in: watches typing and replaces a snippet keyword (like ;sig) with its text in any app.
/// Off by default. Never stores or sends what you type; it only keeps the last few characters in memory.
/// </summary>
internal sealed class SnippetExpander : IDisposable
{
    readonly SnippetTrigger _trigger = new();
    readonly Native.LowLevelKeyboardProc _proc;
    IntPtr _hook;
    bool _expanding;

    public SnippetExpander() { _proc = HookProc; }

    public bool Running => _hook != IntPtr.Zero;

    public void Update(IEnumerable<Snippet> snippets) => _trigger.SetSnippets(snippets);

    public void Start()
    {
        if (Running) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    public void Stop()
    {
        if (!Running) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _trigger.Reset();
    }

    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_expanding && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            try
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & Native.LLKHF_INJECTED) == 0) OnKey(k);
            }
            catch { }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void OnKey(Native.KBDLLHOOKSTRUCT k)
    {
        uint vk = k.vkCode;
        if (vk == 0x08) { _trigger.Backspace(); return; }           // Backspace
        if (vk is 0x10 or 0xA0 or 0xA1 or 0x14) return;              // Shift keys, Caps Lock
        bool ctrl = Down(0x11), alt = Down(0x12), altGr = ctrl && alt;
        if (((ctrl || alt) && !altGr) || Down(0x5B) || Down(0x5C)) { _trigger.Reset(); return; }

        var state = new byte[256];
        if (Down(0x10)) state[0x10] = 0x80;
        if ((Native.GetAsyncKeyState(0x14) & 1) != 0) state[0x14] = 0x01;
        if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
        var fg = Native.GetForegroundWindow();
        var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(fg, out _));
        var buf = new StringBuilder(4);
        int n = Native.ToUnicodeEx(vk, k.scanCode, state, buf, buf.Capacity, 0x4, layout);
        if (n != 1) { if (n == 0) _trigger.Reset(); return; }

        var hit = _trigger.Feed(buf[0]);
        if (hit == null) return;
        var keywordLength = hit.Keyword.Length;
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            _expanding = true;
            try
            {
                await Task.Delay(30); // let the last key land first
                for (int i = 0; i < keywordLength; i++) InputService.SendCombo("Back");
                var text = TextTools.ExpandPlaceholders(hit.Text, DateTime.Now, InputService.GetClipboardText);
                await InputService.PasteAsync(text, IntPtr.Zero, restoreClipboard: true);
            }
            catch (Exception ex) { Log.Error("snippet expand", ex); }
            finally { _expanding = false; }
        });
    }

    public void Dispose() => Stop();
}
