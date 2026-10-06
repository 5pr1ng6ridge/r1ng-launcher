using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Files;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public class LauncherViewModelTests
{
    [Fact]
    public void Quick_toggle_properties_map_to_file_search_options()
    {
        using var viewModel = CreateViewModel(new SearchController(new NoopAppProvider(), new NoopFileProvider()));

        // Every refinement starts off, including match-path.
        Assert.True(viewModel.FileOptions.IsDefault);
        Assert.True(viewModel.IsWholeWordOptionEnabled);

        viewModel.IsMatchCase = true;
        viewModel.IsMatchWholeWord = true;
        viewModel.IsMatchPath = true;

        Assert.True(viewModel.FileOptions.MatchCase);
        Assert.True(viewModel.FileOptions.MatchWholeWord);
        Assert.True(viewModel.FileOptions.MatchPath);
        Assert.False(viewModel.FileOptions.Regex);
        Assert.False(viewModel.FileOptions.IsDefault);
    }

    [Fact]
    public void Regex_disables_the_whole_word_toggle_and_drops_its_wire_flag()
    {
        using var viewModel = CreateViewModel(new SearchController(new NoopAppProvider(), new NoopFileProvider()));

        viewModel.IsMatchWholeWord = true;
        viewModel.IsRegex = true;

        Assert.False(viewModel.IsWholeWordOptionEnabled);
        // The toggle keeps its state...
        Assert.True(viewModel.FileOptions.MatchWholeWord);
        // ...but the wire flags stay honest: Everything ignores whole word under regex.
        Assert.Equal(EverythingProtocol.SearchFlagRegex, EverythingProtocol.ToSearchFlags(viewModel.FileOptions));
    }

    [Fact]
    public void Typing_invalidates_and_cancels_the_in_flight_query_before_the_debounce_fires()
    {
        var files = new PendingFileProvider();
        var viewModel = CreateViewModel(new SearchController(new NoopAppProvider(), files));

        // Start a real (pending) query.
        viewModel.Query = "ab";
        viewModel.SetScope(SearchScope.Files);

        Assert.NotNull(files.LastToken);
        Assert.False(files.LastToken!.Value.IsCancellationRequested);

        // Editing again invalidates the generation and cancels the running query immediately, even
        // though the debounce timer has not fired yet. The rows on screen are untouched.
        viewModel.Query = "abc";

        Assert.True(files.LastToken!.Value.IsCancellationRequested);

        viewModel.Dispose();
    }

    [Fact]
    public void Quit_command_raises_the_quit_request()
    {
        using var viewModel = CreateViewModel(new SearchController(new NoopAppProvider(), new NoopFileProvider()));

        var raised = 0;
        viewModel.RequestQuit += () => raised++;

        Assert.True(viewModel.QuitCommand.CanExecute(null));
        viewModel.QuitCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Hotkey_notice_state_is_surfaced_for_the_banner()
    {
        using var viewModel = CreateViewModel(new SearchController(new NoopAppProvider(), new NoopFileProvider()));

        Assert.False(viewModel.ShowHotKeyNotice);
        Assert.False(viewModel.IsHotKeyFailure);

        viewModel.SetHotKeyStatus("Alt + Space 已被占用，已改用 Ctrl + Shift + Space", isFailure: false);

        Assert.True(viewModel.ShowHotKeyNotice);
        Assert.False(viewModel.IsHotKeyFailure);

        viewModel.SetHotKeyStatus("均注册失败；点击界面右下角“退出”可关闭本程序", isFailure: true);

        Assert.True(viewModel.ShowHotKeyNotice);
        Assert.True(viewModel.IsHotKeyFailure);

        viewModel.SetHotKeyStatus(notice: null, isFailure: false);

        Assert.False(viewModel.ShowHotKeyNotice);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(2)]
    [InlineData(0)]
    public void A_completed_result_refresh_resets_selection_to_the_first_row(int refreshedCount)
    {
        var apps = new MutableAppProvider { Items = CreateResults(8) };
        using var viewModel = CreateViewModel(new SearchController(apps, new NoopFileProvider()));
        viewModel.Query = "result";
        viewModel.SetScope(SearchScope.Applications);

        Assert.Equal(8, viewModel.Results.Count);
        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.True(viewModel.MoveSelectionToEnd());
        Assert.Equal(7, viewModel.SelectedIndex);

        apps.Items = CreateResults(refreshedCount);
        viewModel.RefreshFileBackendCommand.Execute(null);

        Assert.Equal(refreshedCount, viewModel.Results.Count);
        Assert.Equal(refreshedCount == 0 ? -1 : 0, viewModel.SelectedIndex);
        Assert.Equal(refreshedCount > 0, viewModel.HasSelection);
        Assert.Equal(refreshedCount > 0, viewModel.OpenCommand.CanExecute(null));

        if (refreshedCount > 0)
        {
            // The navigation model must reset its count and index as well as the bound property.
            viewModel.MoveSelection(-1);
            Assert.Equal(refreshedCount - 1, viewModel.SelectedIndex);
        }
    }

    [Fact]
    public void Refreshing_an_already_selected_first_row_still_notifies_the_view()
    {
        var apps = new MutableAppProvider { Items = CreateResults(3) };
        using var viewModel = CreateViewModel(new SearchController(apps, new NoopFileProvider()));
        viewModel.Query = "result";
        viewModel.SetScope(SearchScope.Applications);
        Assert.Equal(0, viewModel.SelectedIndex);
        var selectionNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LauncherViewModel.SelectedIndex))
                selectionNotifications++;
        };

        apps.Items = CreateResults(4);
        viewModel.RefreshFileBackendCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.True(selectionNotifications > 0, "the view must be able to scroll a refreshed first row into view");
    }

    [Fact]
    public void Editing_the_query_preserves_the_old_rows_and_selection_until_results_arrive()
    {
        var apps = new MutableAppProvider { Items = CreateResults(5) };
        using var viewModel = CreateViewModel(new SearchController(apps, new NoopFileProvider()));
        viewModel.Query = "result";
        viewModel.SetScope(SearchScope.Applications);
        viewModel.MoveSelectionToEnd();
        var selected = viewModel.SelectedItem;

        viewModel.Query = "next result";

        Assert.Equal(5, viewModel.Results.Count);
        Assert.Equal(4, viewModel.SelectedIndex);
        Assert.Same(selected, viewModel.SelectedItem);

        apps.Items = CreateResults(2);
        viewModel.RefreshFileBackendCommand.Execute(null);
        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.Equal(2, viewModel.Results.Count);
    }

    [Fact]
    public void Results_arriving_after_an_empty_refresh_select_the_first_row()
    {
        var apps = new MutableAppProvider();
        using var viewModel = CreateViewModel(new SearchController(apps, new NoopFileProvider()));
        viewModel.Query = "result";
        viewModel.SetScope(SearchScope.Applications);
        Assert.Equal(-1, viewModel.SelectedIndex);
        Assert.False(viewModel.HasSelection);

        apps.Items = CreateResults(3);
        viewModel.RefreshFileBackendCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.True(viewModel.HasSelection);
        Assert.Same(viewModel.Results[0], viewModel.SelectedItem);
    }

    private static IReadOnlyList<SearchResultItem> CreateResults(int count) =>
        Enumerable.Range(0, count).Select(index => new SearchResultItem
        {
            Id = $"result-{index}",
            Kind = ResultKind.Application,
            Title = $"Result {index}",
            TypeLabel = "应用",
            Score = count - index,
        }).ToArray();

    private static LauncherViewModel CreateViewModel(SearchController controller) =>
        new(controller, new NoopHistoryProvider(), Dispatcher.CurrentDispatcher);

    private sealed class MutableAppProvider : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = Array.Empty<SearchResultItem>();

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(Items);
    }

    private sealed class NoopAppProvider : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
    }

    private sealed class NoopFileProvider : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        ) => Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }

    private sealed class PendingFileProvider : IFileSearchProvider
    {
        private readonly TaskCompletionSource<FileSearchOutcome> _pending = new();

        public CancellationToken? LastToken { get; private set; }

        public string? LastFolderConstraint { get; private set; }

        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        )
        {
            LastToken = cancellationToken;
            LastFolderConstraint = folderConstraint;
            return _pending.Task;
        }
    }

    private sealed class NoopHistoryProvider : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();

        public void Record(SearchResultItem item)
        {
        }
    }
}
