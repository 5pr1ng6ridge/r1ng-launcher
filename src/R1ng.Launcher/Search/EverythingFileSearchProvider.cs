using R1ng.Launcher.Core;
using R1ng.Launcher.Files;

namespace R1ng.Launcher.Search;

/// <summary>
/// Everything-backed file search provider. Availability is probed lazily and cached briefly so the
/// UI can render an explicit unavailable state without hammering IPC.
/// </summary>
public sealed class EverythingFileSearchProvider : IFileSearchProvider, IDisposable
{
    public const int MinQueryLength = 2;
    public const int MaxHits = 120;

    private static readonly TimeSpan DefaultAvailabilityTtl = TimeSpan.FromSeconds(10);

    private readonly IEverythingQueryClient _client;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _availabilityTtl;
    private readonly object _gate = new();

    private FileSearchAvailability _cachedAvailability = FileSearchAvailability.Unavailable("尚未检测");
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private bool _hasCached;

    public EverythingFileSearchProvider(
        IEverythingQueryClient? client = null,
        Func<DateTime>? clock = null,
        TimeSpan? availabilityTtl = null
    )
    {
        _client = client ?? new EverythingIpcClient();
        _clock = clock ?? (() => DateTime.UtcNow);
        _availabilityTtl = availabilityTtl ?? DefaultAvailabilityTtl;
    }

    /// <summary>Probes (or returns the cached) backend state. Runs IPC on the calling thread.</summary>
    public FileSearchAvailability ProbeAvailability()
    {
        lock (_gate)
        {
            if (_hasCached && _clock() - _cachedAtUtc < _availabilityTtl)
                return _cachedAvailability;

            var availability = _client.Probe();
            _cachedAvailability = availability;
            _cachedAtUtc = _clock();
            _hasCached = true;
            return availability;
        }
    }

    public void InvalidateAvailability()
    {
        lock (_gate)
            _hasCached = false;
    }

    public async Task<FileSearchOutcome> SearchAsync(
        string query,
        CancellationToken cancellationToken,
        FileSearchOptions options = default,
        string? folderConstraint = null
    )
    {
        var availability = await Task.Run(ProbeAvailability, cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
            return new FileSearchOutcome(availability, Array.Empty<FileSearchHit>());

        query = query?.Trim() ?? string.Empty;
        if (query.Length < MinQueryLength)
        {
            return new FileSearchOutcome(
                availability,
                Array.Empty<FileSearchHit>(),
                $"输入至少 {MinQueryLength} 个字符以搜索文件"
            );
        }

        // The literal folder is encoded separately from the terms so the quick-toggle flags stay
        // honest; see FileQueryBuilder for the exact Everything modifier used.
        var built = FileQueryBuilder.Build(folderConstraint, query, options);
        var raw = await _client.QueryAsync(built.Text, MaxHits, cancellationToken, built.EffectiveOptions).ConfigureAwait(false);
        if (raw.Count == 0)
            return new FileSearchOutcome(availability, Array.Empty<FileSearchHit>());

        var hits = new List<FileSearchHit>(raw.Count);
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.FullPath))
                continue;

            hits.Add(new FileSearchHit(item.FullPath, item.IsFolder));
        }

        return new FileSearchOutcome(availability, hits);
    }

    public void Dispose()
    {
        if (_client is IDisposable disposable)
            disposable.Dispose();
    }
}
