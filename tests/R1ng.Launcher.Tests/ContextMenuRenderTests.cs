using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Real WPF STA checks for the result-row context menu. The menu is taken from the actual
/// <c>MainWindow</c> result <c>DataTemplate</c> (the realized list row), styled by the real
/// production <c>Themes/Controls.xaml</c> and the real palette, then templated, measured, arranged
/// and rendered offscreen. The popup is never opened (no foreground/mouse capture), no hotkey is
/// registered, and no production command is executed, so nothing is launched and the clipboard is
/// never touched; command wiring is asserted by identity.
///
/// An unopened ContextMenu is not part of the window tree, so — like the application scope that
/// App.xaml provides in production — the window's resource scope and the row's data context are
/// attached to it explicitly (WPF does the equivalent when it opens the popup).
/// </summary>
public class ContextMenuRenderTests
{
    /// <summary>Finite bounds used only to realize the result rows; the window's own Height is never touched.</summary>
    private static readonly Size RealizeBounds = new(760, 520);

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task The_menu_uses_the_themed_template_with_no_system_colored_gutter(AppTheme theme)
    {
        await RunAsync(theme, fixture =>
        {
            var menu = fixture.Menu;
            var palette = ThemePalette.Create(theme);

            // The production template, not WPF's default one (which has the icon gutter).
            var chrome = Assert.IsType<Border>(menu.Template.FindName("MenuChrome", menu));
            AssertBrush(palette, "DropdownBackgroundBrush", chrome.Background);
            AssertBrush(palette, "DropdownBorderBrush", chrome.BorderBrush);
            Assert.Equal(new Thickness(1), chrome.BorderThickness);
            Assert.True(chrome.CornerRadius.TopLeft > 0, "the menu border must be rounded");
            Assert.Same(chrome, VisualTreeHelper.GetChild(menu, 0));

            // Nothing in the rendered tree is a system gutter: no shapes at all, and every border,
            // panel and text brush is transparent or a colour of the active palette.
            var allowed = PaletteColors(palette);
            var visuals = Descendants(menu).ToList();
            Assert.DoesNotContain(visuals, visual => visual is Shape);
            foreach (var visual in visuals)
            {
                switch (visual)
                {
                    case Border border:
                        AssertThemed(border.Background, allowed, "Border.Background");
                        AssertThemed(border.BorderBrush, allowed, "Border.BorderBrush");
                        break;
                    case Panel panel:
                        AssertThemed(panel.Background, allowed, "Panel.Background");
                        break;
                    case TextBlock text:
                        AssertThemed(text.Foreground, allowed, "TextBlock.Foreground");
                        break;
                }
            }

            // Pixels, not just brushes: the area to the left of the item text is the plain dropdown
            // surface (a default icon gutter would paint a grey strip here), the separator is the
            // themed hairline inside its margin, and the rounded corner is transparent.
            var pixels = Pixels.Render(menu);
            var surface = BrushColor(palette, "DropdownBackgroundBrush");
            var hairline = Over(BrushColor(palette, "DropdownBorderBrush"), surface);

            Assert.Equal(0, pixels.At(0, 0).A);

            foreach (var item in fixture.Items)
            {
                var bounds = BoundsIn(menu, item);
                var y = (int)Math.Round(bounds.Top + bounds.Height / 2);
                for (var x = (int)Math.Ceiling(bounds.Left) + 1; x <= (int)Math.Ceiling(bounds.Left) + 8; x++)
                    AssertPixel(surface, pixels.At(x, y), $"left of '{item.Header}' at x={x}");

                // Right of the shortcut text, up to the border.
                for (var x = (int)Math.Floor(bounds.Right) - 8; x < (int)Math.Floor(bounds.Right); x++)
                    AssertPixel(surface, pixels.At(x, y), $"right of '{item.Header}' at x={x}");
            }

            var separator = fixture.Separator;
            var separatorBounds = BoundsIn(menu, separator);
            var midX = (int)Math.Round(separatorBounds.Left + separatorBounds.Width / 2);
            var line = Enumerable
                .Range((int)Math.Floor(separatorBounds.Top) - 1, 4)
                .Select(row => Distance(hairline, pixels.At(midX, row)))
                .Min();
            Assert.True(line <= 3, $"the separator hairline is not the themed colour (distance {line})");
            AssertPixel(surface, pixels.At(midX, (int)Math.Floor(separatorBounds.Top) - 2), "above the separator");
            AssertPixel(surface, pixels.At(midX, (int)Math.Ceiling(separatorBounds.Bottom) + 2), "below the separator");
        });
    }

