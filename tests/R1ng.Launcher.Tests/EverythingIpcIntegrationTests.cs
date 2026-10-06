using System.IO;
using System.Text.RegularExpressions;
using R1ng.Launcher.Files;
using R1ng.Launcher.Search;
using Xunit.Abstractions;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Exercises the real Everything IPC client. On machines without a running Everything the contract
/// under test is the explicit unavailable state; when Everything is present the same test performs a
/// real query round-trip.
/// </summary>
public class EverythingIpcIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public EverythingIpcIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe_always_reports_a_reason_and_query_round_trips_when_available()
    {
        using var client = new EverythingIpcClient(TimeSpan.FromSeconds(3));

        var availability = client.Probe();
        _output.WriteLine($"probe: available={availability.Available} message={availability.Message}");

        Assert.False(string.IsNullOrWhiteSpace(availability.Message));

        if (!availability.Available)
            return;

        var hits = await client.QueryAsync("explorer.exe", 5, CancellationToken.None);
        _output.WriteLine($"hits: {hits.Count}");

        Assert.NotNull(hits);
        Assert.All(hits, hit => Assert.False(string.IsNullOrWhiteSpace(hit.FullPath)));
    }

    [Fact]
    public async Task A_live_folder_constrained_query_stays_inside_the_folder_when_everything_is_available()
    {
        using var client = new EverythingIpcClient(TimeSpan.FromSeconds(3));
        var availability = client.Probe();
        _output.WriteLine($"folder probe: available={availability.Available} message={availability.Message}");
        if (!availability.Available)
            return;

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var built = FileQueryBuilder.Build(folder, "notepad", Core.FileSearchOptions.Default);
        _output.WriteLine($"everything query: {built.Text}");

        var hits = await client.QueryAsync(built.Text, 20, CancellationToken.None, built.EffectiveOptions);
        _output.WriteLine($"folder hits: {hits.Count}");

        // Everything must return at most the folder's subtree; if the syntax is unsupported by an
        // older build it may return nothing, which is still an honest (non-stale) result. The
        // boundary check treats the folder itself as inside but rejects a sibling such as
        // C:\Windows\system32-old, and every hit must actually match the "notepad" name.
        foreach (var hit in hits)
        {
            Assert.True(
                PathContainment.IsWithinFolder(folder, hit.FullPath),
                $"'{hit.FullPath}' must be inside the folder '{folder}'"
            );
            Assert.Contains("notepad", Path.GetFileName(hit.FullPath), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_live_folder_constrained_regex_query_stays_inside_the_folder_when_everything_is_available()
    {
        using var client = new EverythingIpcClient(TimeSpan.FromSeconds(3));
        var availability = client.Probe();
        _output.WriteLine($"regex folder probe: available={availability.Available} message={availability.Message}");
        if (!availability.Available)
            return;

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // A regex plus every toggle on: the visible toggles stay on and the folder-constrained query
        // encodes the regex with the documented regex: function.
        var built = FileQueryBuilder.Build(
            folder,
            @"^notepad.*\.exe$",
            new Core.FileSearchOptions
            {
                Regex = true,
                MatchCase = false,
                MatchPath = true,
                MatchWholeWord = true,
            }
        );
        _output.WriteLine($"everything regex query: {built.Text}");

        var hits = await client.QueryAsync(built.Text, 20, CancellationToken.None, built.EffectiveOptions);
        _output.WriteLine($"regex folder hits: {hits.Count}");

        // Real Everything 1.4.1 resolves this to exactly the folder's notepad.exe; every hit must
        // stay inside the folder and its basename must match the regex.
        Assert.NotEmpty(hits);
        var pattern = new Regex(@"^notepad.*\.exe$", RegexOptions.IgnoreCase);
        foreach (var hit in hits)
        {
            Assert.True(
                PathContainment.IsWithinFolder(folder, hit.FullPath),
                $"'{hit.FullPath}' must be inside the folder '{folder}'"
            );
            Assert.Matches(pattern, Path.GetFileName(hit.FullPath));
        }
    }

    [Fact]
    public async Task Query_with_a_cancelled_token_completes_without_hanging()
    {
        using var client = new EverythingIpcClient(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var hits = await client.QueryAsync("explorer.exe", 5, cancellation.Token);

        Assert.NotNull(hits);
    }

    [Fact]
    public async Task Provider_applies_a_live_folder_constraint_when_everything_is_available()
    {
        using var provider = new EverythingFileSearchProvider(new EverythingIpcClient(TimeSpan.FromSeconds(3)));
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.System);

        var outcome = await provider.SearchAsync("notepad", CancellationToken.None, default, folder);
        _output.WriteLine($"provider folder: available={outcome.Availability.Available} hits={outcome.Hits.Count}");

        if (!outcome.Availability.Available)
            return;

        foreach (var hit in outcome.Hits)
        {
            Assert.True(
                PathContainment.IsWithinFolder(folder, hit.FullPath),
                $"'{hit.FullPath}' must be inside the folder '{folder}'"
            );
            Assert.Contains("notepad", Path.GetFileName(hit.FullPath), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Provider_reports_an_explicit_state_and_maps_real_hits()
    {
        using var provider = new EverythingFileSearchProvider(new EverythingIpcClient(TimeSpan.FromSeconds(3)));

        var outcome = await provider.SearchAsync("explorer.exe", CancellationToken.None);
        _output.WriteLine($"provider: available={outcome.Availability.Available} message={outcome.Availability.Message} hits={outcome.Hits.Count}");

        if (!outcome.Availability.Available)
        {
            Assert.Empty(outcome.Hits);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Availability.Message));
            return;
        }

        Assert.NotEmpty(outcome.Hits);
        Assert.All(outcome.Hits, hit => Assert.True(hit.FullPath.Length > 0));
    }
}
