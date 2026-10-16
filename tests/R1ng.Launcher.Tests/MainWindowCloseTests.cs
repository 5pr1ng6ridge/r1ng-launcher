using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Exercises the real WPF window on an STA thread (the same pattern as the dispatcher wiring test in
/// <see cref="DeactivationGraceTests"/>) to prove that a user close is intercepted and only an
/// explicit shutdown actually tears the window down.
/// </summary>
public class MainWindowCloseTests
{
    [Fact]
    public async Task A_user_close_hides_the_window_and_only_an_explicit_shutdown_closes_it()
    {
        Exception? failure = null;
        var outcome = new TaskCompletionSource<(bool ClosedAfterUserRequest, bool ClosedAfterShutdown, int TrayDisposedAfterHide, int TrayDisposeCount)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var viewModel = new LauncherViewModel(
                    new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
                    new EmptyHistoryProvider(),
                    dispatcher,
                    previewService: new LauncherActionTests.FakePreviewService()
                );

                var tray = new TestLauncherTrayIcon();
                var window = new MainWindow(viewModel, tray);
                window.Initialize(registerHotKeys: false);
                var closed = false;
                window.Closed += (_, _) => closed = true;

                // Alt+F4 / a normal close request hides the window but must keep the tray icon alive.
                window.Close();
                var closedAfterUserRequest = closed;
                var trayDisposedAfterHide = tray.DisposeCalls;

                // Session end allows the close so the process can actually exit.
                window.AllowClose();
                window.Close();

                outcome.SetResult((closedAfterUserRequest, closed, trayDisposedAfterHide, tray.DisposeCalls));
            }
            catch (Exception exception)
            {
                failure = exception;
                outcome.TrySetResult((false, false, -1, -1));
            }
        })
        {
            IsBackground = true,
            Name = "R1ng window close test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(failure);

            var (closedAfterUserRequest, closedAfterShutdown, trayDisposedAfterHide, trayDisposeCount) = await outcome.Task;
            Assert.False(closedAfterUserRequest, "a user close must hide the window, not close it");
            Assert.Equal(0, trayDisposedAfterHide);
            Assert.True(closedAfterShutdown, "an explicit shutdown must be able to close the window");
            Assert.Equal(1, trayDisposeCount);
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task The_quit_command_allows_the_close_and_requests_application_shutdown()
    {
        Exception? failure = null;
        var outcome = new TaskCompletionSource<(bool ShutdownCalled, bool Closed)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var viewModel = new LauncherViewModel(
                    new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
                    new EmptyHistoryProvider(),
                    dispatcher,
                    previewService: new LauncherActionTests.FakePreviewService()
                );

                var shutdown = new RecordingShutdown();
                var tray = new TestLauncherTrayIcon();
                var window = new MainWindow(viewModel, tray, shutdown);
                window.Initialize(registerHotKeys: false);
                var closed = false;
                window.Closed += (_, _) => closed = true;

                // The injected tray Quit command goes through RequestQuit -> Quit (AllowClose + shutdown).
                viewModel.QuitCommand.Execute(null);

                // A close after an explicit quit must really close instead of hiding like Alt+F4.
                window.Close();

                outcome.SetResult((shutdown.Called, closed));
            }
            catch (Exception exception)
            {
                failure = exception;
                outcome.TrySetResult((false, false));
            }
        })
        {
            IsBackground = true,
            Name = "R1ng quit test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(failure);

            var (shutdownCalled, closed) = await outcome.Task;
            Assert.True(shutdownCalled, "the quit command must request application shutdown");
            Assert.True(closed, "after the quit request a close must be allowed through");
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class RecordingShutdown : IApplicationShutdown
    {
        public bool Called { get; private set; }

        public void Shutdown() => Called = true;
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

        public void Record(SearchResultItem item) { }
    }
}
