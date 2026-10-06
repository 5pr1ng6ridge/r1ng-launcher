namespace R1ng.Launcher.Core;

/// <summary>
/// Deterministic ordering for the merged result list: score first, then a stable kind/title/id
/// tie-break so identical inputs always render in the same order.
/// </summary>
public static class ResultRanker
{
    public const int MaxResults = 60;

    /// <summary>Score bonus per application source. Start Menu beats desktop beats startup run entries.</summary>
    public static int SourceBonus(string source) => source switch
    {
        AppSources.StartMenu => 40,
        AppSources.AppsFolder => 30,
        AppSources.Desktop => 25,
        AppSources.Startup => 15,
        _ => 0,
    };

    /// <param name="isVisible">
    /// Optional visibility filter (e.g. hidden results). It sees each row after equivalent rows were
    /// merged and their aliases combined, and runs before ordering and the <paramref name="maxResults"/>
    /// cut so a hidden row never takes a slot from the next best one. Null keeps every row.
    /// </param>
    public static IReadOnlyList<SearchResultItem> Rank(IEnumerable<SearchResultItem> items, int maxResults = MaxResults,
        IReadOnlyDictionary<string, ResultPreference>? preferences = null, Func<SearchResultItem, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var merged = items
            .GroupBy(item => item.IdentityKey ?? item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Score).First() with
            {
                IdentityAliases = group.SelectMany(item => item.IdentityAliases)
                    .Distinct(StringComparer.Ordinal).ToArray(),
            });
        if (isVisible is not null)
            merged = merged.Where(isVisible);

        return merged
            .OrderByDescending(item => PreferenceFor(item, preferences).Count)
            .ThenByDescending(item => PreferenceFor(item, preferences).LastUsedUtc)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => KindWeight(item.Kind))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxResults))
            .ToList();
    }

    internal static ResultPreference PreferenceFor(SearchResultItem item, IReadOnlyDictionary<string, ResultPreference>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
            return default;
        long count = 0;
        var last = DateTime.MinValue;
        foreach (var key in item.IdentityAliases.Prepend(LaunchIdentity.ForItem(item)).Distinct(StringComparer.Ordinal))
        {
            if (!preferences.TryGetValue(key, out var usage))
                continue;
            count += usage.Count;
            if (usage.LastUsedUtc > last)
                last = usage.LastUsedUtc;
        }
        return new ResultPreference((int)Math.Min(int.MaxValue, count), last);
    }

    /// <summary>Applications and history are more actionable than files and sort above them on ties.</summary>
    private static int KindWeight(ResultKind kind) => kind switch
    {
        ResultKind.Application => 0,
        ResultKind.History => 1,
        ResultKind.Folder => 2,
        ResultKind.File => 3,
        _ => 4,
    };
}
