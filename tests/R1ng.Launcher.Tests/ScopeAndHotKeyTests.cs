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

    [Theory]
    [InlineData(SearchScope.All, true, true)]
    [InlineData(SearchScope.Applications, true, false)]
    [InlineData(SearchScope.Files, false, true)]
    public void Scope_icon_states_are_only_a_projection_of_the_existing_three_modes(SearchScope scope, bool applications, bool files)
    {
        using var fixture = new Fixture();
        fixture.ViewModel.SetScope(scope);
        Assert.Equal(applications, fixture.ViewModel.IsApplicationsIncluded);
        Assert.Equal(files, fixture.ViewModel.IsFilesIncluded);
    }

    [Theory]
    [InlineData(SearchScope.All, true, SearchScope.Files)]
    [InlineData(SearchScope.All, false, SearchScope.Applications)]
    [InlineData(SearchScope.Applications, true, SearchScope.Files)]
    [InlineData(SearchScope.Applications, false, SearchScope.All)]
    [InlineData(SearchScope.Files, true, SearchScope.All)]
    [InlineData(SearchScope.Files, false, SearchScope.Applications)]
    public void Scope_icon_clicks_use_single_or_double_selection_and_last_deselection_switches_modes(
        SearchScope initial, bool clickApplications, SearchScope expected)
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.SetScope(initial);
        vm.Query = "report";
        if (clickApplications)
            vm.IsApplicationsIncluded = !vm.IsApplicationsIncluded;
        else
            vm.IsFilesIncluded = !vm.IsFilesIncluded;

        Assert.Equal(expected == SearchScope.All, vm.IsScopeAll);
        Assert.Equal(expected == SearchScope.Applications, vm.IsScopeApplications);
        Assert.Equal(expected == SearchScope.Files, vm.IsScopeFiles);
        Assert.True(vm.IsApplicationsIncluded || vm.IsFilesIncluded);
        Assert.Equal("report", vm.Query);
    }

    [Fact]
    public void Scope_icons_keep_folder_prefix_rules_and_follow_file_hotkey_notifications()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Query = "\"C:\\folder\" report";
        vm.IsFilesIncluded = false;
        Assert.True(vm.IsScopeApplications);
        Assert.Equal("report", vm.Query);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.ApplyFileScope(@"D:\other");
        Assert.False(vm.IsApplicationsIncluded);
        Assert.True(vm.IsFilesIncluded);
        Assert.Contains(nameof(LauncherViewModel.IsApplicationsIncluded), changed);
        Assert.Contains(nameof(LauncherViewModel.IsFilesIncluded), changed);
        Assert.Equal("\"D:\\other\" report", vm.Query);
        vm.IsApplicationsIncluded = true;
        Assert.True(vm.IsScopeAll);
        Assert.Equal("\"D:\\other\" report", vm.Query);
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
    public void Theme_toggle_invokes_the_apply_seam_and_updates_its_accessible_action_icon()
    {
        AppTheme? applied = null;
        using var viewModel = new LauncherViewModel(
            new SearchController(new NoopAppProvider(), new RecordingFileProvider()),
            new NoopHistoryProvider(),
            Dispatcher.CurrentDispatcher,
            applyTheme: theme => applied = theme
        );

        Assert.Equal(AppTheme.Dark, viewModel.Theme);
        Assert.Equal("\uE706", viewModel.ThemeToggleGlyph);
        Assert.Equal("切换到浅色主题", viewModel.ThemeToggleAutomationName);

        viewModel.ToggleThemeCommand.Execute(null);

        Assert.Equal(AppTheme.Light, applied);
        Assert.Equal("\uE708", viewModel.ThemeToggleGlyph);
        Assert.Equal("切换到深色主题", viewModel.ThemeToggleAutomationName);
        viewModel.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.Dark, applied);
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
