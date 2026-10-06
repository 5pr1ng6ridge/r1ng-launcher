using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class FileResultMapperTests
{
    [Fact]
    public void Map_builds_title_detail_and_badge_from_a_file_hit()
    {
        var hit = new FileSearchHit(@"C:\Users\test\Documents\report.docx", IsFolder: false);

        var item = FileResultMapper.Map(hit, "report");

        Assert.NotNull(item);
        Assert.Equal("report.docx", item!.Title);
        Assert.Equal(@"C:\Users\test\Documents", item.Detail);
        Assert.Equal("文件", item.TypeLabel);
        Assert.Equal(ResultKind.File, item.Kind);
        Assert.Equal(AppSources.Everything, item.Source);
        Assert.Equal(@"C:\Users\test\Documents", item.FolderPath);
        Assert.Equal(hit.FullPath, item.CopyText);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, item.TitleMatchIndices);
    }

    [Fact]
    public void Map_marks_folders_and_keeps_their_parent_for_open_location()
    {
        var hit = new FileSearchHit(@"C:\Users\test\Projects", IsFolder: true);

        var item = FileResultMapper.Map(hit, "projects");

        Assert.NotNull(item);
        Assert.Equal(ResultKind.Folder, item!.Kind);
        Assert.True(item.IsFolder);
        Assert.Equal("文件夹", item.TypeLabel);
        Assert.Equal(@"C:\Users\test", item.Detail);
    }

    [Fact]
    public void Map_scores_name_matches_above_index_only_matches()
    {
        var nameMatch = FileResultMapper.Map(new FileSearchHit(@"C:\a\note.txt", false), "note");
        var pathOnly = FileResultMapper.Map(new FileSearchHit(@"C:\note\other.txt", false), "note");

        Assert.NotNull(nameMatch);
        Assert.NotNull(pathOnly);
        Assert.True(nameMatch!.Score > pathOnly!.Score);
    }

    [Fact]
    public void Map_ignores_empty_paths()
    {
        Assert.Null(FileResultMapper.Map(new FileSearchHit("   ", false), "x"));
    }

    [Fact]
    public void Map_returns_ranked_items_capped_by_the_limit()
    {
        var hits = Enumerable.Range(0, 10)
            .Select(index => new FileSearchHit($@"C:\tmp\file{index}.txt", false))
            .ToList();

        var items = FileResultMapper.Map(hits, "file", maxResults: 3);

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.EndsWith(".txt", item.Title, StringComparison.Ordinal));
    }

    [Fact]
    public void Root_paths_fall_back_to_the_full_path_as_title()
    {
        var item = FileResultMapper.Map(new FileSearchHit(@"C:\", IsFolder: true), "c:");

        Assert.NotNull(item);
        Assert.Equal(@"C:\", item!.Title);
    }
}