    [Fact]
    public async Task The_menu_separator_gets_the_themed_hairline_style()
    {
        await RunAsync(AppTheme.Dark, fixture =>
        {
            var separator = fixture.Separator;

            // WPF styles a Separator inside a menu through MenuItem.SeparatorStyleKey, which wins
            // over an implicit Separator style; the production dictionary must register that key.
            Assert.Same(
                fixture.Window.Resources[MenuItem.SeparatorStyleKey],
                separator.Style
            );

            var line = Assert.IsType<Border>(VisualTreeHelper.GetChild(separator, 0));
            AssertBrush(ThemePalette.Create(AppTheme.Dark), "DropdownBorderBrush", line.Background);
            Assert.Equal(1, line.Height);
            Assert.Equal(new Thickness(6, 4, 6, 4), separator.Margin);
            Assert.Equal(1, separator.ActualHeight);
        });
    }

    [Fact]
    public async Task The_same_menu_follows_a_live_palette_swap_without_being_rebuilt()
    {
        await RunAsync(AppTheme.Dark, fixture =>
        {
            var menu = fixture.Menu;
            var chrome = Assert.IsType<Border>(menu.Template.FindName("MenuChrome", menu));
            var separatorLine = Assert.IsType<Border>(VisualTreeHelper.GetChild(fixture.Separator, 0));
            var menuBorder = ItemBorder(fixture.Items[0]);

            AssertBrush(ThemePalette.Create(AppTheme.Dark), "DropdownBackgroundBrush", chrome.Background);

            ThemeManager.Apply(AppTheme.Light, fixture.Window.Resources);
            LayoutMenu(menu);

            var light = ThemePalette.Create(AppTheme.Light);
            AssertBrush(light, "DropdownBackgroundBrush", chrome.Background);
            AssertBrush(light, "DropdownBorderBrush", chrome.BorderBrush);
            AssertBrush(light, "DropdownBorderBrush", separatorLine.Background);
            AssertBrush(light, "TextPrimaryBrush", fixture.Items[0].Foreground);

            SetHighlighted(fixture.Items[0], true);
            AssertBrush(light, "DropdownHoverBackgroundBrush", menuBorder.Background);

            // And it has pixels of the light surface, not stale dark ones.
            var pixels = Pixels.Render(menu);
            var bounds = BoundsIn(menu, fixture.Items[1]);
            AssertPixel(
                BrushColor(light, "DropdownBackgroundBrush"),
                pixels.At((int)Math.Ceiling(bounds.Left) + 3, (int)Math.Round(bounds.Top + bounds.Height / 2)),
                "light surface after swap"
            );
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Items_keep_their_text_shortcuts_commands_highlight_and_disabled_state(AppTheme theme)
    {
        await RunAsync(theme, fixture =>
        {
            var palette = ThemePalette.Create(theme);
            var items = fixture.Items;

            // Commands bound to the row's own view model and shortcut text come through the template.
            Assert.Equal(
                new[] { "打开", "打开所在位置", "QuickLook 预览", "复制路径", "置顶到历史", "隐藏此项" },
                items.Select(item => item.Header)
            );
            Assert.Equal(
                new[] { "Enter", "Ctrl+Enter", "Ctrl+Space", "Ctrl+C", "", "" },
                items.Select(item => item.InputGestureText)
            );
            Assert.Same(fixture.Item.OpenCommand, items[0].Command);
            Assert.Same(fixture.Item.OpenContainingFolderCommand, items[1].Command);
            Assert.Same(fixture.Item.PreviewCommand, items[2].Command);
            Assert.Same(fixture.Item.CopyPathCommand, items[3].Command);
            Assert.Same(fixture.Item.TogglePinCommand, items[4].Command);
            Assert.Same(fixture.Item.HideCommand, items[5].Command);
            Assert.All(items, item =>
            {
                Assert.True(item.IsEnabled);
                Assert.True(item.Command.CanExecute(null));
                Assert.True(item.Focusable);
                Assert.Null(item.FocusVisualStyle);
            });

            foreach (var item in items)
            {
                var texts = Descendants(item).OfType<TextBlock>().Select(block => block.Text).ToList();
                Assert.Contains((string)item.Header, texts);
                Assert.Contains(item.InputGestureText, texts);
                Assert.All(
                    Descendants(item).OfType<TextBlock>().Where(block => block.Text == (string)item.Header),
                    block => AssertBrush(palette, "TextPrimaryBrush", block.Foreground)
                );
            }

            // Highlight: the themed hover brush on that item only, in the rendered pixels as well.
            var surface = BrushColor(palette, "DropdownBackgroundBrush");
            var hover = Over(BrushColor(palette, "DropdownHoverBackgroundBrush"), surface);
            Assert.True(Distance(surface, hover) > 4, "the highlight must be visible against the surface");

            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<SolidColorBrush>(ItemBorder(items[1]).Background).Color);
            SetHighlighted(items[1], true);
            AssertBrush(palette, "DropdownHoverBackgroundBrush", ItemBorder(items[1]).Background);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<SolidColorBrush>(ItemBorder(items[0]).Background).Color);

            var pixels = Pixels.Render(fixture.Menu);
            var bounds = BoundsIn(fixture.Menu, items[1]);
            var x = (int)Math.Ceiling(bounds.Left) + 3;
            AssertPixel(hover, pixels.At(x, (int)Math.Round(bounds.Top + bounds.Height / 2)), "highlighted item");
            var other = BoundsIn(fixture.Menu, items[0]);
            AssertPixel(surface, pixels.At(x, (int)Math.Round(other.Top + other.Height / 2)), "neighbour of the highlighted item");

            SetHighlighted(items[1], false);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<SolidColorBrush>(ItemBorder(items[1]).Background).Color);

            // Disabled: dimmed, and a command that cannot execute disables the item and follows
            // CanExecuteChanged. The command here is a stand-in, never a production one.
            Assert.Equal(1.0, items[0].Opacity);
            items[0].IsEnabled = false;
            Assert.Equal(0.5, items[0].Opacity);
            items[0].IsEnabled = true;
            Assert.Equal(1.0, items[0].Opacity);

            var allowed = false;
            var executed = 0;
            var gated = new RelayCommand(() => executed++, () => allowed);
            var blocked = new MenuItem { Header = "受限", Command = gated, InputGestureText = "F2" };
            fixture.Menu.Items.Add(blocked);
            LayoutMenu(fixture.Menu);

            Assert.False(blocked.IsEnabled);
            Assert.Equal(0.5, blocked.Opacity);
            allowed = true;
            gated.RaiseCanExecuteChanged();
            Assert.True(blocked.IsEnabled);
            Assert.Equal(1.0, blocked.Opacity);
            Assert.Equal(0, executed);
        });
    }

