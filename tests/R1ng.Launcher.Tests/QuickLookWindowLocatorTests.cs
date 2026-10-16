using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

public class QuickLookWindowLocatorTests
{
    [Theory]
    [InlineData(true, true, false, true, false, 400, 300, true)]
    [InlineData(false, true, false, true, false, 400, 300, false)]
    [InlineData(true, false, false, true, false, 400, 300, false)]
    [InlineData(true, true, true, true, false, 400, 300, false)]
    [InlineData(true, true, false, false, false, 400, 300, false)]
    [InlineData(true, true, false, true, true, 400, 300, false)]
    [InlineData(true, true, false, true, false, 0, 300, false)]
    [InlineData(true, true, false, true, false, 400, 0, false)]
    public void Preview_candidates_must_be_visible_restored_uncloaked_top_level_nonempty_windows(
        bool topLevel, bool visible, bool minimized, bool cloakingReadable, bool cloaked,
        int width, int height, bool expected)
    {
        Assert.Equal(expected, QuickLookWindowLocator.IsEligiblePreviewWindow(
            topLevel, visible, minimized, cloakingReadable, cloaked, width, height,
            @"C:\Program Files\QuickLook\QuickLook.exe", "QuickLook - report.txt"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", "QuickLook", true)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", "QuickLook - report.txt", true)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", "QuickLook - 视频.mp4", true)]
    [InlineData(@"C:\Program Files\Other\QuickLook.exe", "QuickLook - report.txt", true)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", "QuickLook Settings", false)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", "Report - QuickLook", false)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLookHelper.exe", "QuickLook", false)]
    [InlineData(@"C:\Program Files\NotQuickLook.exe", "QuickLook", false)]
    [InlineData(null, "QuickLook", false)]
    [InlineData(@"C:\Program Files\QuickLook\QuickLook.exe", null, false)]
    public void Only_the_expected_executable_and_preview_title_shape_are_candidates(
        string? imagePath,
        string? title,
        bool expected)
    {
        Assert.Equal(expected, QuickLookWindowLocator.IsPreviewIdentity(imagePath, title));
    }
}
