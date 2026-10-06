namespace R1ng.Launcher.Core;

/// <summary>What a result row represents. Drives the badge, the icon and the context actions.</summary>
public enum ResultKind
{
    Application = 0,
    File = 1,
    Folder = 2,
    History = 3,
}

/// <summary>Which providers participate in a search.</summary>
public enum SearchScope
{
    All = 0,
    Applications = 1,
    Files = 2,
}

/// <summary>
/// One launcher result row. Immutable so a snapshot can be handed to the UI and the staleness guard
/// without any shared mutable state.
/// </summary>
public sealed record SearchResultItem
{
    /// <summary>Stable identity used for de-duplication and history round-tripping.</summary>
    public required string Id { get; init; }

    public required ResultKind Kind { get; init; }

    /// <summary>Primary line, e.g. the shortcut display name or the file name.</summary>
    public required string Title { get; init; }

    /// <summary>Secondary line: parent folder, shortcut source or launch target.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Localized type badge, e.g. 应用 / 文件 / 文件夹.</summary>
    public required string TypeLabel { get; init; }

    /// <summary>Launch target: absolute path, .lnk path or shell:AppsFolder\&lt;id&gt;.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Where the entry came from, e.g. 开始菜单 / 桌面 / 启动 / 应用 / Everything.</summary>
    public string Source { get; init; } = string.Empty;

    public int Score { get; init; }

    /// <summary>Verified launch identity; null keeps the provider's original row identity.</summary>
    public string? IdentityKey { get; init; }

    /// <summary>Equivalent original shortcut path identities, for legacy history and preferences.</summary>
    public IReadOnlyList<string> IdentityAliases { get; init; } = Array.Empty<string>();

    public bool IsPinned { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>Folder that "open containing folder" should reveal, when known.</summary>
    public string? FolderPath { get; init; }

    /// <summary>Indices in <see cref="Title"/> that matched the query, for highlighting.</summary>
    public IReadOnlyList<int> TitleMatchIndices { get; init; } = Array.Empty<int>();

    /// <summary>Path used to resolve a shell icon (shortcut path, executable or folder).</summary>
    public string? IconPath { get; init; }

    /// <summary>Icon index inside <see cref="IconPath"/>; -1 means "use the file's default icon".</summary>
    public int IconIndex { get; init; } = -1;

    /// <summary>Text handed to the clipboard by the "copy path" action.</summary>
    public string CopyText => string.IsNullOrEmpty(Target) ? Title : Target;

    public SearchResultItem WithScore(int score) => this with { Score = score };
}

/// <summary>Immutable view of the launcher state produced by <c>SearchController</c>.</summary>
public sealed record SearchSnapshot
{
    public static SearchSnapshot Idle { get; } = new()
    {
        Query = string.Empty,
        Scope = SearchScope.All,
        Items = Array.Empty<SearchResultItem>(),
        IsSearching = false,
        StatusText = null,
        FileNotice = null,
        FilesUnavailable = false,
    };

    public required string Query { get; init; }

    public required SearchScope Scope { get; init; }

    public required IReadOnlyList<SearchResultItem> Items { get; init; }

    /// <summary>True while a newer query is still running; <see cref="Items"/> keeps the previous results.</summary>
    public required bool IsSearching { get; init; }

    /// <summary>Transient footer message (e.g. "输入至少 2 个字符").</summary>
    public string? StatusText { get; init; }

    /// <summary>Persistent explanation shown when the Everything-backed file scope is not usable.</summary>
    public string? FileNotice { get; init; }

    public bool FilesUnavailable { get; init; }

    public SearchContext? Context { get; init; }

    /// <summary>
    /// Keeps the previous rows visible while the next query runs, so the list never blanks between
    /// keystrokes.
    /// </summary>
    public SearchSnapshot AsSearching(string query, SearchScope scope) => this with
    {
        Query = query,
        Scope = scope,
        IsSearching = true,
        StatusText = null,
    };

    public static SearchSnapshot FromItems(
        string query,
        SearchScope scope,
        IReadOnlyList<SearchResultItem> items,
        bool isSearching = false,
        string? statusText = null,
        string? fileNotice = null,
        bool filesUnavailable = false,
        SearchContext? context = null
    ) => new()
    {
        Query = query,
        Scope = scope,
        Items = items,
        IsSearching = isSearching,
        StatusText = statusText,
        FileNotice = fileNotice,
        FilesUnavailable = filesUnavailable,
        Context = context,
    };
}
