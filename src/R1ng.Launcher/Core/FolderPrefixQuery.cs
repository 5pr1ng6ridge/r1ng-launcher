namespace R1ng.Launcher.Core;

/// <summary>
/// Splits a search-box query into an optional leading quoted folder prefix and the remaining terms.
/// <para>
/// The independent file-scope hotkey injects a visible <c>"&lt;physical folder&gt;" terms</c> query.
/// The prefix stays part of the editable text box, so it is re-parsed from the text on every query
/// instead of being stored separately: the file backend restricts to the folder while highlighting
/// and ranking use only the terms. Only a fully qualified local drive or UNC folder is recognized
/// (never a drive-/root-relative, virtual or URI string), which keeps a stray leading quote in a
/// normal keyword query from being mistaken for a folder.
/// </para>
/// </summary>
public static class FolderPrefixQuery
{
    /// <summary>
    /// Parses the leading quoted folder (when present) and returns the remaining terms. A query
    /// without a valid prefix yields <c>false</c> with <paramref name="terms"/> equal to the whole
    /// query, so callers can pass the result to the backend unchanged.
    /// </summary>
    public static bool TryParse(string? query, out string folder, out string terms)
    {
        folder = string.Empty;
        terms = query ?? string.Empty;

        if (string.IsNullOrEmpty(query) || query[0] != '"')
            return false;

        var closing = query.IndexOf('"', 1);
        if (closing < 0)
            return false;

        var candidate = query[1..closing];
        if (!IsPhysicalFolder(candidate))
            return false;

        folder = candidate;
        terms = query[(closing + 1)..].TrimStart();
        return true;
    }

    /// <summary>
    /// True for a fully qualified local drive or UNC folder path that can actually constrain a file
    /// search. Drive-relative (<c>C:foo</c>) and root-relative (<c>\foo</c>) strings are rejected
    /// because Windows resolves them against the process' current directory/drive, which would let
    /// the launcher invent a constraint from a stray leading quote. Virtual shell locations
    /// (<c>::{...}</c>), URIs and relative strings are rejected for the same reason.
    /// </summary>
    public static bool IsPhysicalFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var value = path.Trim();
        if (value.Length < 2)
            return false;
        if (value.StartsWith("::", StringComparison.Ordinal))
            return false;
        if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Contains("://", StringComparison.Ordinal))
            return false;
        if (value.IndexOf('"') >= 0)
            return false;

        return IsDriveAbsolute(value) || IsUncShare(value);
    }

    /// <summary>
    /// <c>X:\</c> or <c>X:/</c> plus an optional remainder: a real local drive root/folder. A bare
    /// <c>X:foo</c> is drive-relative (resolved against the current directory on that drive) and is
    /// deliberately not accepted.
    /// </summary>
    private static bool IsDriveAbsolute(string value) =>
        value.Length >= 3
        && char.IsAsciiLetter(value[0])
        && value[1] == ':'
        && (value[2] == '\\' || value[2] == '/');

    /// <summary>
    /// <c>\\server\share</c> or <c>//server/share</c> with an optional remainder. A UNC path needs
    /// both a server and a share; the <c>\\?\</c>/<c>\\.\</c> device namespaces (which Explorer never
    /// shows as a browsable folder) are rejected.
    /// </summary>
    private static bool IsUncShare(string value)
    {
        var separator = value[0];
        if ((separator != '\\' && separator != '/') || value.Length < 2 || value[1] != separator)
            return false;

        var segments = value[2..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0] != "?" && segments[0] != ".";
    }

    /// <summary>Renders the visible folder prefix token, including the quotes.</summary>
    public static string Quote(string folder) => $"\"{folder}\"";

    /// <summary>
    /// Builds the query text: <c>"folder" terms</c>, or <c>"folder" </c> (with the separator
    /// space kept) when no terms are known so the caret lands after the prefix. The previous
    /// automatic prefix is never stacked because callers pass terms that were already stripped with
    /// <see cref="TryParse"/>.
    /// </summary>
    public static string Compose(string? folder, string terms)
    {
        terms ??= string.Empty;
        if (string.IsNullOrWhiteSpace(folder))
            return terms;

        // Always keep the separator, even with no terms: the visible query stays path + space and
        // the caret can sit right after the space ready for typing.
        return $"{Quote(folder)} {terms}";
    }

    /// <summary>
    /// Index in the composed query where the terms start (after the quoted prefix and its space), or
    /// the end of the text when there are no terms. The window uses it to select only the keywords.
    /// </summary>
    public static int TermsStartIndex(string query)
    {
        if (TryParse(query, out _, out var terms))
            return query.Length - terms.Length;

        return 0;
    }
}
