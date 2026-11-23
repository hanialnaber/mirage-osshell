using System.IO;
using System.Text;
using Mirage.Core.Agent;

namespace Mirage.App;

internal sealed record OpenWindow(IntPtr Handle, string Title, string ProcessName, string? ExePath);

/// <summary>Alt+Tab style window list, focusing and basic window actions.</summary>
internal static class WindowService
{
    public static List<OpenWindow> List(IntPtr exclude = default)
    {
        var result = new List<OpenWindow>();
        var myPid = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (h == exclude || !IsAltTabWindow(h)) return true;
                var len = Native.GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(h, sb, sb.Capacity);
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid == myPid) return true;
                var path = Native.ProcessPath(pid);
                var name = path != null ? Path.GetFileNameWithoutExtension(path) : "app";
                result.Add(new OpenWindow(h, sb.ToString(), name, path));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static bool IsAltTabWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        // Walk to the last active popup of the root owner: only that one shows in Alt+Tab
        var root = Native.GetAncestor(h, Native.GA_ROOTOWNER);
        IntPtr walk = root, last;
        do
        {
            last = walk;
            walk = Native.GetLastActivePopup(last);
            if (Native.IsWindowVisible(walk)) break;
        } while (walk != last);
        if (walk != h) return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(h, cls, 64);
        var c = cls.ToString();
        return c is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    public static bool Focus(IntPtr h)
    {
        if (!Native.IsWindow(h)) return false;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(h)) return true;
        // Foreground lock workaround: attach to the current foreground thread briefly
        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var me = Native.GetCurrentThreadId();
        Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);       // tap Alt: lets us take the foreground
            Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Native.BringWindowToTop(h);
            return Native.SetForegroundWindow(h);
        }
        finally { Native.AttachThreadInput(me, fgThread, false); }
    }

    public static bool Act(IntPtr h, string action)
    {
        if (!Native.IsWindow(h)) return false;
        switch (action)
        {
            case "minimize": return Native.ShowWindow(h, Native.SW_MINIMIZE) || true;
            case "maximize": return Native.ShowWindow(h, Native.SW_MAXIMIZE) || true;
            case "restore": return Native.ShowWindow(h, Native.SW_RESTORE) || true;
            case "close": return Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            default: return false;
        }
    }

    public static WindowInfo ToInfo(OpenWindow w, IntPtr active) =>
        new(w.Handle.ToInt64(), w.Title, w.ProcessName, w.Handle == active);
}
