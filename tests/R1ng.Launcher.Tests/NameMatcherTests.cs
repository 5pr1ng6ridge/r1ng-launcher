using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class NameMatcherTests
{
    [Fact]
    public void Empty_query_matches_everything_with_zero_score()
    {
        var match = NameMatcher.MatchLiteral("Notepad", "  ");
        Assert.True(match.Matched);
        Assert.Equal(0, match.Score);
    }

    [Fact]
    public void Exact_match_outranks_prefix_and_substring()
    {
        var exact = NameMatcher.MatchLiteral("notepad", "notepad");
        var prefix = NameMatcher.MatchLiteral("notepad++", "notepad");
        var substring = NameMatcher.MatchLiteral("my-notepad", "notepad");

        Assert.True(exact.Score > prefix.Score);
        Assert.True(prefix.Score > substring.Score);
    }

    [Fact]
    public void Literal_prefix_reports_the_matched_indices()
    {
        var match = NameMatcher.MatchLiteral("Notepad", "Note");
        Assert.True(match.Matched);
        Assert.Equal(new[] { 0, 1, 2, 3 }, match.Indices);
    }

    [Fact]
    public void Substring_match_reports_offset_indices()
    {
        var match = NameMatcher.MatchLiteral("My Notepad", "Note");
        Assert.True(match.Matched);
        Assert.Equal(new[] { 3, 4, 5, 6 }, match.Indices);
    }

    [Fact]
    public void Pinyin_initials_match_chinese_names()
    {
        var index = SearchTextIndex.Create("记事本");
        var match = NameMatcher.MatchText(index, "jsb");

        Assert.True(match.Matched);
        Assert.Equal(NameMatcher.InitialsExactScore, match.Score);
        Assert.Equal(new[] { 0, 1, 2 }, match.Indices);
    }

    [Fact]
    public void Pinyin_initials_prefix_beats_character_subsequence()
    {
        var index = SearchTextIndex.Create("记事本");
        var initialsMatch = NameMatcher.MatchText(index, "js");
        var subsequenceMatch = NameMatcher.MatchText(index, "记本");

        Assert.True(initialsMatch.Matched);
        Assert.True(subsequenceMatch.Matched);
        Assert.True(initialsMatch.Score > subsequenceMatch.Score);
    }

    [Fact]
    public void Character_subsequence_matches_when_literal_and_initials_fail()
    {
        var index = SearchTextIndex.Create("Visual Studio Code");
        var match = NameMatcher.MatchText(index, "vsc");

        Assert.True(match.Matched);
        Assert.Equal(3, match.Indices.Count);
    }

    [Fact]
    public void Unrelated_query_does_not_match()
    {
        var index = SearchTextIndex.Create("记事本");
        var match = NameMatcher.MatchText(index, "zzz");

        Assert.False(match.Matched);
    }

    [Fact]
    public void Literal_matching_is_used_for_files_so_chinese_queries_still_work()
    {
        var match = NameMatcher.MatchLiteral("项目计划.docx", "项目");
        Assert.True(match.Matched);
        Assert.Equal(new[] { 0, 1 }, match.Indices);
    }
}
