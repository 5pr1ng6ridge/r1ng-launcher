using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

public sealed class ToolbarAndScrollBarTests
{
    [Fact]
    public async Task Scope_icons_toggle_the_three_modes_and_follow_existing_show_and_tab_rules()
    {
        await RunSta(fixture =>
        {
            var window = fixture.Window;
            var vm = fixture.ViewModel;
            Assert.Equal("搜索应用", AutomationProperties.GetName(window.ApplicationsScopeToggle));
            Assert.Equal("搜索文件", AutomationProperties.GetName(window.FilesScopeToggle));
            Assert.Equal(30, window.ApplicationsScopeToggle.Width);
            Assert.Equal(24, window.ApplicationsScopeToggle.Height);
            Assert.Same(window.ApplicationsScopeToggle.Style, window.FilesScopeToggle.Style);
            Assert.Empty(Descendants<RadioButton>(window));
            Assert.False(window.ApplicationsScopeToggle.Focusable);

            vm.SetScope(SearchScope.All);
            fixture.Pump();
            AssertScopeIcons(window, applications: true, files: true);
            Toggle(window.ApplicationsScopeToggle);
            fixture.Pump();
            Assert.True(vm.IsScopeFiles);
            AssertScopeIcons(window, applications: false, files: true);
            Toggle(window.FilesScopeToggle); // Last selected icon switches to the other single mode.
            fixture.Pump();
            Assert.True(vm.IsScopeApplications);
            AssertScopeIcons(window, applications: true, files: false);
            Toggle(window.FilesScopeToggle);
            fixture.Pump();
            Assert.True(vm.IsScopeAll);
            AssertScopeIcons(window, applications: true, files: true);

            // The old keyboard transition is unchanged, even though All now has two checked icons.
            vm.CycleScope(backwards: false);
            fixture.Pump();
            AssertScopeIcons(window, applications: true, files: false);
            vm.CycleScope(backwards: false);
            fixture.Pump();
            AssertScopeIcons(window, applications: false, files: true);
            vm.SetScope(SearchScope.All);
            vm.CycleScope(backwards: true);
            fixture.Pump();
            AssertScopeIcons(window, applications: false, files: true);

            window.HideLauncher();
            window.ToggleLauncher(); // Main-hotkey show still opens All without changing the query.
            fixture.Pump();
            Assert.Equal("result", vm.Query);
            AssertScopeIcons(window, applications: true, files: true);
            window.ApplyExplorerFolder(@"C:\Docs");
            fixture.Pump();
            AssertScopeIcons(window, applications: false, files: true);
            Assert.Equal("\"C:\\Docs\" result", vm.Query);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Checked_scope_and_search_option_buttons_keep_a_themed_gray_fill_without_outlines(AppTheme theme)
    {
        await RunSta(fixture =>
        {
            var window = fixture.Window;
            var vm = fixture.ViewModel;
            ThemeManager.Apply(theme, window.Resources);
            vm.SetScope(SearchScope.All);
            var toggles = Descendants<ToggleButton>(window).ToArray();
            Assert.Equal(6, toggles.Length);
            foreach (var toggle in toggles)
            {
                // SetCurrentValue retains the binding; regex may disable the whole-word option.
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                fixture.Pump();
                var border = Assert.IsType<Border>(toggle.Template.FindName("ToggleBorder", toggle));
                AssertGrayFillAndBorder(window, border);
                AssertBrush(window.Resources, "TextPrimaryBrush", toggle.Foreground);
            }

            // Replacing the palette repaints checked fills without bringing outlines back.
            ThemeManager.Apply(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, window.Resources);
            fixture.Pump();
            foreach (var toggle in toggles)
            {
                var border = Assert.IsType<Border>(toggle.Template.FindName("ToggleBorder", toggle));
                AssertGrayFillAndBorder(window, border);
                AssertBrush(window.Resources, "TextPrimaryBrush", toggle.Foreground);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Windows_controls_and_tooltips_are_borderless_with_background_focus_feedback(AppTheme theme)
    {
        await RunSta(fixture =>
        {
            var window = fixture.Window;
            ThemeManager.Apply(theme, window.Resources);
            fixture.Pump();
            AssertBorderless(window);

            Assert.Null(window.ThemeToggleButton.FocusVisualStyle);
            Assert.True(window.ThemeToggleButton.Focus());
            fixture.Pump();
            Assert.True(window.ThemeToggleButton.IsKeyboardFocused);
            var themeBorder = Assert.IsType<Border>(window.ThemeToggleButton.Template.FindName("ThemeButtonBorder", window.ThemeToggleButton));
            AssertBrush(window.Resources, "ToggleHoverBackgroundBrush", themeBorder.Background);

            fixture.ViewModel.SetScope(SearchScope.All);
            fixture.ViewModel.Query = string.Empty;
            fixture.ViewModel.Prime();
            fixture.Pump();
            var tile = Descendants<Button>(window).First(button => ReferenceEquals(button.Style, window.Resources["PinnedTileStyle"]));
            Assert.Null(tile.FocusVisualStyle);
            Assert.True(tile.Focus());
            fixture.Pump();
            Assert.True(tile.IsKeyboardFocused);
            var tileBorder = Assert.IsType<Border>(tile.Template.FindName("TileBorder", tile));
            AssertBrush(window.Resources, "ResultSelectedBackgroundBrush", tileBorder.Background);
            AssertBorderless(window);

            var hidden = new HiddenItemsWindow(fixture.ViewModel);
            try
            {
                ThemeRenderTests.LoadProductionControlStyles(hidden.Resources);
                ThemeManager.Apply(theme, hidden.Resources);
                var content = Assert.IsAssignableFrom<FrameworkElement>(hidden.Content);
                LayoutSurface(content);
                AssertBorderless(content);
                foreach (var button in Descendants<Button>(content))
                    Assert.Null(button.FocusVisualStyle);
            }
            finally { hidden.Close(); }

            var tooltip = new ToolTip { Content = "Borderless tooltip" };
            ThemeRenderTests.LoadProductionControlStyles(tooltip.Resources);
            ThemeManager.Apply(theme, tooltip.Resources);
            LayoutSurface(tooltip);
            Assert.Equal(new Thickness(0), tooltip.BorderThickness);
            AssertBorderless(tooltip);
            AssertBrush(tooltip.Resources, "DropdownBackgroundBrush", tooltip.Background);

            var combo = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedIndex = 0 };
            ThemeRenderTests.LoadProductionControlStyles(combo.Resources);
            ThemeManager.Apply(theme, combo.Resources);
            LayoutSurface(combo);
            Assert.Null(combo.FocusVisualStyle);
            AssertBorderless(combo);
            var dropdown = Assert.IsType<Border>(combo.Template.FindName("DropdownPanel", combo));
            Assert.Equal(new Thickness(0), dropdown.BorderThickness);
            AssertBrush(combo.Resources, "DropdownBackgroundBrush", dropdown.Background);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Hover_adds_only_a_gray_outline_without_changing_fills_layout_or_hit_testing(AppTheme theme)
    {
        await RunSta(fixture =>
        {
            var window = fixture.Window;
            ThemeManager.Apply(theme, window.Resources);
            window.SearchBox.Focus();
            fixture.Pump();
            foreach (var toggle in Descendants<ToggleButton>(window).Where(toggle => toggle.IsEnabled))
                AssertHoverOnly(toggle, "ToggleBorder", "ToggleOutline", fixture.Pump);
            AssertHoverOnly(window.ThemeToggleButton, "ThemeButtonBorder", "ThemeOutline", fixture.Pump);
            window.EditingButton.ApplyTemplate();
            AssertHoverOnly(window.EditingButton, "ActionButtonBorder", "ActionOutline", fixture.Pump);

            fixture.ViewModel.SelectedIndex = 0;
            foreach (var index in new[] { 0, 1 })
            {
                var row = Assert.IsType<ListBoxItem>(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(index));
                AssertHoverOnly(row, "RowBackground", "RowOutline", fixture.Pump);
            }
            var thumb = TrackOf(Bar(Descendants<ScrollViewer>(window.ResultsList).First(), Orientation.Vertical)).Thumb;
            AssertHoverOnly(thumb, "ThumbBorder", "ThumbOutline", fixture.Pump);

            fixture.ViewModel.SetScope(SearchScope.All);
            fixture.ViewModel.Query = string.Empty;
            fixture.ViewModel.Prime();
            fixture.Pump();
            var tiles = Descendants<Button>(window).Where(button => ReferenceEquals(button.Style, window.Resources["PinnedTileStyle"])).Take(2).ToArray();
            Assert.Equal(2, tiles.Length);
            foreach (var tile in tiles)
                AssertHoverOnly(tile, "TileBorder", "TileOutline", fixture.Pump);
            var homeRow = Assert.IsType<ListBoxItem>(window.HomeHistoryList.ItemContainerGenerator.ContainerFromIndex(0));
            AssertHoverOnly(homeRow, "RowBackground", "RowOutline", fixture.Pump);

            var hidden = new HiddenItemsWindow(fixture.ViewModel);
            try
            {
                ThemeRenderTests.LoadProductionControlStyles(hidden.Resources);
                ThemeManager.Apply(theme, hidden.Resources);
                hidden.Rows.Add(new HiddenItemRow { Key = "fake-hidden", Title = "Hidden item" });
                LayoutSurface(Assert.IsAssignableFrom<FrameworkElement>(hidden.Content));
                var row = Assert.IsType<ListBoxItem>(hidden.RowsList.ItemContainerGenerator.ContainerFromIndex(0));
                AssertHoverOnly(row, "RowBackground", "RowOutline", fixture.Pump);
                foreach (var button in Descendants<Button>(Assert.IsAssignableFrom<DependencyObject>(hidden.Content)).Where(button => button.IsEnabled))
                    AssertHoverOnly(button, "ButtonBorder", "ButtonOutline", fixture.Pump);
            }
            finally { hidden.Close(); }

            var combo = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedIndex = 0 };
            ThemeRenderTests.LoadProductionControlStyles(combo.Resources);
            ThemeManager.Apply(theme, combo.Resources);
            LayoutSurface(combo);
            AssertHoverOnly(combo, "Chrome", "ComboOutline", fixture.Pump);

            // A live palette swap also repaints an outline that is currently visible.
            var scope = window.ApplicationsScopeToggle;
            SetMouseOver(scope, true);
            ThemeManager.Apply(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, window.Resources);
            fixture.Pump();
            var scopeOutline = Assert.IsType<Border>(scope.Template.FindName("ToggleOutline", scope));
            AssertBrush(window.Resources, "HoverBorderBrush", scopeOutline.BorderBrush);
            AssertBrush(window.Resources, "ToggleCheckedBackgroundBrush",
                Assert.IsType<Border>(scope.Template.FindName("ToggleBorder", scope)).Background);
            SetMouseOver(scope, false);

            // Regex disables whole-word search; disabled controls must not gain a hover outline.
            fixture.ViewModel.IsRegex = true;
            fixture.Pump();
            var disabled = Descendants<ToggleButton>(window).First(toggle => !toggle.IsEnabled);
            SetMouseOver(disabled, true);
            fixture.Pump();
            var disabledOutline = Assert.IsType<Border>(disabled.Template.FindName("ToggleOutline", disabled));
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(disabledOutline.BorderBrush).Color.A);
        });
    }

    private static void AssertHoverOnly(Control control, string backgroundName, string outlineName, Action pump)
    {
        SetMouseOver(control, false);
        pump();
        var background = Assert.IsType<Border>(control.Template.FindName(backgroundName, control));
        var outline = Assert.IsType<Border>(control.Template.FindName(outlineName, control));
        var originalFill = Assert.IsType<SolidColorBrush>(background.Background).Color;
        var originalSize = control.RenderSize;
        var content = Descendants<ContentPresenter>(control).FirstOrDefault();
        var originalContentSize = content?.RenderSize;
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color.A);
        Assert.False(outline.IsHitTestVisible);
        Assert.Null(outline.Background);
        SetMouseOver(control, true);
        pump();
        Assert.Equal(new Thickness(1), outline.BorderThickness);
        Assert.Equal(Assert.IsType<SolidColorBrush>(control.FindResource("HoverBorderBrush")).Color,
            Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color);
        Assert.Equal(originalFill, Assert.IsType<SolidColorBrush>(background.Background).Color);
        Assert.Equal(originalSize, control.RenderSize);
        Assert.Equal(originalContentSize, content?.RenderSize);
        SetMouseOver(control, false);
        pump();
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color.A);
        Assert.Equal(originalFill, Assert.IsType<SolidColorBrush>(background.Background).Color);
    }

    private static void SetMouseOver(UIElement element, bool value)
    {
        var key = Assert.IsType<DependencyPropertyKey>(typeof(UIElement)
            .GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null));
        element.SetValue(key, value);
    }

    private static void LayoutSurface(FrameworkElement element)
    {
        element.Measure(new Size(640, 400));
        element.Arrange(new Rect(0, 0, 640, 400));
        element.UpdateLayout();
    }

    private static void AssertBorderless(DependencyObject root)
    {
        var borders = Descendants<Border>(root).ToArray();
        Assert.NotEmpty(borders);
        foreach (var control in Descendants<Control>(root))
            SetMouseOver(control, false);
        foreach (var border in borders)
            Assert.True(border.BorderThickness == new Thickness(0) || border.BorderBrush is null
                || Assert.IsType<SolidColorBrush>(border.BorderBrush).Color.A == 0, $"{border.Name} paints a resting outline");
    }

    [Fact]
    public async Task Default_result_and_home_scrollbars_are_thin_theme_aware_and_work_in_both_directions()
    {
        await RunSta(fixture =>
        {
            var window = fixture.Window;
            var vm = fixture.ViewModel;
            var resultViewer = Descendants<ScrollViewer>(window.ResultsList).First();
            var resultBar = Bar(resultViewer, Orientation.Vertical);
            AssertThinBar(window, resultBar, Orientation.Vertical);
            var resultTrack = TrackOf(resultBar);
            Assert.True(resultTrack.IsDirectionReversed);
            Assert.Equal(ScrollBar.PageDownCommand, resultTrack.IncreaseRepeatButton.Command);
            ScrollBar.PageDownCommand.Execute(null, resultTrack.IncreaseRepeatButton);
            fixture.Pump();
            Assert.True(resultViewer.VerticalOffset > 0, "vertical track paging must scroll the result viewport");
            Assert.Null(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(49));

            vm.SetScope(SearchScope.All);
            vm.Query = string.Empty;
            vm.Prime();
            fixture.Pump();
            Assert.Equal(12, vm.HomePins.Count);
            Assert.Equal(20, vm.Results.Count);
            var homeBar = Bar(window.HomeScroll, Orientation.Vertical);
            var pinBar = Bar(window.PinnedScroll, Orientation.Horizontal);
            AssertThinBar(window, homeBar, Orientation.Vertical);
            AssertThinBar(window, pinBar, Orientation.Horizontal);
            var pinTrack = TrackOf(pinBar);
            Assert.False(pinTrack.IsDirectionReversed);
            Assert.Equal(ScrollBar.PageLeftCommand, pinTrack.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageRightCommand, pinTrack.IncreaseRepeatButton.Command);
            ScrollBar.PageRightCommand.Execute(null, pinTrack.IncreaseRepeatButton);
            fixture.Pump();
            Assert.True(window.PinnedScroll.HorizontalOffset > 0, "horizontal track paging must scroll right");
            ScrollBar.PageLeftCommand.Execute(null, pinTrack.DecreaseRepeatButton);
            fixture.Pump();
            Assert.Equal(0, window.PinnedScroll.HorizontalOffset);

            pinTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            pinTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            pinTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(20, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            fixture.Pump();
            Assert.True(window.PinnedScroll.HorizontalOffset > 0, "dragging a horizontal thumb right must increase its offset");

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme, window.Resources);
                fixture.Pump();
                AssertThinBar(window, homeBar, Orientation.Vertical);
                AssertThinBar(window, pinBar, Orientation.Horizontal);
            }
        });
    }

    private static void AssertScopeIcons(MainWindow window, bool applications, bool files)
    {
        Assert.Equal(applications, window.ApplicationsScopeToggle.IsChecked);
        Assert.Equal(files, window.FilesScopeToggle.IsChecked);
    }

    private static void Toggle(ToggleButton button)
    {
        var provider = Assert.IsAssignableFrom<IToggleProvider>(
            new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle));
        provider.Toggle();
    }

    private static ScrollBar Bar(ScrollViewer viewer, Orientation orientation) => Assert.IsType<ScrollBar>(
        viewer.Template.FindName(orientation == Orientation.Vertical ? "PART_VerticalScrollBar" : "PART_HorizontalScrollBar", viewer));

    private static Track TrackOf(ScrollBar bar) => Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));

