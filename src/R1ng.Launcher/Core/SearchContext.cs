namespace R1ng.Launcher.Core;

/// <summary>The completed request a row belongs to, not the text currently being edited.</summary>
public sealed record SearchContext(string Key)
{
    public static SearchContext? Create(string query, SearchScope scope, FileSearchOptions options)
    {
        var constrained = FolderPrefixQuery.TryParse(query, out var folder, out var terms);
        terms = terms.Trim();
        if (terms.Length == 0)
            return null;

        if (!options.MatchCase && !options.Regex)
            terms = terms.ToUpperInvariant();
        var key = LaunchIdentity.Digest("query", terms, ((int)scope).ToString(),
            constrained ? LaunchIdentity.NormalizePath(folder) : string.Empty,
            options.MatchCase.ToString(), options.MatchWholeWord.ToString(), options.MatchPath.ToString(), options.Regex.ToString());
        return new SearchContext(key);
    }
}

/// <summary>Same-query usage, applied only to candidates that matched the current request.</summary>
public readonly record struct ResultPreference(int Count, DateTime LastUsedUtc);
