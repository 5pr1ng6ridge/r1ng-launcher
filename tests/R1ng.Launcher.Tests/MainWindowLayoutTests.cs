using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Real-window layout, placement and scrolling behavior on an STA thread: the window grows with a few
/// results and stops at its cap with the header still visible, the bounded virtualized list keeps the
/// keyboard selection in view, a refresh that keeps the first row scrolls back to the top, and the
/// native window is placed in device pixels inside a work area sampled once per show. The production
/// control styles and palette are merged into the window's own scope (no Application exists in a test);
/// no hotkey is registered.
/// </summary>
public class MainWindowLayoutTests
{
    private const double Tolerance = 1.5;

    [Fact]
    public async Task The_window_grows_with_a_few_results_and_stops_at_its_cap_without_a_footer()
    {
        await RunAsync(harness =>
        {
            var window = harness.Window;

            harness.SetResults(1);
            harness.Show();
            var oneRow = window.ActualHeight;
            var top = harness.WindowTopPixels();

            harness.SetResults(3);
            var threeRows = window.ActualHeight;
            Assert.True(threeRows > oneRow + 10, $"3 rows ({threeRows}) must be taller than 1 row ({oneRow})");
            AssertHeaderVisible(window);
            Assert.Equal(Visibility.Collapsed, ScrollViewerOf(window).ComputedVerticalScrollBarVisibility);

            harness.SetResults(40);
            var capped = window.ActualHeight;
            Assert.True(capped > threeRows + 10, $"40 rows ({capped}) must be taller than 3 rows ({threeRows})");
            Assert.True(
                Math.Abs(capped - window.MaxHeight) <= Tolerance,
                $"the window must stop at its cap: height {capped}, cap {window.MaxHeight}"
            );
            Assert.True(window.MaxHeight <= MainWindow.MaxDesignHeight);

            // The header stays outside scrolling; results fill the card down to its bottom margin
            // and stay virtualized instead of realizing every row.
            AssertHeaderVisible(window);
            AssertResultsFillCard(window);
            var viewer = ScrollViewerOf(window);
            Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);
            Assert.True(viewer.ViewportHeight > 0 && viewer.ViewportHeight < viewer.ExtentHeight);
            Assert.Null(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(39));

            // The top anchor does not move while the content changes height.
            Assert.Equal(top, harness.WindowTopPixels());

            harness.SetResults(2);
            Assert.True(window.ActualHeight < capped - 10, "the window must shrink again for a short list");
            AssertHeaderVisible(window);
            AssertResultsFillCard(window);
            Assert.Equal(top, harness.WindowTopPixels());
        });
    }

    [Fact]
    public async Task Arrow_keys_wrap_and_keep_the_selected_row_inside_the_viewport()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(40);
            harness.Show();
            var searchBoxFocused = harness.Window.SearchBox.IsKeyboardFocusWithin;
            Assert.True(ScrollViewerOf(harness.Window).ViewportHeight < ScrollViewerOf(harness.Window).ExtentHeight);

            for (var step = 1; step <= 14; step++)
            {
                Assert.True(harness.Press(Key.Down));
                Assert.Equal(step, harness.ViewModel.SelectedIndex);
                AssertRowInViewport(harness.Window, step);
            }

            for (var step = 13; step >= 0; step--)
            {
                Assert.True(harness.Press(Key.Up));
                Assert.Equal(step, harness.ViewModel.SelectedIndex);
                AssertRowInViewport(harness.Window, step);
            }

            // Up from the first row wraps to the last, which has to be scrolled into view.
            Assert.True(harness.Press(Key.Up));
            Assert.Equal(39, harness.ViewModel.SelectedIndex);
            AssertRowInViewport(harness.Window, 39);

            Assert.True(harness.Press(Key.Home));
            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            AssertRowInViewport(harness.Window, 0);
            Assert.Equal(0, ScrollViewerOf(harness.Window).VerticalOffset);

            Assert.True(harness.Press(Key.PageDown));
            Assert.True(harness.Press(Key.PageDown));
            Assert.Equal(10, harness.ViewModel.SelectedIndex);
            AssertRowInViewport(harness.Window, 10);

            Assert.True(harness.Press(Key.End));
            Assert.Equal(39, harness.ViewModel.SelectedIndex);
            AssertRowInViewport(harness.Window, 39);

            Assert.True(harness.Press(Key.PageUp));
            Assert.Equal(34, harness.ViewModel.SelectedIndex);
            AssertRowInViewport(harness.Window, 34);

            // Navigation never moves the keyboard focus (into the list or out of the query box).
            Assert.Equal(searchBoxFocused, harness.Window.SearchBox.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public async Task Home_on_the_first_row_scrolls_back_to_the_top_after_the_wheel_moved_the_view()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(40);
            harness.Show();

            var viewer = ScrollViewerOf(harness.Window);
            viewer.ScrollToVerticalOffset(400);
            harness.Pump();
            Assert.True(viewer.VerticalOffset > 300, "the test needs a scrolled list");
            Assert.Equal(0, harness.ViewModel.SelectedIndex);

            // The selection does not change at a boundary, but the row has to become visible again.
            harness.Press(Key.Home);

            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            Assert.Equal(0, viewer.VerticalOffset);
            AssertRowInViewport(harness.Window, 0);
        });
    }

    [Fact]
    public async Task A_refresh_that_keeps_the_first_row_selected_returns_the_list_to_the_top()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(40);
            harness.Show();

            var viewer = ScrollViewerOf(harness.Window);
            viewer.ScrollToVerticalOffset(400);
            harness.Pump();
            Assert.True(viewer.VerticalOffset > 300, "the test needs a scrolled list");
            Assert.Equal(0, harness.ViewModel.SelectedIndex);

            // Same selected index (0) after the refresh: only the explicit notification can reveal it.
            harness.SetResults(40);

            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            Assert.Equal(0, viewer.VerticalOffset);
            AssertRowInViewport(harness.Window, 0);
        });
    }

    [Fact]
    public async Task A_refresh_after_navigating_down_selects_and_shows_the_first_row()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(40);
            harness.Show();
            harness.Press(Key.End);
            Assert.Equal(39, harness.ViewModel.SelectedIndex);
            Assert.True(ScrollViewerOf(harness.Window).VerticalOffset > 0);

            harness.SetResults(25);

            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            Assert.Equal(0, ScrollViewerOf(harness.Window).VerticalOffset);
            AssertRowInViewport(harness.Window, 0);
        });
    }

    [Fact]
    public async Task An_empty_result_list_shows_its_message_in_a_compact_window()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(40);
            harness.Show();
            var full = harness.Window.ActualHeight;

            harness.SetResults(0);

            var window = harness.Window;
            var message = FindDescendants<TextBlock>(window)
                .FirstOrDefault(block => block.Text == harness.ViewModel.StatusText && block.IsVisible);
            Assert.NotNull(message);
            Assert.False(string.IsNullOrEmpty(message!.Text));

            var messageBounds = BoundsIn(window, message);
            var themeBounds = BoundsIn(window, window.ThemeToggleButton);
            Assert.True(messageBounds.Height > 0);
            Assert.True(messageBounds.Top >= themeBounds.Bottom - Tolerance, "the message must sit below the header");
            Assert.True(window.ActualHeight < full - 50, "an empty list must not keep the full-height window");
            Assert.True(window.ActualHeight >= messageBounds.Bottom, "the message must stay inside the window");
            AssertHeaderVisible(window);
            AssertResultsFillCard(window);
        });
    }

    [Fact]
    public async Task The_window_is_placed_in_device_pixels_inside_the_work_area_sampled_once_per_show()
    {
        await RunAsync(harness =>
        {
            var window = harness.Window;
            harness.SetResults(2);
            harness.Show();

            var area = harness.Pinned.WorkArea;
            Assert.Equal(1, harness.SampleCalls);

            Assert.True(ScreenPlacement.TryGetWindowBounds(harness.Handle, out var bounds));
            var scale = harness.WindowScale();
            var (expectedLeft, expectedTop) = ScreenPlacement.Place(
                bounds.Width,
                Math.Round(window.MaxHeight * scale),
                area
            );
            Assert.True(Math.Abs(bounds.Left - expectedLeft) <= 1, $"left {bounds.Left}, expected {expectedLeft}");
            Assert.True(Math.Abs(bounds.Top - expectedTop) <= 1, $"top {bounds.Top}, expected {expectedTop}");
            Assert.True(bounds.Left >= area.Left && bounds.Right <= area.Right);
            Assert.True(bounds.Top >= area.Top && bounds.Bottom <= area.Bottom);

            // Resizing for new results never re-reads the cursor or moves the top.
            harness.SetResults(40);
            harness.SetResults(1);
            Assert.Equal(1, harness.SampleCalls);
            Assert.True(ScreenPlacement.TryGetWindowBounds(harness.Handle, out var resized));
            Assert.Equal(bounds.Top, resized.Top);
            Assert.Equal(bounds.Left, resized.Left);
            Assert.Equal(bounds.Width, resized.Width);

            // The next show samples again.
            window.HideLauncher();
            harness.Show();
            Assert.Equal(2, harness.SampleCalls);
        });
    }

    [Fact]
    public async Task A_short_narrow_work_area_keeps_header_visible_without_inline_warning_banners()
    {
        await RunAsync(harness =>
        {
            var window = harness.Window;
            var scale = harness.WindowScale();

            // 420 x 330 DIPs worth of real pixels, offset from the monitor origin: far below the
            // 760 x 520 design size, even with a long two-line transient hotkey warning.
            var real = ScreenPlacement.SampleCursorMonitor().WorkArea;
            var narrow = new ScreenPlacement.PixelsRect(
                real.Left + 30,
                real.Top + 20,
                real.Left + 30 + (int)(420 * scale),
                real.Top + 20 + (int)(330 * scale)
            );
            Assert.True(narrow.Right <= real.Right && narrow.Bottom <= real.Bottom, "the test screen is too small");
            harness.Pinned = new ScreenPlacement.MonitorSample(narrow, scale);

            harness.ViewModel.SetHotKeyStatus(
                "Alt + Space 与 Ctrl + Shift + Space 均已被其他程序占用；可通过系统托盘图标打开或退出。",
                isFailure: true
            );
            harness.SetResults(40);
            harness.Show();

            Assert.True(ScreenPlacement.TryGetWindowBounds(harness.Handle, out var bounds));
            Assert.True(bounds.Left >= narrow.Left && bounds.Right <= narrow.Right, $"{bounds} outside {narrow}");
            Assert.True(bounds.Top >= narrow.Top && bounds.Bottom <= narrow.Bottom, $"{bounds} outside {narrow}");
            Assert.True(window.MaxHeight <= (narrow.Height / scale) + Tolerance);
            Assert.True(window.Width < MainWindow.DesignWidth);

            // Diagnostics never consume card height or duplicate their text in the results.
            Assert.Null(window.FindName("HotKeyNoticeBanner"));
            Assert.DoesNotContain(FindDescendants<TextBlock>(window), block => block.Text.StartsWith("Alt + Space"));
            AssertHeaderVisible(window);
            AssertResultsFillCard(window);

            harness.Press(Key.End);
            AssertRowInViewport(window, 39);
        });
    }

    [Fact]
    public async Task Theme_button_has_no_tooltip_owns_its_focus_and_returns_focus_to_query_after_toggle()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(10);
            harness.Show();
            var button = harness.Window.ThemeToggleButton;
            var initialTheme = harness.ViewModel.Theme;

            Assert.Null(button.ToolTip);
            Assert.Null(System.Windows.Controls.ToolTipService.GetToolTip(button));
            Assert.Empty(FindDescendants<ComboBox>(harness.Window));
            Keyboard.Focus(button);
            Assert.True(button.IsKeyboardFocusWithin);
            Assert.True(harness.Press(Key.Down), "keyboard arrows still navigate results while a header control has focus");
            Assert.Equal(1, harness.ViewModel.SelectedIndex);
            Assert.True(harness.Press(Key.End));
            Assert.Equal(9, harness.ViewModel.SelectedIndex);

            button.Command.Execute(null);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            harness.Pump();
            Assert.NotEqual(initialTheme, harness.ViewModel.Theme);
            Assert.True(harness.Window.SearchBox.IsKeyboardFocusWithin);
            Assert.True(harness.Press(Key.Down));
            Assert.Equal(0, harness.ViewModel.SelectedIndex);
        });
    }

    [Fact]
    public async Task Enter_and_space_are_not_hijacked_for_theme_button_activation_but_escape_still_hides()
    {
        await RunAsync(harness =>
        {
            harness.SetResults(10);
            harness.Show();
            Keyboard.Focus(harness.Window.ThemeToggleButton);

            Assert.False(harness.Press(Key.Enter), "Enter belongs to the focused button");
            Assert.False(harness.Press(Key.Space), "Space belongs to the focused button");
            Assert.True(harness.Window.IsVisible);
            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            Assert.Null(harness.ViewModel.StatusText);
            Assert.True(harness.Press(Key.Escape));
            Assert.False(harness.Window.IsVisible);
        });
    }

    [Fact]
    public async Task Placement_uses_the_new_width_when_the_sampled_dpi_differs_from_the_window_dpi()
    {
        await RunAsync(harness =>
        {
            var real = harness.Pinned.WorkArea;
            var scale = harness.WindowScale();
            var area = new ScreenPlacement.PixelsRect(
                real.Left,
                real.Top,
                real.Left + (int)(800 * scale),
                real.Top + (int)(600 * scale)
            );
            Assert.True(area.Right <= real.Right && area.Bottom <= real.Bottom, "the test screen is too small");
            harness.Pinned = new ScreenPlacement.MonitorSample(area, scale * 2);
            harness.SetResults(3);
            harness.Show();

            Assert.True(ScreenPlacement.TryGetWindowBounds(harness.Handle, out var bounds));
            var actualScale = harness.WindowScale();
            var expectedWidth = ScreenPlacement.FitSize(MainWindow.DesignWidth, MainWindow.MaxDesignHeight, area, actualScale).Width;
            Assert.Equal(expectedWidth, harness.Window.Width);
            Assert.Equal(Math.Round(area.Left + (area.Width - bounds.Width) / 2.0), bounds.Left);
        });
    }

    private static void AssertResultsFillCard(MainWindow window)
    {
        var card = BoundsIn(window, window.CardBorder);
        var results = BoundsIn(window, window.ResultsViewport);
        Assert.InRange(card.Bottom - results.Bottom, 8 - Tolerance, 10 + Tolerance);
        Assert.True(results.Top >= BoundsIn(window, window.SearchHeader).Bottom);
        Assert.Null(window.FindName("FooterHintText"));
    }

    private static void AssertHeaderVisible(MainWindow window)
    {
        var theme = BoundsIn(window, window.ThemeToggleButton);
        Assert.True(theme.Height > 0);
        Assert.True(theme.Top >= 0);
        Assert.True(
            theme.Bottom <= window.ActualHeight + Tolerance,
            $"header bottom {theme.Bottom} is outside the window height {window.ActualHeight}"
        );
    }

    private static void AssertRowInViewport(MainWindow window, int index)
    {
        var container = window.ResultsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        Assert.True(container is not null, $"row {index} was not realized");

        var viewer = ScrollViewerOf(window);
        var bounds = container!.TransformToAncestor(viewer)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

        Assert.True(
            bounds.Top >= -0.5 && bounds.Bottom <= viewer.ViewportHeight + 0.5,
            $"row {index} spans {bounds.Top}..{bounds.Bottom} but the viewport is 0..{viewer.ViewportHeight}"
        );
    }

    private static ScrollViewer ScrollViewerOf(MainWindow window) =>
        FindDescendants<ScrollViewer>(window.ResultsList).First();

    private static Rect BoundsIn(Visual root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static IReadOnlyList<SearchResultItem> CreateResults(int count) =>
        Enumerable.Range(0, count).Select(index => new SearchResultItem
        {
            Id = $"result-{index}",
            Kind = ResultKind.Application,
            Title = $"Result {index}",
            Detail = $@"C:\Programs\Result {index}\result.exe",
            TypeLabel = "应用",
            Source = "开始菜单",
            Score = count - index,
        }).ToArray();

    private static async Task RunAsync(Action<Harness> body)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            Harness? harness = null;
            try
            {
                harness = new Harness();
                body(harness);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    harness?.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "R1ng window layout test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(60)));
        Assert.True(completed, "the window layout test timed out");
        Assert.Null(failure);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Owns one real window with the production styles, palette and a controllable result list.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly MutableAppProvider _apps = new();
        private bool _shown;

        public Harness()
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            ViewModel = new LauncherViewModel(
                new SearchController(_apps, new EmptyFileProvider()),
                new EmptyHistoryProvider(),
                Dispatcher,
                previewService: new LauncherActionTests.FakePreviewService()
            );

            Window = new MainWindow(ViewModel);
            Window.Resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/R1ngLauncher;component/Themes/Controls.xaml",
                        UriKind.Absolute
                    ),
                }
            );
            ThemeManager.Apply(AppTheme.Dark, Window.Resources);

            // The cursor monitor, sampled by the real native call, pinned for the whole test.
            Pinned = ScreenPlacement.SampleCursorMonitor();
            Window.SampleMonitor = () =>
            {
                SampleCalls++;
                return Pinned;
            };

            // Results come from the (inline, completed) provider through the real search pipeline.
            ViewModel.Query = "result";
            ViewModel.SetScope(SearchScope.Applications);
        }

        public MainWindow Window { get; }

        public LauncherViewModel ViewModel { get; }

        public Dispatcher Dispatcher { get; }

        public ScreenPlacement.MonitorSample Pinned { get; set; }

        public int SampleCalls { get; private set; }

        public IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(Window).Handle;

        /// <summary>Replaces the rows through a real search refresh and lets layout and callbacks settle.</summary>
        public void SetResults(int count)
        {
            _apps.Items = CreateResults(count);
            ViewModel.RefreshFileBackendCommand.Execute(null);
            Pump();
        }

        public void Show()
        {
            Window.ShowLauncher();
            _shown = true;
            Pump();
        }

        public double WindowScale() => ScreenPlacement.GetWindowDpiScale(
            new System.Windows.Interop.WindowInteropHelper(Window).EnsureHandle(),
            Pinned.DpiScale
        );

        public int WindowTopPixels() =>
            ScreenPlacement.TryGetWindowBounds(Handle, out var bounds) ? bounds.Top : int.MinValue;

        /// <summary>Raises a preview key press on the window; true when the window handled it.</summary>
        public bool Press(Key key)
        {
            var source = PresentationSource.FromVisual(Window);
            Assert.NotNull(source);

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            Window.RaiseEvent(args);
            Pump();
            return args.Handled;
        }

        /// <summary>Runs everything queued up to idle priority: bindings, layout, placement, scrolling.</summary>
        public void Pump()
        {
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Window.UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        /// <summary>Processes timer-driven popup animations as well as queued layout callbacks.</summary>
        public void PumpUntil(Func<bool> condition, string description)
        {
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            timer.Tick += (_, _) =>
            {
                if (condition() || DateTime.UtcNow >= deadline)
                    frame.Continue = false;
            };

            timer.Start();
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
            }

            Pump();
            Assert.True(condition(), description);
        }

        public void Dispose()
        {
            if (_shown)
                Window.HideLauncher();

            Window.AllowClose();
            Window.Close();
            ViewModel.Dispose();
        }
    }

    private sealed class MutableAppProvider : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = Array.Empty<SearchResultItem>();

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(Items);
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