    private static void AssertThinBar(MainWindow window, ScrollBar bar, Orientation orientation)
    {
        Assert.True(bar.IsVisible);
        Assert.Equal(orientation, bar.Orientation);
        Assert.InRange(orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight, 5.5, 6.5);
        Assert.False(bar.Focusable);
        var track = TrackOf(bar);
        Assert.Equal(orientation, track.Orientation);
        var border = Assert.IsType<Border>(track.Thumb.Template.FindName("ThumbBorder", track.Thumb));
        AssertBrush(window.Resources, "TextMutedBrush", border.Background);
    }

    private static void AssertGrayFillAndBorder(MainWindow window, Border border)
    {
        AssertBrush(window.Resources, "ToggleCheckedBackgroundBrush", border.Background);
        Assert.Equal(new Thickness(0), border.BorderThickness);
        var color = Assert.IsType<SolidColorBrush>(border.Background).Color;
        Assert.True(color.A > 0);
        Assert.Equal(color.R, color.G);
        Assert.Equal(color.G, color.B);
    }

    private static void AssertBrush(ResourceDictionary resources, string key, Brush? actual) =>
        Assert.Equal(Assert.IsType<SolidColorBrush>(resources[key]).Color, Assert.IsType<SolidColorBrush>(actual).Color);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Task RunSta(Action<Fixture> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var fixture = new Fixture();
                action(fixture);
                done.SetResult();
            }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true, Name = "R1ng toolbar and scrollbar test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-toolbar-" + Guid.NewGuid().ToString("N"));
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var history = new SearchHistoryStore(Path.Combine(_root, "history.json"));
            foreach (var pin in Enumerable.Range(60, 12).Select(Item))
                Assert.True(history.TrySetPinned(pin, true));
            foreach (var recent in Enumerable.Range(100, 20).Select(Item))
                history.Record(recent);
            ViewModel = new LauncherViewModel(new SearchController(new Apps(), new Files(), history), history,
                _dispatcher, iconLoader: (_, _, _) => null, previewService: new LauncherActionTests.FakePreviewService());
            Window = new MainWindow(ViewModel, new TestLauncherTrayIcon());
            Window.Initialize(registerHotKeys: false);
            Window.ModifierProvider = () => ModifierKeys.None;
            var monitor = ScreenPlacement.SampleCursorMonitor();
            Window.SampleMonitor = () => monitor with
            {
                WorkArea = new ScreenPlacement.PixelsRect(monitor.WorkArea.Left, monitor.WorkArea.Top,
                    monitor.WorkArea.Left + (int)(420 * monitor.DpiScale),
                    monitor.WorkArea.Top + (int)(330 * monitor.DpiScale)),
            };
            ThemeRenderTests.LoadProductionControlStyles(Window.Resources);
            ThemeManager.Apply(AppTheme.Dark, Window.Resources);
            ViewModel.Query = "result";
            ViewModel.SetScope(SearchScope.Applications);
            Window.ShowLauncher();
            Pump();
        }

        public MainWindow Window { get; }
        public LauncherViewModel ViewModel { get; }

        public void Pump()
        {
            _dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Window.UpdateLayout();
            _dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        public void Dispose()
        {
            Window.AllowClose();
            Window.Close();
            ViewModel.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private static SearchResultItem Item(int index) => new()
    {
        Id = $"toolbar-{index}", Kind = ResultKind.Application, Title = $"result {index:00}",
        Target = $@"C:\Fake\result-{index}.exe", TypeLabel = "应用", Score = 1000 - index,
    };

    private sealed class Apps : IAppSearchProvider
    {
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Enumerable.Range(0, 50).Select(Item).ToArray());
    }

    private sealed class Files : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }
}
