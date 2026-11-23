using System.IO;
using System.Text.Json;
using System.Windows;
using Clipboard = System.Windows.Clipboard;

namespace Mirage.App;

internal sealed class ClipEntry
{
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
}

/// <summary>Text clipboard history. Skips content that password managers mark as private.</summary>
internal sealed class ClipboardService
{
    readonly List<ClipEntry> _items = new();
    readonly object _lock = new();
    public bool Enabled { get; set; } = true;
    public int Limit { get; set; } = 100;
    public bool Persist { get; set; }

    public ClipboardService()
    {
        try
        {
            if (File.Exists(Paths.Clipboard))
                _items.AddRange(JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(Paths.Clipboard)) ?? new());
        }
        catch { }
    }

    public IReadOnlyList<ClipEntry> Items { get { lock (_lock) return _items.ToList(); } }

    public void OnClipboardChanged()
    {
        if (!Enabled || ClipboardSuppress.Active) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            // Respect apps that ask clipboard managers to stay away (KeePass, 1Password, Bitwarden...)
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("Clipboard Viewer Ignore")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms)
            {
                var b = ms.ToArray();
                if (b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0) return;
            }
            if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
            var text = data.GetData(DataFormats.UnicodeText) as string;
            Add(text);
        }
        catch (Exception ex) { Log.Error("clipboard read", ex); }
    }

    public void Add(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
        lock (_lock)
        {
            _items.RemoveAll(i => i.Text == text);
            _items.Insert(0, new ClipEntry { Text = text, Time = DateTime.Now });
            if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        }
        Save();
    }

    public void Remove(ClipEntry e) { lock (_lock) _items.Remove(e); Save(); }

    public void Clear() { lock (_lock) _items.Clear(); Save(); }

    public void Save()
    {
        try
        {
            if (!Persist) { if (File.Exists(Paths.Clipboard)) File.Delete(Paths.Clipboard); return; }
            Directory.CreateDirectory(Paths.Root);
            List<ClipEntry> copy;
            lock (_lock) copy = _items.ToList();
            File.WriteAllText(Paths.Clipboard, JsonSerializer.Serialize(copy));
        }
        catch { }
    }
}
