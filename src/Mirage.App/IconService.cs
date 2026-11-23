using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mirage.App;

/// <summary>Loads crisp 48px shell icons on a background STA thread, with caching.</summary>
internal static class IconService
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<(string key, string target, Action<ImageSource> done)> Queue = new();
    static Native.IImageList? _imageList;

    static IconService()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "MirageIcons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static string KeyFor(string target)
    {
        var ext = Path.GetExtension(target);
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Directory.Exists(target)) return target;
        return ext.ToLowerInvariant() switch
        {
            ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms" or "" => target,
            _ => "ext:" + ext,
        };
    }

    /// <summary>Returns the cached icon now, or null and calls back on the UI thread when ready.</summary>
    public static ImageSource? Get(string target, Action<ImageSource> whenReady)
    {
        var key = KeyFor(target);
        if (Cache.TryGetValue(key, out var img)) return img;
        Queue.Add((key, target, whenReady));
        return null;
    }

    static void Worker()
    {
        foreach (var (key, target, done) in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (!Cache.TryGetValue(key, out var img))
                {
                    img = Load(target);
                    Cache[key] = img;
                }
                if (img != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => done(img));
            }
            catch (Exception ex) { Log.Error("icon " + target, ex); Cache[key] = null; }
        }
    }

    static ImageSource? Load(string target)
    {
        var info = new Native.SHFILEINFO();
        var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            if (Native.SHParseDisplayName(target, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
            try { Native.SHGetFileInfoPidl(pidl, 0, ref info, size, Native.SHGFI_SYSICONINDEX | Native.SHGFI_PIDL); }
            finally { Native.CoTaskMemFree(pidl); }
        }
        else if (KeyFor(target).StartsWith("ext:"))
        {
            Native.SHGetFileInfo(Path.GetExtension(target), 0x80, ref info, size, Native.SHGFI_SYSICONINDEX | Native.SHGFI_USEFILEATTRIBUTES);
        }
        else
        {
            Native.SHGetFileInfo(target, 0, ref info, size, Native.SHGFI_SYSICONINDEX);
        }

        if (_imageList == null)
        {
            var iid = Native.IID_IImageList;
            if (Native.SHGetImageList(Native.SHIL_EXTRALARGE, ref iid, out var list) != 0) return null;
            _imageList = list;
        }
        IntPtr hIcon = IntPtr.Zero;
        if (_imageList.GetIcon(info.iIcon, (int)Native.ILD_TRANSPARENT, ref hIcon) != 0 || hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(hIcon); }
    }
}
