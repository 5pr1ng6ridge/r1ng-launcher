using System.IO;
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

/// <summary>Checks the empty-query pinned home as a real WPF single-row tile strip.</summary>
public sealed class PinnedTilesLayoutTests
{
    [Fact]
    public async Task Pins_are_one_scrolling_tile_row_and_home_history_does_not_duplicate_them()
    {
        await RunSta(dispatcher =>
        {
            Opened.Clear();
            var path = Path.Combine(Path.GetTempPath(), "r1ng-tiles-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var history = new SearchHistoryStore(path);
                var pins = Enumerable.Range(0, 12).Select(Item).ToArray();
                foreach (var pin in pins)
                    Assert.True(history.TrySetPinned(pin, true));
                var recents = Enumerable.Range(30, 3).Select(Item).ToArray();
                foreach (var recent in recents)
                    history.Record(recent);

                var apps = new MutableApps { Items = [pins[0] with { Title = "Pinned Match" }] };
                var openedContaining = new List<string>();
                var preview = new RecordingPreview();
                var vm = new LauncherViewModel(new SearchController(apps, new EmptyFiles(), history), history,
                    dispatcher, openTarget: item => { Opened.Add(item.Target); return true; },
                    iconLoader: (_, _, _) => null, iconRetryDelay: TimeSpan.Zero,
                    openContainingTarget: item => { openedContaining.Add(item.Target); return true; },
                    previewService: preview);
                var tray = new TestLauncherTrayIcon();
                var window = new MainWindow(vm, tray,
                    quickLookWindowLocator: new EmptyQuickLookLocator(),
                    launcherWindowPlacement: new NoOpLauncherPlacement(),
                    quickLookAvoidanceDelay: (_, _) => Task.CompletedTask);
                window.Initialize(registerHotKeys: false);
                var sampled = ScreenPlacement.SampleCursorMonitor();
                var narrow = new ScreenPlacement.PixelsRect(
                    sampled.WorkArea.Left + 10,
                    sampled.WorkArea.Top + 10,
                    sampled.WorkArea.Left + 10 + (int)(420 * sampled.DpiScale),
                    sampled.WorkArea.Top + 10 + (int)(330 * sampled.DpiScale));
                Assert.True(narrow.Right <= sampled.WorkArea.Right && narrow.Bottom <= sampled.WorkArea.Bottom,
                    "the test monitor is too small for the bounded-layout check");
                window.SampleMonitor = () => new ScreenPlacement.MonitorSample(narrow, sampled.DpiScale);
                vm.SetHotKeyStatus("Alt + Space 与 Ctrl + Shift + Space 均不可用；请使用系统托盘图标退出本程序。",
                    isFailure: true);
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/R1ngLauncher;component/Themes/Controls.xaml", UriKind.Absolute),
                });
                ThemeManager.Apply(AppTheme.Dark, window.Resources);
                vm.Prime();
                PumpUntil(dispatcher, () => vm.HomePins.Count == pins.Length && vm.Results.Count == recents.Length);
                window.ShowLauncher();
                Pump(dispatcher, window);

                Assert.True(vm.IsHomeQuery);
                Assert.True(vm.ShowHomeTiles);
                Assert.Equal(pins.Length, window.HomePinsList.Items.Count);
                Assert.Equal(recents.Length, window.HomeHistoryList.Items.Count);
                Assert.All(vm.Results, row => Assert.False(row.IsPinned));
                Assert.Empty(vm.Results.Select(row => row.Model.Target).Intersect(vm.HomePins.Select(row => row.Model.Target)));
                Assert.InRange(window.PinnedScroll.ActualHeight, 50, 70);
                Assert.True(window.MaxHeight <= 330 + 1.5, "the launcher must cap itself to the tiny work area");
                var footerBounds = window.ThemeToggleButton.TransformToAncestor(window)
                    .TransformBounds(new Rect(0, 0, window.ThemeToggleButton.ActualWidth, window.ThemeToggleButton.ActualHeight));
                Assert.True(footerBounds.Bottom <= window.ActualHeight + 1.5, "the header actions must remain visible outside the home viewport");

                var tileViewport = Assert.IsType<ScrollViewer>(window.PinnedScroll);
                Assert.True(tileViewport.ScrollableWidth > 0, "more than one row's viewport must scroll horizontally");
                Assert.Equal(Visibility.Visible, tileViewport.ComputedHorizontalScrollBarVisibility);
                foreach (var index in Enumerable.Range(0, pins.Length))
                {
                    var container = Assert.IsAssignableFrom<FrameworkElement>(window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(index));
                    var tile = Assert.IsType<Button>(FindDescendant<Button>(container));
                    Assert.Equal(48, tile.Width);
                    Assert.Equal(48, tile.Height);
                }

                var firstTileContainer = Assert.IsAssignableFrom<FrameworkElement>(window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(0));
                var firstTile = Assert.IsType<Button>(FindDescendant<Button>(firstTileContainer));
                var content = Assert.IsType<Grid>(firstTile.Content);
                Assert.Equal(2, content.Children.Count);
                Assert.IsType<Image>(content.Children[1]);
                Assert.Equal(vm.HomePins[0].Glyph, Assert.IsType<TextBlock>(content.Children[0]).Text);
                Assert.Equal(vm.HomePins[0].Title, System.Windows.Automation.AutomationProperties.GetName(firstTile));
                Assert.Equal(vm.HomePins[0].Title, firstTile.ToolTip);

                // A different tile may own keyboard focus than the previous aggregate selection
                // (for example after Tab traversal). Enter, Ctrl+Enter and Ctrl+Space must use the
                // focused tile, not the stale aggregate row; Space retains native button activation.
                var focusedIndex = 7;
                var focusedTarget = vm.HomePins[focusedIndex].Model.Target;
                vm.SelectedIndex = 2;
                window.PinnedScroll.ScrollToHorizontalOffset(focusedIndex * 54);
                Pump(dispatcher, window);
                var focusedContainer = Assert.IsAssignableFrom<FrameworkElement>(
                    window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(focusedIndex));
                var focusedTile = Assert.IsType<Button>(FindDescendant<Button>(focusedContainer));
                Assert.Same(focusedTile, Keyboard.Focus(focusedTile));
                Assert.Equal(focusedIndex, vm.SelectedIndex);

                window.ModifierProvider = () => ModifierKeys.None;
                vm.SelectedIndex = 2;
                Assert.True(RaiseKey(window, Key.Enter).Handled);
                Assert.Equal(focusedTarget, Assert.Single(Opened));
                Assert.Equal(focusedIndex, vm.SelectedIndex);
                Assert.Empty(openedContaining);
                Assert.Empty(preview.PreviewTargets);

                window.ShowLauncher();
                Pump(dispatcher, window);
                window.PinnedScroll.ScrollToHorizontalOffset(focusedIndex * 54);
                Pump(dispatcher, window);
                Assert.Same(focusedTile, Keyboard.Focus(focusedTile));
                Opened.Clear();
                Assert.False(RaiseKey(window, Key.Space).Handled);
                Assert.Empty(Opened);

                window.ModifierProvider = () => ModifierKeys.Control;
                Assert.True(RaiseKey(window, Key.Space).Handled);
                PumpUntil(dispatcher, () => preview.PreviewTargets.Count == 1);
                Assert.Equal(focusedTarget, Assert.Single(preview.PreviewTargets));
                Assert.Equal(focusedIndex, vm.SelectedIndex);

                Assert.True(RaiseKey(window, Key.Enter).Handled);
                Assert.Equal(focusedTarget, Assert.Single(openedContaining));
                Assert.Equal(focusedIndex, vm.SelectedIndex);
                Assert.Empty(Opened);
                window.ModifierProvider = () => Keyboard.Modifiers;

                // Enter from the search box follows aggregate selection; tile clicks still open
                // the hit row even after selection moves elsewhere.
                window.ShowLauncher();
                Pump(dispatcher, window);
                Assert.Same(window.SearchBox, Keyboard.Focus(window.SearchBox));
                vm.SelectedIndex = 0;
                Assert.True(vm.MoveSelection(5));
                var clicked = vm.HomePins[5];
                var source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(window));
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(enter);
                Assert.True(enter.Handled);
                Assert.Equal(clicked.Model.Target, Assert.Single(Opened));

                window.ShowLauncher();
                Pump(dispatcher, window);
                vm.SelectedIndex = 0;
                Opened.Clear();
                var clickedTileContainer = Assert.IsAssignableFrom<FrameworkElement>(window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(5));
                var clickedTile = Assert.IsType<Button>(FindDescendant<Button>(clickedTileContainer));
                clickedTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clickedTile));
                Assert.Equal(5, vm.SelectedIndex);
                Assert.Equal(clicked.Model.Target, Assert.Single(Opened));

                // The tile context menu commands are bound to that same clicked tile, including unpin.
                var menu = Assert.IsType<ContextMenu>(clickedTile.ContextMenu);
                menu.DataContext = clicked;
                foreach (var menuItem in menu.Items.OfType<MenuItem>())
                    menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                var unpin = menu.Items.OfType<MenuItem>().Single(item => item.Command == clicked.TogglePinCommand);
                Assert.Equal("取消置顶", unpin.Header);
                unpin.Command!.Execute(unpin.CommandParameter);
                Assert.False(history.IsPinned(clicked.Model));
                Assert.False(clicked.IsPinned);

                // Existing pins still participate in matching searches and are marked there; the tile
                // strip collapses as soon as the query becomes nonblank.
                vm.Query = "pinned";
                Pump(dispatcher, window);
                Assert.False(vm.IsHomeQuery);
                Assert.False(vm.ShowHomeTiles);
                Assert.Equal(Visibility.Collapsed, window.PinnedScroll.Visibility);
                vm.SetScope(SearchScope.Applications);
                PumpUntil(dispatcher, () => !vm.IsSearching && vm.Results.Count == 1);
                Assert.True(vm.Results[0].IsPinned);

                // Removing all remaining pins and returning to blank query leaves no empty tile band.
                foreach (var row in history.GetHome(SearchScope.All, 60).Where(item => item.IsPinned).ToArray())
                    Assert.True(history.TrySetPinned(row, false));
                vm.Query = string.Empty;
                var expectedHomeCount = history.GetHome(SearchScope.Applications, int.MaxValue).Count;
                vm.Prime();
                PumpUntil(dispatcher, () => !vm.IsSearching && vm.IsHomeQuery && vm.HomePins.Count == 0
                    && vm.Results.Count == expectedHomeCount);
                Pump(dispatcher, window);
                Assert.False(vm.ShowHomeTiles);
                Assert.Equal(Visibility.Collapsed, window.PinnedScroll.Visibility);
                Assert.True(vm.Results.All(row => !row.IsPinned));

                window.AllowClose();
                window.Close();
                vm.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        });
    }

    [Fact]
    public async Task Wheel_over_tiles_or_history_scrolls_the_whole_home_while_the_header_stays_put()
    {
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            Assert.True(window.HomeScroll.ScrollableHeight > 0);
            Assert.Equal(Visibility.Visible, window.HomeScroll.ComputedVerticalScrollBarVisibility);
            Assert.Equal(0, FindDescendant<ScrollViewer>(window.HomeHistoryList)!.ScrollableHeight);
            var tileTop = BoundsIn(window.HomeScroll, window.PinnedScroll).Top;
            var historyTop = BoundsIn(window.HomeScroll, window.HomeHistoryList).Top;
            var footerTop = BoundsIn(window, window.ThemeToggleButton).Top;

            foreach (var hit in new UIElement[] { TileAt(window, 0), window.HomeHistoryList })
            {
                window.HomeScroll.ScrollToTop();
                Pump(dispatcher, window);
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = Mouse.PreviewMouseWheelEvent,
                };
                hit.RaiseEvent(wheel);
                Pump(dispatcher, window);
                Assert.True(wheel.Handled);
                Assert.True(window.HomeScroll.VerticalOffset > 0, "nested viewers must not swallow the home wheel");
                Assert.InRange(BoundsIn(window.HomeScroll, window.PinnedScroll).Top,
                    tileTop - window.HomeScroll.VerticalOffset - 1, tileTop - window.HomeScroll.VerticalOffset + 1);
                Assert.InRange(BoundsIn(window.HomeScroll, window.HomeHistoryList).Top,
                    historyTop - window.HomeScroll.VerticalOffset - 1, historyTop - window.HomeScroll.VerticalOffset + 1);
                Assert.Equal(footerTop, BoundsIn(window, window.ThemeToggleButton).Top);
            }

            window.HomeScroll.ScrollToVerticalOffset(300);
            Pump(dispatcher, window);
            Assert.True(BoundsIn(window.HomeScroll, window.PinnedScroll).Bottom < 0,
                "pins must scroll out of view, not remain fixed above history");
            Assert.True(RaiseKey(window, Key.Home).Handled);
            Pump(dispatcher, window);
            Assert.Equal(0, window.HomeScroll.VerticalOffset);
            AssertHomeSelectionVisible(window, vm);
        });
    }

    [Fact]
    public async Task Left_right_from_empty_query_span_pins_and_history_wrap_and_keep_selection_visible()
    {
        await RunHomeTest((dispatcher, window, vm, preview) =>
        {
            Assert.True(window.SearchBox.IsKeyboardFocusWithin);
            var count = vm.HomePins.Count + vm.Results.Count;
            Assert.True(RaiseKey(window, Key.Left).Handled);
            Pump(dispatcher, window);
            Assert.Equal(count - 1, vm.SelectedIndex);
            AssertHomeSelectionVisible(window, vm);
            Assert.True(window.HomeScroll.VerticalOffset > 0);

            Assert.True(RaiseKey(window, Key.Right).Handled);
            Pump(dispatcher, window);
            Assert.Equal(0, vm.SelectedIndex);
            Assert.Equal(0, window.HomeScroll.VerticalOffset);
            for (var index = 1; index < count; index++)
            {
                Assert.True(RaiseKey(window, Key.Right).Handled);
                Pump(dispatcher, window);
                Assert.Equal(index, vm.SelectedIndex);
                AssertHomeSelectionVisible(window, vm);
                if (index == vm.HomePins.Count - 1)
                    Assert.True(window.PinnedScroll.HorizontalOffset > 0);
            }
            Assert.True(RaiseKey(window, Key.Left).Handled);
            Pump(dispatcher, window);
            Assert.Equal(count - 2, vm.SelectedIndex);
            AssertHomeSelectionVisible(window, vm);
            Assert.True(window.SearchBox.IsKeyboardFocusWithin);
            Assert.NotEmpty(preview.SwitchTargets);
            Assert.Equal(vm.SelectedItem!.Model.Target, preview.SwitchTargets[^1]);
            Assert.Empty(preview.PreviewTargets);
        });
    }

    [Fact]
    public async Task Focused_tile_arrows_follow_focus_and_enter_opens_the_new_tile_not_the_old_one()
    {
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            Assert.Same(TileAt(window, 7), Keyboard.Focus(TileAt(window, 7)));
            vm.SelectedIndex = 1; // Deliberately stale aggregate selection.
            Assert.True(RaiseKey(window, Key.Right).Handled);
            Pump(dispatcher, window);
            Assert.Equal(8, vm.SelectedIndex);
            Assert.Same(TileAt(window, 8), Keyboard.FocusedElement);
            AssertHomeSelectionVisible(window, vm);

            Assert.True(RaiseKey(window, Key.Left).Handled);
            Pump(dispatcher, window);
            Assert.Equal(7, vm.SelectedIndex);
            Assert.Same(TileAt(window, 7), Keyboard.FocusedElement);
            Assert.True(RaiseKey(window, Key.Enter).Handled);
            Assert.Equal(vm.HomePins[7].Model.Target, Assert.Single(Opened));

            window.ShowLauncher();
            Pump(dispatcher, window);
            Assert.Same(TileAt(window, vm.HomePins.Count - 1), Keyboard.Focus(TileAt(window, vm.HomePins.Count - 1)));
            Assert.True(RaiseKey(window, Key.Right).Handled);
            Pump(dispatcher, window);
            Assert.Equal(vm.HomePins.Count, vm.SelectedIndex);
            Assert.True(window.SearchBox.IsKeyboardFocusWithin);
            AssertHomeSelectionVisible(window, vm);
        });
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    public async Task Horizontal_navigation_does_not_steal_query_editing_modified_arrows_or_menu_keys(Key key)
    {
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt })
            {
                window.ModifierProvider = () => modifiers;
                Assert.False(RaiseKey(window, key).Handled);
                Assert.Equal(0, vm.SelectedIndex);
            }
            window.ModifierProvider = () => ModifierKeys.None;
            vm.Query = " "; // Blank home query, but an editable character is present.
            Pump(dispatcher, window);
            Assert.True(vm.ShowHomeTiles);
            Assert.False(RaiseKey(window, key).Handled);
            vm.Query = "pin";
            Pump(dispatcher, window);
            Assert.False(RaiseKey(window, key).Handled);
            vm.Query = string.Empty;
            vm.Prime();
            PumpUntil(dispatcher, () => vm.ShowHomeTiles && !vm.IsSearching);
            Pump(dispatcher, window);

            var tile = TileAt(window, 0);
            var menu = Assert.IsType<ContextMenu>(tile.ContextMenu);
            menu.PlacementTarget = tile;
            try
            {
                menu.IsOpen = true;
                Pump(dispatcher, window);
                Assert.True(menu.IsOpen);
                Assert.True(window.IsDeactivationSuppressed);
                Assert.False(RaiseKey(window, key).Handled);
                Assert.Equal(0, vm.SelectedIndex);
            }
            finally
            {
                menu.IsOpen = false;
                PumpUntil(dispatcher, () => !window.IsDeactivationSuppressed);
            }
        });
    }

    [Fact]
    public async Task Home_without_pins_keeps_history_scrolling_but_does_not_capture_horizontal_arrows()
    {
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            Assert.False(vm.ShowHomeTiles);
            Assert.Equal(Visibility.Collapsed, window.PinnedScroll.Visibility);
            Assert.False(RaiseKey(window, Key.Left).Handled);
            Assert.False(RaiseKey(window, Key.Right).Handled);
            Assert.True(RaiseKey(window, Key.End).Handled);
            Pump(dispatcher, window);
            Assert.Equal(vm.Results.Count - 1, vm.SelectedIndex);
            AssertHomeSelectionVisible(window, vm);
            Assert.True(RaiseKey(window, Key.Home).Handled);
            Pump(dispatcher, window);
            Assert.Equal(0, window.HomeScroll.VerticalOffset);
        }, pinCount: 0);
    }

    [Fact]
    public async Task Pins_only_home_is_compact_and_horizontal_navigation_wraps_without_history()
    {
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            Assert.Empty(vm.Results);
            Assert.Equal(0, window.HomeScroll.ScrollableHeight);
            Assert.True(window.ActualHeight < 300);
            Assert.True(RaiseKey(window, Key.Left).Handled);
            Pump(dispatcher, window);
            Assert.Equal(vm.HomePins.Count - 1, vm.SelectedIndex);
            AssertHomeSelectionVisible(window, vm);
            Assert.True(RaiseKey(window, Key.Right).Handled);
            Pump(dispatcher, window);
            Assert.Equal(0, vm.SelectedIndex);
            AssertHomeSelectionVisible(window, vm);
        }, recentCount: 0);
    }

    [Fact]
    public async Task Only_application_pins_are_tiles_and_pinned_files_and_folders_stay_in_the_list()
    {
        var app = Item(0);
        var file = Item(80) with
        {
            Kind = ResultKind.File, Title = "Pinned document.txt", Target = @"C:\Docs\Pinned document.txt",
            TypeLabel = "文件", FolderPath = @"C:\Docs", IconPath = @"C:\Icons\document.ico", IconIndex = 2,
        };
        var folder = Item(81) with
        {
            Kind = ResultKind.Folder, Title = "Pinned folder", Target = @"C:\Docs\Pinned folder",
            TypeLabel = "文件夹", IsFolder = true, FolderPath = @"C:\Docs\Pinned folder",
        };
        var recent = Item(82) with { Kind = ResultKind.File, Title = "Recent.txt", Target = @"C:\Docs\Recent.txt" };
        await RunHomeTest((dispatcher, window, vm, _) =>
        {
            Assert.Equal(app.Target, Assert.Single(vm.HomePins).Model.Target);
            Assert.Equal(3, vm.Results.Count);
            Assert.All(vm.Results.Take(2), row => Assert.True(row.IsPinned));
            Assert.Equal(new[] { file.Target, folder.Target }.OrderBy(target => target),
                vm.Results.Take(2).Select(row => row.Model.Target).OrderBy(target => target));
            Assert.Equal(recent.Target, vm.Results[2].Model.Target);
            var fileRow = vm.Results.Single(row => row.Model.Target == file.Target);
            Assert.Equal(file.IconPath, fileRow.Model.IconPath);
            Assert.Equal(file.IconIndex, fileRow.Model.IconIndex);
            Assert.Equal(file.FolderPath, fileRow.Model.FolderPath);
            Assert.Equal("取消置顶", fileRow.PinHeader);

            // Aggregate navigation crosses from the only tile into a normal pinned-file/folder row.
            Assert.True(RaiseKey(window, Key.Right).Handled);
            Pump(dispatcher, window);
            Assert.Equal(1, vm.SelectedIndex);
            Assert.Same(vm.Results[0], vm.SelectedItem);
            AssertHomeSelectionVisible(window, vm);
            var openedTarget = vm.SelectedItem!.Model.Target;
            Assert.True(RaiseKey(window, Key.Enter).Handled);
            Assert.Equal(openedTarget, Assert.Single(Opened));

            window.ShowLauncher();
            vm.SetScope(SearchScope.Files);
            PumpUntil(dispatcher, () => !vm.IsSearching && vm.HomePins.Count == 0 && vm.Results.Count == 3);
            Pump(dispatcher, window);
            Assert.False(vm.ShowHomeTiles);
            Assert.Equal(Visibility.Collapsed, window.PinnedScroll.Visibility);
            Assert.False(RaiseKey(window, Key.Right).Handled);
            Assert.All(vm.Results.Take(2), row => Assert.True(row.IsPinned));

            // A file that was actually opened survives unpinning as real history, never as a tile.
            var openedRow = vm.Results.Single(row => row.Model.Target == openedTarget);
            openedRow.TogglePinCommand.Execute(null);
            PumpUntil(dispatcher, () => !vm.IsSearching && vm.Results.All(row =>
                row.Model.Target != openedTarget || !row.IsPinned));
            Assert.Contains(vm.Results, row => row.Model.Target == openedTarget && !row.IsPinned);
            var pinOnlyRow = vm.Results.Single(row => row.IsPinned);
            var pinOnlyTarget = pinOnlyRow.Model.Target;
            pinOnlyRow.TogglePinCommand.Execute(null);
            PumpUntil(dispatcher, () => !vm.IsSearching && vm.Results.All(row => row.Model.Target != pinOnlyTarget));
            Assert.Equal(2, vm.Results.Count);
            Assert.Empty(vm.HomePins);
        }, pins: new[] { app, file, folder }, recents: new[] { recent });
    }

    [Fact]
    public async Task Mouse_selection_of_home_history_and_pins_switches_without_toggling_and_keeps_tile_open_behavior()
    {
        await RunHomeTest((dispatcher, window, vm, preview) =>
        {
            var historyRow = vm.Results[1];
            var historyContainer = Assert.IsType<ListBoxItem>(window.HomeHistoryList.ItemContainerGenerator.ContainerFromIndex(1));
            RaiseMouseDown(Assert.IsType<Border>(historyContainer.Template.FindName("RowBackground", historyContainer)));
            Pump(dispatcher, window);
            Assert.Equal(vm.HomePins.Count + 1, vm.SelectedIndex);
            Assert.Same(historyRow, vm.SelectedItem);
            Assert.Equal(new[] { historyRow.Model.Target }, preview.SwitchTargets);
            Assert.True(window.SearchBox.IsKeyboardFocusWithin);
            Assert.Empty(preview.PreviewTargets);
            Assert.Empty(Opened);

            var pin = vm.HomePins[1];
            var pinContainer = Assert.IsAssignableFrom<FrameworkElement>(window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(1));
            var button = Assert.IsType<Button>(FindDescendant<Button>(pinContainer));
            RaiseMouseDown(button);
            Pump(dispatcher, window);
            Assert.Equal(1, vm.SelectedIndex);
            Assert.Equal(new[] { historyRow.Model.Target, pin.Model.Target }, preview.SwitchTargets);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Assert.Equal(pin.Model.Target, Assert.Single(Opened));
            Assert.Equal(2, preview.SwitchTargets.Count);
            Assert.Empty(preview.PreviewTargets);
        }, pinCount: 3, recentCount: 4);
    }

    private static void RaiseMouseDown(UIElement hit)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, 1);
        hit.RaiseEvent(args);
    }

    private static Task RunHomeTest(Action<Dispatcher, MainWindow, LauncherViewModel, RecordingPreview> action,
        int pinCount = 12, int recentCount = 30, IReadOnlyList<SearchResultItem>? pins = null,
        IReadOnlyList<SearchResultItem>? recents = null) => RunSta(dispatcher =>
    {
        Opened.Clear();
        var path = Path.Combine(Path.GetTempPath(), "r1ng-home-scroll-" + Guid.NewGuid().ToString("N") + ".json");
        MainWindow? window = null;
        LauncherViewModel? vm = null;
        try
        {
            var history = new SearchHistoryStore(path);
            foreach (var pin in pins ?? Enumerable.Range(0, pinCount).Select(Item).ToArray())
                Assert.True(history.TrySetPinned(pin, true));
            foreach (var recent in recents ?? Enumerable.Range(40, recentCount).Select(Item).ToArray())
                history.Record(recent);
            var home = history.GetHome(SearchScope.All, 60);
            var tileCount = home.Count(item => item.IsPinned && item.Kind == ResultKind.Application);
            var rowCount = home.Count - tileCount;
            var preview = new RecordingPreview();
            vm = new LauncherViewModel(new SearchController(new MutableApps(), new EmptyFiles(), history),
                history, dispatcher, openTarget: item => { Opened.Add(item.Target); return true; },
                iconLoader: (_, _, _) => null, previewService: preview);
            window = new MainWindow(vm, new TestLauncherTrayIcon(),
                quickLookWindowLocator: new EmptyQuickLookLocator(), launcherWindowPlacement: new NoOpLauncherPlacement());
            window.Initialize(registerHotKeys: false);
            window.ModifierProvider = () => ModifierKeys.None;
            var monitor = ScreenPlacement.SampleCursorMonitor();
            window.SampleMonitor = () => monitor with
            {
                WorkArea = new ScreenPlacement.PixelsRect(monitor.WorkArea.Left, monitor.WorkArea.Top,
                    monitor.WorkArea.Left + (int)(420 * monitor.DpiScale),
                    monitor.WorkArea.Top + (int)(330 * monitor.DpiScale)),
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/R1ngLauncher;component/Themes/Controls.xaml", UriKind.Absolute),
            });
            ThemeManager.Apply(AppTheme.Dark, window.Resources);
            vm.Prime();
            PumpUntil(dispatcher, () => !vm.IsSearching && vm.HomePins.Count == tileCount && vm.Results.Count == rowCount);
            window.ShowLauncher();
            Pump(dispatcher, window);
            action(dispatcher, window, vm, preview);
        }
        finally
        {
            window?.AllowClose();
            window?.Close();
            vm?.Dispose();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    });

    private static Button TileAt(MainWindow window, int index) => Assert.IsType<Button>(FindDescendant<Button>(
        Assert.IsAssignableFrom<DependencyObject>(window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(index))));

    private static Rect BoundsIn(Visual parent, FrameworkElement element) =>
        element.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void AssertHomeSelectionVisible(MainWindow window, LauncherViewModel vm)
    {
        var index = vm.SelectedIndex;
        var container = Assert.IsAssignableFrom<FrameworkElement>(index < vm.HomePins.Count
            ? window.HomePinsList.ItemContainerGenerator.ContainerFromIndex(index)
            : window.HomeHistoryList.ItemContainerGenerator.ContainerFromIndex(index - vm.HomePins.Count));
        var bounds = BoundsIn(window.HomeScroll, container);
        Assert.True(bounds.Top >= -1 && bounds.Bottom <= window.HomeScroll.ViewportHeight + 1,
            $"home selection {index}: {bounds.Top}..{bounds.Bottom}, viewport {window.HomeScroll.ViewportHeight}");
        if (index < vm.HomePins.Count)
        {
            var horizontal = BoundsIn(window.PinnedScroll, container);
            Assert.True(horizontal.Left >= -1 && horizontal.Right <= window.PinnedScroll.ViewportWidth + 1,
                $"tile {index}: {horizontal.Left}..{horizontal.Right}, viewport {window.PinnedScroll.ViewportWidth}");
        }
    }

    private static readonly List<string> Opened = new();

    private static SearchResultItem Item(int id) => new()
    {
        Id = $"tile-{id}", Kind = ResultKind.Application, Title = $"Pin {id:00}",
        Detail = "Test application", TypeLabel = "应用", Source = "开始菜单",
        Target = $@"C:\Apps\tile-{id}.exe", Score = 1_000 - id,
    };

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static KeyEventArgs RaiseKey(MainWindow window, Key key)
    {
        var source = PresentationSource.FromVisual(window);
        Assert.NotNull(source);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
        return args;
    }

    private static void Pump(Dispatcher dispatcher, MainWindow window)
    {
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        window.UpdateLayout();
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "the home search/layout did not settle before timeout");
    }

    private static Task RunSta(Action<Dispatcher> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(Dispatcher.CurrentDispatcher); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true, Name = "R1ng pinned tiles test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class MutableApps : IAppSearchProvider
    {
        public IReadOnlyList<SearchResultItem> Items { get; set; } = [];
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Items.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class EmptyFiles : IFileSearchProvider
    {
        public Task<FileSearchOutcome> SearchAsync(string query, CancellationToken cancellationToken,
            FileSearchOptions options = default, string? folderConstraint = null) =>
            Task.FromResult(new FileSearchOutcome(FileSearchAvailability.Ready(), Array.Empty<FileSearchHit>()));
    }

    private sealed class RecordingPreview : IQuickLookPreviewService
    {
        public List<string> PreviewTargets { get; } = new();
        public List<string> SwitchTargets { get; } = new();
        public Task<QuickLookPreviewResult> CloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuickLookPreviewResult(true, "test close"));
        public bool CanPreview(SearchResultItem item) => true;
        public Task<QuickLookPreviewResult> PreviewAsync(SearchResultItem item, CancellationToken cancellationToken = default)
        {
            PreviewTargets.Add(item.Target);
            return Task.FromResult(new QuickLookPreviewResult(true, "test preview"));
        }
        public Task<QuickLookPreviewResult> SwitchAsync(SearchResultItem item, CancellationToken cancellationToken = default)
        {
            SwitchTargets.Add(item.Target);
            return Task.FromResult(new QuickLookPreviewResult(true, "test switch"));
        }
    }

    private sealed class EmptyQuickLookLocator : IQuickLookWindowLocator
    {
        public Task<IReadOnlyList<QuickLookWindowCandidate>> FindVisiblePreviewsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuickLookWindowCandidate>>(Array.Empty<QuickLookWindowCandidate>());
    }

    private sealed class NoOpLauncherPlacement : ILauncherWindowPlacement
    {
        public bool TryCapture(IntPtr handle, out LauncherWindowSnapshot snapshot)
        {
            snapshot = default;
            return false;
        }
        public bool TryMove(IntPtr handle, int x, int y) => false;
    }
}
