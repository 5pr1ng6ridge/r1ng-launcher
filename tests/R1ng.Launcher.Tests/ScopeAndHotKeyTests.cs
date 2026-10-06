using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Tab scope transitions, the file hotkey definition/ids and the file-scope query composition.
/// </summary>
public class ScopeAndHotKeyTests
{
    [Fact]
    public void The_file_hotkey_is_ctrl_alt_space_with_no_repeat()
    {
        // Alt (1) | Control (2) | NoRepeat (0x4000) == 0x4003; this must not be confused with
        // Ctrl + Shift + Space (0x4006).
        Assert.Equal(0x4003u, HotKeyDefinition.CtrlAltSpace.Modifiers);
        Assert.NotEqual(HotKeyDefinition.CtrlShiftSpace.Modifiers, HotKeyDefinition.CtrlAltSpace.Modifiers);
        Assert.Contains("Ctrl + Alt + Space", HotKeyDefinition.CtrlAltSpace.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_and_file_hotkeys_use_independent_ids()
    {
        var ids = new[]
        {
            GlobalHotKey.LauncherHotKeyId,
            GlobalHotKey.LauncherFallbackHotKeyId,
            GlobalHotKey.FileHotKeyId,
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Tab_forward_goes_all_to_applications_and_toggles_the_two_modes()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.CycleScope(backwards: false);
        Assert.True(fixture.ViewModel.IsScopeApplications);

        fixture.ViewModel.CycleScope(backwards: false);
        Assert.True(fixture.ViewModel.IsScopeFiles);

        fixture.ViewModel.CycleScope(backwards: false);
        Assert.True(fixture.ViewModel.IsScopeApplications);
    }

    [Fact]
    public void Shift_tab_backward_maps_all_to_files_and_toggles_the_two_modes()
    {
        using var fixture = new Fixture();

        fixture.ViewModel.CycleScope(backwards: true);
        Assert.True(fixture.ViewModel.IsScopeFiles);

        fixture.ViewModel.CycleScope(backwards: true);
        Assert.True(fixture.ViewModel.IsScopeApplications);
    }

    [Fact]
    public void The_file_hotkey_replaces_the_prefix_instead_of_stacking_it()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.ViewModel;

        viewModel.Query = "report";
        viewModel.ApplyFileScope(@"C:\folder");

        Assert.True(viewModel.IsScopeFiles);
        Assert.Equal("\"C:\\folder\" report", viewModel.Query);
        Assert.Equal("report", viewModel.CurrentTerms);
        Assert.Equal(@"C:\folder", fixture.Files.LastFolderConstraint);

        // A second invoke replaces the previous automatic prefix (never stacks two paths).
        viewModel.ApplyFileScope(@"D:\other");
        Assert.Equal("\"D:\\other\" report", viewModel.Query);
        Assert.Equal(@"D:\other", fixture.Files.LastFolderConstraint);
    }

    [Fact]
    public void Without_an_explorer_folder_the_terms_are_kept_and_the_prefix_is_removed()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.ViewModel;

        viewModel.Query = "\"C:\\old\" report";
        viewModel.ApplyFileScope(folder: null);

        Assert.True(viewModel.IsScopeFiles);
        Assert.Equal("report", viewModel.Query);
        Assert.Null(fixture.Files.LastFolderConstraint);
    }

    [Fact]
    public void The_file_hotkey_keeps_the_separator_when_there_are_no_terms()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.ViewModel;

        var text = viewModel.ApplyFileScope(@"C:\folder");

        Assert.True(viewModel.IsScopeFiles);
        Assert.Equal("\"C:\\folder\" ", text);
        Assert.Equal("\"C:\\folder\" ", viewModel.Query);
        Assert.Equal(string.Empty, viewModel.CurrentTerms);
        Assert.Equal(text.Length, viewModel.TermsStartIndex);
        Assert.Equal(@"C:\folder", fixture.Files.LastFolderConstraint);
    }

    [Fact]
    public void Switching_to_applications_drops_the_folder_prefix_but_keeps_the_terms()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.ViewModel;

        viewModel.Query = "\"C:\\folder\" report";
        viewModel.SetScope(SearchScope.Applications);

        Assert.True(viewModel.IsScopeApplications);
        Assert.Equal("report", viewModel.Query);
        Assert.Equal("report", viewModel.CurrentTerms);
    }

    [Fact]
    public void Theme_selection_invokes_the_apply_seam_and_exposes_both_palettes()
    {
        AppTheme? applied = null;
        using var viewModel = new LauncherViewModel(
            new SearchController(new NoopAppProvider(), new RecordingFileProvider()),
            new NoopHistoryProvider(),
            Dispatcher.CurrentDispatcher,
            applyTheme: theme => applied = theme
        );

        Assert.Equal(AppTheme.Dark, viewModel.Theme);
        Assert.Equal(2, viewModel.ThemeOptions.Count);
        Assert.Equal("深色", viewModel.ThemeLabel);

        viewModel.Theme = AppTheme.Light;

        Assert.Equal(AppTheme.Light, applied);
        Assert.Equal("浅色", viewModel.ThemeLabel);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Files = new RecordingFileProvider();
            ViewModel = new LauncherViewModel(
                new SearchController(new NoopAppProvider(), Files),
                new NoopHistoryProvider(),
                Dispatcher.CurrentDispatcher
            );
        }

        public RecordingFileProvider Files { get; }

        public LauncherViewModel ViewModel { get; }

        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class NoopAppProvider : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
    }

    private sealed class RecordingFileProvider : IFileSearchProvider
    {
        public string? LastFolderConstraint { get; private set; }

        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        )
        {
            LastFolderConstraint = folderConstraint;
            return Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
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
