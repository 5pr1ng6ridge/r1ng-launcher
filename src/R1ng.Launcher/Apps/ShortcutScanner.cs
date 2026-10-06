using System.IO;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Apps;

/// <summary>
/// Walks the shortcut roots and turns every .lnk or .url file into a catalog entry.
/// The traversal is explicit (stack based) so recursion depth and entry count stay bounded and
/// unreadable folders are skipped instead of aborting the whole scan.
/// </summary>
public static class ShortcutScanner
{
    private const int MaxEntries = 5000;
    private const int MaxDepth = 6;

    /// <summary>Scans all roots; deterministic (sorted by source, then display name).</summary>
    public static IReadOnlyList<AppCatalogEntry> Scan(
        IReadOnlyList<ShortcutRoot> roots,
        Action<string, Exception>? onError = null
    )
    {
        ArgumentNullException.ThrowIfNull(roots);

        var entries = new List<AppCatalogEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (entries.Count >= MaxEntries)
                break;

            ScanRoot(root, entries, seenTargets, onError ?? ((_, _) => { }));
        }

        return entries
            .OrderBy(entry => entry.Source, StringComparer.Ordinal)
            // Ordinal so the catalog order is machine- and culture-independent.
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanRoot(
        ShortcutRoot root,
        List<AppCatalogEntry> entries,
        HashSet<string> seenTargets,
        Action<string, Exception> onError
    )
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root.Path, 0));

        while (pending.Count > 0 && entries.Count < MaxEntries)
        {
            var (path, depth) = pending.Pop();

            if (!TryEnumerate(path, out var files, out var directories, onError))
                continue;

            foreach (var file in files)
            {
                if (entries.Count >= MaxEntries)
                    return;

                var isUrl = file.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
                if (!isUrl && !file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    continue;

                var displayName = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(file))
                    continue;

                if (!seenTargets.Add(file))
                    continue;

                var entry = new AppCatalogEntry
                {
                    DisplayName = displayName.Trim(),
                    // .url and .lnk keep the shortcut itself as the shell-safe launch target.
                    Target = file,
                    Source = root.Source,
                    FolderPath = Path.GetDirectoryName(file),
                    IconPath = file,
                };

                if (isUrl)
                {
                    var metadata = UrlShortcut.Read(file);
                    entry = entry with
                    {
                        IdentityKey = metadata.Url is null ? LaunchIdentity.ForTarget(file) : LaunchIdentity.ForUrl(metadata.Url),
                        IconPath = metadata.Icon.File ?? file,
                        IconIndex = metadata.Icon.Index,
                    };
                }
                else
                {
                    entry = entry with { IdentityKey = ShellLinkReader.ReadIdentity(file) ?? LaunchIdentity.ForTarget(file) };
                }

                entries.Add(entry);
            }

            if (depth >= MaxDepth)
                continue;

            foreach (var directory in directories)
                pending.Push((directory, depth + 1));
        }
    }

    private static bool TryEnumerate(
        string path,
        out string[] files,
        out string[] directories,
        Action<string, Exception> onError
    )
    {
        files = Array.Empty<string>();
        directories = Array.Empty<string>();

        try
        {
            var shortcuts = Directory.GetFiles(path, "*.lnk", SearchOption.TopDirectoryOnly);
            var internetShortcuts = Directory.GetFiles(path, "*.url", SearchOption.TopDirectoryOnly);
            files = new string[shortcuts.Length + internetShortcuts.Length];
            Array.Copy(shortcuts, files, shortcuts.Length);
            Array.Copy(internetShortcuts, 0, files, shortcuts.Length, internetShortcuts.Length);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            onError(path, exception);
            return false;
        }

        try
        {
            directories = Directory.GetDirectories(path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            onError(path, exception);
            return false;
        }

        return true;
    }
}
