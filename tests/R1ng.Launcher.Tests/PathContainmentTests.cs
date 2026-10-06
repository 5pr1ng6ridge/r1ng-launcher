using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The folder boundary helper used by the live Everything integration assertions: it must accept the
/// folder itself and its real descendants but reject siblings (including case-variant siblings) that
/// merely share a name prefix.
/// </summary>
public class PathContainmentTests
{
    [Theory]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32\notepad.exe")]
    [InlineData(@"C:\Windows\system32\", @"C:\Windows\system32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows\system32", @"C:\WINDOWS\SYSTEM32\NOTEPAD.EXE")]
    [InlineData(@"C:\Windows\system32\", @"C:\Windows\System32")]
    public void The_folder_and_its_descendants_are_inside(string folder, string candidate) =>
        Assert.True(PathContainment.IsWithinFolder(folder, candidate));

    [Theory]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32-old")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32-old\notepad.exe")]
    [InlineData(@"C:\Windows\system32", @"C:\WINDOWS\SYSTEM32-OLD\notepad.exe")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32extra\a.txt")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32-")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows")]
    [InlineData(@"C:\Windows\system32", @"C:\Windows\system32x")]
    [InlineData(@"C:\Windows\system32", @"System32\notepad.exe")]
    [InlineData(@"C:\Windows", @"C:\")]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir2\file.txt")]
    [InlineData(@"\\server\share\dir\file.txt", @"\\server\share\dir")]
    [InlineData(@"C:\Windows\system32", "")]
    public void Siblings_and_lookalikes_are_rejected(string folder, string candidate) =>
        Assert.False(PathContainment.IsWithinFolder(folder, candidate));

    [Theory]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir\file.txt")]
    [InlineData(@"\\SERVER\SHARE\DIR", @"\\server\share\dir\file.txt")]
    [InlineData(@"C:\", @"C:\Windows")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\", @"c:\windows\system32")]
    public void Unc_and_drive_roots_use_a_whole_segment_boundary(string folder, string candidate) =>
        Assert.True(PathContainment.IsWithinFolder(folder, candidate));

    [Fact]
    public void Junk_inputs_are_rejected()
    {
        Assert.False(PathContainment.IsWithinFolder("", @"C:\Windows"));
        Assert.False(PathContainment.IsWithinFolder(@"C:\Windows", ""));
        Assert.False(PathContainment.IsWithinFolder("\\", @"C:\Windows"));
    }
}
