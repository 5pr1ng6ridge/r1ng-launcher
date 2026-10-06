using System.IO;
using R1ng.Launcher.Core;
using R1ng.Launcher.Apps;

namespace R1ng.Launcher.Search;

/// <summary>
/// Maps file-index hits onto launcher rows. Files are matched literally (Everything did the query);
/// pinyin-initial search is intentionally not offered for files and is documented as a limitation.
/// </summary>
public static class FileResultMapper
{
    public const int NameMatchBase = 400;
    public const int PathOnlyMatchBase = 300;

    public static IReadOnlyList<SearchResultItem> Map(
        IReadOnlyList<FileSearchHit> hits,
        string query,
        int maxResults = ResultRanker.MaxResults
    )
    {
        ArgumentNullException.ThrowIfNull(hits);

        var items = new List<SearchResultItem>(hits.Count);
        foreach (var hit in hits)
        {
            var item = Map(hit, query);
            if (item is not null)
                items.Add(item);
        }

        return ResultRanker.Rank(items, maxResults);
    }

    public static SearchResultItem? Map(FileSearchHit hit, string query)
    {
        if (string.IsNullOrWhiteSpace(hit.FullPath))
            return null;

        var fullPath = hit.FullPath;
        var title = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
            title = fullPath;

        var folder = hit.IsFolder
            ? Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetDirectoryName(fullPath);

        var match = NameMatcher.MatchLiteral(title, query);
        var score = match.Matched ? NameMatchBase + match.Score / 4 : PathOnlyMatchBase;

        var identity = LaunchIdentity.ForTarget(fullPath, hit.IsFolder);
        if (!hit.IsFolder && ShellLinkReader.IsLocalAbsolute(fullPath))
        {
            if (fullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                identity = ShellLinkReader.ReadIdentity(fullPath) ?? identity;
            else if (fullPath.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && UrlShortcut.Read(fullPath).Url is { } url)
                identity = LaunchIdentity.ForUrl(url);
        }

        return new SearchResultItem
        {
            Id = $"file|{fullPath}",
            IdentityKey = identity,
            IdentityAliases = [LaunchIdentity.ForTarget(fullPath, hit.IsFolder)],
            Kind = hit.IsFolder ? ResultKind.Folder : ResultKind.File,
            Title = title,
            Detail = folder ?? fullPath,
            TypeLabel = hit.IsFolder ? "文件夹" : "文件",
            Target = fullPath,
            Source = AppSources.Everything,
            Score = score,
            TitleMatchIndices = match.Indices,
            IsFolder = hit.IsFolder,
            FolderPath = folder,
            IconPath = fullPath,
        };
    }
}
