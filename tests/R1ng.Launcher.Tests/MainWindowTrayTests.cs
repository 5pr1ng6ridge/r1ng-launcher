using System.IO;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>Exercises resident tray routing with an injected icon/menu fake; no shell icon or live dialog is created.</summary>
public sealed class MainWindowTrayTests
{
    [Fact]
    public async Task Tray_show_manage_and_quit_callbacks_keep_the_icon_until_actual_shutdown()
    {
        Exception? failure = null;
        var outcome = new TaskCompletionSource<(bool Shown, int Managers, bool ShutdownRequested, int DisposeAfterHide, int FinalDisposeCount, bool TaskbarEntry, bool IconAssetPackaged, bool WindowIconLoaded)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var viewModel = CreateViewModel(Dispatcher.CurrentDispatcher);
                var tray = new TestLauncherTrayIcon();
                var shutdown = new RecordingShutdown();
                var window = new MainWindow(viewModel, tray, shutdown);
                var managerCalls = 0;
                window.HiddenWindowPresenter = _ => { managerCalls++; return null; };
                window.Initialize(registerHotKeys: false);
                var taskbarEntry = window.ShowInTaskbar;
                var iconAssetPackaged = File.Exists(Path.Combine(AppContext.BaseDirectory, "app.ico"));
                var windowIconLoaded = window.Icon is not null;

                tray.RaiseShow();
                var shown = window.IsVisible;
                window.HideLauncher();
                var disposeAfterHide = tray.DisposeCalls;
                tray.RaiseManageHidden();
                var managers = managerCalls;
                tray.RaiseQuit();
                var shutdownRequested = shutdown.Called;
                window.Close();

                outcome.SetResult((shown, managers, shutdownRequested, disposeAfterHide, tray.DisposeCalls,
                    taskbarEntry, iconAssetPackaged, windowIconLoaded));
            }
            catch (Exception exception)
            {
                failure = exception;
                outcome.TrySetException(exception);
            }
        }) { IsBackground = true, Name = "R1ng tray lifecycle test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var result = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Null(failure);
            Assert.True(result.Shown);
            Assert.Equal(1, result.Managers);
            Assert.True(result.ShutdownRequested);
            Assert.Equal(0, result.DisposeAfterHide);
            Assert.Equal(1, result.FinalDisposeCount);
            Assert.False(result.TaskbarEntry);
            Assert.True(result.IconAssetPackaged, "app.ico must be copied beside the published/runtime executable");
            Assert.True(result.WindowIconLoaded, "the WPF launcher window must load the shared icon resource");
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task A_tray_failure_is_visible_and_alt_f4_can_exit_without_stranding_the_process()
    {
        Exception? failure = null;
        var outcome = new TaskCompletionSource<(bool Available, bool Banner, bool RemainedVisible, bool Shutdown, bool Closed, int Disposals)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var viewModel = CreateViewModel(Dispatcher.CurrentDispatcher);
                var tray = new TestLauncherTrayIcon(initializeResult: false);
                var shutdown = new RecordingShutdown();
                var window = new MainWindow(viewModel, tray, shutdown);
                window.Initialize(registerHotKeys: false);
                window.Show();
                var banner = viewModel.ShowHotKeyNotice && viewModel.HotKeyNotice!.Contains("Alt+F4", StringComparison.Ordinal);
                window.HideLauncher();
                var remainedVisible = window.IsVisible;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                outcome.SetResult((window.TrayIconAvailable, banner, remainedVisible, shutdown.Called, closed, tray.DisposeCalls));
            }
            catch (Exception exception)
            {
                failure = exception;
                outcome.TrySetException(exception);
            }
        }) { IsBackground = true, Name = "R1ng tray failure test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var result = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Null(failure);
            Assert.False(result.Available);
            Assert.True(result.Banner);
            Assert.True(result.RemainedVisible, "without a tray icon or hotkey, hide must not strand the launcher");
            Assert.True(result.Shutdown);
            Assert.True(result.Closed);
            Assert.Equal(1, result.Disposals);
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Taskbar_restart_failure_keeps_no_hotkey_launcher_reachable_and_recovery_clears_status()
    {
        await RunSta(dispatcher =>
        {
            using var viewModel = CreateViewModel(dispatcher);
            var tray = new TestLauncherTrayIcon();
            var shutdown = new RecordingShutdown();
            var window = new MainWindow(viewModel, tray, shutdown);
            window.Initialize(registerHotKeys: false);
            Assert.Equal(1, tray.AvailabilityChangedSubscribers);

            window.ShowLauncher();
            window.HideLauncher();
            Assert.False(window.IsVisible);

            tray.SimulateTaskbarRestart(addSucceeded: false, failureMessage: "injected taskbar re-registration failure");
            Assert.False(window.TrayIconAvailable);
            Assert.True(window.IsVisible, "the hidden no-hotkey launcher must be surfaced when Explorer rejects the tray re-add");
            Assert.Contains("injected taskbar re-registration failure", viewModel.HotKeyNotice);
            Assert.Contains("Alt+F4", viewModel.HotKeyNotice);
            window.HideLauncher();
            Assert.True(window.IsVisible, "failure mode must retain the no-hotkey escape window");

            tray.SimulateTaskbarRestart(addSucceeded: true);
            Assert.True(window.TrayIconAvailable);
            Assert.DoesNotContain("injected taskbar re-registration failure", viewModel.HotKeyNotice ?? string.Empty);
            window.HideLauncher();
            Assert.False(window.IsVisible, "a recovered tray is again a safe hide route");

            tray.SimulateTaskbarRestart(addSucceeded: false, failureMessage: "second re-registration failure");
            Assert.True(window.IsVisible);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close(); // Alt+F4 now exits because there is no hotkey or available tray route.
            Assert.True(shutdown.Called);
            Assert.True(closed);
            Assert.Equal(0, tray.AvailabilityChangedSubscribers);

            tray.SimulateTaskbarRestart(addSucceeded: true);
            Assert.False(window.TrayIconAvailable, "a closed window must no longer receive tray status events");
        });
    }

    private static Task RunSta(Action<Dispatcher> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(Dispatcher.CurrentDispatcher); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true, Name = "R1ng tray recovery test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    private static LauncherViewModel CreateViewModel(Dispatcher dispatcher) => new(
        new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
        new EmptyHistoryProvider(),
        dispatcher,
        previewService: new LauncherActionTests.FakePreviewService()
    );

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
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }

    private sealed class EmptyHistoryProvider : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();
        public void Record(SearchResultItem item) { }
    }
}
