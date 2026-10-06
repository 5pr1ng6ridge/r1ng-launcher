using System.IO;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class HiddenResultFilteringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "r1ng-hidden-filter-" + Guid.NewGuid().ToString("N"));
    private readonly HiddenResultStore _store;

    public HiddenResultFilteringTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new HiddenResultStore(Path.Combine(_directory, "hidden.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }

    // ---- ResultRanker -------------------------------------------------------------------------

    [Fact]
    public void Rank_without_a_visibility_filter_behaves_as_before()
    {
        var items = new[]
        {
            Row("app|a", ResultKind.Application, "alpha", 500),
            Row("app|b", ResultKind.Application, "beta", 900),
            Row("file|c", ResultKind.File, "gamma", 100),
        };

        var plain = ResultRanker.Rank(items).Select(item => item.Id).ToArray();

        Assert.Equal(plain, ResultRanker.Rank(items, isVisible: null).Select(item => item.Id));
        Assert.Equal(plain, ResultRanker.Rank(items, isVisible: _ => true).Select(item => item.Id));
        Assert.Equal(new[] { "app|b", "app|a", "file|c" }, plain);
    }

    [Fact]
    public void Rank_applies_the_filter_to_rows_after_equivalent_rows_were_merged()
    {
        var items = new[]
        {
            Row("app|strong", ResultKind.Application, "Notepad", 900, identity: "same", aliases: ["alias-a"]),
            Row("file|weak", ResultKind.File, "notepad.lnk", 100, identity: "same", aliases: ["alias-b"]),
            Row("app|other", ResultKind.Application, "Calc", 500, identity: "calc"),
        };
        var seen = new List<string[]>();

        var ranked = ResultRanker.Rank(items, isVisible: item =>
        {
            seen.Add(item.IdentityAliases.Order(StringComparer.Ordinal).ToArray());
            // Only the weak row knows alias-b; after the merge the surviving row knows it too.
            return !item.IdentityAliases.Contains("alias-b");
        });

        Assert.Equal(new[] { "app|other" }, ranked.Select(item => item.Id));
        Assert.Contains(seen, aliases => aliases.SequenceEqual(new[] { "alias-a", "alias-b" }));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Rank_filters_before_the_cut_so_the_next_best_rows_fill_in()
    {
        var items = Enumerable.Range(0, 5).Select(index => Row($"file|{index}", ResultKind.File, $"f{index}", 500 - index * 100)).ToArray();

        var ranked = ResultRanker.Rank(items, 2, isVisible: item => item.Id is not ("file|0" or "file|1"));

        Assert.Equal(new[] { "file|2", "file|3" }, ranked.Select(item => item.Id));
    }

    // ---- AppSearchProvider (own top 20) -------------------------------------------------------

    [Fact]
    public async Task Hidden_apps_are_skipped_before_the_application_top_twenty()
    {
        var entries = Enumerable.Range(0, 25).Select(index => new AppCatalogEntry
        {
            DisplayName = $"app{index:D2}",
            Target = $@"shell:AppsFolder\app{index:D2}",
            Source = AppSources.AppsFolder,
        }).ToArray();
        var provider = new AppSearchProvider(
            new AppCatalog(() => Array.Empty<ShortcutRoot>(), _ => entries), hiddenResults: _store);

        var before = await provider.SearchAsync("app", CancellationToken.None);
        Assert.Equal(20, before.Count);
        Assert.Equal("app00", before[0].Title);
        Assert.Equal("app19", before[^1].Title);

        foreach (var item in before.Take(3))
            Assert.True(_store.TryHide(item));
        var after = await provider.SearchAsync("app", CancellationToken.None);

        Assert.Equal(20, after.Count);
        Assert.DoesNotContain(after, item => item.Title is "app00" or "app01" or "app02");
        Assert.Equal("app03", after[0].Title);
        Assert.Equal("app22", after[^1].Title);

        Assert.True(_store.TryRestoreAll());
        Assert.Equal("app00", (await provider.SearchAsync("app", CancellationToken.None))[0].Title);
    }

    [Fact]
    public async Task An_application_provider_without_a_store_is_unchanged()
    {
        var entries = new[] { new AppCatalogEntry { DisplayName = "app00", Target = @"shell:AppsFolder\app00", Source = AppSources.AppsFolder } };
        var provider = new AppSearchProvider(new AppCatalog(() => Array.Empty<ShortcutRoot>(), _ => entries));

        Assert.Single(await provider.SearchAsync("app", CancellationToken.None));
    }

    // ---- SearchController (final top 60) ------------------------------------------------------

    [Fact]
    public async Task Hidden_rows_are_skipped_before_the_final_sixty_and_the_next_hits_fill_in()
    {
        var files = new StaticFileProvider
        {
            Hits = Enumerable.Range(0, 100).Select(index => new FileSearchHit($@"C:\hidden-tests\file{index:D3}.txt", false)).ToArray(),
        };
        var controller = new SearchController(new ManualAppProvider(), files, hiddenResults: _store);

        var before = await controller.SearchAsync("file", SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        Assert.Equal(60, before.Items.Count);
        Assert.Equal("file000.txt", before.Items[0].Title);
        Assert.Equal("file059.txt", before.Items[^1].Title);

        foreach (var item in before.Items.Take(5))
            Assert.True(_store.TryHide(item));
        var after = await controller.SearchAsync("file", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(60, after.Items.Count);
        Assert.Equal("file005.txt", after.Items[0].Title);
        Assert.Equal("file064.txt", after.Items[^1].Title);
        Assert.All(before.Items.Take(5), hidden => Assert.DoesNotContain(after.Items, item => item.Id == hidden.Id));
        Assert.Equal(after.Items.Select(item => item.Id), controller.Current.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task The_controller_hides_rows_from_providers_that_do_not_filter_and_applies_to_every_scope()
    {
        var apps = new ManualAppProvider { Immediate = [App("calc", "计算器"), App("notepad", "记事本")] };
        var files = new StaticFileProvider
        {
            Hits = [new FileSearchHit(@"C:\tmp\notes.txt", false), new FileSearchHit(@"C:\tmp\notebook", true)],
        };
        var controller = new SearchController(apps, files, hiddenResults: _store);
        Assert.True(_store.TryHide(App("notepad", "记事本")));
        Assert.True(_store.TryHide(FileHit(@"C:\tmp\notebook", folder: true)));

        var all = await controller.SearchAsync("no", SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        var applications = await controller.SearchAsync("no", SearchScope.Applications, controller.BeginQuery(), CancellationToken.None);
        var filesOnly = await controller.SearchAsync("no", SearchScope.Files, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(new[] { "notes.txt", "计算器" }, all.Items.Select(item => item.Title).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "计算器" }, applications.Items.Select(item => item.Title));
        Assert.Equal(new[] { "notes.txt" }, filesOnly.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task Restoring_makes_a_row_visible_again_and_keeps_history_and_pin_state()
    {
        var history = new FakeHistoryProvider();
        history.Pinned.Add("notepad");
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本"), App("calc", "计算器")] };
        var controller = new SearchController(apps, new StaticFileProvider(), history, hiddenResults: _store);
        var notepad = App("notepad", "记事本");

        Assert.True(_store.TryHide(notepad));
        var hidden = await controller.SearchAsync("a", SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        Assert.DoesNotContain(hidden.Items, item => item.Id == "notepad");

        Assert.True(_store.TryRestore(Assert.Single(_store.GetHidden()).Key));
        var restored = await controller.SearchAsync("a", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        var row = Assert.Single(restored.Items, item => item.Id == "notepad");
        Assert.True(row.IsPinned);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task Empty_input_reads_the_whole_bounded_history_filters_hidden_rows_then_shows_sixty()
    {
        var history = new FakeHistoryProvider();
        foreach (var index in Enumerable.Range(0, 70))
            history.Items.Add(App($"hist{index:D2}", $"历史{index:D2}"));
        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider(), history, hiddenResults: _store);

        var before = await controller.SearchAsync(string.Empty, SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        Assert.Equal(60, before.Items.Count);
        Assert.Equal("hist59", before.Items[^1].Id);

        foreach (var item in before.Items.Take(5))
            Assert.True(_store.TryHide(item));
        var after = await controller.SearchAsync(string.Empty, SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(60, after.Items.Count);
        Assert.Equal("hist05", after.Items[0].Id);
        Assert.Equal("hist64", after.Items[^1].Id);
        Assert.Equal(10_000, after.Items[0].Score);
        Assert.Equal(10_000 - 59, after.Items[^1].Score);
        // Every home request asks for the complete history rather than a pre-cut list.
        Assert.All(history.RequestedCounts, count => Assert.Equal(int.MaxValue, count));
        Assert.NotEmpty(history.RequestedCounts);
        Assert.False(after.IsSearching);
        Assert.Equal(after.Items.Select(item => item.Id), controller.Current.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Empty_input_filters_hidden_history_rows_for_each_scope()
    {
        var history = new FakeHistoryProvider();
        history.Items.Add(App("app-hidden", "隐藏应用"));
        history.Items.Add(App("app-shown", "可见应用"));
        history.Items.Add(FileHit(@"C:\tmp\shown.txt", folder: false));
        history.Items.Add(FileHit(@"C:\tmp\hidden-folder", folder: true));
        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider(), history, hiddenResults: _store);
        Assert.True(_store.TryHide(history.Items[0]));
        Assert.True(_store.TryHide(history.Items[3]));

        var all = await controller.SearchAsync(string.Empty, SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        var applications = await controller.SearchAsync(string.Empty, SearchScope.Applications, controller.BeginQuery(), CancellationToken.None);
        var filesOnly = await controller.SearchAsync(string.Empty, SearchScope.Files, controller.BeginQuery(), CancellationToken.None);

        Assert.Equal(new[] { "app-shown", @"file|C:\tmp\shown.txt" }, all.Items.Select(item => item.Id));
        Assert.Equal(new[] { "app-shown" }, applications.Items.Select(item => item.Id));
        Assert.Equal(new[] { @"file|C:\tmp\shown.txt" }, filesOnly.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task A_searching_snapshot_never_keeps_a_row_hidden_since_it_was_published()
    {
        var apps = new ManualAppProvider { Immediate = [App("notepad", "记事本"), App("calc", "计算器")] };
        var controller = new SearchController(apps, new StaticFileProvider(), hiddenResults: _store);
        var first = await controller.SearchAsync("a", SearchScope.All, controller.BeginQuery(), CancellationToken.None);
        Assert.Equal(2, first.Items.Count);
        var published = new List<SearchSnapshot>();
        controller.Changed += published.Add;

        Assert.True(_store.TryHide(App("notepad", "记事本")));
        var pending = apps.Enqueue();
        var secondRun = controller.SearchAsync("ab", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        // The in-flight snapshot keeps the old rows, minus the one hidden in the meantime.
        Assert.True(controller.Current.IsSearching);
        Assert.Equal(new[] { "calc" }, controller.Current.Items.Select(item => item.Id));
        Assert.Equal(new[] { "calc" }, Assert.Single(published).Items.Select(item => item.Id));

        pending.SetResult([App("notepad", "记事本"), App("calc", "计算器")]);
        var second = await secondRun;

        Assert.Equal(new[] { "calc" }, second.Items.Select(item => item.Id));
        Assert.Equal(new[] { "calc" }, controller.Current.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task A_row_hidden_while_a_query_runs_is_missing_from_the_returned_and_published_snapshot()
    {
        var apps = new ManualAppProvider();
        var controller = new SearchController(apps, new StaticFileProvider(), hiddenResults: _store);
        var pending = apps.Enqueue();
        var run = controller.SearchAsync("a", SearchScope.All, controller.BeginQuery(), CancellationToken.None);

        Assert.True(_store.TryHide(App("notepad", "记事本")));
        pending.SetResult([App("notepad", "记事本"), App("calc", "计算器")]);
        var snapshot = await run;

        Assert.Equal(new[] { "calc" }, snapshot.Items.Select(item => item.Id));
        Assert.Same(snapshot, controller.Current);
    }

    [Fact]
    public void TryPublish_and_FilterVisible_drop_hidden_rows_and_keep_the_same_instance_when_nothing_changes()
    {
        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider(), hiddenResults: _store);
        var visibleOnly = SearchSnapshot.FromItems("q", SearchScope.All, [App("calc", "计算器")]);
        var mixed = SearchSnapshot.FromItems("q", SearchScope.All, [App("notepad", "记事本"), App("calc", "计算器")]) with
        {
            StatusText = "状态",
            FileNotice = "提示",
        };
        Assert.True(_store.TryHide(App("notepad", "记事本")));

        Assert.Same(visibleOnly, controller.FilterVisible(visibleOnly));
        var filtered = controller.FilterVisible(mixed);
        Assert.Equal(new[] { "calc" }, filtered.Items.Select(item => item.Id));
        Assert.Equal("状态", filtered.StatusText);
        Assert.Equal("提示", filtered.FileNotice);
        Assert.True(controller.IsHidden(App("notepad", "记事本")));
        Assert.False(controller.IsHidden(App("calc", "计算器")));

        SearchSnapshot? seen = null;
        controller.Changed += snapshot => seen = snapshot;
        var generation = controller.BeginQuery();
        Assert.True(controller.TryPublish(mixed, generation));
        Assert.Equal(new[] { "calc" }, controller.Current.Items.Select(item => item.Id));
        Assert.Same(controller.Current, seen);

        // A stale generation still publishes nothing.
        controller.BeginQuery();
        Assert.False(controller.TryPublish(visibleOnly, generation));
        Assert.Same(seen, controller.Current);
    }

    [Fact]
    public void A_controller_without_a_store_filters_nothing()
    {
        var controller = new SearchController(new ManualAppProvider(), new StaticFileProvider());
        var snapshot = SearchSnapshot.FromItems("q", SearchScope.All, [App("notepad", "记事本")]);

        Assert.Same(snapshot, controller.FilterVisible(snapshot));
        Assert.False(controller.IsHidden(App("notepad", "记事本")));
        Assert.True(controller.TryPublish(snapshot, controller.BeginQuery()));
        Assert.Same(snapshot, controller.Current);
    }

    private static SearchResultItem Row(string id, ResultKind kind, string title, int score, string? identity = null,
        IReadOnlyList<string>? aliases = null) => new()
    {
        Id = id,
        Kind = kind,
        Title = title,
        TypeLabel = kind == ResultKind.Application ? "应用" : "文件",
        Score = score,
        IdentityKey = identity,
        IdentityAliases = aliases ?? Array.Empty<string>(),
    };

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

    private static SearchResultItem FileHit(string path, bool folder) => FileResultMapper.Map(new FileSearchHit(path, folder), "x")!;

    private sealed class ManualAppProvider : IAppSearchProvider
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<SearchResultItem>>> _pending = new();

        public IReadOnlyList<SearchResultItem> Immediate { get; set; } = Array.Empty<SearchResultItem>();

        public TaskCompletionSource<IReadOnlyList<SearchResultItem>> Enqueue()
        {
            var source = new TaskCompletionSource<IReadOnlyList<SearchResultItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(source);
            return source;
        }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            _pending.Count > 0 ? _pending.Dequeue().Task : Task.FromResult(Immediate);
    }

    private sealed class StaticFileProvider : IFileSearchProvider
    {
        public IReadOnlyList<FileSearchHit> Hits { get; set; } = Array.Empty<FileSearchHit>();

        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Hits));
    }

    private sealed class FakeHistoryProvider : IHistoryProvider
    {
        public List<SearchResultItem> Items { get; } = new();

        public HashSet<string> Pinned { get; } = new();

        public List<int> RequestedCounts { get; } = new();

        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Items.Take(count).ToList();

        public void Record(SearchResultItem item) => Items.Insert(0, item);

        public IReadOnlyList<SearchResultItem> GetHome(SearchScope scope, int count)
        {
            RequestedCounts.Add(count);
            return Items.Where(item => scope == SearchScope.All
                    || (scope == SearchScope.Applications && item.Kind == ResultKind.Application)
                    || (scope == SearchScope.Files && item.Kind is ResultKind.File or ResultKind.Folder))
                .Take(Math.Max(0, count)).ToList();
        }

        public bool IsPinned(SearchResultItem item) => Pinned.Contains(item.Id);
    }
}
