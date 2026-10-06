using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class SelectionModelTests
{
    [Fact]
    public void Reset_selects_the_first_row_and_clamps_the_preferred_index()
    {
        var selection = new SelectionModel();
        selection.Reset(3, preferredIndex: 9);

        Assert.Equal(2, selection.Index);
        Assert.True(selection.HasSelection);
    }

    [Fact]
    public void Reset_with_no_rows_clears_the_selection()
    {
        var selection = new SelectionModel();
        selection.Reset(0);

        Assert.Equal(-1, selection.Index);
        Assert.False(selection.HasSelection);
    }

    [Fact]
    public void Move_wraps_around_both_ends()
    {
        var selection = new SelectionModel();
        selection.Reset(3, preferredIndex: 2);

        Assert.True(selection.Move(1));
        Assert.Equal(0, selection.Index);

        Assert.True(selection.Move(-1));
        Assert.Equal(2, selection.Index);
    }

    [Fact]
    public void Move_page_clamps_instead_of_wrapping()
    {
        var selection = new SelectionModel();
        selection.Reset(10);

        Assert.True(selection.MovePage(1, 5));
        Assert.Equal(5, selection.Index);

        Assert.True(selection.MovePage(1, 5));
        Assert.Equal(9, selection.Index);

        Assert.False(selection.MovePage(1, 5));
        Assert.Equal(9, selection.Index);

        Assert.True(selection.MovePage(-1, 5));
        Assert.Equal(4, selection.Index);
    }

    [Fact]
    public void Move_to_start_and_end_report_whether_anything_changed()
    {
        var selection = new SelectionModel();
        selection.Reset(5, preferredIndex: 3);

        Assert.True(selection.MoveToStart());
        Assert.Equal(0, selection.Index);
        Assert.False(selection.MoveToStart());

        Assert.True(selection.MoveToEnd());
        Assert.Equal(4, selection.Index);
        Assert.False(selection.MoveToEnd());
    }

    [Fact]
    public void Move_on_an_empty_list_is_a_no_op()
    {
        var selection = new SelectionModel();
        selection.Reset(0);

        Assert.False(selection.Move(1));
        Assert.False(selection.MovePage(1, 5));
        Assert.False(selection.MoveToStart());
        Assert.Equal(-1, selection.Index);
    }
}
