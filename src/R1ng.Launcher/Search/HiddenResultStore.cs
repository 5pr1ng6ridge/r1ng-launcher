using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

/// <summary>Results the user chose to hide from search and the empty-input history list.</summary>
public interface IHiddenResultStore
{
    /// <summary>True when the row's canonical identity, an alias or its original target is hidden.</summary>
    bool IsHidden(SearchResultItem item);

    /// <summary>All hidden entries, newest first, with enough data to display and restore them.</summary>
    IReadOnlyList<HiddenResultRecord> GetHidden();

    /// <summary>Hides the row durably; false (with <see cref="Error"/>) when it could not be saved.</summary>
    bool TryHide(SearchResultItem item);

    /// <summary>Restores one entry by <see cref="HiddenResultRecord.Key"/>; false (with <see cref="Error"/>) on failure.</summary>
    bool TryRestore(string key);

    /// <summary>Restores every entry; false (with <see cref="Error"/>) when it could not be saved.</summary>
    bool TryRestoreAll();

    /// <summary>Latest read/save problem for the UI to show, or null when the store is healthy.</summary>
    string? Error { get; }
}

/// <summary>A hidden result as stored: canonical <see cref="Key"/>, verified aliases and display fields.</summary>
public sealed record HiddenResultRecord
{
    /// <summary>Canonical launch identity (<see cref="LaunchIdentity.ForItem"/>); never the title or row id.</summary>
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    /// <summary>Original launch target, kept so an equivalent row with the same target stays hidden.</summary>
    public string Target { get; init; } = string.Empty;

    public string TypeLabel { get; init; } = string.Empty;

    public ResultKind Kind { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>Verified equivalent identities (e.g. the original shortcut path) seen when hiding.</summary>
    public IReadOnlyList<string> IdentityAliases { get; init; } = Array.Empty<string>();

    public DateTime HiddenAtUtc { get; init; }
}

/// <summary>
/// Durable, thread-safe hidden-result list. Every change is made on a copy, saved atomically and only
/// then committed in memory, so a failed save never pretends something was hidden or restored. The
/// store never evicts entries and never overwrites a file it could not read.
/// </summary>
public sealed class HiddenResultStore : IHiddenResultStore
{
    public const int DefaultMaxRecords = 5_000;
    public const int MaxFileBytes = 8 * 1024 * 1024;
    private const int CurrentVersion = 1;

    private readonly string _filePath;
    private readonly Func<DateTime> _clock;
    private readonly int _maxRecords;
    private readonly object _gate = new();
    private State? _state;
    private bool _loadFailed;
    private string? _error;

    public HiddenResultStore(string filePath, Func<DateTime>? clock = null)
        : this(filePath, clock, DefaultMaxRecords)
    {
    }

    internal HiddenResultStore(string filePath, Func<DateTime>? clock, int maxRecords)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A hidden-results file path is required.", nameof(filePath));
        _filePath = filePath;
        _clock = clock ?? (() => DateTime.UtcNow);
        _maxRecords = Math.Max(1, maxRecords);
    }

    public string FilePath => _filePath;

    public string? Error
    {
        get
        {
            lock (_gate)
                return _error;
        }
    }

    public bool IsHidden(SearchResultItem item)
    {
        if (item is null)
            return false;
        lock (_gate)
        {
            // A missing or unreadable file hides nothing; the failure is reported through Error.
            if (!EnsureLoaded(retryFailure: false) || _state!.Index.Count == 0)
                return false;
            foreach (var key in KeysOf(item))
            {
                if (_state.Index.Contains(key))
                    return true;
            }
            return false;
        }
    }

    public IReadOnlyList<HiddenResultRecord> GetHidden()
    {
        lock (_gate)
            return EnsureLoaded(retryFailure: true) ? _state!.Records : Array.Empty<HiddenResultRecord>();
    }

