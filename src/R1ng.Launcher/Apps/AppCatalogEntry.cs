using System.IO;
namespace R1ng.Launcher.Apps;

/// <summary>An application-like launch candidate discovered from a shortcut folder or AppsFolder.</summary>
public sealed record AppCatalogEntry
{
    public required string DisplayName { get; init; }

    /// <summary>Absolute .lnk path or shell:AppsFolder\&lt;appUserModelId&gt;.</summary>
    public required string Target { get; init; }

    public required string Source { get; init; }

    /// <summary>Folder that contains the shortcut (null for AppsFolder entries).</summary>
    public string? FolderPath { get; init; }

    /// <summary>Icon source path. For shortcuts this is the .lnk itself.</summary>
    public string? IconPath { get; init; }

    /// <summary>Icon index inside <see cref="IconPath"/>; -1 means "use the file's default icon".</summary>
    public int IconIndex { get; init; } = -1;

    public string? IdentityKey { get; init; }

    public IReadOnlyList<string> NameAliases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> IdentityAliases { get; init; } = Array.Empty<string>();

    /// <summary>Provider row id; launch identity is independent of its source.</summary>
    public string Id => $"{Source}|{Target}";
}

/// <summary>A folder that contains shortcuts plus the label to attribute them to.</summary>
public sealed record ShortcutRoot(string Path, string Source);

/// <summary>Resolves the well-known application shortcut folders.</summary>
public static class AppFolders
{
    private static readonly (Environment.SpecialFolder Folder, string Source)[] Definitions =
    [
        (Environment.SpecialFolder.StartMenu, Core.AppSources.StartMenu),
        (Environment.SpecialFolder.CommonStartMenu, Core.AppSources.StartMenu),
        (Environment.SpecialFolder.Startup, Core.AppSources.Startup),
        (Environment.SpecialFolder.CommonStartup, Core.AppSources.Startup),
        (Environment.SpecialFolder.DesktopDirectory, Core.AppSources.Desktop),
        (Environment.SpecialFolder.CommonDesktopDirectory, Core.AppSources.Desktop),
    ];

    /// <summary>
    /// Existing roots only, de-duplicated by path (the user and machine folders can resolve to the
    /// same path in some profiles).
    /// </summary>
    public static IReadOnlyList<ShortcutRoot> GetRoots(
        Func<Environment.SpecialFolder, string> resolve,
        Func<string, bool>? directoryExists = null
    )
    {
        ArgumentNullException.ThrowIfNull(resolve);
        directoryExists ??= Directory.Exists;

        var roots = new List<ShortcutRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, source) in Definitions)
        {
            string path;
            try
            {
                path = resolve(folder);
            }
            catch (Exception)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(path) || !directoryExists(path))
                continue;

            var fullPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seen.Add(fullPath))
                continue;

            roots.Add(new ShortcutRoot(fullPath, source));
        }

        return roots;
    }
}
