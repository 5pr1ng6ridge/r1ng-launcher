using R1ng.Launcher.Apps;

namespace R1ng.Launcher.Tests;

/// <summary>
/// AppsFolder items expose a mix of raw AppUserModelIds, qualified shell targets, filesystem paths
/// and URIs. Only a raw id may be qualified; everything else must launch as-is.
/// </summary>
public class AppsFolderTargetTests
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]
    public void A_raw_appusermodelid_is_qualified(string aumid) =>
        Assert.Equal($"shell:AppsFolder\\{aumid}", AppsFolderEnumerator.BuildTarget(aumid));

    [Fact]
    public void An_already_qualified_shell_target_is_left_alone() =>
        Assert.Equal(
            "shell:AppsFolder\\Microsoft.Paint_8wekyb3d8bbwe!App",
            AppsFolderEnumerator.BuildTarget("shell:AppsFolder\\Microsoft.Paint_8wekyb3d8bbwe!App")
        );

    [Fact]
    public void An_absolute_filesystem_path_is_left_alone() =>
        Assert.Equal(
            @"C:\Program Files\SomeApp\someapp.exe",
            AppsFolderEnumerator.BuildTarget(@"C:\Program Files\SomeApp\someapp.exe")
        );

    [Theory]
    [InlineData("steam://rungameid/504230")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("ms-settings:")]
    public void A_uri_is_left_alone(string uri) => Assert.Equal(uri, AppsFolderEnumerator.BuildTarget(uri));

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void Unc_and_virtual_shell_paths_are_left_alone(string target) =>
        Assert.Equal(target, AppsFolderEnumerator.BuildTarget(target));

    [Fact]
    public void A_drive_letter_is_not_mistaken_for_a_uri_scheme() =>
        Assert.False(AppsFolderEnumerator.IsUri(@"C:\apps\thing.exe"));

    [Fact]
    public void Blank_targets_are_returned_unchanged() =>
        Assert.Equal(string.Empty, AppsFolderEnumerator.BuildTarget(string.Empty));
}
