using System.IO;
using System.Text.Json;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

public interface IQueryPreferenceStore
{
    IReadOnlyDictionary<string, ResultPreference> GetPreferences(SearchContext? context);
    void Record(SearchContext? context, SearchResultItem item);
}

/// <summary>Bounded same-query usage memory, independent of the recent-launch history.</summary>
public sealed class QueryPreferenceStore : IQueryPreferenceStore
{
    private readonly string _filePath;
    private readonly Func<DateTime> _clock;
    private readonly int _queryCapacity;
    private readonly int _resultCapacity;
    private readonly object _gate = new();
    private List<QueryRecord>? _records;

    public QueryPreferenceStore(string? filePath = null, Func<DateTime>? clock = null, int queryCapacity = 200, int resultCapacity = 8)
    {
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "r1ng-launcher", "preferences.json");
        _clock = clock ?? (() => DateTime.UtcNow);
        _queryCapacity = Math.Max(1, queryCapacity);
        _resultCapacity = Math.Max(1, resultCapacity);
    }

    public IReadOnlyDictionary<string, ResultPreference> GetPreferences(SearchContext? context)
    {
        lock (_gate)
        {
            var query = context is null ? null : Load().FirstOrDefault(record => record.Key == context.Key);
            return query?.Results.ToDictionary(record => record.IdentityKey,
                record => new ResultPreference(record.Count, record.LastUsedUtc), StringComparer.Ordinal)
                ?? new Dictionary<string, ResultPreference>(StringComparer.Ordinal);
        }
    }

    public void Record(SearchContext? context, SearchResultItem item)
    {
        if (context is null || string.IsNullOrWhiteSpace(item.Target))
            return;
        lock (_gate)
        {
            var records = Load();
            var query = records.FirstOrDefault(record => record.Key == context.Key);
            if (query is null)
            {
                query = new QueryRecord { Key = context.Key };
                records.Add(query);
            }
            var key = LaunchIdentity.ForItem(item);
            var matching = query.Results.Where(record => record.IdentityKey == key || item.IdentityAliases.Contains(record.IdentityKey)).ToArray();
            var count = matching.Sum(record => (long)record.Count);
            query.Results.RemoveAll(record => matching.Contains(record));
            query.Results.Insert(0, new UsageRecord { IdentityKey = key, Count = (int)Math.Min(int.MaxValue, count + 1), LastUsedUtc = _clock() });
            query.LastUsedUtc = _clock();
            query.Results = query.Results.OrderByDescending(record => record.LastUsedUtc).ThenBy(record => record.IdentityKey, StringComparer.Ordinal)
                .Take(_resultCapacity).ToList();
            _records = records.OrderByDescending(record => record.LastUsedUtc).ThenBy(record => record.Key, StringComparer.Ordinal)
                .Take(_queryCapacity).ToList();
            Save();
        }
    }

    private List<QueryRecord> Load()
    {
        if (_records is not null)
            return _records;
        try
        {
            if (File.Exists(_filePath) && new FileInfo(_filePath).Length <= 4 * 1024 * 1024)
            {
                var records = JsonSerializer.Deserialize<List<QueryRecord>>(File.ReadAllText(_filePath));
                _records = (records ?? []).Where(record => record is not null && !string.IsNullOrWhiteSpace(record.Key) && record.Results is not null)
                    .GroupBy(record => record.Key, StringComparer.Ordinal).Select(group => group.OrderByDescending(record => record.LastUsedUtc).First())
                    .OrderByDescending(record => record.LastUsedUtc).Take(_queryCapacity).ToList();
                foreach (var record in _records)
                    record.Results = record.Results.Where(usage => usage is not null && !string.IsNullOrWhiteSpace(usage.IdentityKey) && usage.Count > 0)
                        .GroupBy(usage => usage.IdentityKey, StringComparer.Ordinal).Select(group => group.OrderByDescending(usage => usage.LastUsedUtc).First())
                        .OrderByDescending(usage => usage.LastUsedUtc).Take(_resultCapacity).ToList();
                return _records;
            }
        }
        catch (Exception)
        {
            // Usage memory must not block searching or launching.
        }
        return _records = [];
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_filePath + ".tmp", JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_filePath + ".tmp", _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort, just like the recent history.
        }
    }

    private sealed class QueryRecord
    {
        public string Key { get; set; } = string.Empty;
        public DateTime LastUsedUtc { get; set; }
        public List<UsageRecord> Results { get; set; } = [];
    }

    private sealed class UsageRecord
    {
        public string IdentityKey { get; set; } = string.Empty;
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
