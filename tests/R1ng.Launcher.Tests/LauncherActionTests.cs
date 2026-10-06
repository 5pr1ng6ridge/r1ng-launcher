using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// View-model level checks for the new row actions: hiding and restoring results, QuickLook preview
/// and the visible action notice. Everything external is a fake (hidden store, QuickLook service,
/// history, open action): nothing is launched, no pipe is opened and no profile file is written.
/// Each body runs on its own STA thread without a running dispatcher frame, so a task completed by
/// the test resumes the view model inline and the assertions stay deterministic.
/// </summary>
public class LauncherActionTests
{
    [Fact]
    public async Task Hiding_removes_every_equivalent_row_at_once_and_keeps_history_and_pins()
    {
        await RunAsync(context =>
        {
            var (viewModel, apps, hidden, history) = (context.ViewModel, context.Apps, context.Hidden, context.History);
            apps.Items = [Item(1, "report"), Item(2, "report copy", target: Item(1).Target), Item(3, "other")];
            Search(viewModel);
            Assert.Equal(3, viewModel.Results.Count);
            viewModel.MoveSelectionToEnd();
            var other = viewModel.SelectedItem;
            var calls = apps.Calls;

            viewModel.Results[0].HideCommand.Execute(null);

            // Both rows of the hidden item go immediately; the unrelated row keeps being selected.
            Assert.Equal(1, hidden.HideCalls);
            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));
            Assert.Equal(other!.Title, viewModel.SelectedItem?.Title);
            Assert.True(viewModel.HasSelection);
            Assert.Equal(1, viewModel.HiddenCount);
            Assert.Equal("已隐藏 (1)", viewModel.HiddenButtonText);
            Assert.Equal(Item(1).Target, Assert.Single(viewModel.HiddenItems).Target);

            // The immediate refresh ran, and its (hidden-unaware) provider rows are filtered again.
            Assert.Equal(calls + 1, apps.Calls);
            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));

            // Hiding is not launching: no history, no pin or preference change, no window hide.
            Assert.Empty(history.Recorded);
            Assert.Empty(history.PinChanges);
            Assert.Equal(0, context.HideRequests);
            Assert.Empty(context.Opened);
        });
    }

    [Fact]
    public async Task A_failed_hide_keeps_the_row_and_shows_the_store_reason_without_touching_the_footer_hint()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            var hint = viewModel.FooterHint;
            var calls = context.Apps.Calls;
            context.Hidden.FailNext = "磁盘已满";

            viewModel.Results[0].HideCommand.Execute(null);

            Assert.Equal(2, viewModel.Results.Count);
            Assert.Equal(0, viewModel.HiddenCount);
            Assert.Contains("隐藏失败", viewModel.ActionNotice);
            Assert.Contains("磁盘已满", viewModel.ActionNotice);
            Assert.True(viewModel.HasActionNotice);

            // The original footer hint keeps its meaning; the visible footer shows the notice.
            Assert.Equal(hint, viewModel.FooterHint);
            Assert.Equal(viewModel.ActionNotice, viewModel.DisplayFooterHint);

            // Nothing changed, so nothing was refreshed.
            Assert.Equal(calls, context.Apps.Calls);
        });
    }

    [Fact]
    public async Task Hiding_without_a_store_says_so_instead_of_pretending()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);

            viewModel.Results[0].HideCommand.Execute(null);

            Assert.Single(viewModel.Results);
            Assert.Contains("隐藏失败", viewModel.ActionNotice);
            Assert.Equal(0, viewModel.HiddenCount);
        }, withStore: false);
    }

    [Fact]
    public async Task The_notice_survives_the_next_search_and_a_different_query_clears_it()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            viewModel.Results[0].HideCommand.Execute(null);
            var notice = viewModel.ActionNotice;
            Assert.Contains("已隐藏", notice);

            // An asynchronous search result (and its footer hint) must not erase the notice.
            viewModel.RefreshFileBackendCommand.Execute(null);
            Assert.Equal(notice, viewModel.ActionNotice);
            Assert.Equal(notice, viewModel.DisplayFooterHint);
            Assert.Equal("1 个结果", viewModel.FooterHint);

            // Typing something else makes it obsolete and the regular hint is shown again.
            var changed = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LauncherViewModel.DisplayFooterHint))
                    changed++;
            };
            viewModel.Query = "another";
            Assert.Null(viewModel.ActionNotice);
            Assert.False(viewModel.HasActionNotice);
            Assert.Equal(viewModel.FooterHint, viewModel.DisplayFooterHint);
            Assert.True(changed > 0, "the footer must be told that the displayed hint changed");
        });
    }

    [Fact]
    public async Task A_snapshot_that_still_contains_a_hidden_item_is_filtered_by_the_latest_store()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);

            // Hidden behind the view model's back (for example by another window of the manager).
            Assert.True(context.Hidden.TryHide(Item(1)));
            viewModel.RefreshFileBackendCommand.Execute(null);

            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));
            Assert.Equal("1 个结果", viewModel.FooterHint);

            // Hiding everything leaves an honest empty state rather than a stale list.
            Assert.True(context.Hidden.TryHide(Item(2)));
            viewModel.RefreshFileBackendCommand.Execute(null);
            Assert.Empty(viewModel.Results);
            Assert.False(viewModel.HasSelection);
            Assert.Equal("没有匹配的结果", viewModel.StatusText);
            Assert.True(viewModel.ShowStateMessage);
        });
    }

    [Fact]
    public async Task A_pending_search_from_before_the_hide_is_cancelled_and_cannot_bring_the_item_back()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            Assert.Equal(2, viewModel.Results.Count);

            // From now on every search stays pending until the test completes it.
            context.Apps.Pending = new List<PendingCall>();
            viewModel.RefreshFileBackendCommand.Execute(null);
            var stale = Assert.Single(context.Apps.Pending);
            Assert.False(stale.Token.IsCancellationRequested);

            viewModel.Results[0].HideCommand.Execute(null);

            // The hide invalidated and cancelled the older generation and started a new search.
            Assert.True(stale.Token.IsCancellationRequested);
            Assert.Equal(2, context.Apps.Pending.Count);
            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));

            // A late answer of the stale generation (which still lists the hidden item) is ignored...
            stale.Completion.SetResult(context.Apps.Items);
            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));

            // ...and even the current generation, ranked without knowing the hide, is filtered on apply.
            context.Apps.Pending[1].Completion.SetResult(context.Apps.Items);
            Assert.Equal(new[] { "other" }, viewModel.Results.Select(row => row.Title));
            Assert.Contains("已隐藏", viewModel.ActionNotice);
        });
    }

    [Fact]
    public async Task Restoring_one_item_refreshes_the_results_and_leaves_everything_else_hidden()
    {
        await RunAsync(context =>
        {
            var (viewModel, apps, hidden, history) = (context.ViewModel, context.Apps, context.Hidden, context.History);
            apps.Items = [Item(1, "report"), Item(2, "other"), Item(3, "third")];
            Search(viewModel);
            viewModel.Results[0].HideCommand.Execute(null);
            viewModel.Results[0].HideCommand.Execute(null);
            Assert.Equal(new[] { "third" }, viewModel.Results.Select(row => row.Title));
            Assert.Equal(2, viewModel.HiddenCount);

            var calls = apps.Calls;
            var key = viewModel.HiddenItems.Single(record => record.Target == Item(1).Target).Key;
            Assert.True(viewModel.RestoreHidden(key));

            Assert.Equal(calls + 1, apps.Calls);
            Assert.Equal(1, viewModel.HiddenCount);
            Assert.Equal("已隐藏 (1)", viewModel.HiddenButtonText);
            Assert.Equal(new[] { "report", "third" }, viewModel.Results.Select(row => row.Title).Order());
            Assert.Contains("已恢复", viewModel.ActionNotice);
            Assert.Equal(1, hidden.RestoreCalls);
            Assert.Empty(history.Recorded);
            Assert.Empty(history.PinChanges);
        });
    }

    [Fact]
    public async Task Restoring_everything_brings_every_hidden_item_back()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            viewModel.Results[0].HideCommand.Execute(null);
            viewModel.Results[0].HideCommand.Execute(null);
            Assert.Empty(viewModel.Results);

            var calls = context.Apps.Calls;
            Assert.True(viewModel.RestoreAllHidden());

            Assert.Equal(calls + 1, context.Apps.Calls);
            Assert.Equal(0, viewModel.HiddenCount);
            Assert.Equal(2, viewModel.Results.Count);
            Assert.Contains("已恢复 2", viewModel.ActionNotice);

            // Nothing left: asking again is a quiet success and does not search again.
            calls = context.Apps.Calls;
            Assert.True(viewModel.RestoreAllHidden());
            Assert.Equal(calls, context.Apps.Calls);
        });
    }

    [Fact]
    public async Task A_failed_restore_keeps_the_items_hidden_and_reports_why()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            viewModel.Results[0].HideCommand.Execute(null);
            var key = Assert.Single(viewModel.HiddenItems).Key;
            var calls = context.Apps.Calls;

            context.Hidden.FailNext = "无法保存";
            Assert.False(viewModel.RestoreHidden(key));
            Assert.Equal(1, viewModel.HiddenCount);
            Assert.Contains("恢复失败", viewModel.ActionNotice);
            Assert.Contains("无法保存", viewModel.ActionNotice);
            Assert.Equal("无法保存", viewModel.HiddenError);
            Assert.Equal(calls, context.Apps.Calls);

            context.Hidden.FailNext = "还是无法保存";
            Assert.False(viewModel.RestoreAllHidden());
            Assert.Equal(1, viewModel.HiddenCount);
            Assert.Contains("还是无法保存", viewModel.ActionNotice);
            Assert.Equal(calls, context.Apps.Calls);
        });
    }

    [Fact]
    public async Task The_manage_command_raises_the_request_even_with_no_results()
    {
        await RunAsync(context =>
        {
            var raised = 0;
            context.ViewModel.RequestManageHidden += () => raised++;

            Assert.Empty(context.ViewModel.Results);
            Assert.True(context.ViewModel.ManageHiddenCommand.CanExecute(null));
            context.ViewModel.ManageHiddenCommand.Execute(null);

            Assert.Equal(1, raised);
            Assert.Equal("已隐藏 (0)", context.ViewModel.HiddenButtonText);
        });
    }

    [Fact]
    public async Task A_store_problem_found_at_start_is_visible_and_the_first_search_does_not_erase_it()
    {
        await RunAsync(context =>
        {
            Assert.Equal("无法读取已隐藏项（hidden.json）", context.ViewModel.ActionNotice);
            Assert.Equal(context.ViewModel.ActionNotice, context.ViewModel.DisplayFooterHint);
            Assert.Equal("无法读取已隐藏项（hidden.json）", context.ViewModel.HiddenError);

            context.ViewModel.Prime();

            Assert.Equal("无法读取已隐藏项（hidden.json）", context.ViewModel.ActionNotice);
            Assert.Equal(context.ViewModel.ActionNotice, context.ViewModel.DisplayFooterHint);
        }, storeError: "无法读取已隐藏项（hidden.json）");
    }

    [Fact]
    public async Task Preview_sends_the_row_without_opening_recording_or_hiding_the_launcher()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other")];
            Search(viewModel);
            Assert.Equal(0, viewModel.SelectedIndex);

            // The context menu acts on the clicked row, not on the selection.
            viewModel.Results[1].PreviewCommand.Execute(null);

            Assert.Equal(Item(2).Target, Assert.Single(context.Preview.Requests).Target);
            Assert.Contains("已发送到 QuickLook", viewModel.ActionNotice);
            Assert.Contains("other", viewModel.ActionNotice);
            Assert.False(viewModel.IsPreviewBusy);
            Assert.Empty(context.Opened);
            Assert.Empty(context.History.Recorded);
            Assert.Equal(0, context.HideRequests);
            Assert.Equal(0, viewModel.SelectedIndex);

            // The keyboard entry point uses the selected row.
            viewModel.PreviewSelected();
            Assert.Equal(Item(1).Target, context.Preview.Requests[1].Target);
            Assert.Equal(2, context.Preview.Requests.Count);
        });
    }

    [Fact]
    public async Task Preview_shows_the_service_message_when_quicklook_cannot_be_reached()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            context.Preview.Result = new QuickLookPreviewResult(false, "未能连接到 QuickLook（连接超时）");

            viewModel.PreviewSelected();

            Assert.Equal("未能连接到 QuickLook（连接超时）", viewModel.ActionNotice);
            Assert.Equal(viewModel.ActionNotice, viewModel.DisplayFooterHint);
            Assert.Empty(context.Opened);
            Assert.Empty(context.History.Recorded);
            Assert.Equal(0, context.HideRequests);

            context.Preview.Result = new QuickLookPreviewResult(false, "  ");
            viewModel.PreviewSelected();
            Assert.Contains("QuickLook 预览失败", viewModel.ActionNotice);
        });
    }

    [Fact]
    public async Task Preview_refuses_unsupported_rows_without_calling_the_service()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            context.Preview.CanPreviewResult = false;

            viewModel.PreviewSelected();

            Assert.Empty(context.Preview.Requests);
            Assert.Contains("无法预览", viewModel.ActionNotice);
            Assert.False(viewModel.IsPreviewBusy);
        });
    }

    [Fact]
    public async Task Preview_without_a_selection_explains_itself()
    {
        await RunAsync(context =>
        {
            Assert.Empty(context.ViewModel.Results);

            context.ViewModel.PreviewSelected();

            Assert.Empty(context.Preview.Requests);
            Assert.Contains("没有选中", context.ViewModel.ActionNotice);
        });
    }

    [Fact]
    public async Task One_preview_runs_at_a_time_and_the_next_one_works_after_it_finishes()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            var pending = new TaskCompletionSource<QuickLookPreviewResult>();
            context.Preview.Pending = pending;

            viewModel.PreviewSelected();
            Assert.True(viewModel.IsPreviewBusy);
            Assert.Single(context.Preview.Requests);

            // A second request while one is in flight is not queued behind it.
            viewModel.PreviewSelected();
            Assert.Single(context.Preview.Requests);
            Assert.Contains("上一次预览", viewModel.ActionNotice);

            context.Preview.Pending = null;
            pending.SetResult(new QuickLookPreviewResult(true, "预览请求已发送"));
            Assert.False(viewModel.IsPreviewBusy);
            Assert.Contains("已发送到 QuickLook", viewModel.ActionNotice);

            viewModel.PreviewSelected();
            Assert.Equal(2, context.Preview.Requests.Count);
        });
    }

    [Fact]
    public async Task A_throwing_preview_service_is_reported_and_does_not_leave_the_view_model_busy()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            context.Preview.Throw = new InvalidOperationException("boom");

            viewModel.PreviewSelected();

            Assert.Contains("boom", viewModel.ActionNotice);
            Assert.False(viewModel.IsPreviewBusy);

            context.Preview.Throw = null;
            viewModel.PreviewSelected();
            Assert.Contains("已发送到 QuickLook", viewModel.ActionNotice);
        });
    }

    [Fact]
    public async Task Disposing_cancels_an_in_flight_preview_and_reports_nothing_afterwards()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            var pending = new TaskCompletionSource<QuickLookPreviewResult>();
            context.Preview.Pending = pending;
            viewModel.PreviewSelected();
            Assert.True(viewModel.IsPreviewBusy);
            Assert.False(context.Preview.LastToken.IsCancellationRequested);

            viewModel.Dispose();

            Assert.True(context.Preview.LastToken.IsCancellationRequested);

            // The service finishing later (even successfully) must not touch the disposed view model.
            pending.TrySetResult(new QuickLookPreviewResult(true, "预览请求已发送"));
            Assert.Null(viewModel.ActionNotice);

            // And a disposed view model starts nothing new.
            viewModel.PreviewSelected();
            Assert.Single(context.Preview.Requests);
        });
    }

    [Fact]
    public async Task A_pin_failure_is_not_hidden_by_an_earlier_notice_and_a_later_success_clears_it()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report"), Item(2, "other"), Item(3, "third")];
            Search(viewModel);

            // An earlier preview notice owns the footer...
            viewModel.PreviewSelected();
            Assert.Contains("已发送到 QuickLook", viewModel.DisplayFooterHint);

            // ...until the pin fails: the old FooterHint/StatusText behaviour stays, and the very same
            // text is also the visible notice, so the failure cannot stay unseen behind the old one.
            context.History.FailPinChanges = true;
            viewModel.Results[1].TogglePinCommand.Execute(null);

            Assert.False(viewModel.Results[1].IsPinned);
            Assert.Contains("置顶失败", viewModel.FooterHint);
            Assert.Equal(viewModel.FooterHint, viewModel.StatusText);
            Assert.Equal(viewModel.FooterHint, viewModel.ActionNotice);
            Assert.Equal(viewModel.FooterHint, viewModel.DisplayFooterHint);

            // Same after a hide notice.
            viewModel.Results[0].HideCommand.Execute(null);
            Assert.Contains("已隐藏", viewModel.DisplayFooterHint);
            viewModel.Results[0].TogglePinCommand.Execute(null);
            Assert.Contains("置顶失败", viewModel.DisplayFooterHint);
            Assert.Equal(viewModel.ActionNotice, viewModel.FooterHint);

            // A later success removes the stale failure from both places.
            context.History.FailPinChanges = false;
            viewModel.Results[0].TogglePinCommand.Execute(null);

            Assert.Null(viewModel.ActionNotice);
            Assert.DoesNotContain("置顶失败", viewModel.FooterHint);
            Assert.Equal(viewModel.FooterHint, viewModel.DisplayFooterHint);
            Assert.Single(context.History.PinChanges);
        });
    }

    [Fact]
    public async Task Hide_and_restore_do_nothing_on_a_disposed_view_model()
    {
        await RunAsync(context =>
        {
            var viewModel = context.ViewModel;
            context.Apps.Items = [Item(1, "report")];
            Search(viewModel);
            var row = viewModel.Results[0];
            viewModel.Dispose();

            row.HideCommand.Execute(null);

            Assert.Equal(0, context.Hidden.HideCalls);
            Assert.False(viewModel.RestoreHidden("anything"));
            Assert.False(viewModel.RestoreAllHidden());
        });
    }

    // ----- harness ---------------------------------------------------------------------------------

    private static SearchResultItem Item(int number, string? title = null, string? target = null) => new()
    {
        Id = $"app|{number}",
        Kind = ResultKind.Application,
        Title = title ?? $"item {number}",
        TypeLabel = "应用",
        Target = target ?? $@"C:\fake\item-{number}.exe",
        Score = 1_000 - number,
    };

    /// <summary>Runs a real (immediate) application-scope search; the fake provider answers synchronously.</summary>
    private static void Search(LauncherViewModel viewModel)
    {
        viewModel.Query = "item";
        viewModel.SetScope(SearchScope.Applications);
    }

    private sealed class Context
    {
        public required LauncherViewModel ViewModel { get; init; }

        public required MutableAppProvider Apps { get; init; }

        public required FakeHiddenStore Hidden { get; init; }

        public required FakeHistory History { get; init; }

        public required FakePreviewService Preview { get; init; }

        public required List<string> Opened { get; init; }

        public required Func<int> HideRequestCount { get; init; }

        /// <summary>How many times the view model asked the window to hide (a launch would).</summary>
        public int HideRequests => HideRequestCount();
    }

    private static async Task RunAsync(Action<Context> body, bool withStore = true, string? storeError = null)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var apps = new MutableAppProvider();
                var hidden = new FakeHiddenStore { Error = storeError };
                var history = new FakeHistory();
                var preview = new FakePreviewService();
                var opened = new List<string>();
                var hideRequests = 0;

                using var viewModel = new LauncherViewModel(
                    new SearchController(apps, new NoopFileProvider()),
                    history,
                    Dispatcher.CurrentDispatcher,
                    iconLoader: (_, _, _) => null,
                    iconRetryDelay: TimeSpan.Zero,
                    openTarget: item =>
                    {
                        opened.Add(item.Target);
                        return true;
                    },
                    copyTarget: _ => true,
                    hiddenResults: withStore ? hidden : null,
                    previewService: preview
                );

                viewModel.RequestHide += () => hideRequests++;

                body(
                    new Context
                    {
                        ViewModel = viewModel,
                        Apps = apps,
                        Hidden = hidden,
                        History = history,
                        Preview = preview,
                        Opened = opened,
                        HideRequestCount = () => hideRequests,
                    }
                );
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
            Name = "R1ng launcher action test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(completed, "the action test timed out");
        Assert.Null(failure);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    internal sealed record PendingCall(CancellationToken Token, TaskCompletionSource<IReadOnlyList<SearchResultItem>> Completion);

    internal sealed class MutableAppProvider : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = Array.Empty<SearchResultItem>();

        public int Calls { get; private set; }

        /// <summary>When set, searches wait here for the test instead of answering at once.</summary>
        public List<PendingCall>? Pending { get; set; }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            if (Pending is null)
                return Task.FromResult(Items);

            var call = new PendingCall(cancellationToken, new TaskCompletionSource<IReadOnlyList<SearchResultItem>>());
            Pending.Add(call);
            return call.Completion.Task;
        }
    }

    internal sealed class NoopFileProvider : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(
            string query,
            CancellationToken cancellationToken,
            FileSearchOptions options = default,
            string? folderConstraint = null
        ) => Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }

    internal sealed class FakeHistory : IHistoryProvider
    {
        public List<SearchResultItem> Recorded { get; } = new();

        public List<(SearchResultItem Item, bool Pinned)> PinChanges { get; } = new();

        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();

        public void Record(SearchResultItem item) => Recorded.Add(item);

        /// <summary>Makes every pin change fail, like a full pin list or an unwritable history file.</summary>
        public bool FailPinChanges { get; set; }

        public bool TrySetPinned(SearchResultItem item, bool pinned)
        {
            if (FailPinChanges)
                return false;

            PinChanges.Add((item, pinned));
            return true;
        }
    }

    /// <summary>In-memory store; rows are equivalent when they share a target.</summary>
    internal sealed class FakeHiddenStore : IHiddenResultStore
    {
        private readonly List<HiddenResultRecord> _records = new();

        /// <summary>Makes the next hide/restore fail with this message.</summary>
        public string? FailNext { get; set; }

        public string? Error { get; set; }

        public int HideCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public bool IsHidden(SearchResultItem item) => _records.Any(record => record.Key == item.Target);

        public IReadOnlyList<HiddenResultRecord> GetHidden() => _records.ToArray();

        public bool TryHide(SearchResultItem item)
        {
            HideCalls++;
            if (Fail())
                return false;

            if (!IsHidden(item))
            {
                _records.Insert(0, new HiddenResultRecord
                {
                    Key = item.Target,
                    Title = item.Title,
                    Target = item.Target,
                    TypeLabel = item.TypeLabel,
                    Kind = item.Kind,
                });
            }

            return true;
        }

        public bool TryRestore(string key)
        {
            RestoreCalls++;
            if (Fail())
                return false;

            return _records.RemoveAll(record => record.Key == key) > 0;
        }

        public bool TryRestoreAll()
        {
            if (Fail())
                return false;

            _records.Clear();
            return true;
        }

        private bool Fail()
        {
            if (FailNext is null)
            {
                // Like the real store, a successful change clears the last problem.
                Error = null;
                return false;
            }

            Error = FailNext;
            FailNext = null;
            return true;
        }
    }

    internal sealed class FakePreviewService : IQuickLookPreviewService
    {
        public List<SearchResultItem> Requests { get; } = new();

        public bool CanPreviewResult { get; set; } = true;

        public QuickLookPreviewResult Result { get; set; } = new(true, "预览请求已发送");

        /// <summary>When set, the next request waits for the test instead of answering at once.</summary>
        public TaskCompletionSource<QuickLookPreviewResult>? Pending { get; set; }

        public Exception? Throw { get; set; }

        public CancellationToken LastToken { get; private set; }

        public bool CanPreview(SearchResultItem item) => CanPreviewResult;

        public Task<QuickLookPreviewResult> PreviewAsync(SearchResultItem item, CancellationToken cancellationToken = default)
        {
            Requests.Add(item);
            LastToken = cancellationToken;
            if (Throw is not null)
                throw Throw;

            // The real service reports cancellation as a result; a test completes this task itself.
            if (Pending is { } pending)
                return pending.Task;

            return Task.FromResult(Result);
        }
    }
}
