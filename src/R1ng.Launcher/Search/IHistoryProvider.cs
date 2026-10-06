using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

/// <summary>Recent launches, shown when the query is empty.</summary>
public interface IHistoryProvider
{
    IReadOnlyList<SearchResultItem> GetRecent(int count);

    void Record(SearchResultItem item);

    IReadOnlyList<SearchResultItem> GetHome(SearchScope scope, int count) => GetRecent(int.MaxValue)
        .Where(item => scope == SearchScope.All
            || (scope == SearchScope.Applications && item.Kind == ResultKind.Application)
            || (scope == SearchScope.Files && item.Kind is ResultKind.File or ResultKind.Folder))
        .Take(Math.Max(0, count)).ToArray();

    bool IsPinned(SearchResultItem item) => item.IsPinned;

    bool TrySetPinned(SearchResultItem item, bool pinned) => false;
}
