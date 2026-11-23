using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mirage.App;

/// <summary>Places a window on the monitor under the mouse, in physical pixels (per-monitor DPI aware).</summary>
internal static class Monitors
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;

    /// <summary>DPI scale of the monitor under a screen point (1.0 = 96 dpi).</summary>
    public static double ScaleAt(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2);
            if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0) return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>Work area (screen pixels) of the monitor under a screen point.</summary>
    public static (int Left, int Top, int Right, int Bottom) WorkAreaAt(int x, int y)
    {
        var mon = MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi)) return (mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
        return (int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);
    }

    /// <summary>Center horizontally on the cursor's monitor, with the top at a fraction of the work area.</summary>
    public static void PlaceOnCursorMonitor(Window w, double topFraction)
    {
        try
        {
            Native.GetCursorPos(out var pt);
            var mon = MonitorFromPoint(pt, 2 /* nearest */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) throw new InvalidOperationException();
            double scale = 1;
            try { if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0) scale = dx / 96.0; } catch { }
            var work = mi.rcWork;
            int width = (int)Math.Round(w.Width * scale);
            int x = work.Left + (work.Right - work.Left - width) / 2;
            int y = work.Top + (int)((work.Bottom - work.Top) * topFraction);
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        }
        catch
        {
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + (area.Width - w.Width) / 2;
            w.Top = area.Top + area.Height * topFraction;
        }
    }
}
