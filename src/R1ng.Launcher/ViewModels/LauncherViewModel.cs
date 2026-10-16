using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.Theming;

namespace R1ng.Launcher.ViewModels;

/// <summary>
/// Launcher state: the query, the scope chips, the visible rows and the selection.
/// Search itself is delegated to <see cref="SearchController"/>; this type only marshals snapshots
/// into WPF-friendly state and keeps the previous rows visible while the next query runs.
/// </summary>
public sealed class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly IconLoadScheduler _icons;
    private readonly Func<SearchResultItem, bool> _openTarget;
    private readonly Func<SearchResultItem, bool> _openContainingTarget;
    private readonly Func<SearchResultItem, bool> _copyTarget;
    private readonly IHiddenResultStore? _hiddenResults;
    private readonly IQuickLookPreviewService _previewService;
    private CancellationTokenSource? _iconCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _switchCancellation;
    private CancellationTokenSource? _closePreviewCancellation;
    private readonly SemaphoreSlim _previewRequestGate = new(1, 1);
    private SearchResultItem? _queuedSwitch;
    private long _switchGeneration;
    private long _quickLookPlacementGeneration;
    private long _queuedSwitchPlacementGeneration;
    private bool _switchDrainRunning;
    private bool _isPreviewBusy;
    private string? _actionNotice;
    private bool _disposed;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);

    private readonly SearchController _controller;
    private readonly IHistoryProvider _history;
    private readonly Action? _refreshFileBackend;
    private readonly Action<AppTheme>? _applyTheme;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private readonly SelectionModel _selection = new();

    private CancellationTokenSource? _searchCancellation;
    private SearchSnapshot _snapshot = SearchSnapshot.Idle;
    private string _query = string.Empty;
    private SearchScope _scope = SearchScope.All;
    private AppTheme _theme = AppTheme.Dark;
    private int _selectedIndex = -1;
    private int _pendingGeneration;
    private bool _isSearching;
    private bool _isMatchCase;
    private bool _isMatchWholeWord;
    private bool _isMatchPath;
    private bool _isRegex;
    private string? _statusText;
    private string? _fileNotice;
    private string _footerHint = string.Empty;
    private string? _hotKeyNotice;
    private bool _isHotKeyFailure;

    public LauncherViewModel(
        SearchController controller,
        IHistoryProvider history,
        Dispatcher? dispatcher = null,
        Action? refreshFileBackend = null,
        AppTheme initialTheme = AppTheme.Dark,
        Action<AppTheme>? applyTheme = null,
        Func<SearchResultItem, bool>? openTarget = null,
        Func<string, bool, int, System.Windows.Media.ImageSource?>? iconLoader = null,
        TimeSpan? iconRetryDelay = null,
        Func<SearchResultItem, bool>? openContainingTarget = null,
        Func<SearchResultItem, bool>? copyTarget = null,
        IHiddenResultStore? hiddenResults = null,
        IQuickLookPreviewService? previewService = null
    )
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _refreshFileBackend = refreshFileBackend;
        _theme = initialTheme;
        _applyTheme = applyTheme;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _icons = new IconLoadScheduler(iconLoader, iconRetryDelay);
        _openTarget = openTarget ?? TargetLauncher.Open;
        _openContainingTarget = openContainingTarget ?? TargetLauncher.OpenContainingFolder;
        _copyTarget = copyTarget ?? TargetLauncher.CopyPath;
        _hiddenResults = hiddenResults;
        _previewService = previewService ?? new QuickLookPreviewService();

        // A damaged or unreadable hidden list must be visible, not silently empty. The store loads
        // lazily, so touch it once (bounded read) to learn about a load failure now; the notice is an
        // action notice, so the first search (Prime) cannot erase it.
        if (_hiddenResults is not null)
        {
            _ = _hiddenResults.GetHidden();
            if (!string.IsNullOrWhiteSpace(_hiddenResults.Error))
                _actionNotice = _hiddenResults.Error;
        }

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = DebounceDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync(_pendingGeneration);
        };

        SetScopeCommand = new RelayCommand<SearchScope>(SetScope);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        HomePins.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHomePins));
            OnPropertyChanged(nameof(ShowHomeTiles));
        };
        RefreshFileBackendCommand = new RelayCommand(RefreshFileBackend);
        OpenCommand = new RelayCommand(() => OpenSelected(), () => HasSelection);
        OpenContainingFolderCommand = new RelayCommand(() => OpenContainingFolderSelected(), () => HasSelection);
        CopyPathCommand = new RelayCommand(() => CopyPathSelected(), () => HasSelection);
        QuitCommand = new RelayCommand(() => RequestQuit?.Invoke());
        ManageHiddenCommand = new RelayCommand(() => RequestManageHidden?.Invoke());
    }

    public ObservableCollection<ResultItemViewModel> Results { get; } = new();

    /// <summary>Pinned applications on home, displayed as the single horizontal tile row; pinned files stay in Results.</summary>
    public ObservableCollection<ResultItemViewModel> HomePins { get; } = new();

    public bool HasHomePins => HomePins.Count > 0;

    public bool ShowHomeTiles => IsHomeQuery && HasHomePins;

    /// <summary>True for the default blank/whitespace-query home view.</summary>
    public bool IsHomeQuery => string.IsNullOrWhiteSpace(_query);

    public ICommand SetScopeCommand { get; }

    public ICommand ToggleThemeCommand { get; }

    public ICommand RefreshFileBackendCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand OpenContainingFolderCommand { get; }

    public ICommand CopyPathCommand { get; }

    /// <summary>Explicitly quits the resident launcher (wired to the tray menu).</summary>
    public ICommand QuitCommand { get; }

    /// <summary>Opens the hidden-items manager (wired to the tray menu).</summary>
    public ICommand ManageHiddenCommand { get; }

    /// <summary>Raised when the user asks to review or restore hidden items.</summary>
    public event Action? RequestManageHidden;

    public string Query
    {
        get => _query;
        set => SetQuery(value, immediate: false);
    }

    public bool ShowPlaceholder => string.IsNullOrEmpty(_query);

    /// <summary>Active palette. Setting it applies and persists the theme through the injected seam.</summary>
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value))
                return;

            OnPropertyChanged(nameof(ThemeToggleGlyph));
            OnPropertyChanged(nameof(ThemeToggleAutomationName));
            _applyTheme?.Invoke(value);
        }
    }

    /// <summary>The icon advertises the palette that the button will switch to.</summary>
    public string ThemeToggleGlyph => Theme == AppTheme.Dark ? "\uE706" : "\uE708";

    public string ThemeToggleAutomationName => Theme == AppTheme.Dark ? "切换到浅色主题" : "切换到深色主题";

    public void ToggleTheme() => Theme = Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

    /// <summary>
    /// The user's keywords with any leading folder prefix removed. The prefix is only a constraint;
    /// ranking and highlighting always operate on these terms.
    /// </summary>
    public string CurrentTerms => FolderPrefixQuery.TryParse(_query, out _, out var terms) ? terms : _query;

    /// <summary>Index where the editable keywords start inside <see cref="Query"/> (0 without a prefix).</summary>
    public int TermsStartIndex =>
        FolderPrefixQuery.TryParse(_query, out _, out var terms) ? _query.Length - terms.Length : 0;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!SetProperty(ref _selectedIndex, value))
                return;

            // Home navigation covers the tile row followed by the recent-history list.
            if (value >= 0)
                _selection.Reset(SelectionCount, value);

            UpdateSelectionFlags();
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommandStates();
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(ShowStateMessage));
                OnPropertyChanged(nameof(EmptyResultsMessage));
            }
        }
    }

    public string? FileNotice
    {
        get => _fileNotice;
        private set
        {
            if (SetProperty(ref _fileNotice, value))
                OnPropertyChanged(nameof(ShowFileNotice));
        }
    }

    public bool ShowFileNotice => !string.IsNullOrEmpty(_fileNotice);

    /// <summary>Only neutral empty-state guidance belongs in the card; diagnostics use floating notices.</summary>
    public string EmptyResultsMessage => BuildEmptyMessage(_snapshot);

    /// <summary>
    /// Hotkey warning presented in a transient floating notice (for example when the combination fell back or
    /// when no combination could be registered at all). Null when the preferred hotkey is active.
    /// </summary>
    public string? HotKeyNotice
    {
        get => _hotKeyNotice;
        private set
        {
            if (SetProperty(ref _hotKeyNotice, value))
                OnPropertyChanged(nameof(ShowHotKeyNotice));
        }
    }

    public bool ShowHotKeyNotice => !string.IsNullOrEmpty(_hotKeyNotice);

    /// <summary>True when the notice is a hard failure (no hotkey at all) rather than a fallback.</summary>
    public bool IsHotKeyFailure
    {
        get => _isHotKeyFailure;
        private set => SetProperty(ref _isHotKeyFailure, value);
    }

    /// <summary>Records the hotkey registration outcome for a floating notice (called once at startup).</summary>
    public void SetHotKeyStatus(string? notice, bool isFailure)
    {
        IsHotKeyFailure = isFailure;
        HotKeyNotice = notice;
    }

    public string FooterHint
    {
        get => _footerHint;
        private set
        {
            if (SetProperty(ref _footerHint, value))
                OnPropertyChanged(nameof(DisplayFooterHint));
        }
    }

    /// <summary>
    /// Result of the last user action (hide, restore, preview, notepad editing), retained as state
    /// while its independent UI notice expires. It is independent of <see cref="FooterHint"/> and <see cref="StatusText"/>, so a
    /// following asynchronous search cannot erase it; only typing a different query or the next
    /// action replaces it.
    /// </summary>
    public string? ActionNotice
    {
        get => _actionNotice;
        private set
        {
            if (!SetProperty(ref _actionNotice, value))
                return;

            OnPropertyChanged(nameof(HasActionNotice));
            OnPropertyChanged(nameof(DisplayFooterHint));
        }
    }

    public bool HasActionNotice => !string.IsNullOrEmpty(_actionNotice);

    /// <summary>What the footer shows: the pending action notice, otherwise the regular hint.</summary>
    public string DisplayFooterHint => string.IsNullOrEmpty(_actionNotice) ? _footerHint : _actionNotice;

    /// <summary>Shows (or, with null/blank, clears) the visible action notice.</summary>
    public void SetActionNotice(string? notice)
    {
        if (_disposed)
            return;

        var normalized = string.IsNullOrWhiteSpace(notice) ? null : notice;
        if (normalized is not null && string.Equals(ActionNotice, normalized, StringComparison.Ordinal))
            OnPropertyChanged(nameof(ActionNotice)); // A repeated action renews the transient notice.
        else
            ActionNotice = normalized;
    }

    /// <summary>The hidden items of the current profile, for the manager window (never null).</summary>
    public IReadOnlyList<HiddenResultRecord> HiddenItems =>
        _hiddenResults?.GetHidden() ?? Array.Empty<HiddenResultRecord>();

    public int HiddenCount => HiddenItems.Count;

    /// <summary>Latest hidden-list read/save problem, shown in the manager window; null when healthy.</summary>
    public string? HiddenError => _hiddenResults?.Error;

    /// <summary>True while a QuickLook request is in flight; a second request is not queued.</summary>
    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set => SetProperty(ref _isPreviewBusy, value);
    }

    /// <summary>Idle/empty message shown over the (empty) result list.</summary>
    public bool ShowStateMessage => Results.Count == 0 && !string.IsNullOrEmpty(_statusText);

    public bool IsScopeAll
    {
        get => _scope == SearchScope.All;
        set
        {
            if (value)
                SetScope(SearchScope.All);
        }
    }

    /// <summary>Aa: file-search match case (Everything MATCHCASE). Off by default.</summary>
    public bool IsMatchCase
    {
        get => _isMatchCase;
        set => SetFileOption(ref _isMatchCase, value, nameof(IsMatchCase));
    }

    /// <summary>Abl: file-search whole-word matching (Everything MATCHWHOLEWORD). Off by default.</summary>
    public bool IsMatchWholeWord
    {
        get => _isMatchWholeWord;
        set => SetFileOption(ref _isMatchWholeWord, value, nameof(IsMatchWholeWord));
    }

    /// <summary>..\ : search the full path instead of only the name (Everything MATCHPATH). Off by default.</summary>
    public bool IsMatchPath
    {
        get => _isMatchPath;
        set => SetFileOption(ref _isMatchPath, value, nameof(IsMatchPath));
    }

    /// <summary>.* : treat the query as a regular expression (Everything REGEX). Off by default.</summary>
    public bool IsRegex
    {
        get => _isRegex;
        set => SetFileOption(ref _isRegex, value, nameof(IsRegex));
    }

    /// <summary>
    /// Whole-word matching is meaningless for a regex query (Everything ignores it), so the Abl
    /// toggle is disabled while <see cref="IsRegex"/> is on — matching the reference toolbar.
    /// </summary>
    public bool IsWholeWordOptionEnabled => !_isRegex;

    /// <summary>The quick-toggle state handed to the file backend on every query.</summary>
    public FileSearchOptions FileOptions => new()
    {
        MatchCase = _isMatchCase,
        MatchWholeWord = _isMatchWholeWord,
        MatchPath = _isMatchPath,
        Regex = _isRegex,
    };

    public bool IsScopeApplications
    {
        get => _scope == SearchScope.Applications;
        set
        {
            if (value)
                SetScope(SearchScope.Applications);
        }
    }

    public bool IsScopeFiles
    {
        get => _scope == SearchScope.Files;
        set
        {
            if (value)
                SetScope(SearchScope.Files);
        }
    }

    /// <summary>Application scope icon: selected for application-only or all-results mode.</summary>
    public bool IsApplicationsIncluded
    {
        get => _scope != SearchScope.Files;
        set
        {
            if (value != IsApplicationsIncluded)
                SetScope(value ? SearchScope.All : SearchScope.Files);
        }
    }

    /// <summary>File scope icon: selected for file-only or all-results mode; the last deselection switches modes.</summary>
    public bool IsFilesIncluded
    {
        get => _scope != SearchScope.Applications;
        set
        {
            if (value != IsFilesIncluded)
                SetScope(value ? SearchScope.All : SearchScope.Applications);
        }
    }

    /// <summary>Kicks off the initial (empty query) load, e.g. when the window first opens.</summary>
    public void Prime() => ScheduleSearch(immediate: true);

    /// <summary>Raised when the tray menu asks the launcher to exit.</summary>
    public event Action? RequestQuit;

    public void SetScope(SearchScope scope)
    {
        if (_scope == scope)
            return;

        _scope = scope;
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeApplications));
        OnPropertyChanged(nameof(IsScopeFiles));
        OnPropertyChanged(nameof(IsApplicationsIncluded));
        OnPropertyChanged(nameof(IsFilesIncluded));

        // Application matching must never search app names using the folder text: drop the automatic
        // folder constraint while preserving the user's keywords.
        if (scope == SearchScope.Applications && FolderPrefixQuery.TryParse(_query, out _, out var terms))
        {
            SetQuery(terms, immediate: true);
            return;
        }

        ScheduleSearch(immediate: true);
    }

    /// <summary>
    /// Tab / Shift + Tab scope transition while the query box is focused. Forward goes
    /// All → Applications and then toggles Applications ↔ Files; backward maps All → Files and
    /// toggles the same two modes in the other direction.
    /// </summary>
    public void CycleScope(bool backwards)
    {
        var next = (_scope, backwards) switch
        {
            (SearchScope.All, false) => SearchScope.Applications,
            (SearchScope.Applications, false) => SearchScope.Files,
            (SearchScope.Files, false) => SearchScope.Applications,
            (SearchScope.All, true) => SearchScope.Files,
            (SearchScope.Files, true) => SearchScope.Applications,
            (SearchScope.Applications, true) => SearchScope.Files,
            _ => SearchScope.Applications,
        };

        SetScope(next);
    }

    /// <summary>
    /// Applies the independent file-scope hotkey: file scope, the retained keywords and — when a
    /// physical Explorer folder was captured — a freshly composed quoted folder prefix. The previous
    /// automatic prefix is replaced, never stacked. Returns the query text that was applied.
    /// </summary>
    public string ApplyFileScope(string? folder)
    {
        var terms = CurrentTerms;
        var text = FolderPrefixQuery.Compose(folder, terms);

        if (_scope != SearchScope.Files)
        {
            _scope = SearchScope.Files;
            OnPropertyChanged(nameof(IsScopeAll));
            OnPropertyChanged(nameof(IsScopeApplications));
            OnPropertyChanged(nameof(IsScopeFiles));
            OnPropertyChanged(nameof(IsApplicationsIncluded));
            OnPropertyChanged(nameof(IsFilesIncluded));
        }

        // Immediate (not debounced) so the folder constraint is applied at once and a quick repeated
        // invoke replaces it without a stale in-flight response.
        SetQuery(text, immediate: true);
        return text;
    }

    private void SetQuery(string value, bool immediate)
    {
        var wasHome = IsHomeQuery;
        var changed = SetProperty(ref _query, value, nameof(Query));
        if (changed)
        {
            OnPropertyChanged(nameof(ShowPlaceholder));
            var isHome = IsHomeQuery;
            if (wasHome != isHome)
            {
                OnPropertyChanged(nameof(IsHomeQuery));
                OnPropertyChanged(nameof(ShowHomeTiles));
                // The home selection spans pins then history. Once typing hides the pin strip, map a
                // selected history row back to its position in the visible search-result list.
                var preferred = wasHome ? Math.Max(0, _selectedIndex - HomePins.Count) : _selectedIndex;
                _selection.Reset(SelectionCount, preferred);
                _selectedIndex = _selection.Index;
                OnPropertyChanged(nameof(SelectedIndex));
                OnPropertyChanged(nameof(HasSelection));
                UpdateSelectionFlags();
            }

            // A different query makes the previous action's notice obsolete.
            ActionNotice = null;
        }

        if (immediate || changed)
            ScheduleSearch(immediate: immediate);
    }

    public bool MoveSelection(int delta)
    {
        if (!_selection.HasSelection && SelectionCount > 0)
        {
            _selection.Reset(SelectionCount);
            SelectedIndex = _selection.Index;
            return true;
        }

        if (!_selection.Move(delta))
            return false;

        SelectedIndex = _selection.Index;
        return true;
    }

    public bool MoveSelectionPage(int direction, int pageSize = 5)
    {
        if (!_selection.MovePage(direction, pageSize))
            return false;

        SelectedIndex = _selection.Index;
        return true;
    }

    public bool MoveSelectionToStart() => SyncSelection(_selection.MoveToStart());

    public bool MoveSelectionToEnd() => SyncSelection(_selection.MoveToEnd());

    public void OpenSelected()
    {
        if (SelectedItem is { } item)
            Open(item);
    }

    public void OpenContainingFolderSelected()
    {
        if (SelectedItem is { } item)
            OpenContainingFolder(item);
    }

    public void CopyPathSelected()
    {
        if (SelectedItem is { } item)
            CopyPath(item);
    }

    public ResultItemViewModel? SelectedItem
    {
        get
        {
            if (SelectedIndex < 0)
                return null;
            if (IsHomeQuery)
                return SelectedIndex < HomePins.Count
                    ? HomePins[SelectedIndex]
                    : Results.ElementAtOrDefault(SelectedIndex - HomePins.Count);
            return Results.ElementAtOrDefault(SelectedIndex);
        }
    }

    private int SelectionCount => IsHomeQuery ? HomePins.Count + Results.Count : Results.Count;

    private void UpdateSelectionFlags()
    {
        for (var index = 0; index < HomePins.Count; index++)
            HomePins[index].IsSelected = IsHomeQuery && index == _selectedIndex;
        for (var index = 0; index < Results.Count; index++)
            Results[index].IsSelected = index + (IsHomeQuery ? HomePins.Count : 0) == _selectedIndex;
    }

    public bool IsPinned(SearchResultItem item) => _history.IsPinned(item);

    public void TogglePin(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        var pinned = !_history.IsPinned(item.Model);
        if (!_history.TrySetPinned(item.Model, pinned))
        {
            StatusText = pinned ? $"置顶失败：最多保留 {SearchHistoryStore.MaxPins} 项，或历史无法保存" : "取消置顶失败：历史无法保存";
            FooterHint = StatusText;

            // The footer shows the pending action notice first, so an earlier preview/hide notice
            // must not keep hiding this failure.
            SetActionNotice(StatusText);
            return;
        }
        FooterHint = BuildFooterHint(_snapshot);
        SetActionNotice(null);
        item.IsPinned = pinned;
        foreach (var row in Results.Concat(HomePins))
            row.IsPinned = _history.IsPinned(row.Model);
        if (IsHomeQuery)
            ScheduleSearch(immediate: true);
    }

    public void Open(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        if (!_openTarget(item.Model))
        {
            StatusText = $"无法打开：{item.Title}";
            SetActionNotice(StatusText);
            return;
        }
        _history.Record(item.Model);
        _controller.RecordLaunch(item.Context, item.Model);
        RequestHide?.Invoke();
    }

    public void OpenContainingFolder(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        if (!_openContainingTarget(item.Model))
        {
            StatusText = $"无法定位：{item.Title}";
            SetActionNotice(StatusText);
        }
        else
            RequestHide?.Invoke();
    }

    public void CopyPath(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        StatusText = _copyTarget(item.Model) ? $"已复制路径：{item.Model.CopyText}" : "复制失败：剪贴板被占用";
        SetActionNotice(StatusText);
    }

    /// <summary>
    /// Hides the row's item (and every equivalent row) until it is restored. Only a saved hide is
    /// reported as hidden: on failure the rows stay and the store's reason is shown. Usage history,
    /// pins and same-query preferences are left untouched so a restored item comes back unchanged.
    /// </summary>
    public void Hide(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        if (_hiddenResults is null)
        {
            SetActionNotice("隐藏失败：隐藏功能不可用");
            return;
        }

        if (!_hiddenResults.TryHide(item.Model))
        {
            SetActionNotice($"隐藏失败：{DescribeHiddenError("无法保存隐藏列表")}");
            return;
        }

        NotifyHiddenChanged();
        RemoveHiddenRows();
        SetActionNotice($"已隐藏：{item.Title}（可从系统托盘“已隐藏”中恢复）");

        // Immediate, so any generation that may still carry the item is invalidated and cancelled.
        ScheduleSearch(immediate: true);
    }

    /// <summary>Restores one hidden item by its stored key and refreshes the results.</summary>
    public bool RestoreHidden(string key)
    {
        if (_disposed || string.IsNullOrEmpty(key))
            return false;
        if (_hiddenResults is null)
        {
            SetActionNotice("恢复失败：隐藏功能不可用");
            return false;
        }

        var restored = _hiddenResults.TryRestore(key);
        NotifyHiddenChanged();
        if (!restored)
        {
            SetActionNotice($"恢复失败：{DescribeHiddenError("该项已不在隐藏列表中")}");
            return false;
        }

        SetActionNotice("已恢复 1 个隐藏项");
        ScheduleSearch(immediate: true);
        return true;
    }

    /// <summary>Restores every hidden item and refreshes the results.</summary>
    public bool RestoreAllHidden()
    {
        if (_disposed)
            return false;
        if (_hiddenResults is null)
        {
            SetActionNotice("恢复失败：隐藏功能不可用");
            return false;
        }

        var count = HiddenCount;
        if (count == 0)
            return true;

        var restored = _hiddenResults.TryRestoreAll();
        NotifyHiddenChanged();
        if (!restored)
        {
            SetActionNotice($"恢复失败：{DescribeHiddenError("无法保存隐藏列表")}");
            return false;
        }

        SetActionNotice($"已恢复 {count} 个隐藏项");
        ScheduleSearch(immediate: true);
        return true;
    }

    private string DescribeHiddenError(string fallback) =>
        string.IsNullOrWhiteSpace(_hiddenResults?.Error) ? fallback : _hiddenResults!.Error!;

    private void NotifyHiddenChanged()
    {
        OnPropertyChanged(nameof(HiddenError));
        OnPropertyChanged(nameof(HiddenItems));
        OnPropertyChanged(nameof(HiddenCount));
    }

    private bool IsHiddenNow(SearchResultItem item) => _hiddenResults?.IsHidden(item) ?? false;

    /// <summary>Drops every visible row the store now hides, keeping the selection near its old place.</summary>
    private void RemoveHiddenRows()
    {
        var keep = Results.Where(row => !IsHiddenNow(row.Model)).ToList();
        var keepPins = HomePins.Where(row => !IsHiddenNow(row.Model)).ToList();
        if (keep.Count == Results.Count && keepPins.Count == HomePins.Count)
            return;

        var preferredIndex = Math.Max(0, _selectedIndex);
        HomePins.Clear();
        foreach (var row in keepPins)
            HomePins.Add(row);
        Results.Clear();
        foreach (var row in keep)
            Results.Add(row);

        _selection.Reset(SelectionCount, preferredIndex);
        _selectedIndex = _selection.Index;
        OnPropertyChanged(nameof(SelectedIndex));
        UpdateSelectionFlags();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowStateMessage));
        RaiseCommandStates();
    }

    /// <summary>Sends the selected row to QuickLook (the Ctrl+Space entry point).</summary>
    public void PreviewSelected() => _ = PreviewSelectedAsync();

    public Task PreviewSelectedAsync()
    {
        if (SelectedItem is { } item)
            return PreviewAsync(item);

        SetActionNotice("没有选中的结果可预览");
        return Task.CompletedTask;
    }

    /// <summary>Fire-and-forget QuickLook preview of one row; completion is reported through the notice.</summary>
    public void Preview(ResultItemViewModel item) => _ = PreviewAsync(item);

    /// <summary>
    /// Switches an already-open QuickLook preview after keyboard or mouse selection changes;
    /// the protocol's Switch command is a no-op when QuickLook is closed.
    /// </summary>
    public void SwitchPreviewSelected()
    {
        if (_disposed)
            return;

        var item = SelectedItem?.Model;
        if (item is not null)
        {
            try
            {
                if (!_previewService.CanPreview(item))
                    item = null;
            }
            catch (Exception)
            {
                item = null;
            }
        }

        _switchGeneration++;
        _queuedSwitchPlacementGeneration = BeginQuickLookPlacementRequest();
        _queuedSwitch = item;
        try
        {
            _switchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous switch finished between reading the source and cancelling it.
        }

        if (_queuedSwitch is not null && !_switchDrainRunning)
            _ = DrainSwitchRequestsAsync();
    }

    /// <summary>Invalidates old work and orders Close after it, ahead of requests from a later show.</summary>
    public async Task ClosePreviewAsync()
    {
        if (_disposed) return;
        _switchGeneration++;
        _quickLookPlacementGeneration++;
        _queuedSwitch = null;
        CancelPreviewRequest(_previewCancellation);
        CancelPreviewRequest(_switchCancellation);
        CancelPreviewRequest(_closePreviewCancellation);
        _previewCancellation = null;
        IsPreviewBusy = false;
        var cancellation = new CancellationTokenSource();
        _closePreviewCancellation = cancellation;
        var entered = false;
        try
        {
            await _previewRequestGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            entered = true;
            if (!_disposed && !cancellation.IsCancellationRequested)
                await _previewService.CloseAsync(cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Best effort: a missing/stopped QuickLook must not block hiding or create a new warning.
        }
        finally
        {
            if (entered) _previewRequestGate.Release();
            if (ReferenceEquals(_closePreviewCancellation, cancellation)) _closePreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void CancelPreviewRequest(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Asks QuickLook to preview the item. This is a view-only action: it never goes through
    /// <see cref="Open"/>, records no history or preference, and does not hide the launcher (a real
    /// focus loss still hides it through the normal grace period). One request runs at a time.
    /// </summary>
    public async Task PreviewAsync(ResultItemViewModel item)
    {
        if (_disposed || item is null)
            return;
        if (_isPreviewBusy)
        {
            SetActionNotice("QuickLook 正在处理上一次预览请求");
            return;
        }

        if (!_previewService.CanPreview(item.Model))
        {
            SetActionNotice($"无法预览：{item.Title}（只能预览本地文件或文件夹）");
            return;
        }

        var placementGeneration = BeginQuickLookPlacementRequest();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        IsPreviewBusy = true;
        var requestGateEntered = false;
        try
        {
            await _previewRequestGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            requestGateEntered = true;
            var result = await _previewService.PreviewAsync(item.Model, cancellation.Token).ConfigureAwait(true);
            if (_disposed || cancellation.IsCancellationRequested)
                return;

            SetActionNotice(
                result.Success
                    ? $"已发送到 QuickLook：{item.Title}"
                    : string.IsNullOrWhiteSpace(result.Message)
                        ? $"QuickLook 预览失败：{item.Title}"
                        : result.Message
            );

            if (result.Success && placementGeneration == _quickLookPlacementGeneration)
                QuickLookPlacementRequestCompleted?.Invoke(placementGeneration);
        }
        catch (OperationCanceledException)
        {
            // Disposed while the request was in flight; nothing left to report.
        }
        catch (Exception exception)
        {
            if (!_disposed && !cancellation.IsCancellationRequested)
                SetActionNotice($"QuickLook 预览失败：{exception.Message}");
        }
        finally
        {
            if (requestGateEntered)
                _previewRequestGate.Release();
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                if (!_disposed) IsPreviewBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private async Task DrainSwitchRequestsAsync()
    {
        _switchDrainRunning = true;
        try
        {
            while (!_disposed && _queuedSwitch is { } item)
            {
                var generation = _switchGeneration;
                var placementGeneration = _queuedSwitchPlacementGeneration;
                _queuedSwitch = null;
                var cancellation = new CancellationTokenSource();
                _switchCancellation = cancellation;
                var requestGateEntered = false;
                try
                {
                    await _previewRequestGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
                    requestGateEntered = true;
                    if (generation == _switchGeneration && !cancellation.IsCancellationRequested)
                    {
                        var result = await _previewService.SwitchAsync(item, cancellation.Token).ConfigureAwait(true);
                        if (result.Success
                            && !_disposed
                            && !cancellation.IsCancellationRequested
                            && placementGeneration == _quickLookPlacementGeneration)
                        {
                            QuickLookPlacementRequestCompleted?.Invoke(placementGeneration);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Navigation superseded the request, or the view model was disposed.
                }
                catch (Exception)
                {
                    // Switching is opportunistic navigation; it must not interrupt typing or selection.
                }
                finally
                {
                    if (requestGateEntered)
                        _previewRequestGate.Release();
                    if (ReferenceEquals(_switchCancellation, cancellation))
                        _switchCancellation = null;
                    cancellation.Dispose();
                }
            }
        }
        finally
        {
            _switchDrainRunning = false;
            if (!_disposed && _queuedSwitch is not null)
                _ = DrainSwitchRequestsAsync();
        }
    }

    private long BeginQuickLookPlacementRequest()
    {
        var generation = ++_quickLookPlacementGeneration;
        QuickLookPlacementRequestStarted?.Invoke(generation);
        return generation;
    }

    /// <summary>Raised synchronously before sending Toggle/Switch so the view can capture its current HWND.</summary>
    public event Action<long>? QuickLookPlacementRequestStarted;

    /// <summary>Raised after a successful one-way request has been sent; the window can begin bounded late discovery.</summary>
    public event Action<long>? QuickLookPlacementRequestCompleted;

    /// <summary>Raised when a launch succeeded and the window should hide.</summary>
    public event Action? RequestHide;

    public void RefreshFileBackend()
    {
        _refreshFileBackend?.Invoke();
        ScheduleSearch(immediate: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _quickLookPlacementGeneration++;
        _debounce.Stop();
        CancelInFlightSearch();
        _iconCancellation?.Cancel();
        _iconCancellation?.Dispose();
        _iconCancellation = null;

        // The in-flight preview disposes its own source in its finally block; only signal it here.
        try
        {
            _previewCancellation?.Cancel();
            _switchCancellation?.Cancel();
            _closePreviewCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed.
        }

        _queuedSwitch = null;
    }

    private void ScheduleSearch(bool immediate = false)
    {
        if (_disposed)
            return;
        _debounce.Stop();

        // Invalidate immediately, not only when the debounce fires, so a slow earlier query can never
        // publish during the debounce window. The previous rows stay on screen until new ones arrive.
        _pendingGeneration = _controller.BeginQuery();
        CancelInFlightSearch();

        if (immediate)
        {
            _ = RunSearchAsync(_pendingGeneration);
            return;
        }

        _debounce.Start();
    }

    private void CancelInFlightSearch()
    {
        var previous = _searchCancellation;
        _searchCancellation = null;
        if (previous is null)
            return;

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a previous cancellation; nothing to do.
        }

        previous.Dispose();
    }

    private void SetFileOption(ref bool field, bool value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
            return;

        if (propertyName == nameof(IsRegex))
            OnPropertyChanged(nameof(IsWholeWordOptionEnabled));

        OnPropertyChanged(nameof(FileOptions));
        ScheduleSearch(immediate: true);
    }

    private async Task RunSearchAsync(int generation)
    {
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;

        // Rows stay on screen; only the spinner changes.
        IsSearching = true;

        try
        {
            var snapshot = await _controller.SearchAsync(
                    Query,
                    _scope,
                    generation,
                    cancellation.Token,
                    FileOptions
                )
                .ConfigureAwait(true);

            if (!_controller.IsCurrent(generation))
                return;

            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception exception)
        {
            if (_controller.IsCurrent(generation))
            {
                StatusText = $"搜索失败：{exception.Message}";
                SetActionNotice(StatusText);
                IsSearching = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
                _searchCancellation = null;

            cancellation.Dispose();

            if (_controller.IsCurrent(generation))
                IsSearching = false;
        }
    }

    private void Apply(SearchSnapshot snapshot)
    {
        _snapshot = snapshot;

        if (_disposed)
            return;
        Results.Clear();
        HomePins.Clear();

        // Re-check the latest hidden state: a snapshot that was ranked before the item was hidden
        // must not bring it back. On the blank-query home, only pinned applications occupy the
        // separate tile row. Pinned files/folders retain their order at the top of the ordinary list.
        foreach (var item in snapshot.Items)
        {
            if (IsHiddenNow(item))
                continue;
            var row = new ResultItemViewModel(item, this, snapshot.Context);
            if (IsHomeQuery && row.IsPinned && row.Model.Kind == ResultKind.Application)
                HomePins.Add(row);
            else
                Results.Add(row);
        }

        _selection.Reset(SelectionCount);
        _selectedIndex = _selection.Index;
        OnPropertyChanged(nameof(SelectedIndex));
        UpdateSelectionFlags();
        OnPropertyChanged(nameof(HasSelection));

        StatusText = snapshot.StatusText ?? (SelectionCount == 0 ? BuildEmptyMessage(snapshot) : null);
        FileNotice = snapshot.FileNotice;
        if (!string.IsNullOrWhiteSpace(snapshot.StatusText)) SetActionNotice(snapshot.StatusText);
        OnPropertyChanged(nameof(EmptyResultsMessage));
        FooterHint = BuildFooterHint(snapshot);
        OnPropertyChanged(nameof(ShowStateMessage));
        RaiseCommandStates();

        StartIconLoads();
    }

    private string BuildEmptyMessage(SearchSnapshot snapshot)
    {
        if (snapshot.Query.Trim().Length == 0)
        {
            return _scope switch
            {
                SearchScope.Files => "暂无最近使用的文件；输入关键字搜索文件",
                SearchScope.Applications => "暂无最近使用的应用；输入关键字搜索应用",
                _ => "输入关键字搜索应用和文件（Esc 关闭）",
            };
        }

        return "没有匹配的结果";
    }

    private string BuildFooterHint(SearchSnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Query.Trim().Length > 0)
            parts.Add($"{Results.Count} 个结果");
        else
            parts.Add($"置顶 {HomePins.Count + Results.Count(row => row.IsPinned)} · 最近使用 {Results.Count(row => !row.IsPinned)}");

        if (snapshot.FilesUnavailable && _scope != SearchScope.Applications)
            parts.Add("文件：不可用");

        return string.Join(" · ", parts);
    }

    private void StartIconLoads()
    {
        _iconCancellation?.Cancel();
        _iconCancellation?.Dispose();
        _iconCancellation = new CancellationTokenSource();
        var token = _iconCancellation.Token;
        foreach (var row in Results.Concat(HomePins))
            _ = LoadIconAsync(row, token);
    }

    private async Task LoadIconAsync(ResultItemViewModel row, CancellationToken cancellationToken)
    {
        try
        {
            var icon = await _icons.LoadAsync(row.Model, cancellationToken).ConfigureAwait(false);
            if (icon is null || cancellationToken.IsCancellationRequested || _dispatcher.HasShutdownStarted)
                return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && !cancellationToken.IsCancellationRequested
                    && (Results.Contains(row) || HomePins.Contains(row)))
                    row.Icon = icon;
            }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A new snapshot or disposal retired this row, including queued loads and retries.
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted)
        {
            // Dispatcher teardown must not turn an optional icon into an unobserved task failure.
        }
    }

    private bool SyncSelection(bool changed)
    {
        if (changed)
            SelectedIndex = _selection.Index;

        return changed;
    }

    private void RaiseCommandStates()
    {
        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenContainingFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyPathCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

/// <summary>Command carrying a strongly typed parameter (used by the scope chips).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is T typed)
            _execute(typed);
        else if (parameter is null && default(T) is null)
            _execute(default!);
    }
}
