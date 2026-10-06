using R1ng.Launcher.Core;
using R1ng.Launcher.Files;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class EverythingFileSearchProviderTests
{
    [Fact]
    public async Task Quick_toggle_options_are_forwarded_to_the_backend()
    {
        var client = new StubClient();
        using var provider = new EverythingFileSearchProvider(client, () => DateTime.UtcNow);
        var options = new FileSearchOptions
        {
            MatchCase = true,
            MatchWholeWord = true,
            MatchPath = true,
            Regex = false,
        };

        await provider.SearchAsync("report", CancellationToken.None, options);

        Assert.Equal(new[] { options }, client.Options);
    }

    [Fact]
    public async Task Unavailable_backend_reports_its_reason_without_querying()
    {
        var client = new StubClient
        {
            Availability = FileSearchAvailability.Unavailable("Everything 未运行"),
        };
        using var provider = new EverythingFileSearchProvider(client, () => DateTime.UtcNow);

        var outcome = await provider.SearchAsync("report", CancellationToken.None);

        Assert.False(outcome.Availability.Available);
        Assert.Equal("Everything 未运行", outcome.Availability.Message);
        Assert.Empty(outcome.Hits);
        Assert.Empty(client.Queries);
    }

    [Fact]
    public async Task Short_queries_are_rejected_with_a_hint()
    {
        var client = new StubClient();
        using var provider = new EverythingFileSearchProvider(client, () => DateTime.UtcNow);

        var outcome = await provider.SearchAsync("a", CancellationToken.None);

        Assert.Empty(outcome.Hits);
        Assert.NotNull(outcome.StatusText);
        Assert.Contains("2", outcome.StatusText!);
        Assert.Empty(client.Queries);
    }

    [Fact]
    public async Task Available_backend_returns_mapped_hits()
    {
        var client = new StubClient
        {
            Items =
            [
                new EverythingRawItem(@"C:\tmp\report.docx", 0),
                new EverythingRawItem(@"C:\tmp\projects", EverythingProtocol.ItemFlagFolder),
            ],
        };
        using var provider = new EverythingFileSearchProvider(client, () => DateTime.UtcNow);

        var outcome = await provider.SearchAsync("report", CancellationToken.None);

        Assert.True(outcome.Availability.Available);
        Assert.Equal(2, outcome.Hits.Count);
        Assert.False(outcome.Hits[0].IsFolder);
        Assert.True(outcome.Hits[1].IsFolder);
        Assert.Equal(new[] { "report" }, client.Queries);
    }

    [Fact]
    public async Task Availability_probe_is_cached_until_invalidated()
    {
        var now = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var client = new StubClient();
        using var provider = new EverythingFileSearchProvider(
            client,
            () => now,
            TimeSpan.FromSeconds(10)
        );

        await provider.SearchAsync("report", CancellationToken.None);
        await provider.SearchAsync("report", CancellationToken.None);
        Assert.Equal(1, client.ProbeCalls);

        provider.InvalidateAvailability();
        await provider.SearchAsync("report", CancellationToken.None);
        Assert.Equal(2, client.ProbeCalls);

        // The cache also expires with the clock.
        now = now.AddSeconds(30);
        await provider.SearchAsync("report", CancellationToken.None);
        Assert.Equal(3, client.ProbeCalls);
    }

    [Fact]
    public async Task Blank_paths_returned_by_the_backend_are_ignored()
    {
        var client = new StubClient { Items = [new EverythingRawItem("   ", 0)] };
        using var provider = new EverythingFileSearchProvider(client, () => DateTime.UtcNow);

        var outcome = await provider.SearchAsync("report", CancellationToken.None);

        Assert.Empty(outcome.Hits);
    }

    private sealed class StubClient : IEverythingQueryClient
    {
        public FileSearchAvailability Availability { get; set; } = FileSearchAvailability.Ready("Everything 1.4.1.1000");

        public IReadOnlyList<EverythingRawItem> Items { get; set; } = Array.Empty<EverythingRawItem>();

        public List<string> Queries { get; } = new();

        public List<FileSearchOptions> Options { get; } = new();

        public int ProbeCalls { get; private set; }

        public FileSearchAvailability Probe()
        {
            ProbeCalls++;
            return Availability;
        }

        public Task<IReadOnlyList<EverythingRawItem>> QueryAsync(
            string search,
            int maxResults,
            CancellationToken cancellationToken,
            FileSearchOptions options = default
        )
        {
            Queries.Add(search);
            Options.Add(options);
            return Task.FromResult(Items);
        }
    }
}
