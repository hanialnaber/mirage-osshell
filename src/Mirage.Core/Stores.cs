using System.Globalization;
using System.Text.Json;

namespace Mirage.Core;

/// <summary>Plain-markdown quick notes: one bullet per note with a timestamp.</summary>
public sealed class NotesStore
{
    readonly string _path;
    readonly object _lock = new();
    public NotesStore(string path) => _path = path;
    public string FilePath => _path;

    public void Add(string text, DateTime? when = null)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var stamp = (when ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path)) File.WriteAllText(_path, "# Mirage notes\n\n");
            File.AppendAllText(_path, $"- {stamp}  {text.Replace("\r\n", " ").Replace('\n', ' ')}\n");
        }
    }

    public IReadOnlyList<(DateTime When, string Text)> All()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return Array.Empty<(DateTime, string)>();
            var list = new List<(DateTime, string)>();
            foreach (var line in File.ReadAllLines(_path))
            {
                if (!line.StartsWith("- ")) continue;
                var body = line[2..];
                if (body.Length > 18 && DateTime.TryParseExact(body[..16], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    list.Add((dt, body[16..].Trim()));
                else list.Add((DateTime.MinValue, body.Trim()));
            }
            return list;
        }
    }

    public string Tail(int maxChars)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return "";
            var all = File.ReadAllText(_path);
            return all.Length <= maxChars ? all : all[^maxChars..];
        }
    }
}

/// <summary>Remembers what the user launches so frequent picks rank higher.</summary>
public sealed class UsageStore
{
    readonly string _path;
    readonly Dictionary<string, Entry> _data;
    readonly object _lock = new();

    public sealed class Entry
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    public UsageStore(string path)
    {
        _path = path;
        try
        {
            _data = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { _data = new(); }
    }

    public void Record(string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var e)) _data[key] = e = new Entry();
            e.Count++;
            e.Last = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_data));
            }
            catch { }
        }
    }

    /// <summary>0..150 boost: frequency with recency decay.</summary>
    public int Boost(string key, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var e)) return 0;
            var days = ((nowUtc ?? DateTime.UtcNow) - e.Last).TotalDays;
            var recency = days < 1 ? 1.0 : days < 7 ? 0.7 : days < 30 ? 0.4 : 0.2;
            return (int)Math.Min(150, Math.Log2(1 + e.Count) * 30 * recency + (days < 1 ? 20 : 0));
        }
    }

    public IEnumerable<(string Key, int Count, DateTime Last)> Top(int n)
    {
        lock (_lock)
            return _data.OrderByDescending(kv => Boost(kv.Key)).Take(n).Select(kv => (kv.Key, kv.Value.Count, kv.Value.Last)).ToList();
    }
}