    [Fact]
    public async Task A_menu_taller_than_the_screen_keeps_every_item_reachable_by_scrolling()
    {
        await RunAsync(AppTheme.Dark, fixture =>
        {
            var menu = fixture.Menu;
            var palette = ThemePalette.Create(AppTheme.Dark);

            // The menu is capped at the work area rather than left to overflow the screen.
            Assert.Equal(SystemParameters.WorkArea.Height, menu.MaxHeight);

            // A tiny screen: offer far less than the menu needs.
            const double tiny = 60;
            LayoutMenu(menu, tiny);

            var scroll = Assert.IsType<ScrollViewer>(menu.Template.FindName("MenuScroll", menu));
            Assert.True(menu.DesiredSize.Height <= tiny + 0.5, $"the menu ignores the available height ({menu.DesiredSize.Height})");
            Assert.True(scroll.ScrollableHeight > 0, "the overflow must be scrollable");
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            Assert.Equal(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);

            // Nothing was dropped: every item (and the separator) is still there and realized.
            Assert.Equal(7, menu.Items.Count);
            for (var index = 0; index < menu.Items.Count; index++)
                Assert.Same(menu.Items[index], menu.ItemContainerGenerator.ContainerFromIndex(index));
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);

            // The scrollbar is the themed thin bar, not the system one.
            var bar = Assert.IsType<ScrollBar>(Descendants(scroll).OfType<ScrollBar>().Single());
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.True(bar.ActualWidth > 0);
            var thumb = Descendants(bar).OfType<Thumb>().Single();
            var thumbFace = Assert.IsType<Border>(VisualTreeHelper.GetChild(thumb, 0));
            AssertBrush(palette, "TextMutedBrush", thumbFace.Background);
            Assert.DoesNotContain(Descendants(scroll), visual => visual is Shape);

            // The last item can be scrolled into view, and the first returned to.
            var last = fixture.Items[^1];
            scroll.ScrollToBottom();
            scroll.UpdateLayout();
            var bottom = BoundsIn(scroll, last);
            Assert.True(bottom.Bottom <= scroll.ViewportHeight + 1, "the last item is not scrollable into view");
            Assert.True(bottom.Top >= -1);

            scroll.ScrollToTop();
            scroll.UpdateLayout();
            var top = BoundsIn(scroll, fixture.Items[0]);
            Assert.True(top.Top >= -1 && top.Top < scroll.ViewportHeight);
        });
    }

    [Fact]
    public async Task Keyboard_navigation_settings_survive_the_custom_template()
    {
        await RunAsync(AppTheme.Dark, fixture =>
        {
            var presenter = Descendants(fixture.Menu).OfType<ItemsPresenter>().Single();
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(presenter));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(presenter));

            // The scroll host must not take focus away from the items (arrow keys go to the menu).
            var scroll = Descendants(fixture.Menu).OfType<ScrollViewer>().Single();
            Assert.False(scroll.Focusable);

            // Items are real, focusable menu items in a stack in display order.
            Assert.Equal(
                new FrameworkElement[]
                {
                    fixture.Items[0],
                    fixture.Items[1],
                    fixture.Items[2],
                    fixture.Separator,
                    fixture.Items[3],
                    fixture.Items[4],
                    fixture.Items[5],
                },
                fixture.Menu.Items.Cast<FrameworkElement>()
            );
        });
    }

    [Fact]
    public async Task The_pin_header_binding_updates_in_the_existing_menu()
    {
        await RunAsync(AppTheme.Dark, fixture =>
        {
            Assert.Equal("置顶到历史", fixture.Items[4].Header);
            fixture.Item.IsPinned = true;
            LayoutMenu(fixture.Menu);
            Assert.Equal("取消置顶", fixture.Items[4].Header);
            Assert.Same(fixture.Item.TogglePinCommand, fixture.Items[4].Command);
            fixture.Item.IsPinned = false;
            LayoutMenu(fixture.Menu);
            Assert.Equal("置顶到历史", fixture.Items[4].Header);
        });
    }

    // ----- harness -----------------------------------------------------------------------------

    private sealed class Fixture
    {
        public required MainWindow Window { get; init; }

        public required LauncherViewModel ViewModel { get; init; }

        public required ResultItemViewModel Item { get; init; }

        public required ContextMenu Menu { get; init; }

        public MenuItem[] Items => Menu.Items.OfType<MenuItem>().ToArray();

        public Separator Separator => Menu.Items.OfType<Separator>().Single();
    }

    private static async Task RunAsync(AppTheme theme, Action<Fixture> body)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var viewModel = new LauncherViewModel(
                    new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
                    new EmptyHistoryProvider(),
                    dispatcher
                );

                window = new MainWindow(viewModel);

                // App is never created in a test: install the production control chrome and the
                // palette into the window's own scope (never the user's settings).
                ThemeRenderTests.LoadProductionControlStyles(window.Resources);
                ThemeManager.Apply(theme, window.Resources);

                var item = new ResultItemViewModel(
                    new SearchResultItem
                    {
                        Id = "app|notepad",
                        Kind = ResultKind.Application,
                        Title = "记事本",
                        Detail = @"C:\Windows\System32\notepad.exe",
                        TypeLabel = "应用",
                        Target = @"C:\Windows\System32\notepad.exe",
                        Source = "开始菜单",
                        Score = 1_000,
                    },
                    viewModel
                );
                viewModel.Results.Add(item);
                LayoutWindow(window);

                // The menu comes from the real, realized result row of the real window template.
                Assert.Same(window.Resources["ResultTemplate"], window.ResultsList.ItemTemplate);
                var container = Assert.IsType<ListBoxItem>(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(0));
                var row = Descendants(container).OfType<FrameworkElement>().First(element => element.ContextMenu is not null);
                Assert.Same(item, row.DataContext);

                var menu = row.ContextMenu!;
                menu.Resources = window.Resources;
                menu.DataContext = row.DataContext;
                menu.PlacementTarget = row;
                LayoutMenu(menu);

                body(new Fixture { Window = window, ViewModel = viewModel, Item = item, Menu = menu });

                window.AllowClose();
                window.Close();
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
            Name = "R1ng context menu test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(completed, "the context menu test timed out");
        Assert.Null(failure);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Realizes the result rows of a window that was never shown.</summary>
    private static void LayoutWindow(MainWindow window)
    {
        window.Measure(RealizeBounds);
        window.Arrange(new Rect(RealizeBounds));
        window.UpdateLayout();

        if (window.Content is FrameworkElement content)
        {
            content.Measure(RealizeBounds);
            content.Arrange(new Rect(RealizeBounds));
            content.UpdateLayout();
        }
    }

    private static void LayoutMenu(ContextMenu menu, double availableHeight = double.PositiveInfinity)
    {
        menu.InvalidateMeasure();
        menu.Measure(new Size(400, availableHeight));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
    }

    // ----- helpers -----------------------------------------------------------------------------

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;

            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    /// <summary>The named MenuBorder of the production MenuItem template.</summary>
    private static Border ItemBorder(MenuItem item) =>
        Assert.IsType<Border>(item.Template.FindName("MenuBorder", item));

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    /// <summary>
    /// IsHighlighted is read-only and only the mouse or keyboard focus sets it; drive it the way
    /// WPF does, through the property key, so the template trigger is exercised without input.
    /// </summary>
    private static void SetHighlighted(MenuItem item, bool highlighted)
    {
        var key = (DependencyPropertyKey?)typeof(MenuItem)
            .GetField("IsHighlightedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        Assert.NotNull(key);

        if (highlighted)
            item.SetValue(key!, true);
        else
            item.ClearValue(key!);

        item.UpdateLayout();
    }

    private static Color BrushColor(ResourceDictionary palette, string key) => ((SolidColorBrush)palette[key]).Color;

    private static void AssertBrush(ResourceDictionary palette, string key, Brush? actual)
    {
        var solid = Assert.IsAssignableFrom<SolidColorBrush>(actual);
        Assert.Equal(BrushColor(palette, key), solid.Color);
    }

    private static HashSet<Color> PaletteColors(ResourceDictionary palette)
    {
        var colors = new HashSet<Color> { Colors.Transparent, Color.FromArgb(0, 255, 255, 255) };
        foreach (var value in palette.Values.OfType<SolidColorBrush>())
            colors.Add(value.Color);

        return colors;
    }

    private static void AssertThemed(Brush? brush, HashSet<Color> allowed, string what)
    {
        if (brush is null)
            return;

        var solid = Assert.IsAssignableFrom<SolidColorBrush>(brush);
        Assert.True(allowed.Contains(solid.Color), $"{what} is not a palette colour: {solid.Color}");
    }

    /// <summary>Composites a (possibly translucent) colour over an opaque one.</summary>
    private static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static int Distance(Color expected, Color actual) =>
        Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));

    private static void AssertPixel(Color expected, Color actual, string where)
    {
        Assert.True(actual.A == 255, $"{where}: pixel is not opaque ({actual})");
        Assert.True(Distance(expected, actual) <= 2, $"{where}: expected {expected} but rendered {actual}");
    }

    /// <summary>A rendered offscreen frame that can be sampled per pixel.</summary>
    private sealed class Pixels
    {
        private readonly byte[] _data;
        private readonly int _stride;

        private Pixels(byte[] data, int stride)
        {
            _data = data;
            _stride = stride;
        }

        public static Pixels Render(FrameworkElement element)
        {
            var width = (int)Math.Ceiling(element.ActualWidth);
            var height = (int)Math.Ceiling(element.ActualHeight);
            Assert.True(width > 0 && height > 0, "nothing to render");

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);

            var stride = width * 4;
            var data = new byte[stride * height];
            bitmap.CopyPixels(data, stride, 0);
            return new Pixels(data, stride);
        }

        public Color At(int x, int y)
        {
            var offset = y * _stride + x * 4;
            return Color.FromArgb(_data[offset + 3], _data[offset + 2], _data[offset + 1], _data[offset]);
        }
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

        public void Record(SearchResultItem item)
        {
        }
    }
}
