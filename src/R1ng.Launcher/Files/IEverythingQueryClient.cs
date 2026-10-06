using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Files;

/// <summary>File-index backend contract, so the provider can be tested without live IPC.</summary>
public interface IEverythingQueryClient
{
    /// <summary>Reports whether the backend can serve queries right now.</summary>
    FileSearchAvailability Probe();

    /// <summary>
    /// Runs one query. <paramref name="options"/> carries the search-box quick toggles (case, whole
    /// word, path, regex). Must never throw for a missing backend; an empty list is acceptable.
    /// </summary>
    Task<IReadOnlyList<EverythingRawItem>> QueryAsync(
        string search,
        int maxResults,
        CancellationToken cancellationToken,
        FileSearchOptions options = default
    );
}
