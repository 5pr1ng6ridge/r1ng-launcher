using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using R1ng.Launcher.Core;
using R1ng.Launcher.Apps;

namespace R1ng.Launcher.Search;

/// <summary>Recent launches and independent pins. Old history-array files remain readable.</summary>
public sealed class SearchHistoryStore : IHistoryProvider
{
    private const int DefaultCapacity = 30;
    public const int MaxPins = 30;
    private readonly string _filePath;
    private readonly Func<DateTime> _clock;
    private readonly int _capacity;
    private readonly object _gate = new();
    private List<HistoryRecord>? _records;

    public SearchHistoryStore(string? filePath = null, Func<DateTime>? clock = null, int capacity = DefaultCapacity)
    {
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "r1ng-launcher", "history.json");
        _clock = clock ?? (() => DateTime.UtcNow);
        _capacity = Math.Max(1, capacity);
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SearchResultItem> GetRecent(int count)
    {
        lock (_gate)
            return Load().Where(record => record.LastUsedUtc != DateTime.MinValue)
                .OrderByDescending(record => record.LastUsedUtc).Take(Math.Max(0, count)).Select(record => record.ToItem()).ToList();
    }

    public IReadOnlyList<SearchResultItem> GetHome(SearchScope scope, int count)
    {
        lock (_gate)
            return Load().OrderByDescending(record => record.PinnedAtUtc is not null)
                .ThenByDescending(record => record.PinnedAtUtc)
                .ThenByDescending(record => record.LastUsedUtc)
                .ThenBy(record => record.Key, StringComparer.Ordinal)
                .Select(record => record.ToItem())
                .Where(item => scope == SearchScope.All
                    || (scope == SearchScope.Applications && item.Kind == ResultKind.Application)
                    || (scope == SearchScope.Files && item.Kind is ResultKind.File or ResultKind.Folder))
                .Take(Math.Max(0, count)).ToList();
    }

    public bool IsPinned(SearchResultItem item)
    {
        lock (_gate)
            return Load().Any(record => Matches(record, item) && record.PinnedAtUtc is not null);
    }

