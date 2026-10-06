using R1ng.Launcher.Core;

namespace R1ng.Launcher.Files;

/// <summary>
/// The Everything query text plus the search flags that should be sent for it. Usually the options
/// are unchanged; a folder-constrained regex query moves the regex onto the terms and drops the
/// global flags that no longer apply.
/// </summary>
public readonly record struct FileQuery(string Text, FileSearchOptions EffectiveOptions);

/// <summary>
/// Composes the literal folder constraint and the user terms into one Everything query string.
/// <para>
/// The folder is encoded with Everything's documented <c>path:</c> search function, which matches
/// against the full path, so appending a separator restricts the query to that folder's subtree
/// regardless of the quick-toggle flags. The rest of the flags are left untouched — nothing is
/// silently cleared to make the path work. For a regex query the terms are carried by the
/// documented <c>regex:</c> function so the literal folder is never re-interpreted by the regex
/// engine; the wire <c>REGEX</c>/<c>MATCHPATH</c> flags are then omitted because the equivalent
/// semantics live in the query text itself. Without a folder the query and flags pass through
/// unchanged, preserving the existing behaviour exactly.
/// </para>
/// </summary>
public static class FileQueryBuilder
{
    /// <summary>Builds the Everything query for a folder constraint plus literal user terms.</summary>
    public static FileQuery Build(string? folder, string terms, FileSearchOptions options)
    {
        terms ??= string.Empty;

        if (string.IsNullOrWhiteSpace(folder))
            return new FileQuery(terms, options);

        var pathFunction = $"path:\"{NormalizeFolder(folder)}\"";

        if (!options.Regex)
        {
            var text = terms.Length == 0 ? pathFunction : $"{pathFunction} {terms}";
            return new FileQuery(text, options);
        }

        // The regex moves onto the regex: function so the literal folder survives. Match-case is kept;
        // MatchPath and whole-word are intentionally not applicable here (Everything applies the
        // regex: function to names, and whole-word is already ignored for every regex query). This is
        // a explicit, documented consequence of combining a literal folder with a regex, not a silent
        // flag reset: the visible toggles stay on and only the folder-constrained query changes form.
        var effective = options with { Regex = false, MatchPath = false, MatchWholeWord = false };
        if (terms.Length == 0)
            return new FileQuery(pathFunction, effective);

        return new FileQuery($"{pathFunction} regex:\"{EscapeFunctionValue(terms)}\"", effective);
    }

    /// <summary>
    /// Appends a trailing separator unless one is already present, so <c>path:"C:\foo\"</c> matches
    /// the folder's subtree but not a sibling such as <c>C:\foobar</c>.
    /// </summary>
    public static string NormalizeFolder(string folder)
    {
        var value = folder.Trim();
        if (value.Length == 0)
            return value;

        return value.EndsWith('\\') || value.EndsWith('/') ? value : value + '\\';
    }

    /// <summary>Escapes a quote inside an Everything function value (Windows paths cannot contain one).</summary>
    internal static string EscapeFunctionValue(string value) => value.Replace("\"", "\\\"");
}
