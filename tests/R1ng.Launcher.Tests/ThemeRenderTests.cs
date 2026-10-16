using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Drives the real icon-only theme button and production control chrome on an STA thread. The palette
/// and <c>Themes/Controls.xaml</c> (the implicit styles App.xaml merges for context menus and tooltips)
/// are installed into the window's own resource scope — never the user's settings. The test proves
/// the button toggles live <c>DynamicResource</c> palettes, persists through the injected store,
/// exposes an automation name without any tooltip and renders dark/light frames offscreen. No window
/// is shown, no hotkey is registered and no foreground is taken.
/// </summary>
public class ThemeRenderTests
{
    [Fact]
    public async Task The_icon_theme_button_toggles_live_dynamic_resources_and_persists_without_a_tooltip()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), "r1ng-theme-" + Guid.NewGuid().ToString("N") + ".json");
        var artifactDirectory = FindRepoArtifactDirectory();

        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var store = new SettingsStore(settingsFile);

                // The apply seam mirrors App.xaml.cs: swap the palette and persist, but to a temp file.
                Action<AppTheme> applyTheme = theme =>
                {
                    if (window is not null)
                        ThemeManager.Apply(theme, window.Resources);

                    store.SaveTheme(theme);
                };

                using var viewModel = new LauncherViewModel(
                    new SearchController(new EmptyAppProvider(), new EmptyFileProvider()),
                    new EmptyHistoryProvider(),
                    dispatcher,
                    initialTheme: AppTheme.Dark,
                    applyTheme: applyTheme,
                    previewService: new LauncherActionTests.FakePreviewService()
                );

                window = new MainWindow(viewModel);

                // App is never created in a test, so production control styles are merged explicitly.
                LoadProductionControlStyles(window.Resources);
                ThemeManager.Apply(AppTheme.Dark, window.Resources);
                SeedResultSample(viewModel);
                Layout(window);
                RefreshBindings(window);
                Layout(window);

                AssertPalette(window, AppTheme.Dark);
                AssertThemeToggle(window, AppTheme.Dark);
                Render(window, artifactDirectory, "r1ng-launcher-dark.png");

                // Drive the actual theme button command; it applies and persists through the same seam.
                window.ThemeToggleButton.Command.Execute(null);
                window.ThemeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.ThemeToggleButton));
                Layout(window);
                RefreshBindings(window);
                Layout(window);

                Assert.Equal(AppTheme.Light, viewModel.Theme);
                AssertPalette(window, AppTheme.Light);
                AssertThemeToggle(window, AppTheme.Light);
                Render(window, artifactDirectory, "r1ng-launcher-light.png");

                Assert.True(File.Exists(settingsFile), "the theme choice must be persisted");
                Assert.Equal(AppTheme.Light, new SettingsStore(settingsFile).Load().Theme);

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
            Name = "R1ng theme test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(completed, "the theme test timed out");
            Assert.Null(failure);
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
            TryDelete(settingsFile);
            TryDelete(settingsFile + ".tmp");
        }
    }

    /// <summary>Merges the real production control dictionary into the test's resource scope.</summary>
    internal static void LoadProductionControlStyles(ResourceDictionary scope)
    {
        var controls = new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/R1ngLauncher;component/Themes/Controls.xaml",
                UriKind.Absolute
            ),
        };

        scope.MergedDictionaries.Add(controls);
    }

    private static void AssertPalette(MainWindow window, AppTheme theme)
    {
        var palette = ThemePalette.Create(theme);

        AssertBrush(palette, "CardBackgroundBrush", window.CardBorder.Background);
        AssertBrush(palette, "TextPrimaryBrush", window.SearchBox.CaretBrush);
        AssertBrush(palette, "AccentBrush", window.SearchBox.SelectionBrush);
    }

    private static void AssertThemeToggle(MainWindow window, AppTheme theme)
    {
        var button = window.ThemeToggleButton;
        Assert.Null(button.ToolTip);
        Assert.Null(ToolTipService.GetToolTip(button));
        Assert.Equal(theme == AppTheme.Dark ? "\uE706" : "\uE708", button.Content);
        Assert.Equal(theme == AppTheme.Dark ? "切换到浅色主题" : "切换到深色主题", AutomationProperties.GetName(button));
        Assert.IsType<Style>(button.Style);
        Assert.Equal(30, button.Width);
        Assert.Equal(30, button.Height);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;

            var nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    /// <summary>Refreshes the button's icon and automation-name bindings in an unshown window.</summary>
    private static void RefreshBindings(MainWindow window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        window.ThemeToggleButton.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
        window.ThemeToggleButton.GetBindingExpression(AutomationProperties.NameProperty)?.UpdateTarget();
    }

    private static void AssertBrush(ResourceDictionary palette, string key, Brush? actual)
    {
        var solid = Assert.IsAssignableFrom<SolidColorBrush>(actual);
        Assert.Equal(((SolidColorBrush)palette[key]).Color, solid.Color);
    }

    /// <summary>
    /// Puts a few real rows (application/file/folder) into the list so the review frames show the
    /// actual item template and selection highlight, not an empty card.
    /// </summary>
    private static void SeedResultSample(LauncherViewModel viewModel)
    {
        SearchResultItem[] samples =
        [
            new()
            {
                Id = "app|notepad",
                Kind = ResultKind.Application,
                Title = "记事本",
                Detail = @"C:\Windows\System32\notepad.exe",
                TypeLabel = "应用",
                Source = "开始菜单",
                Score = 1_000,
                TitleMatchIndices = [0, 1],
            },
            new()
            {
                Id = "file|report",
                Kind = ResultKind.File,
                Title = "季度报告.docx",
                Detail = @"C:\Users\me\Documents",
                TypeLabel = "文件",
                Source = "Everything",
                Score = 900,
            },
            new()
            {
                Id = "folder|projects",
                Kind = ResultKind.Folder,
                Title = "Projects",
                Detail = @"D:\work",
                TypeLabel = "文件夹",
                Source = "Everything",
                Score = 800,
            },
        ];

        foreach (var sample in samples)
            viewModel.Results.Add(new ResultItemViewModel(sample, viewModel));

        viewModel.SelectedIndex = 0;
    }

    /// <summary>
    /// Ceiling used to measure a window that sizes itself to its content (<c>Height</c> is NaN under
    /// <c>SizeToContent.Height</c>). WPF refuses a NaN constraint, and the window height must never be
    /// forced back to a fixed value here — that would hide an adaptive-height regression.
    /// </summary>
    private const double AdaptiveMeasureCeiling = 1000;

    /// <summary>True when the window has an explicit height; false when it sizes to its content.</summary>
    private static bool HasFixedHeight(Window window) => double.IsFinite(window.Height);

    /// <summary>The finite space offered to the window: its explicit size, else a ceiling for the height.</summary>
    private static Size AvailableSize(Window window)
    {
        var width = double.IsFinite(window.Width) ? window.Width : 760;
        var height = HasFixedHeight(window)
            ? window.Height
            : double.IsFinite(window.MaxHeight) ? window.MaxHeight : AdaptiveMeasureCeiling;

        return new Size(width, height);
    }

    private static void Layout(MainWindow window)
    {
        var available = AvailableSize(window);

        // Measure with a finite constraint (never NaN); arrange at the explicit height when there is
        // one, otherwise at whatever height the content asked for.
        window.Measure(available);
        window.Arrange(
            new Rect(0, 0, available.Width, HasFixedHeight(window) ? available.Height : Math.Min(available.Height, window.DesiredSize.Height))
        );
        window.UpdateLayout();

        // An unshown Window does not measure its content by itself; measure/arrange the content tree
        // explicitly so templates (including the icon button) are realized for assertions.
        if (window.Content is FrameworkElement content)
        {
            content.Measure(available);
            content.Arrange(
                new Rect(0, 0, available.Width, HasFixedHeight(window) ? available.Height : Math.Min(available.Height, content.DesiredSize.Height))
            );
            content.UpdateLayout();
        }
    }

    /// <summary>
    /// The laid-out card must have a real, finite size (also when the window sizes to its content),
    /// never NaN, zero or taller than what it was offered.
    /// </summary>
    private static Size LaidOutSize(MainWindow window)
    {
        var available = AvailableSize(window);
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        Assert.True(double.IsFinite(content.ActualWidth) && content.ActualWidth > 0, "the card has no finite width");
        Assert.True(double.IsFinite(content.ActualHeight) && content.ActualHeight > 0, "the card has no finite height");
        Assert.True(content.ActualWidth <= available.Width + 0.5, "the card is wider than the window");
        Assert.True(content.ActualHeight <= available.Height + 0.5, "the card is taller than the window may be");

        return new Size(content.ActualWidth, content.ActualHeight);
    }

    private static void Render(MainWindow window, string? directory, string fileName)
    {
        // The real layout size is part of the contract (and exercised for both fixed and adaptive
        // window heights); only the optional PNG evidence below is best effort.
        var size = LaidOutSize(window);

        if (directory is null)
            return;

        try
        {
            var width = Math.Max(1, (int)Math.Ceiling(size.Width));
            var height = Math.Max(1, (int)Math.Ceiling(size.Height));

            // A window that was never shown has no rendered visual of its own; render the laid-out
            // content tree so the offscreen frame shows the real card and controls.
            var visual = (Visual)(window.Content ?? window);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, fileName));
            encoder.Save(stream);
        }
        catch (Exception)
        {
            // The PNG is supporting evidence only; the palette and layout assertions are the contract.
        }
    }

    private static string? FindRepoArtifactDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "R1ngLauncher.sln")))
                return Path.Combine(directory.FullName, "artifacts", "theme-render");

            directory = directory.Parent;
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup.
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
