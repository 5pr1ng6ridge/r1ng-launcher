using System.IO;
using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The address shown by a captured Explorer window must be a real physical folder; virtual shell
/// locations, URIs and relative strings are rejected so no path is ever invented.
/// </summary>
public class ExplorerFolderResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-explorer-" + Guid.NewGuid().ToString("N"));

    public ExplorerFolderResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void An_existing_local_folder_is_accepted_without_a_trailing_separator()
    {
        var nested = Path.Combine(_root, "sub");
        Directory.CreateDirectory(nested);

        Assert.Equal(nested, ExplorerFolderResolver.ValidateFolder(nested));
        Assert.Equal(nested, ExplorerFolderResolver.ValidateFolder(nested + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Virtual_uri_relative_and_missing_locations_are_rejected()
    {
        Assert.Null(ExplorerFolderResolver.ValidateFolder("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
        Assert.Null(ExplorerFolderResolver.ValidateFolder("https://example.com"));
        Assert.Null(ExplorerFolderResolver.ValidateFolder("relative"));
        Assert.Null(ExplorerFolderResolver.ValidateFolder(Path.Combine(_root, "does-not-exist")));
        Assert.Null(ExplorerFolderResolver.ValidateFolder(null));
        Assert.Null(ExplorerFolderResolver.ValidateFolder("  "));

        // The resolver shares FolderPrefixQuery's fully-qualified check: drive-relative and
        // root-relative strings are not physical folders even when they name an existing directory.
        Assert.Null(ExplorerFolderResolver.ValidateFolder("C:foo"));
        Assert.Null(ExplorerFolderResolver.ValidateFolder(@"\foo"));
    }

    [Fact]
    public void A_zero_handle_resolves_to_no_folder_immediately()
    {
        Assert.Null(ExplorerFolderResolver.TryResolve(IntPtr.Zero, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void A_drive_root_keeps_its_separator()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var root = Path.GetPathRoot(system);
        if (string.IsNullOrEmpty(root))
            return;

        Assert.Equal(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, ExplorerFolderResolver.ValidateFolder(root));
    }

    [Fact]
    public void A_resolver_that_exceeds_its_bounded_wait_yields_no_folder_and_a_late_completion_is_safe()
    {
        using var gate = new ManualResetEventSlim(false);

        var result = ExplorerFolderResolver.ResolveWith(
            () =>
            {
                gate.Wait(TimeSpan.FromSeconds(2));
                return @"C:\late";
            },
            TimeSpan.FromMilliseconds(30)
        );

        // The caller gets "no folder" and the late worker may finish afterwards without touching a
        // disposed signal.
        Assert.Null(result);
        gate.Set();
    }
}