    public bool TrySetPinned(SearchResultItem item, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(item.Target))
            return false;
        lock (_gate)
        {
            var records = Load().ToList();
            var matches = records.Where(record => Matches(record, item)).ToArray();
            var existingPin = matches.Where(record => record.PinnedAtUtc is not null).Max(record => record.PinnedAtUtc);
            if (pinned && existingPin is null && records.Count(record => record.PinnedAtUtc is not null) >= MaxPins)
                return false;
            var lastUsed = matches.Length == 0 ? DateTime.MinValue : matches.Max(record => record.LastUsedUtc);
            records.RemoveAll(record => matches.Contains(record));
            if (pinned || lastUsed != DateTime.MinValue)
                records.Insert(0, FromItem(item, lastUsed, pinned ? existingPin ?? _clock() : null));
            Trim(records);
            if (!Save(records))
                return false;
            _records = records;
            return true;
        }
    }

    /// <summary>Only called after a successful launch; neither pinning nor unpinning fakes a launch.</summary>
    public void Record(SearchResultItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Title))
            return;
        lock (_gate)
        {
            var records = Load();
            var matches = records.Where(record => Matches(record, item)).ToArray();
            var pinned = matches.Where(record => record.PinnedAtUtc is not null).Max(record => record.PinnedAtUtc);
            records.RemoveAll(record => matches.Contains(record));
            records.Insert(0, FromItem(item, _clock(), pinned));
            Trim(records);
            Save(records);
        }
    }

    private static bool Matches(HistoryRecord record, SearchResultItem item) =>
        record.Key == LaunchIdentity.ForItem(item) || item.IdentityAliases.Contains(record.Key)
        || record.Key == LaunchIdentity.ForTarget(item.Target, item.IsFolder);

    private static HistoryRecord FromItem(SearchResultItem item, DateTime lastUsed, DateTime? pinned) => new()
    {
        Title = item.Title,
        Target = item.CopyText,
        IdentityKey = LaunchIdentity.ForItem(item),
        KindValue = (int)item.Kind,
        Detail = item.Detail,
        TypeLabel = item.TypeLabel,
        LastUsedUtc = lastUsed,
        PinnedAtUtc = pinned,
        IconPath = item.IconPath,
        IconIndex = item.IconIndex,
        FolderPath = item.FolderPath,
    };

    private void Trim(List<HistoryRecord> records)
    {
        var recent = records.Where(record => record.LastUsedUtc != DateTime.MinValue)
            .OrderByDescending(record => record.LastUsedUtc).Take(_capacity).ToHashSet();
        records.RemoveAll(record => record.PinnedAtUtc is null && !recent.Contains(record));
    }

    private List<HistoryRecord> Load()
    {
        if (_records is not null)
            return _records;
        try
        {
            if (File.Exists(_filePath) && new FileInfo(_filePath).Length <= 4 * 1024 * 1024)
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(_filePath));
                _records = (loaded ?? []).Where(record => record is not null && !string.IsNullOrWhiteSpace(record.Title) && !string.IsNullOrWhiteSpace(record.Target))
                    .GroupBy(record => record.Key, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(record => record.PinnedAtUtc).ThenByDescending(record => record.LastUsedUtc).First())
                    .ToList();
                var pins = _records.Where(record => record.PinnedAtUtc is not null)
                    .OrderByDescending(record => record.PinnedAtUtc).Take(MaxPins).ToHashSet();
                _records = _records.Select(record => record.PinnedAtUtc is not null && !pins.Contains(record)
                    ? record with { PinnedAtUtc = null } : record).ToList();
                Trim(_records);
                // Reconcile only the bounded home set; no catalog scan or shell Resolve on startup.
                _records = _records.Select(ResolveLegacyIdentity).GroupBy(record => record.Key, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(record => record.PinnedAtUtc).ThenByDescending(record => record.LastUsedUtc).First() with
                    {
                        PinnedAtUtc = group.Max(record => record.PinnedAtUtc),
                        LastUsedUtc = group.Max(record => record.LastUsedUtc),
                    }).ToList();
                return _records;
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable history must not block startup.
        }
        return _records = [];
    }

    private static HistoryRecord ResolveLegacyIdentity(HistoryRecord record)
    {
        var pathKey = LaunchIdentity.ForTarget(record.Target, record.KindValue == (int)ResultKind.Folder);
        if ((record.IdentityKey is not null && record.IdentityKey != pathKey) || !ShellLinkReader.IsLocalAbsolute(record.Target))
            return record;
        if (record.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLinkReader.ReadIdentity(record.Target) is { } link)
            return record with { IdentityKey = link };
        if (record.Target.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && UrlShortcut.Read(record.Target).Url is { } url)
            return record with { IdentityKey = LaunchIdentity.ForUrl(url) };
        return record;
    }

    private bool Save(List<HistoryRecord> records)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_filePath + ".tmp", JsonSerializer.Serialize(records, SerializerOptions));
            File.Move(_filePath + ".tmp", _filePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed record HistoryRecord
    {
        public string Title { get; init; } = string.Empty;
        public string Target { get; init; } = string.Empty;
        public string? IdentityKey { get; init; }
        public int KindValue { get; init; }
        public string Detail { get; init; } = string.Empty;
        public string TypeLabel { get; init; } = string.Empty;
        public DateTime LastUsedUtc { get; init; }
        public DateTime? PinnedAtUtc { get; init; }
        public string? IconPath { get; init; }
        public int IconIndex { get; init; } = -1;
        public string? FolderPath { get; init; }

        [JsonIgnore]
        public string Key => IdentityKey ?? LaunchIdentity.ForTarget(Target, KindValue == (int)ResultKind.Folder);

        public SearchResultItem ToItem()
        {
            var kind = Enum.IsDefined(typeof(ResultKind), KindValue) ? (ResultKind)KindValue : ResultKind.Application;
            return new SearchResultItem
            {
                Id = $"history|{Target}",
                IdentityKey = Key,
                IsPinned = PinnedAtUtc is not null,
                Kind = kind == ResultKind.History ? ResultKind.Application : kind,
                Title = Title,
                Detail = Detail,
                TypeLabel = string.IsNullOrWhiteSpace(TypeLabel) ? "历史" : TypeLabel,
                Target = Target,
                Source = PinnedAtUtc is not null ? AppSources.Pinned : AppSources.History,
                IsFolder = kind == ResultKind.Folder,
                FolderPath = FolderPath ?? (kind is ResultKind.File or ResultKind.Folder ? Path.GetDirectoryName(Target) : null),
                IconPath = string.IsNullOrWhiteSpace(IconPath) ? Target : IconPath,
                IconIndex = IconIndex,
            };
        }
    }
}
