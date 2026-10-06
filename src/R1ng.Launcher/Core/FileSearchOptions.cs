namespace R1ng.Launcher.Core;

/// <summary>
/// File-search refinements exposed by the search box's quick toggles (Aa / Abl / ..\ / .*).
/// <para>
/// These map 1:1 onto Everything's documented QUERY2 <c>search_flags</c> and only affect the
/// file-index backend; application and history results are always matched case-insensitively and
/// literally. <see cref="Default"/> keeps every refinement off, in particular
/// <see cref="MatchPath"/>, so an unmodified query searches file/folder names only.
/// </para>
/// </summary>
public readonly record struct FileSearchOptions
{
    /// <summary>EVERYTHING_IPC_MATCHCASE: match case (Aa).</summary>
    public bool MatchCase { get; init; }

    /// <summary>EVERYTHING_IPC_MATCHWHOLEWORD: match whole words (Abl). Ignored by regex queries.</summary>
    public bool MatchWholeWord { get; init; }

    /// <summary>EVERYTHING_IPC_MATCHPATH: search the full path and not just the name (..\).</summary>
    public bool MatchPath { get; init; }

    /// <summary>EVERYTHING_IPC_REGEX: treat the query as a regular expression (.*).</summary>
    public bool Regex { get; init; }

    /// <summary>All refinements off; the file backend searches names, case-insensitively.</summary>
    public static FileSearchOptions Default => default;

    /// <summary>True when no refinement is enabled.</summary>
    public bool IsDefault => this == Default;
}
