using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class ResultRankerTests
{
    [Fact]
    public void Rank_orders_by_score_then_kind_then_title()
    {
        var items = new[]
        {
            Item("file|a", ResultKind.File, "alpha", 500),
            Item("app|b", ResultKind.Application, "beta", 500),
            Item("app|a", ResultKind.Application, "alpha", 500),
            Item("app|z", ResultKind.Application, "zulu", 900),
        };

        var ranked = ResultRanker.Rank(items);

        Assert.Equal(new[] { "app|z", "app|a", "app|b", "file|a" }, ranked.Select(item => item.Id));
    }

    [Fact]
    public void Rank_deduplicates_by_id_keeping_the_best_score()
    {
        var items = new[]
        {
            Item("app|x", ResultKind.Application, "x", 10),
            Item("app|x", ResultKind.Application, "x", 900),
        };

        var ranked = ResultRanker.Rank(items);

        Assert.Single(ranked);
        Assert.Equal(900, ranked[0].Score);
    }

    [Fact]
    public void Rank_respects_the_result_cap_and_is_stable_for_equal_keys()
    {
        var items = Enumerable.Range(0, 100).Select(index => Item($"file|{index}", ResultKind.File, $"name{index:D3}", 100));

        var first = ResultRanker.Rank(items, 10);
        var second = ResultRanker.Rank(items.Reverse(), 10);

        Assert.Equal(10, first.Count);
        Assert.Equal(first.Select(item => item.Id), second.Select(item => item.Id));
    }

    [Fact]
    public void SourceBonus_prefers_start_menu_over_startup()
    {
        Assert.True(ResultRanker.SourceBonus(AppSources.StartMenu) > ResultRanker.SourceBonus(AppSources.Startup));
        Assert.Equal(0, ResultRanker.SourceBonus("unknown"));
    }

    private static SearchResultItem Item(string id, ResultKind kind, string title, int score) => new()
    {
        Id = id,
        Kind = kind,
        Title = title,
        TypeLabel = "测试",
        Score = score,
    };
}
