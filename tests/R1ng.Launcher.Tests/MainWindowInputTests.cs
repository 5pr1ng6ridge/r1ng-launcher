using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Real WPF-window wiring for the input rules: hide preserves the query, a regular show selects all,
/// Tab/Shift+Tab switch scope only while the search box owns the keyboard, and the file hotkey always
/// shows the file scope.
/// </summary>
public class MainWindowInputTests
{
    [Fact]
    public async Task Hide_preserves_the_query_and_a_regular_show_selects_all()
    {
        await RunWindowAsync((window, viewModel, _) =>
        {
            viewModel.Query = "abc";
            window.ShowLauncher();
            Assert.Equal(3, window.SearchBox.SelectionLength);
            Assert.Equal(0, window.SearchBox.SelectionStart);

            window.HideLauncher();
            Assert.Equal("abc", viewModel.Query);

            viewModel.Query = "abcd";
            window.ShowLauncher();
            Assert.Equal(4, window.SearchBox.SelectionLength);
            Assert.Equal(0, window.SearchBox.SelectionStart);
        });
    }

    [Theory]
    [InlineData(SearchScope.Applications)]
    [InlineData(SearchScope.Files)]
    public async Task The_main_hotkey_always_opens_all_results_and_preserves_the_query(SearchScope previousScope)
    {
        await RunWindowAsync((window, viewModel, _) =>
        {
            viewModel.Query = "report";
            viewModel.SetScope(previousScope);
            window.ToggleLauncher();

            Assert.True(window.IsVisible);
            Assert.True(viewModel.IsScopeAll);
            Assert.Equal("report", viewModel.Query);
            Assert.Equal("report".Length, window.SearchBox.SelectionLength);

            viewModel.SetScope(previousScope);
            window.ToggleLauncher();
            Assert.False(window.IsVisible);
            Assert.False(viewModel.IsScopeAll); // Hiding does not reset the scope prematurely.

            window.ToggleLauncher();
            Assert.True(window.IsVisible);
            Assert.True(viewModel.IsScopeAll);
            Assert.Equal("report", viewModel.Query);
        });
    }

    [Theory]
    [InlineData(SearchScope.Applications)]
    [InlineData(SearchScope.Files)]
    public async Task A_regular_show_does_not_override_the_selected_scope(SearchScope scope)
    {
        await RunWindowAsync((window, viewModel, _) =>
        {
            viewModel.SetScope(scope);
            window.ShowLauncher();
            Assert.False(viewModel.IsScopeAll);
            Assert.Equal(scope == SearchScope.Files, viewModel.IsScopeFiles);
            Assert.Equal(scope == SearchScope.Applications, viewModel.IsScopeApplications);
        });
    }

    [Fact]
    public async Task The_file_hotkey_always_shows_the_file_scope_with_retained_terms()
    {
        await RunWindowAsync((window, viewModel, _) =>
        {
            viewModel.Query = "report";

            // Resolving the test host's foreground window normally yields no Explorer folder, so the
            // terms are kept and no path is invented.
            window.ShowFilesFromExplorer();

            Assert.True(viewModel.IsScopeFiles);
            Assert.Equal("report", viewModel.CurrentTerms);
        });
    }

    [Fact]
    public async Task Tab_in_the_search_box_cycles_the_scope_and_is_handled()
    {
        await RunWindowAsync((window, viewModel, _) =>
        {
            window.ShowLauncher();
            window.SearchBox.Focus();
            Keyboard.Focus(window.SearchBox);

            // Focus must really be inside the box for the scope gate to apply.
            Assert.True(window.SearchBox.IsKeyboardFocusWithin, "the search box could not take keyboard focus");

            var source = PresentationSource.FromVisual(window);
            Assert.NotNull(source);

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            window.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.True(viewModel.IsScopeApplications);

            // Cycling the scope must not move focus out of the query box.
            Assert.True(
                window.SearchBox.IsKeyboardFocusWithin,
                "Tab must keep the keyboard focus in the query box"
            );
        });
    }

    [Fact]
    public async Task The_injected_explorer_folder_prefixes_the_kept_terms_and_replaces_the_old_prefix()
    {
        await RunWindowAsync(
            (window, viewModel, _) =>
            {
                Assert.Equal(@"C:\injected", window.ResolveExplorerFolder(IntPtr.Zero));

                viewModel.Query = "\"C:\\old\" report";
                var text = window.ApplyExplorerFolder(@"C:\injected");

                Assert.True(viewModel.IsScopeFiles);
                Assert.Equal("\"C:\\injected\" report", text);
                Assert.Equal("\"C:\\injected\" report", viewModel.Query);
                Assert.Equal("\"C:\\injected\" report", window.SearchBox.Text);
                Assert.Equal("report", viewModel.CurrentTerms);

                // Only the keywords are selected so the next keystroke replaces them.
                var termsStart = text.IndexOf("report", StringComparison.Ordinal);
                Assert.Equal(termsStart, window.SearchBox.SelectionStart);
                Assert.Equal("report".Length, window.SearchBox.SelectionLength);

                // A second invoke replaces the automatic prefix instead of stacking two paths.
                var replaced = window.ApplyExplorerFolder(@"D:\other");
                Assert.Equal("\"D:\\other\" report", replaced);
                Assert.Equal("\"D:\\other\" report", viewModel.Query);
            },
            explorerFolderResolver: _ => @"C:\injected"
        );
    }

    [Fact]
    public async Task An_injected_folder_without_terms_keeps_the_separator_and_puts_the_caret_after_it()
    {
        await RunWindowAsync(
            (window, viewModel, _) =>
            {
                var text = window.ApplyExplorerFolder(@"C:\injected");

                Assert.Equal("\"C:\\injected\" ", text);
                Assert.Equal(text, window.SearchBox.Text);
                Assert.Equal(text.Length, window.SearchBox.CaretIndex);
                Assert.Equal(0, window.SearchBox.SelectionLength);
                Assert.Equal(string.Empty, viewModel.CurrentTerms);
            },
            explorerFolderResolver: _ => @"C:\injected"
        );
    }

    [Fact]
    public void An_obsolete_foreground_handle_is_not_reused_for_the_folder_context()
    {
        Assert.True(MainWindow.IsSameForegroundWindow(new IntPtr(42), new IntPtr(42)));
        Assert.False(MainWindow.IsSameForegroundWindow(new IntPtr(42), new IntPtr(43)));
        Assert.False(MainWindow.IsSameForegroundWindow(IntPtr.Zero, new IntPtr(43)));
        Assert.False(MainWindow.IsSameForegroundWindow(new IntPtr(42), IntPtr.Zero));
    }

    private static async Task RunWindowAsync(
        Action<MainWindow, LauncherViewModel, Dispatcher> body,
        Func<IntPtr, string?>? explorerFolderResolver = null
    )
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var viewModel = new LauncherViewModel(
                    new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
                    new EmptyHistoryProvider(),
                    dispatcher
                );

                window = new MainWindow(viewModel, explorerFolderResolver: explorerFolderResolver);
                body(window, viewModel, dispatcher);

                window.AllowClose();
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "R1ng window input test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(completed, "the window test timed out");
        Assert.Null(failure);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class EmptyAppProvider : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
    }

    private sealed class EmptyFileProvider : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        ) => Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }

    private sealed class EmptyHistoryProvider : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();

        public void Record(SearchResultItem item)
        {
        }
    }
}