    public bool TryHide(SearchResultItem item)
    {
        if (item is null)
            return false;
        lock (_gate)
        {
            if (item.IdentityKey is null && string.IsNullOrWhiteSpace(item.Target))
                return Fail("该结果没有可识别的启动目标，无法隐藏。");
            if (!EnsureLoaded(retryFailure: true))
                return false;

            var records = _state!.Records;
            var itemKeys = KeysOf(item).ToHashSet(StringComparer.Ordinal);
            var matches = _state.Entries.Where(entry => entry.Keys.Any(itemKeys.Contains)).ToArray();
            var next = new List<HiddenResultRecord>(records.Count + 1);
            if (matches.Length == 0)
            {
                if (records.Count >= _maxRecords)
                    return Fail($"已隐藏项已达上限（{_maxRecords}），请先在“已隐藏”中恢复部分项目。");
                var key = LaunchIdentity.ForItem(item);
                next.Add(new HiddenResultRecord
                {
                    Key = key,
                    Title = item.Title ?? string.Empty,
                    Detail = item.Detail ?? string.Empty,
                    Target = item.Target ?? string.Empty,
                    TypeLabel = item.TypeLabel ?? string.Empty,
                    Kind = item.Kind,
                    IsFolder = item.IsFolder,
                    IdentityAliases = Aliases(key, item.IdentityAliases),
                    HiddenAtUtc = _clock(),
                });
                next.AddRange(records);
            }
            else
            {
                // Already hidden: keep the primary record's key and fold in whatever equivalent
                // identities this row (and any other record it bridges) adds, so every spelling of
                // the same thing stays hidden through one record.
                var primary = matches[0];
                var known = primary.Keys.ToHashSet(StringComparer.Ordinal);
                var added = matches.Skip(1).SelectMany(match => match.Keys).Concat(itemKeys).Where(key => !known.Contains(key)).ToArray();
                if (matches.Length == 1 && added.Length == 0)
                    return true;
                var merged = primary.Record with { IdentityAliases = Aliases(primary.Record.Key, primary.Record.IdentityAliases.Concat(added)) };
                var absorbed = matches.Select(match => match.Record).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var record in records)
                {
                    if (ReferenceEquals(record, primary.Record))
                        next.Add(merged);
                    else if (!absorbed.Contains(record))
                        next.Add(record);
                }
            }
            return SaveAndCommit(next);
        }
    }

    public bool TryRestore(string key)
    {
        lock (_gate)
        {
            if (!EnsureLoaded(retryFailure: true))
                return false;
            if (string.IsNullOrEmpty(key) || !_state!.Records.Any(record => string.Equals(record.Key, key, StringComparison.Ordinal)))
                return Fail("该项已不在隐藏列表中。");
            return SaveAndCommit(_state.Records.Where(record => !string.Equals(record.Key, key, StringComparison.Ordinal)).ToList());
        }
    }

    public bool TryRestoreAll()
    {
        lock (_gate)
        {
            if (!EnsureLoaded(retryFailure: true))
                return false;
            return _state!.Records.Count == 0 || SaveAndCommit(new List<HiddenResultRecord>());
        }
    }

    private static IEnumerable<string> KeysOf(SearchResultItem item)
    {
        yield return LaunchIdentity.ForItem(item);
        foreach (var alias in item.IdentityAliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
        if (!string.IsNullOrWhiteSpace(item.Target))
            yield return LaunchIdentity.ForTarget(item.Target, item.IsFolder);
    }

    private static IReadOnlyList<string> Aliases(string key, IEnumerable<string>? aliases) =>
        (aliases ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias) && !string.Equals(alias, key, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();

    private bool Fail(string message)
    {
        _error = message;
        return false;
    }

    private bool EnsureLoaded(bool retryFailure)
    {
        if (_state is not null)
            return true;
        if (_loadFailed && !retryFailure)
            return false;
        try
        {
            _state = BuildState(ReadFile());
            _loadFailed = false;
            _error = null;
            return true;
        }
        catch (Exception exception)
        {
            // Keep the file untouched: a corrupt or unreadable list is reported, never replaced.
            _loadFailed = true;
            _error = $"无法读取已隐藏项（{Path.GetFileName(_filePath)}）：{exception.Message} 原文件已保留，修复或删除后可重新打开。";
            return false;
        }
    }

    private List<HiddenResultRecord> ReadFile()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
                throw new InvalidDataException($"文件超过 {MaxFileBytes / (1024 * 1024)} MB 上限。");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        var file = JsonSerializer.Deserialize<HiddenFile>(bytes, ReadOptions);
        if (file is null || file.Version != CurrentVersion || file.Items is null)
            throw new InvalidDataException("文件格式无法识别。");
        if (file.Items.Count > _maxRecords)
            throw new InvalidDataException($"记录数超过 {_maxRecords} 条上限。");

        var records = new List<HiddenResultRecord>(file.Items.Count);
        foreach (var record in file.Items)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.Key))
                throw new InvalidDataException("存在缺少标识的记录。");
            records.Add(record with
            {
                Title = record.Title ?? string.Empty,
                Detail = record.Detail ?? string.Empty,
                Target = record.Target ?? string.Empty,
                TypeLabel = record.TypeLabel ?? string.Empty,
                Kind = Enum.IsDefined(record.Kind) ? record.Kind : ResultKind.Application,
                IdentityAliases = Aliases(record.Key, record.IdentityAliases),
            });
        }
        return records;
    }

    private static State BuildState(List<HiddenResultRecord> loaded)
    {
        var records = loaded.GroupBy(record => record.Key, StringComparer.Ordinal)
            .Select(group => group.Count() == 1
                ? group.First()
                : group.First() with { IdentityAliases = Aliases(group.Key, group.SelectMany(record => record.IdentityAliases)) })
            .ToList();
        return Compose(records);
    }

    private static State Compose(List<HiddenResultRecord> records)
    {
        var entries = records.Select(record =>
        {
            var keys = new List<string>(record.IdentityAliases.Count + 2) { record.Key };
            keys.AddRange(record.IdentityAliases);
            if (!string.IsNullOrWhiteSpace(record.Target))
                keys.Add(LaunchIdentity.ForTarget(record.Target, record.IsFolder || record.Kind == ResultKind.Folder));
            return new Entry(record, keys);
        }).ToArray();
        return new State(new ReadOnlyCollection<HiddenResultRecord>(records), entries,
            entries.SelectMany(entry => entry.Keys).ToHashSet(StringComparer.Ordinal));
    }

    private bool SaveAndCommit(List<HiddenResultRecord> next)
    {
        if (!TrySave(next, out var error))
            return Fail(error!);
        _state = Compose(next);
        _loadFailed = false;
        _error = null;
        return true;
    }

    private bool TrySave(List<HiddenResultRecord> records, out string? error)
    {
        var temporary = _filePath + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new HiddenFile { Version = CurrentVersion, Items = records.ToList<HiddenResultRecord?>() }, WriteOptions);
            if (bytes.Length > MaxFileBytes)
            {
                error = "已隐藏项数据过大，未保存。";
                return false;
            }
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _filePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Best effort; the next save overwrites a leftover temporary file anyway.
            }
            error = $"保存已隐藏项失败，未做更改：{exception.Message}";
            return false;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new();

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Entry(HiddenResultRecord Record, IReadOnlyList<string> Keys);

    private sealed record State(IReadOnlyList<HiddenResultRecord> Records, Entry[] Entries, HashSet<string> Index);

    private sealed class HiddenFile
    {
        public int Version { get; set; }

        public List<HiddenResultRecord?>? Items { get; set; }
    }
}
