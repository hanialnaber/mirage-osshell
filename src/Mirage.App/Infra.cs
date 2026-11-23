using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Mirage.Core;

namespace Mirage.App;

internal static class Paths
{
    /// <summary>Portable mode: a portable.txt next to Mirage.exe keeps all data in a "data" folder beside it.</summary>
    public static readonly bool Portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
    public static readonly string Root = Portable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mirage");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Notes => Path.Combine(Root, "notes.md");
    public static string Usage => Path.Combine(Root, "usage.json");
    public static string Clipboard => Path.Combine(Root, "clipboard.json");
    public static string Log => Path.Combine(Root, "mirage.log");
    public static string Exe => Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Root);
                var fi = new FileInfo(Paths.Log);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>Applies the dark liquid glass backdrop. Windows 11: DWM acrylic/mica. Windows 10: acrylic accent. Else: solid.</summary>
internal static class Glass
{
    public static bool Apply(Window window, GlassMode mode)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            if (mode == GlassMode.Solid)
            {
                if (Environment.OSVersion.Version.Build >= 22621)
                {
                    int none = 1; // DWMSBT_NONE
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
                }
                return false;
            }

            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var build = Environment.OSVersion.Version.Build;
            if (build >= 22621)
            {
                var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int backdrop = mode == GlassMode.Mica ? Native.DWMSBT_TABBEDWINDOW : Native.DWMSBT_TRANSIENTWINDOW;
                return Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
            }
            // Windows 10 / early 11: acrylic accent (undocumented but long-standing)
            var accent = new Native.ACCENT_POLICY
            {
                AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = 0x99140F12, // AABBGGRR: dark tint
            };
            var size = System.Runtime.InteropServices.Marshal.SizeOf(accent);
            var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(accent, ptr, false);
                var data = new Native.WINDOWCOMPOSITIONATTRIBDATA { Attribute = Native.WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                return Native.SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex)
        {
            Log.Error("glass", ex);
            return false;
        }
    }
}

/// <summary>Per-user "start with Windows" via HKCU Run (no admin needed).</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("Mirage", $"\"{Paths.Exe}\" --background");
            else key.DeleteValue("Mirage", false);
        }
        catch (Exception ex) { Log.Error("startup", ex); }
    }
}
