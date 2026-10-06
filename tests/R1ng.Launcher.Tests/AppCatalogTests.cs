using System.IO;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class AppCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-tests-" + Guid.NewGuid().ToString("N"));

    public AppCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the test sandbox.
        }
    }

    [Fact]
    public void Scan_returns_sorted_shortcuts_and_ignores_other_files()
    {
        WriteShortcut("工具", "记事本.lnk");
        WriteShortcut("工具", "计算器.lnk");
        File.WriteAllText(Path.Combine(_root, "工具", "readme.txt"), "not a shortcut");
        WriteShortcut("工具/子目录", "微信.lnk");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(Path.Combine(_root, "工具"), AppSources.StartMenu)]);

        Assert.Equal(new[] { "微信", "计算器", "记事本" }, entries.Select(entry => entry.DisplayName));
        Assert.All(entries, entry => Assert.Equal(AppSources.StartMenu, entry.Source));
        Assert.All(entries, entry => Assert.True(File.Exists(entry.Target)));
        Assert.Contains(entries, entry => entry.DisplayName == "微信" && entry.FolderPath!.EndsWith("子目录", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_skips_unreadable_roots_without_throwing()
    {
        var missing = new ShortcutRoot(Path.Combine(_root, "does-not-exist"), AppSources.Desktop);
        var existing = new ShortcutRoot(Path.Combine(_root, "工具"), AppSources.Desktop);
        WriteShortcut("工具", "记事本.lnk");

        var errors = new List<string>();
        var entries = ShortcutScanner.Scan([missing, existing], (path, _) => errors.Add(path));

        Assert.Single(entries);
        Assert.Single(errors);
    }

    [Fact]
    public void GetRoots_keeps_existing_directories_only_and_deduplicates_paths()
    {
        var startMenu = Path.Combine(_root, "start-menu");
        var desktop = Path.Combine(_root, "desktop");
        Directory.CreateDirectory(startMenu);
        Directory.CreateDirectory(desktop);

        var roots = AppFolders.GetRoots(
            folder => folder switch
            {
                Environment.SpecialFolder.StartMenu => startMenu,
                Environment.SpecialFolder.CommonStartMenu => startMenu,
                Environment.SpecialFolder.DesktopDirectory => desktop,
                Environment.SpecialFolder.CommonDesktopDirectory => Path.Combine(_root, "missing"),
                _ => Path.Combine(_root, "missing"),
            }
        );

        Assert.Equal(2, roots.Count);
        Assert.Equal(AppSources.StartMenu, roots[0].Source);
        Assert.Equal(AppSources.Desktop, roots[1].Source);
    }

    [Fact]
    public async Task Catalog_is_cached_and_invalidated_explicitly()
    {
        WriteShortcut("工具", "记事本.lnk");
        var scans = 0;
        var catalog = new AppCatalog(
            () =>
            {
                scans++;
                return [new ShortcutRoot(Path.Combine(_root, "工具"), AppSources.StartMenu)];
            },
            _ => Array.Empty<AppCatalogEntry>(),
            () => DateTime.UtcNow,
            TimeSpan.FromMinutes(15)
        );

        var first = await catalog.GetAsync();
        var second = await catalog.GetAsync();

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, scans);

        catalog.Invalidate();
        await catalog.GetAsync();
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task Catalog_keeps_same_named_shortcuts_and_appsfolder_entries_when_equivalence_is_unknown()
    {
        WriteShortcut("工具", "记事本.lnk");

        var catalog = new AppCatalog(
            () => [new ShortcutRoot(Path.Combine(_root, "工具"), AppSources.StartMenu)],
            _ =>
            [
                new AppCatalogEntry
                {
                    DisplayName = "记事本",
                    Target = "shell:AppsFolder\\Microsoft.Notepad_8wekyb3d8bbwe!App",
                    Source = AppSources.AppsFolder,
                },
                new AppCatalogEntry
                {
                    DisplayName = "画图",
                    Target = "shell:AppsFolder\\Microsoft.Paint_8wekyb3d8bbwe!App",
                    Source = AppSources.AppsFolder,
                },
            ]
        );

        var entries = await catalog.GetAsync();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, entry => entry.DisplayName == "记事本" && entry.Source == AppSources.AppsFolder);
        Assert.Contains(entries, entry => entry.DisplayName == "记事本" && entry.Source == AppSources.StartMenu);
        Assert.Contains(entries, entry => entry.DisplayName == "画图" && entry.Source == AppSources.AppsFolder);
    }

    [Fact]
    public async Task A_cancelled_waiter_never_orphans_the_in_flight_build()
    {
        WriteShortcut("工具", "记事本.lnk");
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var scans = 0;

        var catalog = new AppCatalog(
            () =>
            {
                Interlocked.Increment(ref scans);
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return [new ShortcutRoot(Path.Combine(_root, "工具"), AppSources.StartMenu)];
            },
            _ => Array.Empty<AppCatalogEntry>()
        );

        using var cancellation = new CancellationTokenSource();
        var first = catalog.GetAsync(cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the catalog build never started");

        // The first waiter gives up mid-build. The build itself must keep running and stay shared.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = catalog.GetAsync();
        try
        {
            // A second waiter must reuse the running build instead of starting a duplicate scan.
            Assert.Equal(1, Volatile.Read(ref scans));
        }
        finally
        {
            release.Set();
        }

        var entries = await second;
        Assert.Single(entries);
        Assert.Equal(1, Volatile.Read(ref scans));

        // The finished snapshot is reused by later callers too.
        await catalog.GetAsync();
        Assert.Equal(1, Volatile.Read(ref scans));
    }

    [Fact]
    public async Task Catalog_never_throws_when_a_scan_fails()
    {
        var catalog = new AppCatalog(
            () => throw new InvalidOperationException("boom"),
            _ => throw new InvalidOperationException("boom"),
            onError: (_, _) => { }
        );

        var entries = await catalog.GetAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public void AppsFolder_targets_are_qualified_or_left_as_shell_paths()
    {
        Assert.Equal(
            "shell:AppsFolder\\Microsoft.Notepad_8wekyb3d8bbwe!App",
            AppsFolderEnumerator.BuildTarget("Microsoft.Notepad_8wekyb3d8bbwe!App")
        );
        Assert.Equal(
            "shell:AppsFolder\\Microsoft.Notepad_8wekyb3d8bbwe!App",
            AppsFolderEnumerator.BuildTarget("shell:AppsFolder\\Microsoft.Notepad_8wekyb3d8bbwe!App")
        );
    }

    private void WriteShortcut(string relativeDirectory, string fileName)
    {
        var directory = Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "stub shortcut");
    }
}
