using System.Data.OleDb;
using System.IO;

namespace Mirage.App;

/// <summary>File name search: Windows Search index first, then a bounded scan of common folders.</summary>
internal static class FileSearchService
{
    static bool _indexBroken;
    static List<string>? _fallbackCache;
    static DateTime _fallbackBuilt;

    public static async Task<List<string>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return new();
        if (!_indexBroken)
        {
            try { return await Task.Run(() => SearchIndex(query, limit, ct), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _indexBroken = true; Log.Error("Windows Search unavailable, using folder scan", ex); }
        }
        return await Task.Run(() => SearchFallback(query, limit, ct), ct);
    }

    static List<string> SearchIndex(string query, int limit, CancellationToken ct)
    {
        var results = new List<string>();
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        var safe = query.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        var sql = $"SELECT TOP {limit} System.ItemPathDisplay FROM SystemIndex " +
                  $"WHERE SCOPE='file:{user}' AND System.FileName LIKE '%{safe}%' " +
                  "AND System.ItemType <> 'Directory' ORDER BY System.DateModified DESC";
        using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        conn.Open();
        using var cmd = new OleDbCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && !ct.IsCancellationRequested)
            if (!reader.IsDBNull(0)) results.Add(reader.GetString(0));
        // Folders too, but fewer
        if (results.Count < limit)
        {
            var sql2 = $"SELECT TOP 5 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:{user}' AND System.FileName LIKE '%{safe}%' AND System.ItemType = 'Directory'";
            using var cmd2 = new OleDbCommand(sql2, conn);
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read() && !ct.IsCancellationRequested)
                if (!r2.IsDBNull(0)) results.Insert(0, r2.GetString(0));
        }
        return results.Take(limit).ToList();
    }

    static List<string> SearchFallback(string query, int limit, CancellationToken ct)
    {
        if (_fallbackCache == null || DateTime.UtcNow - _fallbackBuilt > TimeSpan.FromMinutes(10))
        {
            var list = new List<string>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var dir in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive" }.Select(d => Path.Combine(home, d)).Where(Directory.Exists))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
                    {
                        list.Add(f);
                        if (list.Count > 60_000) break;
                    }
                }
                catch { }
            }
            _fallbackCache = list;
            _fallbackBuilt = DateTime.UtcNow;
        }
        return _fallbackCache
            .Where(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Path.GetFileName(p).StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Length)
            .Take(limit)
            .ToList();
    }

    /// <summary>Recently opened files from the Windows "Recent" folder.</summary>
    public static List<(string Name, string Shortcut)> Recent(int limit)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return new DirectoryInfo(dir).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(limit)
                .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.FullName))
                .ToList();
        }
        catch { return new(); }
    }
}
