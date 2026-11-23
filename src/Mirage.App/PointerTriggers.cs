using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mirage.Core;

namespace Mirage.App;

/// <summary>
/// Pointer ways to open Mirage without the keyboard:
/// a hot corner (polled cursor position, no hook needed) and an optional mouse button (low-level mouse hook, only while one is chosen).
/// Touchpad gestures are mapped by the user in Windows settings to Mirage's shortcuts, because apps can't capture precision-touchpad gestures.
/// </summary>
internal sealed class PointerTriggers : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);

    const int WH_MOUSE_LL = 14;
    const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    const uint LLMHF_INJECTED = 1;
    const int VK_LBUTTON = 1, VK_RBUTTON = 2;

    readonly DispatcherTimer _poll;
    readonly LowLevelMouseProc _proc;
    IntPtr _hook;
    DateTime _enteredCorner = DateTime.MinValue;
    bool _cornerFired;
    Corner _corner = Corner.Off;
    int _delayMs = 250;
    PointerButton _button = PointerButton.None;

    /// <summary>Raised on the UI thread when the hot corner fires. True when the left button is held, i.e. something is being dragged.</summary>
    public event Action<bool>? HotCorner;
    /// <summary>Raised on the UI thread when the chosen mouse button is pressed, with the pointer position in screen pixels.</summary>
    public event Action<int, int>? QuickMenuButton;

    public PointerTriggers()
    {
        _proc = HookProc;
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _poll.Tick += (_, _) => PollCorner();
    }

    public void Configure(Corner corner, int delayMs, PointerButton button)
    {
        _corner = corner; _delayMs = delayMs; _button = button;
        if (corner == Corner.Off) _poll.Stop(); else _poll.Start();

        if (button != PointerButton.None && _hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Error($"mouse hook failed (code {Marshal.GetLastWin32Error()})");
        }
        else if (button == PointerButton.None && _hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Suspends the hot corner for a moment (while a Mirage window is open, for example).</summary>
    public bool Paused { get; set; }

    void PollCorner()
    {
        if (!Native.GetCursorPos(out var pt)) return;
        var mon = MonitorFromPoint(pt, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;
        var r = mi.rcMonitor;
        bool inCorner = PointerMath.InCorner(r.Left, r.Top, r.Right, r.Bottom, pt.X, pt.Y, _corner);
        if (!inCorner) { _enteredCorner = DateTime.MinValue; _cornerFired = false; return; }
        if (_cornerFired || Paused) return;
        // A right-button drag is never a drop for us; a left-button drag waits a little longer so
        // sweeping a window past the corner doesn't open Mirage, but resting a file there does.
        if ((Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0) { _enteredCorner = DateTime.UtcNow; return; }
        bool dragging = (Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (_enteredCorner == DateTime.MinValue) { _enteredCorner = DateTime.UtcNow; return; }
        var wait = dragging ? Math.Max(_delayMs, 550) : _delayMs;
        if ((DateTime.UtcNow - _enteredCorner).TotalMilliseconds < wait) return;
        _cornerFired = true;
        // Not over a full-screen app such as a game or a video
        if (ForegroundIsFullScreen(mi.rcMonitor)) return;
        try { HotCorner?.Invoke(dragging); } catch (Exception ex) { Log.Error("hot corner", ex); }
    }

    static bool ForegroundIsFullScreen(RECT monitor)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        var name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // the desktop counts as "full screen" but isn't
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _button != PointerButton.None)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                bool match = _button switch
                {
                    PointerButton.Middle => msg is WM_MBUTTONDOWN or WM_MBUTTONUP,
                    PointerButton.Back => msg is WM_XBUTTONDOWN or WM_XBUTTONUP && (data.mouseData >> 16) == 1,
                    PointerButton.Forward => msg is WM_XBUTTONDOWN or WM_XBUTTONUP && (data.mouseData >> 16) == 2,
                    _ => false,
                };
                if (match)
                {
                    if (msg is WM_MBUTTONDOWN or WM_XBUTTONDOWN)
                    {
                        int x = data.pt.X, y = data.pt.Y;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            try { QuickMenuButton?.Invoke(x, y); } catch (Exception ex) { Log.Error("quick menu button", ex); }
                        });
                    }
                    return new IntPtr(1); // swallow the click so the app under the pointer doesn't also act on it
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
