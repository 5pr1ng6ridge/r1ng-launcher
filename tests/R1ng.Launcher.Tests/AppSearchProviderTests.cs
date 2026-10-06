using System.IO;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class AppSearchProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-apps-" + Guid.NewGuid().ToString("N"));

    public AppSearchProviderTests()
    {
        Directory.CreateDirectory(_root);
        WriteShortcut("记事本.lnk");
        WriteShortcut("计算器.lnk");
        WriteShortcut("Visual Studio Code.lnk");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Search_matches_literal_names()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("计算", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("计算器", results[0].Title);
        Assert.Equal(ResultKind.Application, results[0].Kind);
        Assert.Equal("应用", results[0].TypeLabel);
    }

    [Fact]
    public async Task Search_matches_pinyin_initials()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("jsb", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("记事本", results[0].Title);
        Assert.Equal(new[] { 0, 1, 2 }, results[0].TitleMatchIndices);
    }

    [Fact]
    public async Task Search_matches_ascii_prefixes()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("visual", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Visual Studio Code", results[0].Title);
    }

    [Fact]
    public async Task Search_reports_launch_targets_and_source_bonus()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("记事", CancellationToken.None);
        var item = Assert.Single(results);

        Assert.True(File.Exists(item.Target));
        Assert.Equal(AppSources.StartMenu, item.Source);
        Assert.True(item.Score > NameMatcher.SubstringScore);
    }

    [Fact]
    public async Task Unmatched_query_returns_nothing()
    {
        var provider = CreateProvider();

        var results = await provider.SearchAsync("zzzz", CancellationToken.None);

        Assert.Empty(results);
    }

    private AppSearchProvider CreateProvider()
    {
        var catalog = new AppCatalog(
            () => [new ShortcutRoot(_root, AppSources.StartMenu)],
            _ => Array.Empty<AppCatalogEntry>()
        );

        return new AppSearchProvider(catalog);
    }

    private void WriteShortcut(string fileName) => File.WriteAllText(Path.Combine(_root, fileName), "stub");
}
