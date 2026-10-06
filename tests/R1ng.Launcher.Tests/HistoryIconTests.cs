using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public class HistoryIconTests
{
    [Fact]
    public async Task Concurrent_same_source_requests_share_one_extraction()
    {
        var cache = new IconCache();
        var calls = 0;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var icon = Image();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Load(@"C:\app.exe", false, -1, () =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return icon;
        }))).ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        var images = await Task.WhenAll(tasks);
        Assert.Equal(1, calls);
        Assert.All(images, image => Assert.Same(icon, image));
        Assert.True(icon.IsFrozen);
    }

    [Fact]
    public void Failed_extraction_is_negative_cached_only_until_cooldown_and_keys_include_folder_and_index()
    {
        var now = DateTime.UtcNow;
        var cache = new IconCache(() => now);
        var calls = 0;
        var icon = Image();
        ImageSource? Extract() => ++calls == 1 ? null : icon;
        Assert.Null(cache.Load(@"C:\app.exe", false, -1, Extract));
        Assert.Null(cache.Load(@"c:\APP.exe", false, -1, Extract));
        Assert.Equal(1, calls);
        now += IconCache.FailureCooldown;
        Assert.Same(icon, cache.Load(@"C:\app.exe", false, -1, Extract));
        Assert.Equal(2, calls);
        Assert.Same(icon, cache.Load(@"C:\app.exe", false, -1, Extract));
        Assert.Equal(2, calls);
        cache.Load(@"C:\app.exe", true, -1, Extract);
        cache.Load(@"C:\app.exe", false, 2, Extract);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Scheduler_limits_concurrency_across_batches_and_cancels_queued_work()
    {
        var active = 0;
        var calls = 0;
        var maximum = 0;
        var sync = new object();
        using var firstFour = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        var icon = Image();
        var scheduler = new IconLoadScheduler((_, _, _) =>
        {
            var current = Interlocked.Increment(ref active);
            lock (sync) maximum = Math.Max(maximum, current);
            var call = Interlocked.Increment(ref calls);
            if (call <= 4) firstFour.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            Interlocked.Decrement(ref active);
            return icon;
        }, TimeSpan.Zero);
        using var oldBatch = new CancellationTokenSource();
        var old = Enumerable.Range(0, 60).Select(i => scheduler.LoadAsync(Item(i), oldBatch.Token)).ToArray();
        Assert.True(firstFour.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, calls);
        oldBatch.Cancel();
        var fresh = scheduler.LoadAsync(Item(99), default);
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(old));
        Assert.Same(icon, await fresh);
        Assert.Equal(5, calls);
        Assert.Equal(4, maximum);
    }

    [Fact]
    public async Task Scheduler_retries_once_after_a_failure_and_never_retries_after_cancellation()
    {
        var calls = 0;
        var icon = Image();
        var scheduler = new IconLoadScheduler((_, _, _) => Interlocked.Increment(ref calls) == 1 ? null : icon, TimeSpan.Zero);
        Assert.Same(icon, await scheduler.LoadAsync(Item(0), default));
        Assert.Equal(2, calls);
        calls = 0;
        scheduler = new IconLoadScheduler((_, _, _) => { Interlocked.Increment(ref calls); return null; }, TimeSpan.Zero);
        Assert.Null(await scheduler.LoadAsync(Item(0), default));
        Assert.Equal(2, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.LoadAsync(Item(1), cancelled.Token));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, false)]
    public async Task Prime_and_search_load_every_row_including_rows_after_twenty_four_on_the_ui_dispatcher(int count, bool home)
    {
        await Sta(dispatcher =>
        {
            var file = Path.Combine(Path.GetTempPath(), "r1ng-icons-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var history = new SearchHistoryStore(file);
                var items = Enumerable.Range(0, count).Select(Item).ToArray();
                if (home)
                    foreach (var item in items) history.Record(item);
                var apps = new Apps { Items = items };
                var icon = Image();
                using var vm = new LauncherViewModel(new SearchController(apps, new Files(), history), history,
                    dispatcher, iconLoader: (_, _, _) => icon);
                if (home) vm.Prime();
                else
                {
                    vm.Query = "result";
                    vm.SetScope(SearchScope.Applications);
                }
                Assert.Equal(count, vm.Results.Count);
                var wrongThread = false;
                foreach (var row in vm.Results)
                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(ResultItemViewModel.Icon)) wrongThread |= !dispatcher.CheckAccess();
                    };
                PumpUntil(dispatcher, () => vm.Results.All(row => row.HasIcon));
                Assert.False(wrongThread);
                Assert.Same(icon, vm.Results[^1].Icon);
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        });
    }

    [Fact]
    public async Task Late_old_icons_never_update_retired_rows_or_rows_after_dispose()
    {
        await Sta(dispatcher =>
        {
            var apps = new Apps { Items = Enumerable.Range(0, 30).Select(Item).ToArray() };
            using var entered = new CountdownEvent(4);
            using var release = new ManualResetEventSlim();
            var calls = 0;
            var icon = Image();
            using var vm = new LauncherViewModel(new SearchController(apps, new Files()), new EmptyHistory(), dispatcher,
                iconLoader: (_, _, _) =>
                {
                    if (Interlocked.Increment(ref calls) <= 4)
                    {
                        entered.Signal();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    }
                    return icon;
                });
            vm.Query = "old";
            vm.SetScope(SearchScope.Applications);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var oldRows = vm.Results.ToArray();
            apps.Items = [Item(99)];
            vm.Query = "new";
            vm.RefreshFileBackend();
            release.Set();
            PumpUntil(dispatcher, () => vm.Results[0].HasIcon);
            Assert.Equal(5, calls);
            Assert.All(oldRows, row => Assert.False(row.HasIcon));
            vm.Dispose();
            apps.Items = [Item(100)];
            vm.RefreshFileBackend();
            Assert.Equal(Item(99).Target, vm.Results[0].Model.Target);
            Assert.Equal(5, calls);
        });
    }

    [Fact]
    public async Task Disposal_cancels_pending_icon_delivery_to_the_current_rows()
    {
        await Sta(dispatcher =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var apps = new Apps { Items = [Item(1)] };
            var image = Image();
            using var vm = new LauncherViewModel(new SearchController(apps, new Files()), new EmptyHistory(), dispatcher,
                iconLoader: (_, _, _) =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return image;
                });
            vm.Query = "app";
            vm.SetScope(SearchScope.Applications);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var row = vm.Results[0];
            vm.Dispose();
            release.Set();
            // Let the retired extraction and dispatcher queue settle without expecting an icon.
            var settled = false;
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { settled = true; timer.Stop(); };
            timer.Start();
            PumpUntil(dispatcher, () => settled);
            Assert.False(row.HasIcon);
        });
    }

    private static SearchResultItem Item(int i) => new()
    {
        Id = $"result-{i}", Kind = ResultKind.Application, Title = $"Result {i}", TypeLabel = "应用", Target = $@"C:\Apps\{i}.exe", Score = 100 - i,
    };

    private static ImageSource Image()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
        image.Freeze();
        return image;
    }

    private static Task Sta(Action<Dispatcher> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { body(Dispatcher.CurrentDispatcher); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "the queued icon update did not finish");
    }

    private sealed class Apps : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = [];
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult(Items);
    }
    private sealed class Files : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken, FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), []));
    }
    private sealed class EmptyHistory : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => [];
        public void Record(SearchResultItem item) { }
    }
}
