using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public sealed class MainWindowQuickLookAvoidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_preview_and_switch_capture_before_send_and_retry_for_late_windows(bool switchPreview)
    {
        var result = await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var launcherBounds = Rect(monitor.WorkArea, 500, 80, 400, 360);
            var preview = new ScreenPlacement.PixelsRect(
                monitor.WorkArea.Left + 80,
                monitor.WorkArea.Top + 100,
                monitor.WorkArea.Left + 420,
                monitor.WorkArea.Top + 480);
            var locator = new FakeLocator((call, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(
                call <= 2 ? Array.Empty<QuickLookWindowCandidate>() : [new(new IntPtr(101), preview)]));
            var placement = new FakeLauncherPlacement(monitor, launcherBounds);
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var row = Assert.Single(viewModel.Results);
                service.CaptureProbe = () => placement.CaptureCalls > 0;
                if (switchPreview)
                {
                    viewModel.SelectedIndex = 0;
                    window.SearchBox.Focus();
                    viewModel.SwitchPreviewSelected();
                }
                else
                {
                    window.SearchBox.Focus();
                    await viewModel.PreviewAsync(row);
                }

                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(service.CapturedBeforeSend, "the launcher HWND/bounds/work area must be captured before the pipe request");
                Assert.Equal(switchPreview ? 1 : 0, service.SwitchCalls);
                Assert.Equal(switchPreview ? 0 : 1, service.PreviewCalls);
                Assert.True(locator.Calls >= 3, "placement must retry after an initially absent asynchronous preview");
                Assert.NotEmpty(placement.Moves);
                Assert.All(placement.Moves, move => Assert.Equal(placement.LauncherHandle, move.Handle));
                Assert.True(window.SearchBox.IsKeyboardFocused, "non-activating self-placement must not steal query focus");
            }
            finally
            {
                Close(window);
            }

            return true;
        });

        Assert.True(result);
    }

    [Fact]
    public async Task A_closed_switch_with_no_visible_previews_does_not_move_the_launcher()
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var placement = new FakeLauncherPlacement(monitor, Rect(monitor.WorkArea, 120, 90, 420, 360));
            var locator = new FakeLocator((_, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>([]));
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                service.CaptureProbe = () => placement.CaptureCalls > 0;
                viewModel.SelectedIndex = 0;
                viewModel.SwitchPreviewSelected(); // A one-way Switch against a closed manager preview is a no-op.
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Equal(1, service.SwitchCalls);
                Assert.True(service.CapturedBeforeSend);
                Assert.Equal(12, locator.Calls);
                Assert.Empty(placement.Moves);
            }
            finally
            {
                Close(window);
            }

            return true;
        });
    }

    [Fact]
    public async Task A_pinned_only_candidate_can_position_only_the_launcher()
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var placement = new FakeLauncherPlacement(monitor, Rect(monitor.WorkArea, 500, 100, 400, 360));
            var pinnedHandle = new IntPtr(202);
            var pinnedBounds = Rect(monitor.WorkArea, 100, 100, 360, 400);
            var locator = new FakeLocator((_, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(
                [new(pinnedHandle, pinnedBounds)]));
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                viewModel.SelectedIndex = 0;
                viewModel.SwitchPreviewSelected();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.NotEmpty(placement.Moves);
                Assert.All(placement.Moves, move => Assert.Equal(placement.LauncherHandle, move.Handle));
                Assert.DoesNotContain(placement.Moves, move => move.Handle == pinnedHandle);
            }
            finally
            {
                Close(window);
            }

            return true;
        });
    }

    [Fact]
    public async Task Multiple_candidates_move_only_the_launcher_and_keep_corrections_beside_them()
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var area = monitor.WorkArea;
            var placement = new FakeLauncherPlacement(monitor, Rect(area, 600, 100, 400, 360));
            var pinned = new ScreenPlacement.PixelsRect(area.Left + 80, area.Top + 100, area.Left + 480, area.Top + 500);
            var second = new ScreenPlacement.PixelsRect(area.Left + 1200, area.Top + 100, area.Left + 1560, area.Top + 500);
            var unrelatedPinnedHandle = new IntPtr(202);
            var otherPinnedHandle = new IntPtr(303);
            var locator = new FakeLocator((_, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(
            [
                new(unrelatedPinnedHandle, pinned),
                new(otherPinnedHandle, second),
            ]));
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                viewModel.SelectedIndex = 0;
                viewModel.SwitchPreviewSelected();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.NotEmpty(placement.Moves);
                Assert.All(placement.Moves, move => Assert.Equal(placement.LauncherHandle, move.Handle));
                Assert.DoesNotContain(placement.Moves, move => move.Handle == unrelatedPinnedHandle || move.Handle == otherPinnedHandle);

                var previous = placement.Bounds;
                window.CorrectPlacement(); // Models the queued layout/DPI correction after the move.
                var centered = ScreenPlacement.Place(previous.Width, previous.Height, area);
                Assert.NotEqual(((int)centered.Left, (int)centered.Top), (placement.Bounds.Left, placement.Bounds.Top));
                Assert.All(placement.Moves, move => Assert.Equal(placement.LauncherHandle, move.Handle));
            }
            finally
            {
                Close(window);
            }

            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hiding_or_disposing_while_discovery_is_inflight_prevents_a_late_move(bool closeWindow)
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var placement = new FakeLauncherPlacement(monitor, Rect(monitor.WorkArea, 200, 100, 400, 360));
            var pending = new TaskCompletionSource<IReadOnlyList<QuickLookWindowCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var locator = new FakeLocator((_, _) =>
            {
                scanStarted.TrySetResult();
                return pending.Task;
            });
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await viewModel.PreviewAsync(Assert.Single(viewModel.Results));
                await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (closeWindow)
                    Close(window);
                else
                    window.HideLauncher();

                pending.TrySetResult([new QuickLookWindowCandidate(new IntPtr(404), Rect(monitor.WorkArea, 800, 100, 300, 300))]);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Empty(placement.Moves);
                return true;
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsafe_or_mismatched_launcher_capture_fails_closed(bool captureEnabled)
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var placement = new FakeLauncherPlacement(monitor, Rect(monitor.WorkArea, 200, 100, 400, 360))
            {
                CaptureEnabled = captureEnabled,
                ReturnMismatchedHandle = captureEnabled,
            };
            var locator = new FakeLocator((_, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(
                [new(new IntPtr(505), Rect(monitor.WorkArea, 800, 100, 300, 300))]));
            var service = new FakePreviewService();
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await viewModel.PreviewAsync(Assert.Single(viewModel.Results));
                Assert.Equal(0, locator.Calls);
                Assert.Empty(placement.Moves);
            }
            finally
            {
                Close(window);
            }

            return true;
        });
    }

    [Fact]
    public async Task A_superseded_switch_generation_never_places_from_its_late_completion()
    {
        await RunStaAsync(async dispatcher =>
        {
            var monitor = ScreenPlacement.SampleCursorMonitor();
            var placement = new FakeLauncherPlacement(monitor, Rect(monitor.WorkArea, 300, 100, 400, 360));
            var newerPreview = Rect(monitor.WorkArea, 900, 100, 320, 360);
            var locator = new FakeLocator((_, _) => Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(
                [new(new IntPtr(606), newerPreview)]));
            var service = new FakePreviewService { HoldFirstSwitch = true };
            using var viewModel = CreateViewModel(dispatcher, service, [Item(1), Item(2)]);
            var window = CreateWindow(viewModel, locator, placement, monitor);
            try
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                viewModel.SelectedIndex = 0;
                viewModel.SwitchPreviewSelected();
                Assert.True(service.FirstSwitchStarted);
                viewModel.MoveSelection(1);
                viewModel.SwitchPreviewSelected();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Equal(new[] { Item(1).Target, Item(2).Target }, service.SwitchTargets);
                Assert.Equal(12, locator.Calls);
                Assert.NotEmpty(placement.Moves);
                Assert.All(placement.Moves, move => Assert.Equal(placement.LauncherHandle, move.Handle));
            }
            finally
            {
                Close(window);
            }

            return true;
        });
    }

    private static MainWindow CreateWindow(
        LauncherViewModel viewModel,
        IQuickLookWindowLocator locator,
        FakeLauncherPlacement placement,
        ScreenPlacement.MonitorSample monitor)
    {
        var window = new MainWindow(
            viewModel,
            new TestLauncherTrayIcon(),
            quickLookWindowLocator: locator,
            launcherWindowPlacement: placement,
            quickLookAvoidanceDelay: (_, token) => token.IsCancellationRequested
                ? Task.FromCanceled(token)
                : Task.CompletedTask);
        window.Initialize(registerHotKeys: false);
        window.SampleMonitor = () => monitor;
        window.ShowLauncher();
        viewModel.Prime();
        return window;
    }

    private static LauncherViewModel CreateViewModel(Dispatcher dispatcher, FakePreviewService preview, IReadOnlyList<SearchResultItem> recent)
    {
        var history = new RecentHistory(recent);
        return new LauncherViewModel(
            new SearchController(new EmptyAppProvider(), new EmptyFileProvider(), history),
            history,
            dispatcher,
            previewService: preview);
    }

    private static SearchResultItem Item(int id) => new()
    {
        Id = $"preview-{id}",
        Kind = ResultKind.File,
        Title = $"preview-{id}.txt",
        TypeLabel = "文件",
        Target = $"C:\\preview-{id}.txt",
        Source = "Everything",
    };

    private static ScreenPlacement.PixelsRect Rect(ScreenPlacement.PixelsRect area, int x, int y, int width, int height) =>
        new(area.Left + x, area.Top + y, area.Left + x + width, area.Top + y + height);

    private static void Close(MainWindow window)
    {
        if (!window.IsLoaded)
            return;
        window.AllowClose();
        window.Close();
    }

    private static async Task<T> RunStaAsync<T>(Func<Dispatcher, Task<T>> action)
    {
        Exception? failure = null;
        var outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(async () =>
            {
                try
                {
                    outcome.SetResult(await action(dispatcher));
                }
                catch (Exception exception)
                {
                    failure = exception;
                    outcome.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "R1ng QuickLook avoidance test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var value = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(failure);
            return value;
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class FakeLocator(Func<int, CancellationToken, Task<IReadOnlyList<QuickLookWindowCandidate>>> find) : IQuickLookWindowLocator
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<QuickLookWindowCandidate>> FindVisiblePreviewsAsync(CancellationToken cancellationToken) =>
            find(++Calls, cancellationToken);
    }

    private sealed class FakeLauncherPlacement(ScreenPlacement.MonitorSample monitor, ScreenPlacement.PixelsRect bounds) : ILauncherWindowPlacement
    {
        private IntPtr _launcherHandle;
        public ScreenPlacement.PixelsRect Bounds { get; private set; } = bounds;
        public List<(IntPtr Handle, int X, int Y)> Moves { get; } = new();
        public int CaptureCalls { get; private set; }
        public bool CaptureEnabled { get; set; } = true;
        public bool ReturnMismatchedHandle { get; set; }
        public IntPtr LauncherHandle => _launcherHandle;

        public bool TryCapture(IntPtr handle, out LauncherWindowSnapshot snapshot)
        {
            CaptureCalls++;
            if (!CaptureEnabled)
            {
                snapshot = default;
                return false;
            }
            if (_launcherHandle == IntPtr.Zero)
                _launcherHandle = handle;
            snapshot = new LauncherWindowSnapshot(
                ReturnMismatchedHandle ? new IntPtr(handle.ToInt64() + 1) : handle,
                Bounds,
                monitor);
            return true;
        }

        public bool TryMove(IntPtr handle, int x, int y)
        {
            if (!CaptureEnabled || handle != _launcherHandle)
                return false;
            Moves.Add((handle, x, y));
            Bounds = new ScreenPlacement.PixelsRect(x, y, x + Bounds.Width, y + Bounds.Height);
            return true;
        }
    }

    private sealed class FakePreviewService : IQuickLookPreviewService
    {
        private readonly TaskCompletionSource<QuickLookPreviewResult> _firstSwitch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<bool>? CaptureProbe { get; set; }
        public bool CapturedBeforeSend { get; private set; }
        public bool HoldFirstSwitch { get; set; }
        public bool FirstSwitchStarted { get; private set; }
        public int PreviewCalls { get; private set; }
        public int SwitchCalls { get; private set; }
        public List<string> SwitchTargets { get; } = new();
        private static QuickLookPreviewResult Success => new(true, "sent");
        public Task<QuickLookPreviewResult> CloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Success);
        public bool CanPreview(SearchResultItem item) => true;

        public Task<QuickLookPreviewResult> PreviewAsync(SearchResultItem item, CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            CapturedBeforeSend = CaptureProbe?.Invoke() == true;
            return Task.FromResult(Success);
        }

        public Task<QuickLookPreviewResult> SwitchAsync(SearchResultItem item, CancellationToken cancellationToken = default)
        {
            SwitchCalls++;
            CapturedBeforeSend = CaptureProbe?.Invoke() == true;
            SwitchTargets.Add(item.Target);
            if (HoldFirstSwitch && SwitchCalls == 1)
            {
                FirstSwitchStarted = true;
                return _firstSwitch.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(Success);
        }
    }

    private sealed class RecentHistory(IReadOnlyList<SearchResultItem> items) : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => items.Take(count).ToArray();
        public void Record(SearchResultItem item) { }
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
}
