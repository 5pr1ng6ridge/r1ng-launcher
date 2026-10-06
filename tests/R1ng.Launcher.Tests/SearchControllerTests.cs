using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class SearchControllerTests
{
    [Fact]
    public async Task Keeps_previous_rows_visible_while_the_next_query_runs()
    {
        var apps = new ManualAppProvider();
        var controller = new SearchController(apps, new StaticFileProvider());

        var first = apps.Enqueue();
        var firstRun = controller.SearchAsync("no", SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        first.SetResult([App("notepad", "记事本")]);
        var firstSnapshot = await firstRun;

        Assert.Single(firstSnapshot.Items);
        Assert.False(controller.Current.IsSearching);

        var second = apps.Enqueue();
        var secondRun = controller.SearchAsync("note", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        // The in-flight snapshot keeps the old rows and only flags that a search is running.
        Assert.True(controller.Current.IsSearching);
        Assert.Equal(new[] { "notepad" }, controller.Current.Items.Select(item => item.Id));

        second.SetResult([App("notepad", "记事本"), App("notepad2", "记事本2")]);
        var secondSnapshot = await secondRun;

        Assert.Equal(2, secondSnapshot.Items.Count);
        Assert.False(controller.Current.IsSearching);
    }

    [Fact]
    public async Task Stale_results_are_never_published()
    {
        var apps = new ManualAppProvider();
        var controller = new SearchController(apps, new StaticFileProvider());

        var slow = apps.Enqueue();
        var staleGeneration = controller.BeginQuery();
        var staleRun = controller.SearchAsync("a", SearchScope.All, staleGeneration, CancellationToken.None);

        var fresh = apps.Enqueue();
        var freshGeneration = controller.BeginQuery();
        var freshRun = controller.SearchAsync("ab", SearchScope.All, freshGeneration, CancellationToken.None);

        fresh.SetResult([App("fresh", "新结果")]);
        await freshRun;
        Assert.Equal("ab", controller.Current.Query);

        // The older query finishes later and must be dropped.
        slow.SetResult([App("stale", "旧结果")]);
        var staleSnapshot = await staleRun;

        Assert.Equal("ab", controller.Current.Query);
        Assert.Equal(new[] { "fresh" }, controller.Current.Items.Select(item => item.Id));
        Assert.False(controller.IsCurrent(staleGeneration));
        Assert.Contains("旧结果", staleSnapshot.Items.Select(item => item.Title));
    }

    [Fact]
    public void TryPublish_ignores_outdated_generations()
    {
        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider());

        var generation = controller.BeginQuery();
        controller.BeginQuery();

        Assert.False(controller.TryPublish(SearchSnapshot.Idle, generation));
        Assert.Same(SearchSnapshot.Idle, controller.Current);
    }

    [Fact]
    public async Task Quick_toggle_options_are_passed_to_the_file_provider_only()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "Notepad")] };
        var files = new StaticFileProvider();
        var controller = new SearchController(apps, files);
        var options = new FileSearchOptions { MatchCase = true, MatchPath = true, Regex = true };

        await controller.SearchAsync("note", SearchScope.All, controller.BeginQuery(), CancellationToken.None, options);

        Assert.Equal(new[] { options }, files.Options);
    }

    [Fact]
    public async Task Application_scope_never_queries_files_with_options()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本")] };
        var files = new StaticFileProvider();
        var controller = new SearchController(apps, files);

        await controller.SearchAsync(
            "note",
            SearchScope.Applications,
            controller.BeginQuery(),
            CancellationToken.None,
            new FileSearchOptions { MatchCase = true }
        );

        Assert.Equal(0, files.Calls);
        Assert.Empty(files.Options);
    }

    [Fact]
    public async Task File_scope_skips_applications_and_surfaces_the_backend_notice()
    {
        var apps = new ManualAppProvider();
        var files = new StaticFileProvider { Availability = FileSearchAvailability.Unavailable("Everything 未运行") };
        var controller = new SearchController(apps, files);

        var snapshot = await controller.SearchAsync("report", SearchScope.Files, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(0, apps.Calls);
        Assert.Empty(snapshot.Items);
        Assert.True(snapshot.FilesUnavailable);
        Assert.Equal("Everything 未运行", snapshot.FileNotice);
    }

    [Fact]
    public async Task Application_scope_skips_file_search()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本")] };
        var files = new StaticFileProvider();
        var controller = new SearchController(apps, files);

        var snapshot = await controller.SearchAsync("note", SearchScope.Applications, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(0, files.Calls);
        Assert.Single(snapshot.Items);
        Assert.False(snapshot.FilesUnavailable);
    }

    [Fact]
    public async Task Combined_search_merges_apps_and_files_into_one_ranked_list()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "Notepad")] };
        var files = new StaticFileProvider
        {
            Hits =
            [
                new FileSearchHit(@"C:\tmp\notepad-notes.txt", false),
                new FileSearchHit(@"C:\tmp\notes", true),
            ],
        };
        var controller = new SearchController(apps, files);

        var snapshot = await controller.SearchAsync("note", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal(ResultKind.Application, snapshot.Items[0].Kind);
        Assert.Contains(snapshot.Items, item => item.Kind == ResultKind.Folder);
    }

    [Fact]
    public async Task A_folder_prefix_constrains_files_and_keeps_terms_for_matching()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本")] };
        var files = new StaticFileProvider
        {
            Hits = [new FileSearchHit(@"C:\folder\report.txt", false)],
        };
        var controller = new SearchController(apps, files);

        var snapshot = await controller.SearchAsync(
            "\"C:\\folder\" report",
            SearchScope.All,
            controller.BeginQuery(),
            CancellationToken.None
        );

        // The file backend gets the terms plus a separate literal folder constraint, never the raw
        // prefixed text; app matching uses the terms too.
        Assert.Equal(new[] { "report" }, files.Queries);
        Assert.Equal(new[] { @"C:\folder" }, files.FolderConstraints);
        Assert.Equal(new[] { "report" }, apps.Queries);

        // Highlighting uses the terms, so the file row is not scored against the path.
        Assert.Contains(snapshot.Items, item => item.Kind == ResultKind.File && item.Title == "report.txt");
    }

    [Fact]
    public async Task A_drive_relative_quote_is_not_a_folder_constraint_and_does_not_strip_keywords()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本")] };
        var files = new StaticFileProvider();
        var controller = new SearchController(apps, files);

        const string text = "\"C:foo\" report";
        await controller.SearchAsync(text, SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        // "C:foo" is drive-relative, so no folder constraint is invented...
        Assert.Equal(new string?[] { null }, files.FolderConstraints);

        // ...and the whole quoted text stays the query exactly once: the keywords are never stripped
        // and the fake prefix is never treated as a path.
        Assert.Equal(new[] { text }, files.Queries);
        Assert.Equal(new[] { text }, apps.Queries);
    }

    [Fact]
    public async Task Empty_query_lists_history_in_recency_order()
    {
        var history = new FakeHistoryProvider();
        history.Items.Add(App("recent", "最近"));
        history.Items.Add(App("older", "较早"));

        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider(), history);

        var snapshot = await controller.SearchAsync(string.Empty, SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(new[] { "recent", "older" }, snapshot.Items.Select(item => item.Id));
        Assert.False(snapshot.IsSearching);
    }

    [Fact]
    public async Task Empty_query_with_an_application_scope_filters_out_file_history()
    {
        var history = new FakeHistoryProvider();
        history.Items.Add(App("app-entry", "应用"));
        history.Items.Add(new SearchResultItem
        {
            Id = "file|C:\\tmp\\a.txt",
            Kind = ResultKind.File,
            Title = "a.txt",
            TypeLabel = "文件",
            Target = @"C:\tmp\a.txt",
        });

        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider(), history);

        var snapshot = await controller.SearchAsync(string.Empty, SearchScope.Applications, controller.BeginQuery(), CancellationToken.None);

        Assert.Single(snapshot.Items);
        Assert.Equal(ResultKind.Application, snapshot.Items[0].Kind);
    }

    private static SearchResultItem App(string id, string title) => new()
    {
        Id = id,
        Kind = ResultKind.Application,
        Title = title,
        TypeLabel = "应用",
        Target = $@"C:\apps\{id}.lnk",
        Source = AppSources.StartMenu,
        Score = NameMatcher.PrefixScore,
    };

    private sealed class ManualAppProvider : IAppSearchProvider
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<SearchResultItem>>> _pending = new();

        public IReadOnlyList<SearchResultItem> Immediate { get; set; } = Array.Empty<SearchResultItem>();

        public int Calls { get; private set; }

        public List<string> Queries { get; } = new();

        public TaskCompletionSource<IReadOnlyList<SearchResultItem>> Enqueue()
        {
            var source = new TaskCompletionSource<IReadOnlyList<SearchResultItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(source);
            return source;
        }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            Queries.Add(query);
            if (_pending.Count > 0)
                return _pending.Dequeue().Task;

            return Task.FromResult(Immediate);
        }
    }

    private sealed class StaticFileProvider : IFileSearchProvider
    {
        public FileSearchAvailability Availability { get; set; } = FileSearchAvailability.Ready();

        public IReadOnlyList<FileSearchHit> Hits { get; set; } = Array.Empty<FileSearchHit>();

        public int Calls { get; private set; }

        public List<FileSearchOptions> Options { get; } = new();

        public List<string?> FolderConstraints { get; } = new();

        public List<string> Queries { get; } = new();

        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        )
        {
            Calls++;
            Options.Add(options);
            FolderConstraints.Add(folderConstraint);
            Queries.Add(query);
            return Task.FromResult(new FileSearchOutcome(Availability, Hits));
        }
    }

    private sealed class FakeHistoryProvider : IHistoryProvider
    {
        public List<SearchResultItem> Items { get; } = new();

        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Items.Take(count).ToList();

        public void Record(SearchResultItem item) => Items.Insert(0, item);
    }
}
