using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class SearchTextIndexTests
{
    [Fact]
    public void Create_collects_initials_in_source_order()
    {
        var index = SearchTextIndex.Create("记事本");

        Assert.Equal("记事本", index.Text);
        Assert.Equal("jsb", index.Initials);
        Assert.Equal(new[] { 0, 1, 2 }, index.MapInitialsRange(0, 3));
    }

    [Fact]
    public void MapInitialsRange_maps_back_to_cjk_source_characters()
    {
        var index = SearchTextIndex.Create("记事本 2024");

        var indices = index.MapInitialsRange(0, 2);
        Assert.Equal(new[] { 0, 1 }, indices);
        Assert.True(index.IsCjkInitial(0));
        Assert.True(index.IsCjkInitial(2));
    }

    [Fact]
    public void MapInitialsRange_out_of_bounds_returns_empty()
    {
        var index = SearchTextIndex.Create("记事本");
        Assert.Empty(index.MapInitialsRange(5, 3));
        Assert.Empty(index.MapInitialsRange(0, 0));
    }

    [Fact]
    public void MapInitialsRange_deduplicates_offsets_for_multi_initial_characters()
    {
        var map = new MultiInitialMap();
        var index = SearchTextIndex.Create("重", map);

        Assert.Equal("cz", index.Initials);
        Assert.Equal(new[] { 0 }, index.MapInitialsRange(0, 2));
    }

    private sealed class MultiInitialMap : IPinyinMap
    {
        public bool TryGetInitials(char value, out string initials)
        {
            if (value == '重')
            {
                initials = "cz";
                return true;
            }

            initials = string.Empty;
            return false;
        }
    }
}
