using System.IO;
using System.Windows.Threading;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public sealed class LauncherMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-memory-" + Guid.NewGuid().ToString("N"));
    public LauncherMemoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string HistoryPath => Path.Combine(_root, "history.json");
    private string PreferencePath => Path.Combine(_root, "preferences.json");

    [Fact]
    public void Same_named_files_in_different_folders_do_not_replace_each_other()
    {
        var store = new SearchHistoryStore(HistoryPath);
        store.Record(Item(1) with { Kind = ResultKind.File, Title = "report.txt", Target = @"C:\one\report.txt" });
        store.Record(Item(2) with { Kind = ResultKind.File, Title = "report.txt", Target = @"C:\two\report.txt" });
        Assert.Equal(2, new SearchHistoryStore(HistoryPath).GetRecent(60).Count);
    }

    [Fact]
    public void Pinning_without_launching_survives_restart_and_unpinning_does_not_invent_history()
    {
        var store = new SearchHistoryStore(HistoryPath);
        Assert.True(store.TrySetPinned(Item(1), true));
        var reloaded = new SearchHistoryStore(HistoryPath);
        var row = Assert.Single(reloaded.GetHome(SearchScope.All, 60));
        Assert.True(row.IsPinned);
        Assert.Equal(AppSources.Pinned, row.Source);
        Assert.Empty(reloaded.GetRecent(60));
        Assert.True(reloaded.TrySetPinned(row, false));
        Assert.Empty(new SearchHistoryStore(HistoryPath).GetHome(SearchScope.All, 60));
    }

    [Fact]
    public void Pins_have_independent_capacity_and_opening_preserves_pin_order()
    {
        var now = DateTime.UtcNow;
        var store = new SearchHistoryStore(HistoryPath, () => now, capacity: 3);
        Assert.True(store.TrySetPinned(Item(1), true));
        now = now.AddMinutes(1);
        Assert.True(store.TrySetPinned(Item(2), true));
        for (var i = 10; i < 20; i++)
        {
            now = now.AddMinutes(1);
            store.Record(Item(i));
        }
        store.Record(Item(1));
        var home = store.GetHome(SearchScope.All, 60);
        Assert.Equal(new[] { Item(2).Target, Item(1).Target }, home.Take(2).Select(item => item.Target));
        Assert.Equal(2, home.Count(item => item.IsPinned));
        Assert.Equal(3, store.GetRecent(60).Count);
        Assert.True(store.TrySetPinned(Item(1), false));
        Assert.Contains(store.GetRecent(60), item => item.Target == Item(1).Target && !item.IsPinned);
    }

    [Fact]
    public void Full_pin_capacity_rejects_a_new_pin_without_silently_evicting_an_existing_one()
    {
        var store = new SearchHistoryStore(HistoryPath);
        for (var i = 0; i < SearchHistoryStore.MaxPins; i++)
            Assert.True(store.TrySetPinned(Item(i), true));
        Assert.False(store.TrySetPinned(Item(99), true));
        Assert.Equal(SearchHistoryStore.MaxPins, new SearchHistoryStore(HistoryPath).GetHome(SearchScope.All, 60).Count);
        Assert.True(store.IsPinned(Item(0)));
        Assert.True(store.TrySetPinned(Item(0), false));
        Assert.True(store.TrySetPinned(Item(99), true));
    }

    [Fact]
    public void Home_filters_scope_before_the_display_limit_and_keeps_icon_metadata()
    {
        var store = new SearchHistoryStore(HistoryPath);
        var file = Item(0) with { Kind = ResultKind.File, IconPath = @"C:\icons\real.ico", IconIndex = 2 };
        Assert.True(store.TrySetPinned(file, true));
        for (var i = 1; i < 30; i++)
            Assert.True(store.TrySetPinned(Item(i), true));
        var row = Assert.Single(new SearchHistoryStore(HistoryPath).GetHome(SearchScope.Files, 1));
        Assert.Equal(file.Target, row.Target);
        Assert.Equal(file.IconPath, row.IconPath);
        Assert.Equal(file.IconIndex, row.IconIndex);
    }

    [Fact]
    public void Verified_shortcut_aliases_reconcile_legacy_history_without_title_matching()
    {
        var store = new SearchHistoryStore(HistoryPath);
        var legacy = Item(1);
        store.Record(legacy);
        Assert.True(store.TrySetPinned(legacy, true));
        var representative = Item(2) with
        {
            IdentityKey = LaunchIdentity.ForShortcut(@"C:\apps\app.exe", "", @"C:\apps", 1, false),
            IdentityAliases = [LaunchIdentity.ForTarget(legacy.Target), LaunchIdentity.ForTarget(Item(2).Target)],
        };
        Assert.True(store.IsPinned(representative));
        store.Record(representative);
        var row = Assert.Single(new SearchHistoryStore(HistoryPath).GetHome(SearchScope.All, 60));
        Assert.True(row.IsPinned);
        Assert.Equal(representative.IdentityKey, row.IdentityKey);
        Assert.Equal(representative.Target, row.Target);
    }

    [Fact]
    public void Legacy_home_merges_verified_shortcut_copies_before_any_catalog_search()
    {
        var first = Path.Combine(_root, "One.lnk");
        var second = Path.Combine(_root, "Copy.lnk");
        var other = Path.Combine(_root, "Other.lnk");
        File.WriteAllBytes(first, ShortcutIdentityTests.Link(@"C:\Apps\app.exe", "--one", @"C:\Apps"));
        File.WriteAllBytes(second, File.ReadAllBytes(first));
        File.WriteAllBytes(other, ShortcutIdentityTests.Link(@"C:\Apps\app.exe", "--two", @"C:\Apps"));
        var now = DateTime.UtcNow;
        File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new SearchHistoryStore.HistoryRecord { Title = "One", Target = first, LastUsedUtc = now.AddMinutes(-1), PinnedAtUtc = now },
            new SearchHistoryStore.HistoryRecord { Title = "Copy", Target = second, LastUsedUtc = now },
            new SearchHistoryStore.HistoryRecord { Title = "Other", Target = other, LastUsedUtc = now },
        }));
        var store = new SearchHistoryStore(HistoryPath);
        var home = store.GetHome(SearchScope.All, 60);
        Assert.Equal(2, home.Count);
        Assert.True(home[0].IsPinned);
        Assert.Equal(2, store.GetRecent(60).Count);
        Assert.True(store.TrySetPinned(home[0], false));
        Assert.Equal(2, new SearchHistoryStore(HistoryPath).GetRecent(60).Count);
        Assert.Equal(3, Directory.GetFiles(_root, "*.lnk").Length);
    }

    [Fact]
    public void Pin_save_failure_does_not_report_success_or_change_in_memory_state()
    {
        Directory.CreateDirectory(HistoryPath); // The file destination is deliberately unwritable.
        var store = new SearchHistoryStore(HistoryPath);
        Assert.False(store.TrySetPinned(Item(1), true));
        Assert.False(store.IsPinned(Item(1)));
        Assert.Empty(store.GetHome(SearchScope.All, 60));
    }

    [Fact]
    public void Preferences_round_trip_independently_of_recent_history_and_rank_by_frequency_then_recency()
    {
        var now = DateTime.UtcNow;
        var context = SearchContext.Create("app", SearchScope.All, default)!;
        var store = new QueryPreferenceStore(PreferencePath, () => now);
        store.Record(context, Item(1));
        store.Record(context, Item(1));
        now = now.AddMinutes(1);
        store.Record(context, Item(2));
        var preferences = new QueryPreferenceStore(PreferencePath).GetPreferences(context);
        var ranked = ResultRanker.Rank(new[] { Item(0) with { Score = 10000 }, Item(2), Item(1) }, preferences: preferences);
        Assert.Equal(new[] { Item(1).Target, Item(2).Target, Item(0).Target }, ranked.Select(item => item.Target));
        now = now.AddMinutes(1);
        store.Record(context, Item(2));
        Assert.Equal(Item(2).Target, ResultRanker.Rank(new[] { Item(1), Item(2) }, preferences: store.GetPreferences(context))[0].Target);
        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public void Context_keys_preserve_scope_options_folder_regex_and_internal_whitespace()
    {
        var plain = SearchContext.Create(" app ", SearchScope.All, default);
        Assert.Equal(plain, SearchContext.Create("APP", SearchScope.All, default));
        Assert.NotEqual(plain, SearchContext.Create("app", SearchScope.Applications, default));
        Assert.NotEqual(plain, SearchContext.Create("app", SearchScope.All, new() { MatchPath = true }));
        Assert.NotEqual(plain, SearchContext.Create("app", SearchScope.All, new() { MatchCase = true }));
        Assert.NotEqual(SearchContext.Create("app", SearchScope.All, new() { Regex = true }), SearchContext.Create("APP", SearchScope.All, new() { Regex = true }));
        Assert.NotEqual(SearchContext.Create("app", SearchScope.All, new() { MatchCase = true }), SearchContext.Create("APP", SearchScope.All, new() { MatchCase = true }));
        Assert.NotEqual(SearchContext.Create("a b", SearchScope.All, default), SearchContext.Create("a  b", SearchScope.All, default));
        Assert.NotEqual(SearchContext.Create("\"C:\\one\" app", SearchScope.Files, default), SearchContext.Create("\"C:\\two\" app", SearchScope.Files, default));
        Assert.Null(SearchContext.Create(" ", SearchScope.All, default));
    }

    [Fact]
    public void Preferences_have_stable_query_and_result_eviction_and_ignore_corrupt_files()
    {
        File.WriteAllText(PreferencePath, "{ broken");
        var now = DateTime.UtcNow;
        var store = new QueryPreferenceStore(PreferencePath, () => now, queryCapacity: 2, resultCapacity: 2);
        var first = SearchContext.Create("first", SearchScope.All, default)!;
        for (var i = 0; i < 3; i++)
        {
            now = now.AddMinutes(1);
            store.Record(first, Item(i));
        }
        Assert.False(store.GetPreferences(first).ContainsKey(LaunchIdentity.ForItem(Item(0))));
        now = now.AddMinutes(1);
        store.Record(SearchContext.Create("second", SearchScope.All, default), Item(2));
        now = now.AddMinutes(1);
        store.Record(SearchContext.Create("third", SearchScope.All, default), Item(2));
        Assert.Empty(new QueryPreferenceStore(PreferencePath, queryCapacity: 2).GetPreferences(first));
    }

    [Fact]
    public async Task Remembered_application_participates_before_the_provider_top_twenty_limit()
    {
        var entries = Enumerable.Range(0, 35).Select(i => new AppCatalogEntry
        {
            DisplayName = $"App {i:D2}", Target = $@"C:\Apps\{i:D2}.exe", Source = AppSources.StartMenu,
        }).ToArray();
        var provider = new AppSearchProvider(new AppCatalog(() => [], _ => entries));
        var context = SearchContext.Create("App", SearchScope.Applications, default)!;
        var store = new QueryPreferenceStore(PreferencePath);
        var remembered = Item(34) with { Target = entries[34].Target };
        store.Record(context, remembered);
        var results = await provider.SearchAsync("App", default, store.GetPreferences(context));
        Assert.Equal(20, results.Count);
        Assert.Equal(remembered.Target, results[0].Target);
        Assert.Empty(await provider.SearchAsync("No match", default, store.GetPreferences(context)));
    }

    [Fact]
    public async Task Remembered_file_participates_before_the_final_sixty_limit_without_inserting_absent_hits()
    {
        var files = new FileProvider { Hits = Enumerable.Range(0, 120).Select(i => new FileSearchHit($@"C:\files\result{i:D3}.txt", false)).ToArray() };
        var store = new QueryPreferenceStore(PreferencePath);
        var context = SearchContext.Create("result", SearchScope.Files, default)!;
        var remembered = Item(119) with { Kind = ResultKind.File, Target = files.Hits[119].FullPath };
        store.Record(context, remembered);
        var controller = new SearchController(new AppProvider(), files, preferences: store);
        var results = await controller.SearchAsync("result", SearchScope.Files, controller.BeginQuery(), default);
        Assert.Equal(60, results.Items.Count);
        Assert.Equal(remembered.Target, results.Items[0].Target);
        files.Hits = [];
        var empty = await controller.SearchAsync("result", SearchScope.Files, controller.BeginQuery(), default);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void Old_rows_remember_their_completed_context_during_debounce_and_across_refreshes()
    {
        var history = new SearchHistoryStore(HistoryPath);
        var preferences = new QueryPreferenceStore(PreferencePath);
        var apps = new AppProvider { Items = [Item(1), Item(2)] };
        var controller = new SearchController(apps, new FileProvider(), history, preferences);
        using var vm = new LauncherViewModel(controller, history, Dispatcher.CurrentDispatcher,
            openTarget: _ => true, iconLoader: (_, _, _) => null, iconRetryDelay: TimeSpan.Zero);
        vm.Query = "old";
        vm.SetScope(SearchScope.Applications);
        var old = vm.Results[1];
        vm.Query = "new";
        old.OpenCommand.Execute(null);
        Assert.Equal(1, preferences.GetPreferences(SearchContext.Create("old", SearchScope.Applications, default))[LaunchIdentity.ForItem(old.Model)].Count);
        Assert.Empty(preferences.GetPreferences(SearchContext.Create("new", SearchScope.Applications, default)));
        vm.RefreshFileBackend();
        Assert.NotSame(old, vm.Results[1]);
        old.OpenCommand.Execute(null); // An old row menu can outlive the refresh.
        Assert.Equal(2, preferences.GetPreferences(old.Context)[LaunchIdentity.ForItem(old.Model)].Count);
        Assert.Empty(preferences.GetPreferences(vm.Results[0].Context));
    }

    [Fact]
    public void Failed_open_copy_location_pin_and_empty_query_launch_do_not_record_query_usage()
    {
        var history = new SearchHistoryStore(HistoryPath);
        var preferences = new QueryPreferenceStore(PreferencePath);
        var apps = new AppProvider { Items = [Item(1)] };
        var succeeds = false;
        using var vm = new LauncherViewModel(new SearchController(apps, new FileProvider(), history, preferences), history,
            Dispatcher.CurrentDispatcher, openTarget: _ => succeeds, iconLoader: (_, _, _) => null,
            iconRetryDelay: TimeSpan.Zero, copyTarget: _ => true, openContainingTarget: _ => true);
        vm.Query = "app";
        vm.SetScope(SearchScope.Applications);
        var row = vm.Results[0];
        row.OpenCommand.Execute(null);
        row.CopyPathCommand.Execute(null);
        row.OpenContainingFolderCommand.Execute(null);
        row.TogglePinCommand.Execute(null);
        Assert.Empty(history.GetRecent(60));
        Assert.Empty(preferences.GetPreferences(row.Context));
        vm.Query = "";
        vm.Prime();
        Assert.Null(vm.Results[0].Context);
        succeeds = true;
        vm.Results[0].OpenCommand.Execute(null);
        Assert.Single(history.GetRecent(60));
        Assert.Empty(preferences.GetPreferences(row.Context));
    }

    [Fact]
    public void Pin_command_acts_on_the_clicked_row_not_the_selection_and_home_refresh_selects_first()
    {
        var history = new SearchHistoryStore(HistoryPath);
        var apps = new AppProvider { Items = [Item(1), Item(2)] };
        using var vm = new LauncherViewModel(new SearchController(apps, new FileProvider(), history), history,
            Dispatcher.CurrentDispatcher, iconLoader: (_, _, _) => null, iconRetryDelay: TimeSpan.Zero);
        vm.Query = "app";
        vm.SetScope(SearchScope.Applications);
        var clicked = vm.Results[1];
        clicked.TogglePinCommand.Execute(null);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.False(vm.Results[0].IsPinned);
        Assert.True(clicked.IsPinned);
        Assert.Equal("取消置顶", clicked.PinHeader);
        Assert.Equal(Item(2).Target, Assert.Single(history.GetHome(SearchScope.All, 60)).Target);
        vm.Query = "";
        vm.Prime();
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Equal(AppSources.Pinned, vm.Results[0].Source);
        vm.Results[0].TogglePinCommand.Execute(null);
        Assert.Empty(vm.Results);
        Assert.Equal(-1, vm.SelectedIndex);
    }

    [Fact]
    public void Full_pin_list_shows_a_footer_hint_and_a_later_success_clears_it()
    {
        var history = new SearchHistoryStore(HistoryPath);
        for (var i = 0; i < SearchHistoryStore.MaxPins; i++)
            Assert.True(history.TrySetPinned(Item(i), true));
        var apps = new AppProvider { Items = [Item(99)] };
        using var vm = new LauncherViewModel(new SearchController(apps, new FileProvider(), history), history,
            Dispatcher.CurrentDispatcher, iconLoader: (_, _, _) => null, iconRetryDelay: TimeSpan.Zero);
        vm.Query = "app";
        vm.SetScope(SearchScope.Applications);
        vm.Results[0].TogglePinCommand.Execute(null);
        Assert.False(vm.Results[0].IsPinned);
        Assert.Contains("置顶失败", vm.FooterHint);
        Assert.True(history.TrySetPinned(Item(0), false));
        vm.Results[0].TogglePinCommand.Execute(null);
        Assert.True(vm.Results[0].IsPinned);
        Assert.DoesNotContain("置顶失败", vm.FooterHint);
    }

    [Fact]
    public void Merged_result_keeps_all_verified_path_aliases_for_usage_and_pins()
    {
        var key = LaunchIdentity.ForShortcut(@"C:\Apps\app.exe", "", @"C:\Apps", 1, false);
        var first = Item(1) with { IdentityKey = key, IdentityAliases = ["old-path-a"], Score = 100 };
        var second = Item(2) with { IdentityKey = key, IdentityAliases = ["old-path-b"], Score = 1 };
        var merged = Assert.Single(ResultRanker.Rank([first, second]));
        Assert.Equal(first.Target, merged.Target);
        Assert.Equal(new[] { "old-path-a", "old-path-b" }, merged.IdentityAliases);
        var preferences = new Dictionary<string, ResultPreference> { ["old-path-b"] = new(3, DateTime.UtcNow) };
        Assert.Equal(3, ResultRanker.PreferenceFor(merged, preferences).Count);
    }

    private static SearchResultItem Item(int i) => new()
    {
        Id = $"app-{i}", Kind = ResultKind.Application, Title = $"App {i}", Target = $@"C:\Apps\{i}.lnk", TypeLabel = "应用", Score = 100 - i,
    };

    private sealed class AppProvider : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = [];
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult(Items);
    }

    private sealed class FileProvider : IFileSearchProvider
    {
        public IReadOnlyList<FileSearchHit> Hits { get; set; } = [];
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken, FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Hits));
    }
}
