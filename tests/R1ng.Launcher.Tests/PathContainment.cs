namespace R1ng.Launcher.Tests;

/// <summary>
/// Directory-boundary check for the live Everything assertions. A plain
/// <c>StartsWith</c> would accept a sibling such as <c>C:\Windows\system32-old</c> when the folder
/// is <c>C:\Windows\system32</c>; comparing whole path segments rejects that while still accepting
/// the folder itself and its real descendants (case-insensitively, as Windows paths are).
/// </summary>
internal static class PathContainment
{
    /// <summary>True when <paramref name="candidate"/> is the folder itself or an item below it.</summary>
    public static bool IsWithinFolder(string folder, string candidate)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(candidate))
            return false;

        var normalizedFolder = folder.TrimEnd('\\', '/');
        if (normalizedFolder.Length == 0)
            return false;

        var normalizedCandidate = candidate.TrimEnd('\\', '/');

        if (string.Equals(normalizedFolder, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            return true;

        // A segment boundary is required so "system32-old" is not treated as inside "system32".
        return normalizedCandidate.StartsWith(normalizedFolder + '\\', StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedFolder + '/', StringComparison.OrdinalIgnoreCase);
    }
}
