using System.Diagnostics;
using System.IO;
using R1ng.Launcher.Core;
using System.Windows;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher;

/// <summary>
/// Composition root. The launcher starts hidden (no auto-show); it appears on the global hotkey, on
/// a request from a second instance, or when started with <c>--show</c> / <c>--query</c>.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private MainWindow? _window;
    private EverythingFileSearchProvider? _fileSearch;
    private LauncherViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "r1ng-launcher");
        var dataDirectory = defaultDirectory;
        if (HasSwitch(e.Args, "--data-dir"))
        {
            try
            {
                if (!TryGetSwitchValue(e.Args, "--data-dir", out var requestedDirectory)
                    || !ShellLinkReader.IsLocalAbsolute(requestedDirectory))
                    throw new ArgumentException("--data-dir requires a fully qualified local directory");
                dataDirectory = Path.GetFullPath(requestedDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log("invalid data directory", exception);
                Shutdown();
                return;
            }
        }
        var instanceKey = string.Equals(LaunchIdentity.NormalizePath(dataDirectory), LaunchIdentity.NormalizePath(defaultDirectory), StringComparison.Ordinal)
            ? null : LaunchIdentity.NormalizePath(dataDirectory);
        if (!SingleInstance.TryAcquire(out var instance, instanceKey))
        {
            Shutdown();
            return;
        }

        _singleInstance = instance;

        // Load and apply the persisted theme before the window is created so the first paint is
        // already correct. A missing/corrupt settings file silently keeps the dark default.
        var settings = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var initialTheme = settings.Load().Theme;
        ThemeManager.Apply(initialTheme);

        var hiddenResults = new HiddenResultStore(Path.Combine(dataDirectory, "hidden.json"));
        var catalog = AppCatalog.CreateDefault(Log);
        var appSearch = new AppSearchProvider(catalog, hiddenResults: hiddenResults);
        _fileSearch = new EverythingFileSearchProvider();
        var history = new SearchHistoryStore(Path.Combine(dataDirectory, "history.json"));
        var preferences = new QueryPreferenceStore(Path.Combine(dataDirectory, "preferences.json"));
        var controller = new SearchController(appSearch, _fileSearch, history, preferences, hiddenResults);
        _viewModel = new LauncherViewModel(
            controller,
            history,
            refreshFileBackend: _fileSearch.InvalidateAvailability,
            initialTheme: initialTheme,
            applyTheme: theme =>
            {
                ThemeManager.Apply(theme);
                settings.SaveTheme(theme);
            },
            hiddenResults: hiddenResults
        );

        _window = new MainWindow(_viewModel);
        MainWindow = _window;
        var hotKeyAvailable = _window.Initialize();

        var window = _window;
        instance.Listen(() => Dispatcher.Invoke(window.ShowLauncher));

        // The window turns a normal close (Alt+F4) into a hide so the launcher stays resident; a real
        // session end must be allowed to close it and release the single-instance guard and hotkey.
        SessionEnding += OnSessionEnding;

        // Warm the catalog in the background so the first hotkey show already has results.
        _ = Task.Run(() => catalog.GetAsync());
        _viewModel.Prime();

        Log($"hotkey: {_window.HotKeyStatus}");

        if (HasSwitch(e.Args, "--show"))
            _window.ShowLauncher();

        if (TryGetSwitchValue(e.Args, "--query", out var query))
            _window.ShowWithQuery(query);

        // Never strand a resident process with no way to open or exit it: when neither hotkey could be
        // registered, come up visible with the diagnostic banner and the footer “退出” button.
        if (!hotKeyAvailable)
        {
            Log("global hotkeys unavailable; showing the launcher with the diagnostic banner");
            _window.ShowLauncher();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _fileSearch?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _window?.AllowClose();
        Shutdown();
    }

    private static bool HasSwitch(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetSwitchValue(string[] args, string name, out string value)
    {
        value = string.Empty;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = args[index + 1];
            return true;
        }

        return false;
    }

    /// <summary>Diagnostics go to the debugger; failures are never fatal to the launcher.</summary>
    private static void Log(string message) => Debug.WriteLine($"[r1ng-launcher] {message}");

    private static void Log(string context, Exception exception) =>
        Debug.WriteLine($"[r1ng-launcher] {context}: {exception.GetType().Name}: {exception.Message}");
}
