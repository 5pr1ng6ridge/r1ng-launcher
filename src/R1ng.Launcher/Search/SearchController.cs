using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

/// <summary>Coordinates providers, same-query usage and history; rejects stale generations.</summary>
public sealed class SearchController
{
    private readonly IAppSearchProvider _apps;
    private readonly IFileSearchProvider _files;
    private readonly IHistoryProvider? _history;
    private readonly IQueryPreferenceStore? _preferences;
    private readonly IHiddenResultStore? _hiddenResults;
    private readonly Func<SearchResultItem, bool>? _isVisible;
    private int _generation;

    public SearchController(IAppSearchProvider apps, IFileSearchProvider files, IHistoryProvider? history = null,
        IQueryPreferenceStore? preferences = null, IHiddenResultStore? hiddenResults = null)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _history = history;
        _preferences = preferences;
        _hiddenResults = hiddenResults;
        _isVisible = hiddenResults is null ? null : item => !hiddenResults.IsHidden(item);
    }

    public SearchSnapshot Current { get; private set; } = SearchSnapshot.Idle;
    public event Action<SearchSnapshot>? Changed;
    public int BeginQuery() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    public void RecordLaunch(SearchContext? context, SearchResultItem item) => _preferences?.Record(context, item);

    /// <summary>True when the row (or an equivalent one) is currently hidden by the user.</summary>
    public bool IsHidden(SearchResultItem item) => _hiddenResults?.IsHidden(item) ?? false;

    /// <summary>
    /// Drops rows hidden right now, so a snapshot computed earlier (a "searching" one that keeps the
    /// previous rows, or one produced before the user hid a row) never shows them. Returns the same
    /// instance when nothing needs to go.
    /// </summary>
    public SearchSnapshot FilterVisible(SearchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_isVisible is null || snapshot.Items.Count == 0)
            return snapshot;
        var visible = snapshot.Items.Where(_isVisible).ToArray();
        return visible.Length == snapshot.Items.Count ? snapshot : snapshot with { Items = visible };
    }

    public bool TryPublish(SearchSnapshot snapshot, int generation) => TryPublish(snapshot, generation, out _);

    private bool TryPublish(SearchSnapshot snapshot, int generation, out SearchSnapshot published)
    {
        published = FilterVisible(snapshot);
        if (!IsCurrent(generation))
            return false;
        Current = published;
        Changed?.Invoke(published);
        return true;
    }

    public async Task<SearchSnapshot> SearchAsync(string query, SearchScope scope, int generation,
        CancellationToken cancellationToken, FileSearchOptions fileOptions = default)
    {
        query ??= string.Empty;
        TryPublish(Current.AsSearching(query, scope), generation);
        if (query.Trim().Length == 0)
        {
            // The history is already bounded (capacity plus pins). Take all of it, drop hidden rows and
            // only then cut to the display limit, so hiding a recent row pulls the next one up.
            var recent = _history?.GetHome(scope, int.MaxValue) ?? Array.Empty<SearchResultItem>();
            if (_isVisible is not null)
                recent = recent.Where(_isVisible).ToArray();
            var idle = SearchSnapshot.FromItems(query, scope,
                recent.Take(ResultRanker.MaxResults).Select((item, index) => item.WithScore(10_000 - index)).ToArray());
            TryPublish(idle, generation, out idle);
            return idle;
        }

        var hasFolderConstraint = FolderPrefixQuery.TryParse(query, out var folderConstraint, out var terms);
        var context = SearchContext.Create(query, scope, fileOptions);
        var preferences = _preferences?.GetPreferences(context) ?? new Dictionary<string, ResultPreference>();
        var appsTask = scope == SearchScope.Files
            ? Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>())
            : _apps.SearchAsync(terms, cancellationToken, preferences);
        var filesTask = scope == SearchScope.Applications
            ? Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()))
            : _files.SearchAsync(terms, cancellationToken, fileOptions, hasFolderConstraint ? folderConstraint : null);

        await Task.WhenAll(appsTask, filesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var apps = await appsTask.ConfigureAwait(false);
        var files = await filesTask.ConfigureAwait(false);
        var merged = new List<SearchResultItem>(apps.Count + files.Hits.Count);
        merged.AddRange(apps);
        // Map all returned backend hits. The backend's 120-hit bound is unchanged; final usage-aware
        // ranking, rather than an earlier mapper TopN, decides which of those hits enter the list.
        merged.AddRange(FileResultMapper.Map(files.Hits, terms, int.MaxValue));
        var ranked = ResultRanker.Rank(merged, preferences: preferences, isVisible: _isVisible)
            .Select(item => item with { IsPinned = _history?.IsPinned(item) ?? false }).ToArray();
        var showFileState = scope != SearchScope.Applications;
        var snapshot = SearchSnapshot.FromItems(query, scope, ranked,
            statusText: showFileState ? files.StatusText : null,
            fileNotice: showFileState && !files.Availability.Available ? files.Availability.Message : null,
            filesUnavailable: showFileState && !files.Availability.Available) with { Context = context };
        // A row hidden while this query ran must not appear in the published or returned snapshot.
        TryPublish(snapshot, generation, out snapshot);
        return snapshot;
    }
}
