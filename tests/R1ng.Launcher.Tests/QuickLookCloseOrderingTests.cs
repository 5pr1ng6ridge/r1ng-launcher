using System.Diagnostics;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public sealed class QuickLookCloseOrderingTests
{
    [Fact]
    public async Task Hide_cancels_an_old_toggle_and_orders_close_before_a_new_shows_toggle()
    {
        await RunSta((vm, preview) =>
        {
            var oldResponse = new TaskCompletionSource<QuickLookPreviewResult>();
            var closeResponse = new TaskCompletionSource<QuickLookPreviewResult>();
            preview.Pending = oldResponse;
            preview.ClosePending = closeResponse;
            var old = vm.PreviewAsync(vm.Results[0]);
            var oldToken = preview.LastToken;
            var close = vm.ClosePreviewAsync();
            Assert.True(oldToken.IsCancellationRequested);
            Assert.Equal(0, preview.CloseCalls);
            preview.Pending = null;
            var fresh = vm.PreviewAsync(vm.Results[1]);
            Assert.True(vm.IsPreviewBusy);
            oldResponse.SetResult(new QuickLookPreviewResult(true, "obsolete response"));
            PumpUntil(() => preview.CloseCalls == 1);
            Assert.True(vm.IsPreviewBusy, "an old finally must not clear the new request's busy flag");
            Assert.Single(preview.Requests);
            Assert.Null(vm.ActionNotice);
            closeResponse.SetResult(new QuickLookPreviewResult(true, "closed"));
            PumpUntil(() => old.IsCompleted && close.IsCompleted && fresh.IsCompleted);
            Assert.Equal(new[] { "Toggle:" + vm.Results[0].Model.Target, "Close", "Toggle:" + vm.Results[1].Model.Target }, preview.Operations);
            Assert.False(vm.IsPreviewBusy);
            Assert.Contains(vm.Results[1].Title, vm.ActionNotice);
        });
    }

    [Fact]
    public async Task Hide_cancels_the_inflight_and_queued_switches_but_keeps_a_later_shows_switch_after_close()
    {
        await RunSta((vm, preview) =>
        {
            var oldResponse = new TaskCompletionSource<QuickLookPreviewResult>();
            var closeResponse = new TaskCompletionSource<QuickLookPreviewResult>();
            preview.SwitchPending = oldResponse;
            preview.ClosePending = closeResponse;
            vm.SwitchPreviewSelected();
            var oldToken = preview.LastSwitchToken;
            vm.SelectedIndex = 1;
            vm.SwitchPreviewSelected(); // This queued target must be dropped by hide.
            var close = vm.ClosePreviewAsync();
            Assert.True(oldToken.IsCancellationRequested);
            preview.SwitchPending = null;
            vm.SelectedIndex = 2;
            vm.SwitchPreviewSelected();
            oldResponse.SetResult(new QuickLookPreviewResult(true, "obsolete response"));
            PumpUntil(() => preview.CloseCalls == 1);
            Assert.Single(preview.SwitchRequests);
            closeResponse.SetResult(new QuickLookPreviewResult(true, "closed"));
            PumpUntil(() => close.IsCompleted && preview.SwitchRequests.Count == 2);
            Assert.Equal(new[] { "Switch:" + vm.Results[0].Model.Target, "Close", "Switch:" + vm.Results[2].Model.Target }, preview.Operations);
            Assert.Empty(preview.Requests);
            Assert.Null(vm.ActionNotice);
        });
    }

    [Fact]
    public async Task Missing_quicklook_and_disposal_make_close_safe_and_quiet()
    {
        await RunSta((vm, preview) =>
        {
            preview.Result = new QuickLookPreviewResult(false, "not running");
            var close = vm.ClosePreviewAsync();
            Assert.True(close.IsCompletedSuccessfully);
            Assert.Null(vm.ActionNotice);
            vm.Dispose();
            Assert.True(vm.ClosePreviewAsync().IsCompletedSuccessfully);
            Assert.Equal(1, preview.CloseCalls);
        });
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5))
            dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert.True(condition(), "preview coordinator did not settle");
    }

    private static Task RunSta(Action<LauncherViewModel, LauncherActionTests.FakePreviewService> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var preview = new LauncherActionTests.FakePreviewService();
                using var vm = new LauncherViewModel(new SearchController(new Apps(), new Files()), new History(), dispatcher,
                    iconLoader: (_, _, _) => null, previewService: preview);
                vm.SetScope(SearchScope.Applications);
                vm.Query = "item";
                vm.Prime();
                PumpUntil(() => !vm.IsSearching && vm.Results.Count == 3);
                Assert.Equal(3, vm.Results.Count);
                body(vm, preview);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "R1ng preview close ordering" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private sealed class Apps : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Enumerable.Range(1, 3).Select(index => new SearchResultItem
            { Id = $"close-{index}", Kind = ResultKind.Application, TypeLabel = "应用", Title = $"item {index}", Target = $@"C:\fake\item-{index}.exe" }).ToArray());
    }
    private sealed class Files : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }
    private sealed class History : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();
        public void Record(SearchResultItem item) { }
    }
}
