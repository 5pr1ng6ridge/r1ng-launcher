using R1ng.Launcher.Core;
using R1ng.Launcher.Files;

namespace R1ng.Launcher.Tests;

public class FileQueryBuilderTests
{
    [Fact]
    public void Without_a_folder_the_query_and_options_pass_through_unchanged()
    {
        var options = new FileSearchOptions { MatchCase = true, MatchPath = true };
        var built = FileQueryBuilder.Build(null, "report", options);

        Assert.Equal("report", built.Text);
        Assert.Equal(options, built.EffectiveOptions);
    }

    [Fact]
    public void A_folder_is_encoded_as_a_path_function_restricted_to_the_subtree()
    {
        var built = FileQueryBuilder.Build(@"C:\Users\me\Desktop", "report", FileSearchOptions.Default);

        Assert.Equal("path:\"C:\\Users\\me\\Desktop\\\" report", built.Text);
        // Non-regex queries keep every quick-toggle flag untouched.
        Assert.True(built.EffectiveOptions.IsDefault);
    }

    [Fact]
    public void An_already_separated_folder_is_not_double_separated()
    {
        var built = FileQueryBuilder.Build(@"C:\Users\me\", "report", FileSearchOptions.Default);
        Assert.Equal("path:\"C:\\Users\\me\\\" report", built.Text);
    }

    [Fact]
    public void A_folder_without_terms_still_restricts_to_the_folder()
    {
        var built = FileQueryBuilder.Build(@"C:\Users\me", string.Empty, FileSearchOptions.Default);
        Assert.Equal("path:\"C:\\Users\\me\\\"", built.Text);
    }

    [Fact]
    public void Regex_terms_move_onto_the_regex_function_so_the_literal_folder_survives()
    {
        var options = new FileSearchOptions { Regex = true, MatchCase = true, MatchPath = true, MatchWholeWord = true };
        var built = FileQueryBuilder.Build(@"C:\log", @"^rep.*\.txt$", options);

        Assert.Equal("path:\"C:\\log\\\" regex:\"^rep.*\\.txt$\"", built.Text);

        // The regex now lives in the query text: the wire REGEX flag is replaced by the regex:
        // function. Match-path and whole-word are not applicable to that function (whole-word is
        // already ignored for every regex query); match-case is preserved.
        Assert.False(built.EffectiveOptions.Regex);
        Assert.False(built.EffectiveOptions.MatchPath);
        Assert.False(built.EffectiveOptions.MatchWholeWord);
        Assert.True(built.EffectiveOptions.MatchCase);
        Assert.Equal(EverythingProtocol.SearchFlagMatchCase, EverythingProtocol.ToSearchFlags(built.EffectiveOptions));
    }

    [Fact]
    public void Quotes_inside_regex_terms_are_escaped()
    {
        var built = FileQueryBuilder.Build(@"C:\x", "a\"b", new FileSearchOptions { Regex = true });
        Assert.Equal("path:\"C:\\x\\\" regex:\"a\\\"b\"", built.Text);
    }

    [Fact]
    public void Normalize_folder_keeps_drive_roots()
    {
        Assert.Equal(@"C:\", FileQueryBuilder.NormalizeFolder(@"C:\"));
        Assert.Equal(@"C:\a\", FileQueryBuilder.NormalizeFolder(@"C:\a"));
    }
}
