using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

public class ScreenPlacementTests
{
    [Fact]
    public void Place_centers_horizontally_and_anchors_in_the_upper_third()
    {
        var area = new ScreenPlacement.PixelsRect(0, 0, 1000, 800);

        var (left, top) = ScreenPlacement.Place(400, 300, area);

        Assert.Equal(300, left);
        Assert.Equal(Math.Round(800 * ScreenPlacement.VerticalAnchor), top);
    }

    [Fact]
    public void Place_accounts_for_a_secondary_monitor_offset()
    {
        var area = new ScreenPlacement.PixelsRect(1920, 0, 3840, 1080);

        var (left, top) = ScreenPlacement.Place(500, 300, area);

        Assert.Equal(1920 + ((1920 - 500) / 2.0), left);
        Assert.True(top >= 0);
    }

    [Fact]
    public void Place_never_pushes_the_window_off_a_short_work_area()
    {
        var area = new ScreenPlacement.PixelsRect(0, 40, 1024, 240);

        var (_, top) = ScreenPlacement.Place(600, 300, area);

        Assert.Equal(40, top);
    }

    [Fact]
    public void Place_clamps_negative_areas_to_their_origin()
    {
        var area = new ScreenPlacement.PixelsRect(-1920, -200, 0, 880);

        var (left, top) = ScreenPlacement.Place(400, 300, area);

        Assert.Equal(-1920 + ((1920 - 400) / 2.0), left);
        Assert.True(top >= -200);
    }

    [Fact]
    public void The_vertical_anchor_sits_in_the_upper_part_of_the_work_area()
    {
        Assert.Equal(0.18, ScreenPlacement.VerticalAnchor);
    }

    [Fact]
    public void Place_keeps_a_window_wider_than_a_narrow_work_area_at_its_left_edge()
    {
        var area = new ScreenPlacement.PixelsRect(100, 0, 600, 900);

        var (left, _) = ScreenPlacement.Place(760, 300, area);

        Assert.Equal(100, left);
    }

    [Fact]
    public void Place_derives_the_top_from_the_largest_height_so_content_changes_never_move_it()
    {
        var area = new ScreenPlacement.PixelsRect(0, 0, 1920, 1040);

        // The caller passes the tallest height the window can reach, never its current content height.
        var (_, top) = ScreenPlacement.Place(760, 520, area);

        Assert.Equal(Math.Round(1040 * ScreenPlacement.VerticalAnchor), top);
        Assert.True(top + 520 <= area.Bottom);
    }

    [Theory]
    [InlineData(1.0, 760.0, 520.0)]
    [InlineData(1.5, 760.0, 520.0)]
    [InlineData(2.0, 760.0, 520.0)]
    public void FitSize_keeps_the_design_size_on_a_roomy_work_area(double scale, double width, double height)
    {
        var area = new ScreenPlacement.PixelsRect(0, 0, 2560, 1552);

        var fit = ScreenPlacement.FitSize(760, 520, area, scale);

        Assert.Equal((width, height), fit);
    }

