using System.Collections.Concurrent;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

/// <summary>Literal and pinyin matching of catalog entries, including equivalent shortcut names.</summary>
public sealed class AppSearchProvider : IAppSearchProvider
{
    private const int MaxResults = 20;
    private readonly AppCatalog _catalog;
    private readonly IPinyinMap _pinyinMap;
    private readonly ConcurrentDictionary<string, SearchTextIndex> _indexCache = new(StringComparer.Ordinal);
    private readonly Func<SearchResultItem, bool>? _isVisible;

    public AppSearchProvider(AppCatalog catalog, IPinyinMap? pinyinMap = null, IHiddenResultStore? hiddenResults = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pinyinMap = pinyinMap ?? Gb2312PinyinMap.Instance;
        _isVisible = hiddenResults is null ? null : item => !hiddenResults.IsHidden(item);
    }

    public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
        SearchAsync(query, cancellationToken, new Dictionary<string, ResultPreference>());

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ResultPreference> preferences)
    {
        var entries = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        query = query?.Trim() ?? string.Empty;
        var matches = new List<SearchResultItem>(Math.Min(entries.Count, MaxResults));
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = NameMatch.None;
            var displayName = entry.DisplayName;
            foreach (var name in entry.NameAliases.Prepend(entry.DisplayName).Distinct(StringComparer.Ordinal))
            {
                var index = _indexCache.GetOrAdd(name, text => SearchTextIndex.Create(text, _pinyinMap));
                var match = NameMatcher.MatchText(index, query);
                if (match.Matched && (!best.Matched || match.Score > best.Score))
                {
                    best = match;
                    displayName = name;
                }
            }
            if (best.Matched)
                matches.Add(ToResult(entry with { DisplayName = displayName }, best));
        }
        // Usage and hidden rows are applied before TopN, otherwise a remembered weaker match could
        // already be lost and a hidden row would leave the list one short.
        return ResultRanker.Rank(matches, MaxResults, preferences, _isVisible);
    }

    internal static SearchResultItem ToResult(AppCatalogEntry entry, NameMatch match) => new()
    {
        Id = $"app|{entry.Id}",
        IdentityKey = entry.IdentityKey ?? LaunchIdentity.ForTarget(entry.Target),
        IdentityAliases = entry.IdentityAliases,
        Kind = ResultKind.Application,
        Title = entry.DisplayName,
        Detail = entry.FolderPath ?? entry.Target,
        TypeLabel = "应用",
        Target = entry.Target,
        Source = entry.Source,
        Score = match.Score + ResultRanker.SourceBonus(entry.Source),
        TitleMatchIndices = match.Indices,
        IsFolder = false,
        FolderPath = entry.FolderPath,
        IconPath = entry.IconPath,
        IconIndex = entry.IconIndex,
    };
}
