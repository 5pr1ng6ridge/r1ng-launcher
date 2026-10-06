using R1ng.Launcher.Core;

namespace R1ng.Launcher.Search;

/// <summary>Supplies application results for a query.</summary>
public interface IAppSearchProvider
{
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ResultPreference> preferences) => SearchAsync(query, cancellationToken);
}

/// <summary>Supplies file/folder results for a query.</summary>
public interface IFileSearchProvider
{
    /// <param name="query">The literal user terms (folder prefix already stripped).</param>
    /// <param name="cancellationToken">Cancels a superseded query.</param>
    /// <param name="options">The quick-toggle refinements.</param>
    /// <param name="folderConstraint">
    /// Optional literal folder the results must live in, injected by the file-scope hotkey. Kept
    /// separate from <paramref name="query"/> so highlighting/ranking still use only the terms.
    /// </param>
    Task<FileSearchOutcome> SearchAsync(
        string query,
        CancellationToken cancellationToken,
        FileSearchOptions options = default,
        string? folderConstraint = null
    );
}

/// <summary>Whether the file index backend can serve queries right now, and why not.</summary>
public readonly record struct FileSearchAvailability(bool Available, string Message)
{
    public static FileSearchAvailability Unavailable(string message) => new(false, message);
    public static FileSearchAvailability Ready(string message = "Everything 已就绪") => new(true, message);
}

/// <summary>File hits plus the backend state that produced them.</summary>
public sealed record FileSearchOutcome(
    FileSearchAvailability Availability,
    IReadOnlyList<FileSearchHit> Hits,
    string? StatusText = null
);

/// <summary>A single file-system hit as returned by the index backend.</summary>
public readonly record struct FileSearchHit(string FullPath, bool IsFolder);
