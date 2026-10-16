using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher;

/// <summary>
/// Borderless floating launcher window: shown on the global hotkey, centered on the monitor under
/// the cursor, hidden on Esc or when it loses focus (unless one of its context menus is open).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>How long the launcher waits after losing focus before hiding.</summary>
    public const int GracePeriodMs = 150;

    /// <summary>Design width (DIPs). The height follows the content up to <see cref="MaxDesignHeight"/>.</summary>
    public const double DesignWidth = 760;

    /// <summary>Tallest the window grows (DIPs); beyond it, or on a smaller work area, the list scrolls.</summary>
    public const double MaxDesignHeight = 520;

    /// <summary>
    /// How many times one show may re-place the window after layout / WPF's own DPI handling settled.
    /// Bounded so a misbehaving DPI notification can never turn into an endless move loop.
    /// </summary>
    private const int MaxPlacementCorrections = 3;
    private const int MaxQuickLookAvoidanceAttempts = 12;
    private static readonly TimeSpan QuickLookAvoidanceRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan QuickLookDiscoveryTimeout = TimeSpan.FromMilliseconds(200);
    private const int QuickLookAvoidanceGap = 16;

    private static int _openContextMenus;

    private readonly LauncherViewModel _viewModel;
    private readonly DeactivationGrace _deactivationGrace;
    private readonly IApplicationShutdown _applicationShutdown;
    private readonly Func<IntPtr, string?> _resolveExplorerFolder;
    private readonly IQueryEditor _queryEditor;
    private readonly Func<string, bool> _copyTextToClipboard;
    private readonly ILauncherTrayIcon _trayIcon;
    private readonly ILauncherNoticePresenter _noticePresenter;
    private DispatcherOperation? _noticeDispatch;
    private string? _pendingNoticeProperty;
    private ResultItemViewModel? _lastMousePreviewItem;
    private readonly IQuickLookWindowLocator _quickLookWindowLocator;
    private readonly ILauncherWindowPlacement _launcherWindowPlacement;
    private readonly Func<TimeSpan, CancellationToken, Task> _quickLookAvoidanceDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private GlobalHotKey? _hotKey;
    private HwndSource? _source;
    private bool _allowClose;
    private bool _closed;
    private bool _trayIconAvailable;
    private bool _trayInitializationAttempted;
    private string? _trayFailureMessage;
    private string _hotKeySummary = "未注册";
    private string? _hotKeyNotice;
    private bool _hotKeyFailure;

    // Modal dialogs (the hidden-items manager) that own the keyboard; they suppress the focus-loss hide
    // separately from open context menus and are always released in a finally block.
    private int _modalDialogs;
    private HiddenItemsWindow? _hiddenDialog;

    // Notepad editing session. UI thread only. The revision counts real query changes; the expected
    // revision is the one the session last agreed with, so a save is only written back while the query
    // is still exactly what the session started from or last received.
    private int _queryRevision;
    private int _expectedRevision;
    private Guid? _sessionId;
    private long _latestSequence;
    private string? _pendingSavedText;
    private readonly Dictionary<Guid, QueryEditorSaved> _earlySaves = new();
    private readonly Dictionary<Guid, QueryEditorClosed> _earlyClosures = new();
    private bool _startingEditor;

    private static readonly char[] LineBreaks = ['\r', '\n'];
    private static readonly string[] PasteTextFormats = [DataFormats.UnicodeText, DataFormats.StringFormat, DataFormats.Text];

    // Work area sampled by the current show (device pixels). Fixed for that show: resizing the window
    // or a late DPI notification must never re-read the cursor and chase it to another monitor.
    private ScreenPlacement.PixelsRect? _workArea;
    private int _placementCorrectionsLeft;
    private DispatcherOperation? _placementCorrection;
    private DispatcherOperation? _selectionScroll;
    private CancellationTokenSource? _quickLookAvoidanceCancellation;
    private QuickLookRequestAnchor? _quickLookRequestAnchor;
    private QuickLookAvoidanceLayout? _quickLookAvoidanceLayout;
    private long _latestQuickLookPlacementGeneration;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ContextMenu),
            System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((_, _) => Interlocked.Increment(ref _openContextMenus))
        );
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ContextMenu),
            System.Windows.Controls.ContextMenu.ClosedEvent,
            new RoutedEventHandler((_, _) => Interlocked.Decrement(ref _openContextMenus))
        );
    }

    public MainWindow(
        LauncherViewModel viewModel,
        IApplicationShutdown? applicationShutdown = null,
        Func<IntPtr, string?>? explorerFolderResolver = null,
        IQueryEditor? queryEditor = null,
        Func<string, bool>? copyTextToClipboard = null
    ) : this(viewModel, new LauncherTrayIcon(), applicationShutdown, explorerFolderResolver, queryEditor, copyTextToClipboard)
    {
    }

    internal MainWindow(
        LauncherViewModel viewModel,
        ILauncherTrayIcon trayIcon,
        IApplicationShutdown? applicationShutdown = null,
        Func<IntPtr, string?>? explorerFolderResolver = null,
        IQueryEditor? queryEditor = null,
        Func<string, bool>? copyTextToClipboard = null,
        IQuickLookWindowLocator? quickLookWindowLocator = null,
        ILauncherWindowPlacement? launcherWindowPlacement = null,
        Func<TimeSpan, CancellationToken, Task>? quickLookAvoidanceDelay = null,
        ILauncherNoticePresenter? noticePresenter = null,
        IGraceTimer? deactivationTimer = null,
        Func<bool>? launcherIsActive = null
    )
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _applicationShutdown = applicationShutdown ?? new WpfApplicationShutdown();
        _resolveExplorerFolder = explorerFolderResolver ?? (handle => ExplorerFolderResolver.TryResolve(handle));

        // The window owns the editor: it is disposed in OnClosed (the user's notepad is not closed).
        _queryEditor = queryEditor ?? new NotepadQueryEditor();
        _copyTextToClipboard = copyTextToClipboard ?? TryCopyTextToClipboard;
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _noticePresenter = noticePresenter ?? (Application.Current is null
            ? new NullLauncherNoticePresenter() : new LauncherNoticePresenter());
        _quickLookWindowLocator = quickLookWindowLocator ?? new QuickLookWindowLocator();
        _launcherWindowPlacement = launcherWindowPlacement ?? new Win32LauncherWindowPlacement();
        _quickLookAvoidanceDelay = quickLookAvoidanceDelay ?? ((delay, token) => Task.Delay(delay, token));
        _trayIcon.ShowRequested += OnTrayShowRequested;
        _trayIcon.ManageHiddenRequested += OnTrayManageHiddenRequested;
        _trayIcon.QuitRequested += OnTrayQuitRequested;
        _trayIcon.AvailabilityChanged += OnTrayAvailabilityChanged;
        SampleEditorContext = queryEditor is null ? CaptureEditorContext : () => null;
        HiddenWindowPresenter = PresentHiddenWindow;
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RequestHide += HideLauncher;
        _viewModel.RequestQuit += Quit;
        _viewModel.RequestManageHidden += OnRequestManageHidden;
        _viewModel.QuickLookPlacementRequestStarted += OnQuickLookPlacementRequestStarted;
        _viewModel.QuickLookPlacementRequestCompleted += OnQuickLookPlacementRequestCompleted;
        _queryEditor.Saved += OnEditorSaved;
        _queryEditor.Failed += OnEditorFailed;
        _queryEditor.Closed += OnEditorClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
        ResultsList.PreviewMouseLeftButtonDown += OnResultsPreviewMouseLeftButtonDown;
        DataObject.AddPastingHandler(SearchBox, OnSearchBoxPasting);

        _deactivationGrace = new DeactivationGrace(
            deactivationTimer ?? new DispatcherGraceTimer(Dispatcher, TimeSpan.FromMilliseconds(GracePeriodMs)),
            shouldHide: () => IsVisible && !(launcherIsActive?.Invoke() ?? IsActive),
            isSuppressed: () => IsDeactivationSuppressed,
            hide: HideAfterDeactivation
        );
    }

    /// <summary>Samples the monitor to place on; replaceable so tests can pin a work area.</summary>
    internal Func<ScreenPlacement.MonitorSample> SampleMonitor { get; set; } = ScreenPlacement.SampleCursorMonitor;

    /// <summary>Captures desktop identity for conservative draft-close detection, never for positioning Notepad.</summary>
    internal Func<QueryEditorLaunchContext?> SampleEditorContext { get; set; }

    /// <summary>Reads the keyboard modifiers; replaceable so tests can drive Ctrl shortcuts without real input.</summary>
    internal Func<ModifierKeys> ModifierProvider { get; set; } = () => Keyboard.Modifiers;

    /// <summary>Shows the hidden-items dialog modally; replaceable so tests never block on a real dialog.</summary>
    internal Func<HiddenItemsWindow, bool?> HiddenWindowPresenter { get; set; }

    /// <summary>
    /// True while the focus-loss hide must wait: an open context menu or a modal dialog owned by the
    /// launcher. The two reasons are tracked separately so neither can release the other's hold.
    /// </summary>
    internal bool IsDeactivationSuppressed =>
        Volatile.Read(ref _openContextMenus) > 0 || Volatile.Read(ref _modalDialogs) > 0;

    /// <summary>Id of the active notepad editing session, or null.</summary>
    internal Guid? EditingSessionId => _sessionId;

    /// <summary>Latest saved text that was not written back because the query changed meanwhile.</summary>
    internal string? PendingSavedText => _pendingSavedText;

    /// <summary>Counts real query changes; the editing session compares against it to reject stale saves.</summary>
    internal int QueryRevision => _queryRevision;

    /// <summary>Human-readable hotkey state, surfaced in the README's manual checks and logs.</summary>
    public string HotKeyStatus { get; private set; } = "未注册";

    /// <summary>False when neither the preferred nor the fallback combination could be registered.</summary>
    public bool HotKeyAvailable { get; private set; }

    /// <summary>False when the independent Ctrl + Alt + Space file shortcut could not be registered.</summary>
    public bool FileHotKeyAvailable { get; private set; }

    /// <summary>False when the notification-area icon could not be installed.</summary>
    public bool TrayIconAvailable => _trayIconAvailable;

    /// <summary>Creates the window handle, tray icon, hotkeys and native-message hook.</summary>
    /// <returns><c>true</c> when the launcher hotkey is active.</returns>
    public bool Initialize() => Initialize(registerHotKeys: true);

    /// <summary>Injectable initialization seam: tests can exercise tray callbacks without registering hotkeys.</summary>
    internal bool Initialize(bool registerHotKeys)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);

        try
        {
            _trayIconAvailable = _trayIcon.Initialize(handle);
        }
        catch (Exception exception)
        {
            _trayIconAvailable = false;
            _trayFailureMessage = exception.Message;
        }
        _trayInitializationAttempted = true;
        if (_trayIconAvailable)
            _trayFailureMessage = null;
        else
            _trayFailureMessage ??= _trayIcon.FailureMessage;

        var registered = false;
        var fileRegistered = false;
        HotKeyRegistrationStatus? status = null;
        if (registerHotKeys)
        {
            _hotKey = new GlobalHotKey(handle);
            registered = _hotKey.RegisterLauncher(HotKeyDefinition.AltSpace, HotKeyDefinition.CtrlShiftSpace);
            fileRegistered = _hotKey.RegisterFileHotKey(HotKeyDefinition.CtrlAltSpace);
            status = HotKeyRegistrationStatus.WithFileHotKey(
                HotKeyRegistrationStatus.From(registered, _hotKey.Active, _hotKey.FailureMessage),
                fileRegistered,
                _hotKey.FileActive,
                _hotKey.FileFailureMessage
            );
            HotKeyStatus = status.Value.Summary;
        }
        else
        {
            HotKeyStatus = "快捷键未在测试中注册";
        }

        HotKeyAvailable = registered;
        FileHotKeyAvailable = fileRegistered;
        _hotKeySummary = HotKeyStatus;
        _hotKeyNotice = status?.Notice;
        _hotKeyFailure = status?.IsFailure ?? false;
        RefreshTrayNotice();

        return registered;
    }

    /// <summary>
    /// Explicitly exits the resident launcher. Allows the close to proceed and shuts the application
    /// down, which closes the window and releases the tray icon, hotkeys and single-instance guard.
    /// </summary>
    public void Quit()
    {
        AllowClose();
        _applicationShutdown.Shutdown();
    }

    /// <summary>The main hotkey toggles visibility and always opens in the all-results scope.</summary>
    public void ToggleLauncher()
    {
        if (IsVisible)
            HideLauncher();
        else
        {
            _viewModel.SetScope(SearchScope.All);
            ShowLauncher();
        }
    }

    /// <summary>Shows, focuses and positions the launcher on the monitor that holds the cursor.</summary>
    public void ShowLauncher()
    {
        _deactivationGrace.Cancel();
        PositionOnCursorMonitor();
        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        FocusSearchBox();
        QueuePlacementCorrection();
        RefreshEditorSession();
    }

    /// <summary>
    /// Hides the launcher while preserving the query: the input survives Esc, the hotkey, Alt+F4,
    /// focus loss and a successful launch. The next regular show selects the full input so typing
    /// replaces it, and the file hotkey can replace only the keyword portion of a folder query.
    /// </summary>
    public void HideLauncher()
    {
        if (_trayInitializationAttempted && !_trayIconAvailable && !HotKeyAvailable)
        {
            // With neither recovery route available, hiding would leave a resident single-instance
            // process that cannot be shown again. Keep the diagnostic window visible; Alt+F4 exits.
            _viewModel.SetActionNotice("系统托盘与快捷键均不可用，窗口保持显示；按 Alt+F4 退出。");
            return;
        }

        _deactivationGrace.Cancel();
        CancelQuickLookAvoidance(resetLayout: true);
        EndPlacement();

        // A modal manager window must not outlive the launcher's own hide.
        if (_hiddenDialog is { IsVisible: true } dialog)
            dialog.Close();

        Hide();
    }

    /// <summary>
    /// Independent file-scope shortcut. Captures the foreground window <em>before</em> the launcher is
    /// activated, resolves it (bounded) to a physical File Explorer folder, and always shows the file
    /// scope with the retained keywords — never merely toggles hide. With no qualifying Explorer
    /// folder the keywords are kept and no path is invented.
    /// </summary>
    public void ShowFilesFromExplorer()
    {
        // Must be sampled before Show()/Activate(): afterwards the launcher itself is foreground.
        var foreground = NativeMethods.GetForegroundWindow();
        var folder = ResolveExplorerFolder(foreground);

        // The bounded lookup can take up to 300 ms. If the user switched windows meanwhile, the
        // resolved folder describes a window that is no longer foreground: drop it instead of
        // showing/activating the launcher with an obsolete folder context (the retained keywords are
        // still kept).
        if (!IsSameForegroundWindow(foreground, NativeMethods.GetForegroundWindow()))
            folder = null;

        _deactivationGrace.Cancel();
        PositionOnCursorMonitor();
        var text = _viewModel.ApplyFileScope(folder);
        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        FocusSearchBox();
        QueuePlacementCorrection();
        SelectTerms(text);
        RefreshEditorSession();
    }

    /// <summary>
    /// True when the window captured before the bounded folder lookup is still the foreground window,
    /// so its resolved folder is not obsolete. Internal and pure so the revalidation rule is testable
    /// without moving the real foreground window.
    /// </summary>
    internal static bool IsSameForegroundWindow(IntPtr captured, IntPtr current) =>
        captured != IntPtr.Zero && captured == current;

    private QueryEditorLaunchContext CaptureEditorContext()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            handle = new WindowInteropHelper(this).EnsureHandle();

        return new QueryEditorLaunchContext(ScreenPlacement.TryGetWindowDesktopId(handle));
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text ?? string.Empty);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Resolves the folder for a captured handle through the injected resolver seam.</summary>
    internal string? ResolveExplorerFolder(IntPtr windowHandle) => _resolveExplorerFolder(windowHandle);

    /// <summary>
    /// Applies a resolved (or absent) Explorer folder to the file-scope query and selects only the
    /// keywords. Split out so the injected-folder behavior can be driven deterministically on an STA
    /// thread without showing or activating a window.
    /// </summary>
    internal string ApplyExplorerFolder(string? folder)
    {
        var text = _viewModel.ApplyFileScope(folder);

        // This seam runs without a shown window, where the source-to-target binding update can still
        // be pending. Sync the visible text before selecting so the selection indices line up.
        if (!string.Equals(SearchBox.Text, text, StringComparison.Ordinal))
            SearchBox.Text = text;

        SelectTerms(text);
        return text;
    }

    /// <summary>Selects only the keywords after a folder prefix so they can be replaced easily.</summary>
    private void SelectTerms(string query)
    {
        var start = FolderPrefixQuery.TermsStartIndex(query);
        var length = Math.Max(0, query.Length - start);

        if (length > 0)
        {
            SearchBox.Select(start, length);
            return;
        }

        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchBox.Select(SearchBox.Text.Length, 0);
    }

    /// <summary>Sets the query text and shows the launcher (used by the <c>--query</c> switch).</summary>
    public void ShowWithQuery(string query)
    {
        ShowLauncher();
        _viewModel.Query = query;
    }

    /// <summary>
    /// Allows the next close request to tear the window down. Called only while the whole application
    /// is shutting down (for example on session end); a normal user close is turned into a hide.
    /// </summary>
    public void AllowClose() => _allowClose = true;

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Pins this show to the monitor under the cursor and moves the native window there in device
    /// pixels, before it is shown. WPF's DIP coordinates cannot address a monitor whose DPI differs
    /// from the one the window currently lives on, so no DIP transform of the target screen is used.
    /// </summary>
    private void PositionOnCursorMonitor()
    {
        CancelQuickLookAvoidance(resetLayout: true);
        var sample = SampleMonitor();
        _workArea = sample.WorkArea;
        _placementCorrectionsLeft = MaxPlacementCorrections;

        ApplyPlacement(new WindowInteropHelper(this).EnsureHandle(), sample.DpiScale, actualWidth: null);
    }

    /// <summary>
    /// Limits the window to the pinned work area at <paramref name="dpiScale"/> and places it. The
    /// vertical position comes from the <em>largest</em> height the window can reach, so the top stays
    /// put while results make the window grow or shrink.
    /// </summary>
    private void ApplyPlacement(IntPtr handle, double dpiScale, int? actualWidth)
    {
        if (_workArea is not { } workArea)
            return;

        var (width, maxHeight) = ScreenPlacement.FitSize(DesignWidth, MaxDesignHeight, workArea, dpiScale);
        var widthChanged = Width != width;
        Width = width;
        MaxHeight = maxHeight;

        // A bounds snapshot taken before changing Width belongs to the old layout.
        var widthPixels = !widthChanged && actualWidth is { } measuredWidth
            ? measuredWidth
            : (int)Math.Round(width * dpiScale);
        if (widthChanged && IsVisible)
            QueuePlacementCorrection();
        var maxHeightPixels = (int)Math.Round(maxHeight * dpiScale);
        if (_quickLookAvoidanceLayout is { } avoidance)
        {
            if (_launcherWindowPlacement.TryCapture(handle, out var current)
                && current.Handle == handle
                && current.Monitor.WorkArea == avoidance.WorkArea)
            {
                var reference = new ScreenPlacement.PixelsRect(
                    current.Bounds.Left,
                    current.Bounds.Top,
                    SaturatingPixelAdd(current.Bounds.Left, widthPixels),
                    SaturatingPixelAdd(current.Bounds.Top, maxHeightPixels));
                TryPlaceBesideQuickLook(handle, reference, avoidance.PreviewBounds, avoidance.WorkArea, requestGeneration: null, out _);
            }

            // A monitor/DPI transition invalidates the captured avoidance anchor. Do not silently
            // re-center over the preview; the next explicit show samples a fresh monitor.
            return;
        }

        var (x, y) = ScreenPlacement.Place(widthPixels, maxHeightPixels, workArea);

        if (ScreenPlacement.TryGetWindowBounds(handle, out var bounds) && bounds.Left == x && bounds.Top == y)
            return;

        ScreenPlacement.TryMoveWindow(handle, (int)x, (int)y);
    }

    /// <summary>
    /// Re-places the window once layout settled and WPF applied the target monitor's DPI, using the
    /// authoritative native DPI and window rectangle. Queued (never run inside the DPI notification
    /// itself) so it cannot re-enter WPF's WM_DPICHANGED handling; bounded per show.
    /// </summary>
    private void QueuePlacementCorrection()
    {
        if (_workArea is null
            || _placementCorrectionsLeft <= 0
            || _placementCorrection is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _placementCorrection = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CorrectPlacement));
    }

    internal void CorrectPlacement()
    {
        _placementCorrection = null;
        var handle = new WindowInteropHelper(this).Handle;
        if (_workArea is null || !IsVisible || handle == IntPtr.Zero || _placementCorrectionsLeft <= 0)
            return;

        _placementCorrectionsLeft--;
        var scale = ScreenPlacement.GetWindowDpiScale(handle, VisualTreeHelper.GetDpi(this).DpiScaleX);
        int? width = ScreenPlacement.TryGetWindowBounds(handle, out var bounds) ? bounds.Width : null;
        ApplyPlacement(handle, scale, width);
    }

    private void EndPlacement()
    {
        _workArea = null;
        _placementCorrection?.Abort();
        _placementCorrection = null;
    }

    private void OnQuickLookPlacementRequestStarted(long generation)
    {
        _lastMousePreviewItem = null;
        CancelQuickLookAvoidance(resetLayout: false);
        _latestQuickLookPlacementGeneration = generation;
        if (_closed || !IsVisible)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (_launcherWindowPlacement.TryCapture(handle, out var snapshot) && snapshot.Handle == handle)
            _quickLookRequestAnchor = new QuickLookRequestAnchor(generation, snapshot);
    }

    private void OnQuickLookPlacementRequestCompleted(long generation)
    {
        if (!IsCurrentQuickLookRequest(generation, out var anchor))
            return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _quickLookAvoidanceCancellation = cancellation;
        _ = FindAndAvoidQuickLookAsync(generation, anchor, cancellation);
    }

    private async Task FindAndAvoidQuickLookAsync(
        long generation,
        QuickLookRequestAnchor anchor,
        CancellationTokenSource cancellation)
    {
        ScreenPlacement.AvoidancePlacement? lastPlacement = null;
        try
        {
            for (var attempt = 0; attempt < MaxQuickLookAvoidanceAttempts; attempt++)
            {
                var token = cancellation.Token;
                if (attempt > 0)
                    await _quickLookAvoidanceDelay(QuickLookAvoidanceRetryDelay, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsCurrentQuickLookRequest(generation, out var currentAnchor)
                    || currentAnchor.Snapshot.Handle != anchor.Snapshot.Handle)
                {
                    return;
                }

                IReadOnlyList<QuickLookWindowCandidate> candidates;
                var scan = _quickLookWindowLocator.FindVisiblePreviewsAsync(token);
                try
                {
                    candidates = await scan.WaitAsync(QuickLookDiscoveryTimeout, token).ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    ObserveLateFault(scan);
                    continue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    continue;
                }

                if (token.IsCancellationRequested || !IsCurrentQuickLookRequest(generation, out currentAnchor))
                    return;

                var previewBounds = candidates
                    .Where(candidate => candidate.Handle != IntPtr.Zero)
                    .Select(candidate => candidate.Bounds)
                    .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
                    .Distinct()
                    .ToArray();
                if (previewBounds.Length == 0)
                    continue;

                if (TryPlaceBesideQuickLook(
                    anchor.Snapshot.Handle,
                    currentAnchor.Snapshot.Bounds,
                    previewBounds,
                    anchor.Snapshot.Monitor.WorkArea,
                    generation,
                    out var placement))
                {
                    lastPlacement = placement;
                }
            }

            if (lastPlacement is { IsClear: false } || lastPlacement is { WithinWorkArea: false })
            {
                _viewModel.SetActionNotice("当前工作区无法完全并排显示 QuickLook 与启动器，已尽量减少重叠。");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Hiding, another request or shutdown cancels discovery before any later move.
        }
        catch (Exception)
        {
            // Preview avoidance is opportunistic; a discovery or placement failure must not block the UI.
        }
        finally
        {
            if (ReferenceEquals(_quickLookAvoidanceCancellation, cancellation))
                _quickLookAvoidanceCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool TryPlaceBesideQuickLook(
        IntPtr expectedHandle,
        ScreenPlacement.PixelsRect launcherBounds,
        IReadOnlyList<ScreenPlacement.PixelsRect> previewBounds,
        ScreenPlacement.PixelsRect workArea,
        long? requestGeneration,
        out ScreenPlacement.AvoidancePlacement placement)
    {
        placement = default;
        if (_closed || !IsVisible || expectedHandle == IntPtr.Zero
            || new WindowInteropHelper(this).Handle != expectedHandle
            || requestGeneration is { } generation && !IsCurrentQuickLookRequest(generation, out _)
            || !_launcherWindowPlacement.TryCapture(expectedHandle, out var current)
            || current.Handle != expectedHandle
            || current.Monitor.WorkArea != workArea)
        {
            return false;
        }

        var result = ScreenPlacement.PlaceBeside(launcherBounds, previewBounds, workArea, QuickLookAvoidanceGap);
        if (result is not { } target)
            return false;

        var alreadyThere = current.Bounds.Left == target.X && current.Bounds.Top == target.Y;
        if (!alreadyThere && !_launcherWindowPlacement.TryMove(expectedHandle, target.X, target.Y))
            return false;

        placement = target;
        _quickLookAvoidanceLayout = new QuickLookAvoidanceLayout(workArea, previewBounds.ToArray());
        return true;
    }

    private bool IsCurrentQuickLookRequest(long generation, out QuickLookRequestAnchor anchor)
    {
        anchor = default;
        if (_closed || !IsVisible
            || generation != _latestQuickLookPlacementGeneration
            || _quickLookRequestAnchor is not { } captured
            || captured.Generation != generation)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero
            || handle != captured.Snapshot.Handle
            || !_launcherWindowPlacement.TryCapture(handle, out var current)
            || current.Handle != handle
            || current.Monitor.WorkArea != captured.Snapshot.Monitor.WorkArea)
        {
            return false;
        }

        anchor = captured with { Snapshot = current };
        _quickLookRequestAnchor = anchor;
        return true;
    }

    private void CancelQuickLookAvoidance(bool resetLayout)
    {
        _quickLookRequestAnchor = null;
        var cancellation = _quickLookAvoidanceCancellation;
        _quickLookAvoidanceCancellation = null;
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The bounded retry completed while cancellation was being requested.
            }
            cancellation.Dispose();
        }

        if (resetLayout)
            _quickLookAvoidanceLayout = null;
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static int SaturatingPixelAdd(int value, int offset) =>
        (int)Math.Clamp((long)value + offset, int.MinValue, int.MaxValue);

    protected override void OnDpiChanged(DpiScale oldDpiScaleInfo, DpiScale newDpiScaleInfo)
    {
        base.OnDpiChanged(oldDpiScaleInfo, newDpiScaleInfo);

        // WPF is in the middle of WM_DPICHANGED here; only schedule the correction.
        QueuePlacementCorrection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (e.PropertyName is nameof(LauncherViewModel.ActionNotice) or nameof(LauncherViewModel.FileNotice)
            or nameof(LauncherViewModel.HotKeyNotice))
            QueueNotice(e.PropertyName);
        else if (e.PropertyName == nameof(LauncherViewModel.Theme))
            _noticePresenter.UpdateTheme(_viewModel.Theme);
        // Raised for every selection change and for a refresh that keeps the same index.
        if (e.PropertyName == nameof(LauncherViewModel.SelectedIndex))
        {
            _lastMousePreviewItem = null;
            QueueSelectionIntoView();
        }
        else if (e.PropertyName == nameof(LauncherViewModel.Query))
        {
            _lastMousePreviewItem = null;
            _queryRevision++;
        }
    }

    private void QueueNotice(string property)
    {
        var message = NoticeMessage(property);
        if (string.IsNullOrWhiteSpace(message)) return;
        _pendingNoticeProperty = property;
        if (_noticeDispatch is { Status: DispatcherOperationStatus.Pending }) return;
        _noticeDispatch = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _noticeDispatch = null;
            if (_closed || _pendingNoticeProperty is not { } kind) return;
            _pendingNoticeProperty = null;
            var text = NoticeMessage(kind);
            if (string.IsNullOrWhiteSpace(text)) return;
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = ScreenPlacement.TrySampleWindowMonitor(handle, out var sampled) ? sampled : SampleMonitor();
            var bounds = ScreenPlacement.TryGetWindowBounds(handle, out var measured) ? measured : monitor.WorkArea;
            try
            {
                _noticePresenter.Show(new LauncherNotice(text, kind == nameof(LauncherViewModel.HotKeyNotice) && _viewModel.IsHotKeyFailure),
                    new NoticeAnchor(bounds, monitor), _viewModel.Theme);
            }
            catch (Exception)
            {
                // A notification failure must never steal focus, block an action, or crash the launcher.
            }
        }));
    }

    private string? NoticeMessage(string property) => property switch
    {
        nameof(LauncherViewModel.ActionNotice) => _viewModel.ActionNotice,
        nameof(LauncherViewModel.FileNotice) => _viewModel.FileNotice,
        nameof(LauncherViewModel.HotKeyNotice) => _viewModel.HotKeyNotice,
        _ => null,
    };

    /// <summary>
    /// Brings the selected row into the bounded viewport. Deferred until the list has generated its
    /// rows and merged into one callback that reads the selection at that time, so a burst of changes
    /// (a refresh clears and refills the list) scrolls once to the final selection.
    /// </summary>
    private void QueueSelectionIntoView()
    {
        if (_selectionScroll is { Status: DispatcherOperationStatus.Pending })
            return;

        _selectionScroll = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollSelectionIntoView));
    }

    private void ScrollSelectionIntoView()
    {
        _selectionScroll = null;
        var index = _viewModel.SelectedIndex;
        if (index < 0)
            return;

        if (_viewModel.IsHomeQuery)
        {
            if (index == 0)
                HomeScroll.ScrollToTop();
            if (index < _viewModel.HomePins.Count)
            {
                (HomePinsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)?.BringIntoView();
                return;
            }

            var historyIndex = index - _viewModel.HomePins.Count;
            if (historyIndex < 0 || historyIndex >= _viewModel.Results.Count)
                return;
            (HomeHistoryList.ItemContainerGenerator.ContainerFromIndex(historyIndex) as FrameworkElement)?.BringIntoView();
            return;
        }

        if (index >= _viewModel.Results.Count)
            return;
        if (index == 0)
            FindDescendant<ScrollViewer>(ResultsList)?.ScrollToTop();
        ResultsList.ScrollIntoView(_viewModel.Results[index]);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Arm the full grace period; the timer re-checks activity and context-menu suppression when
        // it elapses, so the window is reliably hidden after the grace period instead of dropping
        // the hide when a background-priority callback runs early.
        _deactivationGrace.NotifyDeactivated();
    }

    /// <summary>Only accepted focus-loss hides close QuickLook; menus/dialogs and refused hides do not.</summary>
    private void HideAfterDeactivation()
    {
        if (!IsVisible) return;
        HideLauncher();
        if (!IsVisible) _ = _viewModel.ClosePreviewAsync();
    }

    private void SelectAndFollowPreview(int index)
    {
        var before = _viewModel.SelectedItem;
        _viewModel.SelectedIndex = index;
        var selected = _viewModel.SelectedItem;
        if (!ReferenceEquals(before, selected) || !ReferenceEquals(_lastMousePreviewItem, selected))
        {
            _viewModel.SwitchPreviewSelected();
            _lastMousePreviewItem = selected;
        }
    }

    private void OnPinnedTilePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is Button { DataContext: ResultItemViewModel item }
            && _viewModel.HomePins.IndexOf(item) is var index && index >= 0)
            SelectAndFollowPreview(index);
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnPinnedTileGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: ResultItemViewModel item }
            && _viewModel.HomePins.IndexOf(item) is var index && index >= 0)
            _viewModel.SelectedIndex = index;
    }

    private void OnPinnedTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ResultItemViewModel item })
            return;
        var index = _viewModel.HomePins.IndexOf(item);
        if (index >= 0)
            SelectAndFollowPreview(index);
        item.OpenCommand.Execute(null);
    }

    private ResultItemViewModel? GetFocusedPinnedTile()
    {
        for (var current = Keyboard.FocusedElement as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is Button { DataContext: ResultItemViewModel item } && _viewModel.HomePins.Contains(item))
                return item;
        }
        return null;
    }

    private bool SelectFocusedPinnedTile(ResultItemViewModel item)
    {
        var index = _viewModel.HomePins.IndexOf(item);
        if (index < 0)
            return false;

        _viewModel.SelectedIndex = index;
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Volatile.Read(ref _openContextMenus) > 0)
            return;

        var modifiers = ModifierProvider();
        var control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        // An active (Chinese) IME swallows Ctrl+Space and hands the key over as ImeProcessed, with the
        // real key in ImeProcessedKey. Only the two explicit Ctrl chords look through it: Enter, Esc,
        // Tab and the arrows must keep belonging to the IME while it is composing or choosing.
        var chordKey = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var focusedPinnedTile = GetFocusedPinnedTile();
        var buttonKey = e.Key is Key.Enter or Key.Space;

        // Keep native Space activation, but handle Enter explicitly: a non-default WPF Button
        // does not promise Enter activation. Modified chords still reach the launcher below.
        if (buttonKey && ThemeToggleButton.IsKeyboardFocusWithin)
            return;
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && focusedPinnedTile is not null)
        {
            if (SelectFocusedPinnedTile(focusedPinnedTile))
                focusedPinnedTile.OpenCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (buttonKey && focusedPinnedTile is not null && modifiers != ModifierKeys.Control)
            return;

        if (focusedPinnedTile is not null && modifiers == ModifierKeys.Control && chordKey == Key.Space)
            SelectFocusedPinnedTile(focusedPinnedTile);
        if (modifiers == ModifierKeys.Control && TryHandleControlChord(e, chordKey))
            return;

        if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && focusedPinnedTile is not null)
        {
            if (SelectFocusedPinnedTile(focusedPinnedTile))
                focusedPinnedTile.OpenContainingFolderCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher();
                e.Handled = true;
                break;

            case Key.Tab:
                // Only while the query box owns the keyboard, and never while a result context menu
                // is up, so menu navigation is never hijacked.
                if (ShouldCycleScope())
                {
                    _viewModel.CycleScope((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
                    e.Handled = true;
                }

                break;

            case Key.Right when ShouldNavigateHomeHorizontally(modifiers, focusedPinnedTile):
            case Key.Down when ShouldNavigateResults():
                if (focusedPinnedTile is not null)
                    SelectFocusedPinnedTile(focusedPinnedTile);
                if (_viewModel.MoveSelection(1))
                    _viewModel.SwitchPreviewSelected();
                SyncNavigation(e);
                break;

            case Key.Left when ShouldNavigateHomeHorizontally(modifiers, focusedPinnedTile):
            case Key.Up when ShouldNavigateResults():
                if (focusedPinnedTile is not null)
                    SelectFocusedPinnedTile(focusedPinnedTile);
                if (_viewModel.MoveSelection(-1))
                    _viewModel.SwitchPreviewSelected();
                SyncNavigation(e);
                break;

            case Key.PageDown when ShouldNavigateResults():
                _viewModel.MoveSelectionPage(1);
                SyncNavigation(e);
                break;

            case Key.PageUp when ShouldNavigateResults():
                _viewModel.MoveSelectionPage(-1);
                SyncNavigation(e);
                break;

            case Key.Home when ShouldNavigateResults():
                _viewModel.MoveSelectionToStart();
                SyncNavigation(e);
                break;

            case Key.End when ShouldNavigateResults():
                _viewModel.MoveSelectionToEnd();
                SyncNavigation(e);
                break;

            case Key.Enter:
                if (control)
                    _viewModel.OpenContainingFolderSelected();
                else
                    _viewModel.OpenSelected();
                e.Handled = true;
                break;

            case Key.C when control:
                // Let Ctrl+C copy selected search text; only copy the result path when nothing is
                // selected in the search box.
                if (SearchBox.SelectionLength == 0)
                {
                    _viewModel.CopyPathSelected();
                    e.Handled = true;
                }

                break;
        }
    }

    /// <summary>
    /// The exact-Ctrl chords (the caller checked the modifiers): Ctrl+Space previews the selected row
    /// in QuickLook, Ctrl+G edits the complete input (including a folder prefix) in notepad. A plain
    /// Space and Ctrl+Shift/Alt+Space keep their own meaning, and a held key fires only once.
    /// </summary>
    private bool TryHandleControlChord(KeyEventArgs e, Key key)
    {
        switch (key)
        {
            case Key.Space when ShouldNavigateResults():
                if (!e.IsRepeat)
                    _viewModel.PreviewSelected();
                e.Handled = true;
                return true;

            case Key.G when SearchBox.IsKeyboardFocusWithin:
                if (!e.IsRepeat)
                    _ = StartEditingAsync(_viewModel.Query);
                e.Handled = true;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Result navigation keys apply unless a result context menu is using them.
    /// </summary>
    private bool ShouldNavigateResults() => Volatile.Read(ref _openContextMenus) == 0;

    /// <summary>Plain left/right navigate home only when they cannot edit query text.</summary>
    private bool ShouldNavigateHomeHorizontally(ModifierKeys modifiers, ResultItemViewModel? focusedPinnedTile) =>
        modifiers == ModifierKeys.None && _viewModel.ShowHomeTiles && ShouldNavigateResults()
        && (SearchBox.IsKeyboardFocusWithin ? SearchBox.Text.Length == 0 : focusedPinnedTile is not null);

    /// <summary>
    /// Always re-syncs the viewport after a navigation key: at a boundary (Home on the first row after
    /// a mouse-wheel scroll) the selection does not change, yet the row may be out of view.
    /// </summary>
    private void SyncNavigation(KeyEventArgs e)
    {
        // A focused tile must follow selection, otherwise its Enter/Space action would still open
        // the old tile. Entering history returns focus to the query box, without selecting its text.
        if (GetFocusedPinnedTile() is not null)
        {
            var index = _viewModel.SelectedIndex;
            if (_viewModel.IsHomeQuery && index >= 0 && index < _viewModel.HomePins.Count
                && HomePinsList.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container
                && FindDescendant<Button>(container) is { } tile)
                Keyboard.Focus(tile);
            else
                Keyboard.Focus(SearchBox);
        }
        QueueSelectionIntoView();
        e.Handled = true;
    }

    private bool ShouldCycleScope()
    {
        if (Volatile.Read(ref _openContextMenus) > 0)
            return false;
        if ((ModifierProvider() & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            return false;

        return SearchBox.IsKeyboardFocusWithin;
    }

    // ----- mouse: shared home scrolling and row activation -----------------------------------------

    private void OnHomePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Nested ScrollViewers (the horizontal pins and the disabled history viewport) otherwise
        // swallow the wheel. Deliver it straight to the shared viewer using WPF's normal wheel step.
        e.Handled = true;
        HomeScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
        });
    }

    /// <summary>
    /// A left click selects the hit row without taking focus from the query; a double-click also
    /// opens that same row through its own command (the same open flow as Enter). Blank list space,
    /// scroll bars and other mouse buttons do nothing.
    /// </summary>
    private void OnResultsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || FindResultRow(e.OriginalSource as DependencyObject, ResultsList) is not { } row)
            return;

        var index = _viewModel.Results.IndexOf(row);
        if (index < 0)
            return;
        SelectAndFollowPreview(index);
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            row.OpenCommand.Execute(null);
        }
    }

    private void OnHomeHistoryPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || FindResultRow(e.OriginalSource as DependencyObject, HomeHistoryList) is not { } row)
            return;

        var index = _viewModel.Results.IndexOf(row);
        if (index >= 0)
            SelectAndFollowPreview(_viewModel.HomePins.Count + index);
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            row.OpenCommand.Execute(null);
        }
    }

    private static ResultItemViewModel? FindResultRow(DependencyObject? source, ListBox owner)
    {
        for (var current = source; current is not null; current = ParentOf(current))
        {
            if (current is ListBoxItem container)
            {
                // A container of another list or a stale row is never treated as a launcher result.
                if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(container), owner))
                    return null;
                return owner.ItemContainerGenerator.ItemFromContainer(container) as ResultItemViewModel;
            }

            if (ReferenceEquals(current, owner))
                return null;
        }
        return null;
    }

    /// <summary>Parent in the visual tree, or in the logical tree for content elements such as a Run.</summary>
    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    // ----- hidden items manager --------------------------------------------------------------------

    private void OnRequestManageHidden()
    {
        if (_closed || _hiddenDialog is not null)
            return;

        var dialog = new HiddenItemsWindow(_viewModel);
        _hiddenDialog = dialog;

        // Separate from context menus: only this dialog's lifetime holds the focus-loss hide back.
        Interlocked.Increment(ref _modalDialogs);
        try
        {
            HiddenWindowPresenter(dialog);
        }
        finally
        {
            Interlocked.Decrement(ref _modalDialogs);
            _hiddenDialog = null;

            if (!_closed && IsVisible)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        }
    }

    private bool? PresentHiddenWindow(HiddenItemsWindow dialog)
    {
        // An owner must have been shown; the launcher always is when its own button was clicked.
        if (IsVisible)
        {
            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog();
    }

    // ----- notepad editing --------------------------------------------------------------------------

    /// <summary>
    /// Pasting text that contains a line break is turned into a notepad edit: the paste is merged with
    /// the current text over the current selection (every line break kept), written to the input and
    /// handed to the editor. Single-line pastes, and anything that is not text, keep the default
    /// behaviour. One handler covers Ctrl+V, Shift+Insert and the context menu.
    /// </summary>
    private void OnSearchBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_closed || e.IsDragDrop)
            return;
        if (!TryReadPastedText(e, out var pasted) || pasted.IndexOfAny(LineBreaks) < 0)
            return;

        e.CancelCommand();

        var current = SearchBox.Text ?? string.Empty;
        var start = Math.Clamp(SearchBox.SelectionStart, 0, current.Length);
        var length = Math.Clamp(SearchBox.SelectionLength, 0, current.Length - start);
        var merged = current[..start] + pasted + current[(start + length)..];

        // The merged text is in the input before the editor is asked for anything, so a busy or
        // failing editor can never cost the user the pasted lines.
        SetQueryText(merged, start + pasted.Length);
        _ = StartEditingAsync(merged);
    }

    private static bool TryReadPastedText(DataObjectPastingEventArgs e, out string text)
    {
        text = string.Empty;
        try
        {
            foreach (var format in PasteTextFormats)
            {
                if (e.DataObject.GetDataPresent(format) && e.DataObject.GetData(format) is string value)
                {
                    text = value;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A clipboard that cannot be read (busy / vanished): leave the default paste to cope.
        }

        return false;
    }

    /// <summary>
    /// Puts text into the query and the box and keeps both identical (the box may normalize line
    /// breaks). Never shows or activates the window and never moves the keyboard focus.
    /// </summary>
    private void SetQueryText(string text, int? caret = null)
    {
        if (!string.Equals(SearchBox.Text, text, StringComparison.Ordinal))
            SearchBox.Text = text;

        _viewModel.Query = SearchBox.Text;
        SearchBox.CaretIndex = Math.Min(caret ?? SearchBox.Text.Length, SearchBox.Text.Length);
    }

    /// <summary>
    /// Starts a notepad edit of <paramref name="text"/>. Success hides the launcher; failure, a busy
    /// start or an already running session leaves the input untouched and shows an on-demand notice.
    /// </summary>
    internal async Task StartEditingAsync(string text)
    {
        if (_closed)
            return;
        if (_startingEditor)
        {
            _viewModel.SetActionNotice("记事本正在启动，输入已保留；稍后可再按 Ctrl+G 编辑");
            return;
        }

        // The revision the draft is based on is taken now, not when the start returns: anything typed
        // while the editor starts must keep winning over a later save.
        var revision = _queryRevision;
        var token = _lifetime.Token;
        QueryEditorLaunchContext? launchContext;
        try
        {
            launchContext = SampleEditorContext();
        }
        catch (Exception)
        {
            launchContext = null;
        }
        _startingEditor = true;

        QueryEditorStartResult result;
        try
        {
            result = await _queryEditor.StartAsync(text ?? string.Empty, launchContext, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _earlySaves.Clear();
            _earlyClosures.Clear();
            if (!_closed)
                _viewModel.SetActionNotice("记事本启动已取消，输入已保留");
            return;
        }
        catch (Exception exception)
        {
            _earlySaves.Clear();
            _earlyClosures.Clear();
            if (!_closed)
                _viewModel.SetActionNotice($"无法启动记事本：{exception.Message}（输入已保留）");
            return;
        }
        finally
        {
            _startingEditor = false;
        }

        if (_closed)
            return;

        if (!result.Success)
        {
            _earlySaves.Clear();
            _earlyClosures.Clear();
            var reason = string.IsNullOrWhiteSpace(result.Error) ? "未知原因" : result.Error;
            _viewModel.SetActionNotice($"无法启动记事本：{reason}（输入已保留）");
            return;
        }

        if (_sessionId == result.SessionId)
        {
            _earlySaves.Clear();
            _earlyClosures.Clear();
            _viewModel.SetActionNotice("已有进行中的记事本编辑，未创建新的编辑；可点顶部“编辑中”结束编辑后重新开始");
            UpdateEditingIndicator();
            return;
        }

        BeginSession(result.SessionId, revision);
        if (_sessionId == result.SessionId)
            HideLauncher();
    }

    private void BeginSession(Guid sessionId, int revision)
    {
        _sessionId = sessionId;
        _expectedRevision = revision;
        _latestSequence = 0;
        _pendingSavedText = null;

        // A save that was queued before the start returned belongs to this session if the ids agree.
        _earlySaves.TryGetValue(sessionId, out var early);
        _earlyClosures.TryGetValue(sessionId, out var earlyClosure);
        _earlySaves.Clear();
        _earlyClosures.Clear();
        UpdateEditingIndicator();

        if (early is not null)
            HandleEditorSaved(early);
        if (earlyClosure is not null)
            HandleEditorClosed(earlyClosure);
    }

    private void ResetSession()
    {
        _sessionId = null;
        _latestSequence = 0;
        _pendingSavedText = null;
        _earlySaves.Clear();
        _earlyClosures.Clear();
    }

    private void OnEditorSaved(QueryEditorSaved saved) => PostToUi(() => HandleEditorSaved(saved));

    private void OnEditorFailed(QueryEditorError error) => PostToUi(() => HandleEditorFailed(error));

    private void OnEditorClosed(QueryEditorClosed closed) => PostToUi(() => HandleEditorClosed(closed));

    /// <summary>Runs on the UI thread: inline when already there, otherwise queued in event order.</summary>
    private void PostToUi(Action action)
    {
        if (_closed)
            return;
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    if (!_closed)
                        action();
                })
            );
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is shutting down together with the window.
        }
    }

    /// <summary>
    /// A save from the editor. Accepted only for the active session and only with a newer sequence;
    /// written back only while the query is unchanged since the session last agreed with it. Otherwise
    /// the saved text is kept for an explicit “应用已保存内容”, never overwriting what the user typed.
    /// Never shows, activates or focuses the window.
    /// </summary>
    internal void HandleEditorSaved(QueryEditorSaved saved)
    {
        if (_closed || saved is null)
            return;

        if (saved.SessionId != _sessionId)
        {
            // Possibly the save of a session whose start has not returned yet; anything else is stale.
            if (_startingEditor
                && (!_earlySaves.TryGetValue(saved.SessionId, out var known) || saved.Sequence > known.Sequence))
            {
                if (_earlySaves.Count >= 8)
                    _earlySaves.Clear();
                _earlySaves[saved.SessionId] = saved;
            }

            return;
        }

        if (saved.Sequence <= _latestSequence)
            return;

        _latestSequence = saved.Sequence;
        var text = saved.Text ?? string.Empty;

        if (_queryRevision == _expectedRevision || string.Equals(_viewModel.Query, text, StringComparison.Ordinal))
        {
            ApplyEditorText(text);
            _pendingSavedText = null;
            _viewModel.SetActionNotice("已从记事本更新输入");
        }
        else
        {
            _pendingSavedText = text;
            _viewModel.SetActionNotice("记事本已保存新内容，但输入框已被修改，未覆盖；可在顶部“编辑中”菜单应用已保存内容");
        }

        UpdateEditingIndicator();
    }

    internal void HandleEditorClosed(QueryEditorClosed closed)
    {
        if (_closed || closed is null)
            return;

        if (closed.SessionId != _sessionId)
        {
            // The tab can close as soon as Notepad appears, before StartAsync returns its session id.
            if (_startingEditor
                && (!_earlyClosures.TryGetValue(closed.SessionId, out var known)))
            {
                if (_earlyClosures.Count >= 8)
                    _earlyClosures.Clear();
                _earlyClosures[closed.SessionId] = closed;
            }

            return;
        }

        var savedText = closed.SavedText ?? string.Empty;
        ResetSession();
        UpdateEditingIndicator();

        string? endFailure = null;
        try
        {
            _queryEditor.EndSession();
        }
        catch (Exception exception)
        {
            endFailure = exception.Message;
        }

        bool copied;
        try
        {
            copied = _copyTextToClipboard(savedText);
        }
        catch (Exception)
        {
            copied = false;
        }

        ShowLauncher();
        _viewModel.SetActionNotice(copied
            ? endFailure is null
                ? "记事本草稿标签已关闭，最后保存的内容已复制到剪贴板"
                : $"记事本标签已关闭，内容已复制；结束编辑时出错：{endFailure}"
            : "记事本草稿标签已关闭，但复制到剪贴板失败；草稿文件仍已保留");
    }

    private void HandleEditorFailed(QueryEditorError error)
    {
        if (_closed || error is null || error.SessionId != _sessionId)
            return;

        var message = string.IsNullOrWhiteSpace(error.Message) ? "未知错误" : error.Message;
        _viewModel.SetActionNotice($"记事本编辑出错：{message}（草稿文件已保留）");
    }

    /// <summary>Writes editor text into the query and agrees the session's expected revision with it.</summary>
    private void ApplyEditorText(string text)
    {
        SetQueryText(text);
        _expectedRevision = _queryRevision;
    }

    /// <summary>Applies the saved text that was held back because the query had changed.</summary>
    internal bool ApplyPendingSavedText()
    {
        if (_closed || _pendingSavedText is not { } text)
            return false;

        ApplyEditorText(text);
        _pendingSavedText = null;
        UpdateEditingIndicator();
        _viewModel.SetActionNotice("已应用记事本中保存的内容");
        return true;
    }

    /// <summary>
    /// Ends the editing session in the launcher: later saves are ignored. The draft file stays and the
    /// user's notepad window is not closed.
    /// </summary>
    internal void EndEditing()
    {
        if (_closed)
            return;

        var hadSession = _sessionId is not null;
        ResetSession();
        UpdateEditingIndicator();

        try
        {
            _queryEditor.EndSession();
        }
        catch (Exception exception)
        {
            _viewModel.SetActionNotice($"结束编辑时出错：{exception.Message}");
            return;
        }

        if (hadSession)
            _viewModel.SetActionNotice("已结束编辑；草稿文件已保留，记事本窗口未被关闭");
    }

    /// <summary>
    /// On every show, asks the editor to re-read the session's own draft so a save the file watcher
    /// missed is still picked up. The result arrives as a normal save and passes the same checks.
    /// </summary>
    private void RefreshEditorSession()
    {
        if (_closed || _sessionId is null)
            return;

        // The service ended the session by itself (for example after cleaning its draft): forget it.
        if (_queryEditor.CurrentSessionId != _sessionId)
        {
            ResetSession();
            UpdateEditingIndicator();
            return;
        }

        _ = RefreshEditorAsync();
    }

    private async Task RefreshEditorAsync()
    {
        try
        {
            await _queryEditor.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception exception)
        {
            if (!_closed)
                _viewModel.SetActionNotice($"读取记事本草稿失败：{exception.Message}");
        }
    }

    private void UpdateEditingIndicator()
    {
        var active = _sessionId is not null;
        EditingButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        EditingButton.Content = _pendingSavedText is null ? "编辑中" : "编辑中 · 有未应用内容";
        ApplySavedMenuItem.IsEnabled = _pendingSavedText is not null;

        var draft = active ? _queryEditor.DraftPath : null;
        EditingButton.ToolTip = active
            ? string.IsNullOrEmpty(draft)
                ? "记事本编辑进行中\n点击可应用已保存内容或结束编辑"
                : $"记事本编辑进行中\n草稿文件：{draft}\n点击可应用已保存内容或结束编辑"
            : null;
    }

    private void OnEditingButtonClick(object sender, RoutedEventArgs e)
    {
        if (EditingButton.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = EditingButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnApplySavedEditorTextClick(object sender, RoutedEventArgs e) => ApplyPendingSavedText();

    private void OnEndEditingClick(object sender, RoutedEventArgs e) => EndEditing();

    private void OnTrayAvailabilityChanged(bool available, string? failureMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!_closed && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => OnTrayAvailabilityChanged(available, failureMessage)));
            return;
        }
        if (_closed || !_trayInitializationAttempted)
            return;

        _trayIconAvailable = available;
        _trayFailureMessage = available ? null : failureMessage ?? _trayIcon.FailureMessage;
        RefreshTrayNotice();

        // With no registered hotkey a hidden launcher would be unreachable once Explorer rejects the
        // re-registration. Keep the diagnostic visible until the tray route is healthy or Alt+F4 exits.
        if (!available && !HotKeyAvailable && !IsVisible)
            ShowLauncher();
    }

    private void RefreshTrayNotice()
    {
        var notice = _hotKeyNotice;
        var failure = _hotKeyFailure;
        HotKeyStatus = _hotKeySummary;

        if (!_trayIconAvailable)
        {
            var reason = _trayFailureMessage ?? _trayIcon.FailureMessage;
            var trayNotice = $"系统托盘图标不可用：{(string.IsNullOrWhiteSpace(reason) ? "未知错误" : reason)}；窗口会保持显示，按 Alt+F4 退出。";
            if (notice is not null)
                notice = notice.Replace(HotKeyRegistrationStatus.UnavailableNotice, "系统托盘图标不可用时可按 Alt+F4 退出");
            notice = string.IsNullOrEmpty(notice) ? trayNotice : $"{notice}；{trayNotice}";
            failure = true;
            HotKeyStatus = string.IsNullOrEmpty(HotKeyStatus) ? trayNotice : $"{HotKeyStatus}；{trayNotice}";
        }

        _viewModel.SetHotKeyStatus(notice, failure);
    }

    private void OnTrayShowRequested() => DispatchTrayAction(ShowLauncher);

    private void OnTrayManageHiddenRequested() => DispatchTrayAction(() => _viewModel.ManageHiddenCommand.Execute(null));

    private void OnTrayQuitRequested() => DispatchTrayAction(() => _viewModel.QuitCommand.Execute(null));

    private void DispatchTrayAction(Action action)
    {
        if (_closed)
            return;
        if (Dispatcher.CheckAccess())
            action();
        else
            _ = Dispatcher.BeginInvoke(action);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_trayIcon.HandleWindowMessage(message, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WmHotKey)
        {
            // Dispatch by id so the independent file shortcut never toggles the launcher hide.
            if (wParam.ToInt32() == GlobalHotKey.FileHotKeyId)
                ShowFilesFromExplorer();
            else
                ToggleLauncher();

            handled = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The launcher is a resident hotkey app (it starts hidden and has no taskbar entry), so a
        // user close such as Alt+F4 must hide it like Esc does. Actually closing the window here used
        // to dispose the hotkey while the process kept its single-instance mutex, leaving a running
        // process that could no longer be shown or started again. Only an explicit shutdown closes it.
        if (!_allowClose)
        {
            if (_trayInitializationAttempted && !_trayIconAvailable)
            {
                // Never trap a resident process with neither its tray escape route nor a working hotkey.
                AllowClose();
                _applicationShutdown.Shutdown();
            }
            else
            {
                e.Cancel = true;
                HideLauncher();
                return;
            }
        }

        base.OnClosing(e);
    }

    private readonly record struct QuickLookRequestAnchor(long Generation, LauncherWindowSnapshot Snapshot);

    private readonly record struct QuickLookAvoidanceLayout(
        ScreenPlacement.PixelsRect WorkArea,
        IReadOnlyList<ScreenPlacement.PixelsRect> PreviewBounds);

    protected override void OnClosed(EventArgs e)
    {
        // First: any queued editor callback or pending start/refresh continuation sees a closed window.
        _closed = true;
        _noticeDispatch?.Abort();
        _noticePresenter.Dispose();
        CancelQuickLookAvoidance(resetLayout: true);
        if (_hiddenDialog is { IsVisible: true } dialog)
            dialog.Close();

        Deactivated -= OnDeactivated;
        ResultsList.PreviewMouseLeftButtonDown -= OnResultsPreviewMouseLeftButtonDown;
        HomeHistoryList.PreviewMouseLeftButtonDown -= OnHomeHistoryPreviewMouseLeftButtonDown;
        DataObject.RemovePastingHandler(SearchBox, OnSearchBoxPasting);
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.RequestHide -= HideLauncher;
        _viewModel.RequestQuit -= Quit;
        _viewModel.RequestManageHidden -= OnRequestManageHidden;
        _viewModel.QuickLookPlacementRequestStarted -= OnQuickLookPlacementRequestStarted;
        _viewModel.QuickLookPlacementRequestCompleted -= OnQuickLookPlacementRequestCompleted;
        _trayIcon.ShowRequested -= OnTrayShowRequested;
        _trayIcon.ManageHiddenRequested -= OnTrayManageHiddenRequested;
        _trayIcon.QuitRequested -= OnTrayQuitRequested;
        _trayIcon.AvailabilityChanged -= OnTrayAvailabilityChanged;
        try
        {
            _trayIcon.Dispose();
        }
        catch (Exception)
        {
            // Native tray cleanup must not prevent the application from shutting down.
        }

        // Stop the editor's callbacks and release it. Only the launcher's tracking ends here: the
        // user's notepad window and the draft file are not closed or deleted by the window.
        _queryEditor.Saved -= OnEditorSaved;
        _queryEditor.Failed -= OnEditorFailed;
        _queryEditor.Closed -= OnEditorClosed;
        _lifetime.Cancel();
        try
        {
            _queryEditor.Dispose();
        }
        catch (Exception)
        {
            // A failing cleanup must not keep the window from closing.
        }

        _lifetime.Dispose();
        ResetSession();
        _deactivationGrace.Dispose();
        EndPlacement();
        _selectionScroll?.Abort();
        _selectionScroll = null;
        _source?.RemoveHook(WindowMessageHook);
        _hotKey?.Dispose();
        base.OnClosed(e);
    }
}
