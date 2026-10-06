using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class FolderPrefixQueryTests
{
    [Fact]
    public void Quoted_folder_prefix_is_split_from_the_terms()
    {
        var parsed = FolderPrefixQuery.TryParse("\"C:\\Users\\me\\Desktop\" report", out var folder, out var terms);

        Assert.True(parsed);
        Assert.Equal(@"C:\Users\me\Desktop", folder);
        Assert.Equal("report", terms);
    }

    [Fact]
    public void A_query_without_quotes_is_all_terms()
    {
        var parsed = FolderPrefixQuery.TryParse("report notes", out var folder, out var terms);

        Assert.False(parsed);
        Assert.Equal(string.Empty, folder);
        Assert.Equal("report notes", terms);
    }

    [Fact]
    public void A_relative_or_unterminated_prefix_is_not_a_folder_constraint()
    {
        Assert.False(FolderPrefixQuery.TryParse("\"relative\" report", out _, out var relativeTerms));
        Assert.Equal("\"relative\" report", relativeTerms);

        Assert.False(FolderPrefixQuery.TryParse("\"C:\\unterminated", out _, out var unterminated));
        Assert.Equal("\"C:\\unterminated", unterminated);
    }

    [Fact]
    public void Virtual_locations_and_uris_are_rejected_as_folders()
    {
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("shell:AppsFolder\\foo!App"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("https://example.com"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("relative\\path"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(null));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"C:\"));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"\\server\share"));
    }

    [Fact]
    public void Drive_relative_and_root_relative_paths_are_not_folders()
    {
        // Windows' Path.IsPathRooted reports true for both, but neither is fully qualified: "C:foo"
        // is resolved against the current directory on C: and "\foo" against the current drive.
        // Accepting either would invent a constraint from a stray leading quote.
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("C:foo"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(@"C:foo\bar"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder("C:"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(@"\foo"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(@"\foo\bar"));

        // Fully qualified local drive and UNC forms stay accepted.
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"C:\"));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"C:\folder"));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder("C:/folder"));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"\\server\share"));
        Assert.True(FolderPrefixQuery.IsPhysicalFolder(@"\\server\share\sub"));

        // Device namespaces are not browsable Explorer folders.
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(@"\\?\C:\foo"));
        Assert.False(FolderPrefixQuery.IsPhysicalFolder(@"\\.\C:\foo"));
    }

    [Fact]
    public void A_drive_relative_quote_keeps_the_whole_text_as_keywords_and_creates_no_prefix()
    {
        const string text = "\"C:foo\" report";

        Assert.False(FolderPrefixQuery.TryParse(text, out var folder, out var terms));
        Assert.Equal(string.Empty, folder);

        // The keywords are not stripped and the quoted text stays part of the query.
        Assert.Equal(text, terms);
        Assert.Equal(0, FolderPrefixQuery.TermsStartIndex(text));
    }

    [Fact]
    public void Compose_replaces_rather_than_stacks_the_prefix()
    {
        Assert.Equal("\"C:\\a\" report", FolderPrefixQuery.Compose(@"C:\a", "report"));
        Assert.Equal("\"C:\\a\" ", FolderPrefixQuery.Compose(@"C:\a", string.Empty));
        Assert.Equal("report", FolderPrefixQuery.Compose(null, "report"));
        Assert.Equal("report", FolderPrefixQuery.Compose("  ", "report"));
    }

    [Fact]
    public void Compose_keeps_the_separator_even_without_terms_so_the_caret_lands_after_it()
    {
        var text = FolderPrefixQuery.Compose(@"C:\a", string.Empty);

        Assert.Equal("\"C:\\a\" ", text);
        Assert.EndsWith(" ", text, StringComparison.Ordinal);

        // The terms/caret index is exactly the position after the separator space.
        Assert.Equal(text.Length, FolderPrefixQuery.TermsStartIndex(text));
        Assert.True(FolderPrefixQuery.TryParse(text, out var folder, out var terms));
        Assert.Equal(@"C:\a", folder);
        Assert.Equal(string.Empty, terms);
    }

    [Fact]
    public void Terms_start_index_points_past_the_prefix()
    {
        Assert.Equal(0, FolderPrefixQuery.TermsStartIndex("report"));
        Assert.Equal("\"C:\\a\" ".Length, FolderPrefixQuery.TermsStartIndex("\"C:\\a\" report"));
        Assert.Equal("\"C:\\a\"".Length, FolderPrefixQuery.TermsStartIndex("\"C:\\a\""));
        Assert.Equal("\"C:\\a\" ".Length, FolderPrefixQuery.TermsStartIndex("\"C:\\a\" "));
    }
}
