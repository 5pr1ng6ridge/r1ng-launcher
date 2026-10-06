using R1ng.Launcher.Core;

namespace R1ng.Launcher.Apps;

/// <summary>
/// Cached application catalog. The first request scans shortcuts and AppsFolder off the UI thread;
/// later requests reuse the snapshot until the TTL expires or <see cref="Invalidate"/> is called.
/// A failed scan never throws to the caller: cancellation propagates, everything else degrades to
/// whatever could be discovered.
/// </summary>
public sealed class AppCatalog
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly Func<IReadOnlyList<ShortcutRoot>> _roots;
    private readonly Func<Action<string, Exception>?, IReadOnlyList<AppCatalogEntry>> _appsFolder;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string, Exception>? _onError;

    private Task<IReadOnlyList<AppCatalogEntry>>? _cached;
    private DateTime _cachedAtUtc;

    public AppCatalog(
        Func<IReadOnlyList<ShortcutRoot>>? roots = null,
        Func<Action<string, Exception>?, IReadOnlyList<AppCatalogEntry>>? appsFolder = null,
        Func<DateTime>? clock = null,
        TimeSpan? ttl = null,
        Action<string, Exception>? onError = null
    )
    {
        _roots = roots ?? (() => AppFolders.GetRoots(Environment.GetFolderPath));
        _appsFolder = appsFolder ?? (AppsFolderEnumerator.Enumerate);
        _clock = clock ?? (() => DateTime.UtcNow);
        _ttl = ttl ?? DefaultTtl;
        _onError = onError;
    }

    public bool IsBuilding => _cached is { IsCompleted: false };

    /// <summary>Returns the catalog, building it on first use or after the TTL.</summary>
    public async Task<IReadOnlyList<AppCatalogEntry>> GetAsync(CancellationToken cancellationToken = default)
    {
        var existing = _cached;
        if (existing is not null && existing.IsCompletedSuccessfully && _clock() - _cachedAtUtc < _ttl)
            return await existing.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Decide under the gate whether to reuse the in-flight build or start one, then release the
        // gate before awaiting. A cancelled waiter therefore never abandons a running build, and a
        // second caller shares that same build instead of starting a duplicate scan.
        Task<IReadOnlyList<AppCatalogEntry>> build;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            build = GetOrStartBuildLocked();
        }
        finally
        {
            _gate.Release();
        }

        return await build.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the cached snapshot when it is fresh, or the shared in-flight build, and only starts a
    /// new scan when there is nothing usable to await. Must be called while holding <see cref="_gate"/>.
    /// </summary>
    private Task<IReadOnlyList<AppCatalogEntry>> GetOrStartBuildLocked()
    {
        var existing = _cached;
        if (existing is not null)
        {
            // Share an in-flight build, or reuse a fresh completed snapshot.
            if (!existing.IsCompleted)
                return existing;
            if (existing.IsCompletedSuccessfully && _clock() - _cachedAtUtc < _ttl)
                return existing;
        }

        return StartBuild();
    }

    /// <summary>Drops the snapshot so the next request re-scans.</summary>
    public void Invalidate()
    {
        _cached = null;
        _cachedAtUtc = DateTime.MinValue;
    }

    private Task<IReadOnlyList<AppCatalogEntry>> StartBuild()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<AppCatalogEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _cached = completion.Task;
        _cachedAtUtc = _clock();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Build());
            }
            catch (Exception exception)
            {
                _onError?.Invoke("catalog", exception);
                completion.SetResult(Array.Empty<AppCatalogEntry>());
            }
        })
        {
            IsBackground = true,
            Name = "R1ng application catalog",
        };

        // Shell automation wants an STA thread; the shortcut walk is happy either way.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private IReadOnlyList<AppCatalogEntry> Build()
    {
        var shortcuts = ShortcutScanner.Scan(_roots(), _onError);
        var appsFolder = _appsFolder(_onError);

        return MergeEquivalent(shortcuts.Concat(appsFolder));
    }

    internal static IReadOnlyList<AppCatalogEntry> MergeEquivalent(IEnumerable<AppCatalogEntry> entries)
    {
        return entries
            .GroupBy(entry => entry.IdentityKey ?? LaunchIdentity.ForTarget(entry.Target), StringComparer.Ordinal)
            .Select(group =>
            {
                // Stable across scan order. Prefer richer shortcut metadata and the usual source
                // priority, but never infer equivalence from a display name or an icon.
                var representative = group.OrderByDescending(entry => entry.FolderPath is not null)
                    .ThenByDescending(entry => ResultRanker.SourceBonus(entry.Source))
                    .ThenBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Target, StringComparer.Ordinal)
                    .ThenBy(entry => entry.DisplayName, StringComparer.Ordinal).First();
                return representative with
                {
                    IdentityKey = group.Key,
                    NameAliases = group.SelectMany(entry => entry.NameAliases.Prepend(entry.DisplayName))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    IdentityAliases = group.SelectMany(entry => entry.IdentityAliases.Prepend(LaunchIdentity.ForTarget(entry.Target)))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                };
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Target, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Catalog wired to the real shortcut folders and Shell automation.</summary>
    public static AppCatalog CreateDefault(Action<string, Exception>? onError = null) => new(onError: onError);
}