    [Fact]
    public void FitSize_caps_the_window_to_a_short_and_narrow_work_area_in_real_pixels()
    {
        // 600 x 400 physical pixels at 150 % is only 400 x 266 DIPs.
        var area = new ScreenPlacement.PixelsRect(-1920, -300, -1320, 100);

        var (width, height) = ScreenPlacement.FitSize(760, 520, area, 1.5);

        Assert.Equal(400, width);
        Assert.Equal(266, height);
        Assert.True(Math.Round(width * 1.5) <= area.Width);
        Assert.True(Math.Round(height * 1.5) <= area.Height);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void FitSize_treats_an_unknown_scale_as_one(double scale)
    {
        var area = new ScreenPlacement.PixelsRect(0, 0, 500, 300);

        Assert.Equal((500d, 300d), ScreenPlacement.FitSize(760, 520, area, scale));
    }

    [Fact]
    public void A_high_dpi_primary_and_a_standard_dpi_secondary_each_place_in_their_own_pixels()
    {
        // Mixed DPI: the secondary starts at x = 2560 and above the primary (negative Y). Each monitor
        // is sized and placed purely in its own device pixels at its own scale.
        var primary = new ScreenPlacement.PixelsRect(0, 0, 2560, 1552);
        var secondary = new ScreenPlacement.PixelsRect(2560, -674, 4480, 358);

        var (primaryWidth, primaryHeight) = ScreenPlacement.FitSize(760, 520, primary, 2.0);
        var (primaryLeft, primaryTop) = ScreenPlacement.Place(primaryWidth * 2.0, primaryHeight * 2.0, primary);
        Assert.Equal(520, primaryLeft);
        Assert.Equal(Math.Round(1552 * ScreenPlacement.VerticalAnchor), primaryTop);

        var (secondaryWidth, secondaryHeight) = ScreenPlacement.FitSize(760, 520, secondary, 1.0);
        var (secondaryLeft, secondaryTop) = ScreenPlacement.Place(secondaryWidth, secondaryHeight, secondary);
        Assert.Equal(2560 + ((1920 - 760) / 2), secondaryLeft);
        Assert.Equal(-674 + Math.Round(1032 * ScreenPlacement.VerticalAnchor), secondaryTop);
        Assert.True(secondaryTop + secondaryHeight <= secondary.Bottom);
    }

    [Fact]
    public void Sampling_the_cursor_monitor_yields_real_pixels_and_a_positive_scale()
    {
        var sample = ScreenPlacement.SampleCursorMonitor();

        Assert.True(sample.WorkArea.Width > 0);
        Assert.True(sample.WorkArea.Height > 0);
        Assert.True(sample.DpiScale > 0);
    }

    [Fact]
    public void Preview_avoidance_prefers_the_right_when_both_sides_fit()
    {
        var launcher = new ScreenPlacement.PixelsRect(450, 100, 750, 500);
        var preview = new ScreenPlacement.PixelsRect(800, 100, 1100, 600);
        var workArea = new ScreenPlacement.PixelsRect(0, 0, 1600, 900);

        var placement = ScreenPlacement.PlaceBeside(launcher, [preview], workArea);

        Assert.NotNull(placement);
        Assert.Equal((1116, 100), (placement.Value.X, placement.Value.Y));
        Assert.True(placement.Value.IsClear);
        Assert.True(placement.Value.WithinWorkArea);
    }

    [Fact]
    public void Preview_avoidance_uses_the_left_when_the_right_does_not_fit()
    {
        var launcher = new ScreenPlacement.PixelsRect(100, 100, 500, 500);
        var preview = new ScreenPlacement.PixelsRect(800, 100, 1000, 500);
        var workArea = new ScreenPlacement.PixelsRect(0, 0, 1200, 800);

        var placement = ScreenPlacement.PlaceBeside(launcher, [preview], workArea);

        Assert.NotNull(placement);
        Assert.Equal((384, 100), (placement.Value.X, placement.Value.Y));
        Assert.True(placement.Value.IsClear);
    }

    [Fact]
    public void Preview_avoidance_keeps_negative_mixed_dpi_coordinates_in_device_pixels()
    {
        var workArea = new ScreenPlacement.PixelsRect(-1920, -200, 0, 880);
        var launcher = new ScreenPlacement.PixelsRect(-700, 0, -200, 400);
        var preview = new ScreenPlacement.PixelsRect(-800, 0, -300, 500);

        var placement = ScreenPlacement.PlaceBeside(launcher, [preview], workArea);

        Assert.NotNull(placement);
        Assert.Equal((-1316, 0), (placement.Value.X, placement.Value.Y));
        Assert.True(placement.Value.WithinWorkArea);
        Assert.Equal(500, launcher.Width);
        Assert.Equal(400, launcher.Height);
    }

    [Fact]
    public void Preview_avoidance_minimizes_overlap_when_the_total_width_cannot_fit()
    {
        var workArea = new ScreenPlacement.PixelsRect(0, 0, 1000, 800);
        var launcher = new ScreenPlacement.PixelsRect(100, 100, 500, 500);
        var preview = new ScreenPlacement.PixelsRect(50, 100, 650, 500);

        var placement = ScreenPlacement.PlaceBeside(launcher, [preview], workArea);

        Assert.NotNull(placement);
        Assert.Equal(600, placement.Value.X);
        Assert.Equal(5_000, placement.Value.OverlapArea);
        Assert.False(placement.Value.IsClear);
        Assert.True(placement.Value.WithinWorkArea);
    }

    [Fact]
    public void Preview_avoidance_selects_a_clear_gap_that_avoids_multiple_pinned_windows()
    {
        var workArea = new ScreenPlacement.PixelsRect(0, 0, 1600, 800);
        var launcher = new ScreenPlacement.PixelsRect(600, 100, 1000, 500);
        var previews = new[]
        {
            new ScreenPlacement.PixelsRect(100, 100, 500, 500),
            new ScreenPlacement.PixelsRect(1100, 100, 1500, 500),
        };

        var placement = ScreenPlacement.PlaceBeside(launcher, previews, workArea);

        Assert.NotNull(placement);
        Assert.Equal(516, placement.Value.X);
        Assert.True(placement.Value.IsClear);
    }

    [Fact]
    public void Preview_avoidance_returns_no_move_when_no_visible_preview_was_found()
    {
        Assert.Null(ScreenPlacement.PlaceBeside(
            new ScreenPlacement.PixelsRect(100, 100, 500, 500),
            Array.Empty<ScreenPlacement.PixelsRect>(),
            new ScreenPlacement.PixelsRect(0, 0, 1000, 800)));
    }

    [Fact]
    public void Preview_avoidance_clamps_an_oversized_launcher_and_reports_partial_containment()
    {
        var placement = ScreenPlacement.PlaceBeside(
            new ScreenPlacement.PixelsRect(0, 0, 400, 300),
            [new ScreenPlacement.PixelsRect(120, 20, 220, 120)],
            new ScreenPlacement.PixelsRect(-100, -50, 200, 150));

        Assert.NotNull(placement);
        Assert.Equal((-100, -50), (placement.Value.X, placement.Value.Y));
        Assert.False(placement.Value.WithinWorkArea);
    }

    [Fact]
    public void The_fallback_work_area_is_the_primary_screen_in_real_pixels()
    {
        var fallback = ScreenPlacement.FallbackWorkArea();

        Assert.True(fallback.Width > 0);
        Assert.True(fallback.Height > 0);

        // Device pixels never exceed the physical primary screen (SystemParameters would be DIPs).
        Assert.True(fallback.Right <= NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen));
        Assert.True(fallback.Bottom <= NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen));
    }
}
