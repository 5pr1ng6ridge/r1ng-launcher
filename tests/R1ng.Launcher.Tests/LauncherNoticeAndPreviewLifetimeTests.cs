using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public sealed class LauncherNoticeAndPreviewLifetimeTests
{
    [Fact]
    public async Task Diagnostics_are_replaceable_independent_notices_and_never_change_card_height_or_focus()
    {
        await RunSta(f =>
        {
            var height = f.Window.ActualHeight;
            f.ViewModel.SetActionNotice("first warning");
            f.ViewModel.SetActionNotice("latest warning");
            f.Pump();
            Assert.Equal("latest warning", Assert.Single(f.Notices.Seen).Message);
            Assert.Equal(height, f.Window.ActualHeight);
            Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
            foreach (var name in new[] { "ActionNoticeBanner", "FileNoticeBanner", "HotKeyNoticeBanner" })
                Assert.Null(f.Window.FindName(name));
            f.ViewModel.SetActionNotice("latest warning");
            f.Pump();
            Assert.Equal(2, f.Notices.Seen.Count); // Identical repeated actions renew the lifetime.
            f.ViewModel.SetHotKeyStatus("hotkey failed", true);
            f.Pump();
            Assert.True(f.Notices.Seen[^1].IsFailure);
            f.ViewModel.SetScope(SearchScope.Files);
            f.Pump();
            Assert.Equal("injected file warning", f.Notices.Seen[^1].Message);
            Assert.Equal("没有匹配的结果", f.ViewModel.EmptyResultsMessage);
            f.ViewModel.ToggleTheme();
            Assert.Equal(AppTheme.Light, f.Notices.LastTheme);
            Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
            f.Notices.Throw = true;
            f.ViewModel.SetActionNotice("cannot show");
            f.Pump(); // Failed notification presentation must not crash or move focus.
            Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public async Task Accepted_deactivation_hide_closes_preview_but_refocus_and_explicit_hide_do_not()
    {
        await RunSta(f =>
        {
            f.Active = false;
            f.Deactivate();
            Assert.Equal(0, f.Preview.CloseCalls);
            Assert.True(f.Window.IsVisible);
            f.Window.ShowLauncher();
            f.Timer.Fire();
            Assert.Equal(0, f.Preview.CloseCalls);
            f.Deactivate();
            f.Timer.Fire();
            Assert.False(f.Window.IsVisible);
            Assert.Equal(1, f.Preview.CloseCalls);
            f.Window.ShowLauncher();
            f.Window.HideLauncher();
            Assert.Equal(1, f.Preview.CloseCalls);
        });
    }

    [Fact]
    public async Task Own_menus_and_modal_dialogs_defer_both_hiding_and_preview_close()
    {
        await RunSta(f =>
        {
            f.Active = false;
            var menu = new ContextMenu { PlacementTarget = f.Window.SearchBox };
            menu.Items.Add(new MenuItem { Header = "fake action" });
            try
            {
                menu.IsOpen = true;
                f.Pump();
                Assert.True(f.Window.IsDeactivationSuppressed);
                f.Deactivate();
                f.Timer.Fire();
                Assert.True(f.Window.IsVisible);
                Assert.Equal(0, f.Preview.CloseCalls);
            }
            finally
            {
                menu.IsOpen = false;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (f.Window.IsDeactivationSuppressed && DateTime.UtcNow < deadline) f.Pump();
            }
            Assert.False(f.Window.IsDeactivationSuppressed);
            f.Timer.Fire();
            Assert.Equal(1, f.Preview.CloseCalls);
            f.Window.ShowLauncher();
            f.Window.HiddenWindowPresenter = dialog =>
            {
                Assert.True(f.Window.IsDeactivationSuppressed);
                f.Deactivate();
                f.Timer.Fire();
                Assert.Equal(1, f.Preview.CloseCalls);
                Assert.True(f.Window.IsVisible);
                return null;
            };
            f.ViewModel.ManageHiddenCommand.Execute(null);
            f.Window.ShowLauncher(); // Cancels the deferred hide after returning to our own UI.
            f.Timer.Fire();
            Assert.Equal(1, f.Preview.CloseCalls);
        });
    }

    [Fact]
    public async Task A_refused_hide_does_not_close_preview_and_close_disposes_the_notice_presenter()
    {
        await RunSta(f =>
        {
            f.Tray.SimulateTaskbarRestart(false, "no tray");
            f.Active = false;
            f.Deactivate();
            f.Timer.Fire();
            Assert.True(f.Window.IsVisible);
            Assert.Equal(0, f.Preview.CloseCalls);
            f.Window.AllowClose();
            f.Window.Close();
            Assert.Equal(1, f.Notices.DisposeCalls);
            f.Timer.Fire();
            f.ViewModel.SetActionNotice("after close");
            f.Pump();
            Assert.Equal(0, f.Preview.CloseCalls);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Floating_notice_chrome_cannot_activate_and_uses_the_live_palette(AppTheme theme)
    {
        await RunSta(f =>
        {
            var notice = new FloatingNoticeWindow();
            try
            {
                notice.UpdateTheme(theme);
                notice.MessageText.Text = new string('x', 600);
                var content = Assert.IsAssignableFrom<FrameworkElement>(notice.Content);
                content.Measure(new Size(360, 260));
                content.Arrange(new Rect(0, 0, 360, Math.Min(260, content.DesiredSize.Height)));
                content.UpdateLayout();
                Assert.False(notice.ShowActivated);
                Assert.False(notice.ShowInTaskbar);
                Assert.False(notice.Focusable);
                Assert.False(notice.CloseNoticeButton.Focusable);
                Assert.True(notice.Topmost);
                Assert.Null(notice.Owner);
                var border = Assert.IsType<Border>(notice.Content);
                Assert.Equal(new Thickness(0), border.BorderThickness);
                Assert.Equal(Assert.IsType<SolidColorBrush>(notice.Resources["DropdownBackgroundBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(border.Background).Color);
                var handled = false;
                Assert.Equal(new IntPtr(NativeMethods.MaNoActivate), FloatingNoticeWindow.MouseActivationResult(NativeMethods.WmMouseActivate, ref handled));
                Assert.True(handled);
                handled = false;
                Assert.Equal(IntPtr.Zero, FloatingNoticeWindow.MouseActivationResult(123, ref handled));
                Assert.False(handled);
                Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
            }
            finally { notice.Close(); }
        });
    }

    [Theory]
    [InlineData(-1920, 1.5)]
    [InlineData(0, 1.0)]
    [InlineData(2560, 2.0)]
    public void Notice_placement_is_clamped_on_the_anchors_monitor_in_device_pixels(int left, double scale)
    {
        var work = new ScreenPlacement.PixelsRect(left, -400, left + 900, 500);
        var monitor = new ScreenPlacement.MonitorSample(work, scale);
        foreach (var bounds in new[] { new ScreenPlacement.PixelsRect(left + 100, -300, left + 700, 0),
            new ScreenPlacement.PixelsRect(left - 100, 300, left + 950, 480) })
        {
            var (x, y) = FloatingNoticeWindow.Place(new NoticeAnchor(bounds, monitor), 500, 180);
            Assert.InRange(x, work.Left, work.Right - 500);
            Assert.InRange(y, work.Top, work.Bottom - 180);
        }
    }

    private static Task RunSta(Action<Fixture> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { using var f = new Fixture(); body(f); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "R1ng notices and preview lifetime" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    private sealed class Fixture : IDisposable
    {
        public LauncherActionTests.FakePreviewService Preview { get; } = new();
        public Notices Notices { get; } = new();
        public TestLauncherTrayIcon Tray { get; } = new();
        public Timer Timer { get; } = new();
        public bool Active { get; set; } = true;
        public LauncherViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public Fixture()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ViewModel = new LauncherViewModel(new SearchController(new Apps(), new Files()), new History(), dispatcher,
                iconLoader: (_, _, _) => null, previewService: Preview);
            ViewModel.SetScope(SearchScope.Applications);
            Window = new MainWindow(ViewModel, Tray, noticePresenter: Notices, deactivationTimer: Timer, launcherIsActive: () => Active);
            Window.Initialize(registerHotKeys: false);
            ThemeRenderTests.LoadProductionControlStyles(Window.Resources);
            ThemeManager.Apply(AppTheme.Dark, Window.Resources);
            ViewModel.Query = "item";
            Window.ShowLauncher();
            Pump();
        }
        public void Pump()
        {
            Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Window.UpdateLayout();
        }
        public void Deactivate() => typeof(MainWindow).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .Invoke(Window, new object?[] { Window, EventArgs.Empty });
        public void Dispose()
        {
            Window.AllowClose();
            Window.Close();
            ViewModel.Dispose();
        }
    }
    private sealed class Notices : ILauncherNoticePresenter
    {
        public List<LauncherNotice> Seen { get; } = new();
        public AppTheme? LastTheme { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Throw { get; set; }
        public void Show(LauncherNotice notice, NoticeAnchor anchor, AppTheme theme)
        {
            if (Throw) throw new InvalidOperationException("injected presentation failure");
            Seen.Add(notice);
            LastTheme = theme;
        }
        public void UpdateTheme(AppTheme theme) => LastTheme = theme;
        public void Dispose() => DisposeCalls++;
    }
    private sealed class Timer : IGraceTimer
    {
        public bool IsEnabled { get; private set; }
        public event EventHandler? Elapsed;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() { if (IsEnabled) Elapsed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() => Stop();
    }
    private sealed class Apps : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Enumerable.Range(1, 3).Select(index => new SearchResultItem
            { Id = $"notice-{index}", Title = $"item {index}", Target = $@"C:\fake\item-{index}.exe", Kind = ResultKind.Application, TypeLabel = "应用" }).ToArray());
    }
    private sealed class Files : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Unavailable("injected file warning"), Array.Empty<FileSearchHit>()));
    }
    private sealed class History : IHistoryProvider
    {
        public IReadOnlyList<SearchResultItem> GetRecent(int count) => Array.Empty<SearchResultItem>();
        public void Record(SearchResultItem item) { }
    }
}
