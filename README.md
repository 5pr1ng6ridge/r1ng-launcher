# r1ng-launcher

A standalone, dependency-free Windows launcher (WPF, `net8.0-windows`, x64) with a floating
borderless search window inspired by the R1ng toolbar look: a prominent search box with trailing
quick toggles (`Aa` case · `Abl` whole word · `..\` path · `.*` regex), functional
two compact **应用 / 文件** scope icons (both selected means **全部**), crisp result rows (icon, title, detail, type badge), and
open / open-location / copy-path / pin / hide / preview actions.

This project is an **independent new project**. It shares no projects, links or submodules with
`r1ng-toolbar/` or `tool_r1ng/`; those two repositories were only read as behavioral references.

## Features

- **Floating launcher**: borderless, rounded, always-on-top window that is **hidden on start** and
  appears only on the global hotkey (`Alt + Space`, falling back to `Ctrl + Shift + Space` when the
  first combination is taken), centered on the monitor that currently holds the mouse cursor.
  Each main-hotkey show switches back to **全部** while retaining the query; pressing it while the
  window is already visible still hides it. A notification-area tray icon (never a taskbar button)
  remains available while hidden; its left-click/double-click opens the launcher. The independent file
  shortcut continues to open **文件**.
  Placement uses physical monitor coordinates and rechecks the actual window bounds after DPI/layout
  changes, including monitors with different scaling or negative desktop coordinates. The top anchor
  is at 18% of the work area, slightly above the previous 22% position.
- **Content-sized results**: the window grows and shrinks with the result rows, up to 520 DIPs (or the
  current monitor's smaller usable height); longer lists scroll internally beneath the search header.
  Diagnostics use independent transient windows and never consume result space. There is no bottom hint/count/shortcut bar:
  results extend to the card's bottom, and theme/editing actions live in the header. Keyboard navigation keeps the selected row visible,
  and each completed result refresh selects the first row and returns the list to the top. Pending
  searches still preserve the previous rows and selection. On the blank/empty-query home, pinned applications
  appear once as a single left-to-right row of compact 48-DIP icon-only tiles, with titles available
  through tooltips and accessibility names. The row scrolls horizontally when needed; tiles and
  pinned file/folder rows and recent launches share one vertical viewport, so tiles scroll away with the list.
- **Thin themed scrollbars**: ordinary vertical and horizontal scrollbars use 6-DIP rounded thumbs
  and transparent tracks; their colours follow the live dark/light palette, with accent hover/drag
  feedback. Native scrolling, track paging, dragging and result virtualization are retained.
- **Independent file shortcut**: `Ctrl + Alt + Space` always shows the launcher in the **文件** scope
  (it never merely toggles hide). Before the launcher is activated it captures the foreground window;
  when that window is File Explorer showing a physical folder, the query becomes
  `"<folder>" <last keywords>` with only the keywords selected, so the file backend is constrained to
  that folder while ranking/highlighting still use the keywords. The separator space is kept even when
  there are no keywords (the caret sits right after the space, ready for typing). Re-invoking replaces
  the automatic prefix instead of stacking paths, the prefix stays editable, and no folder is invented
  from a virtual location, a previous window or a background window. Because the bounded lookup can
  take up to 300 ms, the captured window is re-validated before the launcher is shown, so a lookup
  that outlived the foreground window never applies (and focuses) an obsolete folder context. The
  shortcut has its own hotkey id, so it registers, fails and disposes independently of the launcher
  toggle.
- **Tab scope switching**: with the query box focused, `Tab` goes 全部 → 应用 and then toggles
  应用 ↔ 文件; `Shift + Tab` maps 全部 → 文件 and toggles the same two modes backwards. Focus stays in
  the input and the two scope icons update immediately. `Tab` is ignored while a result context menu is
  active, so normal menu navigation is never hijacked.
- **Query retention**: hiding the launcher (`Esc`, the hotkey, `Alt+F4`, focus loss or a successful
  launch) now **preserves the query**; a regular show keeps it and selects the whole input so typing
  replaces it. Ordinary query retention is kept in-process only and is not restored on startup;
  explicitly editing in Notepad creates a temporary text draft (see below).
- **Theme toggle**: a small square Sun/Moon icon button switches immediately between the complete
  **深色** (default) and **浅色** palettes. It has an accessible automation name but deliberately no
  tooltip; after clicking, focus returns to the query. The choice is applied live through
  `DynamicResource` brushes and persisted best-effort to `%AppData%\r1ng-launcher\settings.json`.
  A missing/corrupt file keeps dark.
- **Hide rules**: `Esc`, `Alt+F4` (any window close request) or losing focus hides the launcher
  while preserving the query.
  The launcher is a resident hotkey app, so a close is intercepted and turned into a hide — the
  single-instance mutex and the global hotkey stay registered and the process stays reachable. Only an
  explicit shutdown (sign-out / session end) actually closes the window and releases the guard.
  Losing focus is deferred by a 150 ms grace period implemented with a dispatcher **timer** (not a
  background-priority callback), so the topmost window is reliably hidden after the grace period; the
  timer re-checks activity and keeps deferring while a result context menu is open, then hides once it
  closes. Launching a result hides it.
- **System tray and explicit quit**: the notification-area icon uses the root `app.ico`, stays alive
  while the launcher is hidden, and is removed only on actual shutdown/session end. Left-click or
  double-click opens the launcher. Right-click offers **已隐藏** (the existing hidden-items manager)
  and **退出** (explicit shutdown and release of the icon, global hotkeys and single-instance lock).
  Explorer restarts recreate the icon. No normal taskbar button or footer manager/quit controls are used.
- **Honest hotkey diagnostics**: if the preferred `Alt + Space` is taken, a floating notice says the
  fallback (`Ctrl + Shift + Space`) is active; if **both** combinations fail, the launcher comes up
  visible instead of staying hidden, and the tray remains the open/exit route. A tray initialization
  failure is also surfaced visibly; without a tray icon `Alt+F4` exits rather than trapping the
  resident process. It never claims a hotkey that was not actually registered.
- **Search box**: `Segoe MDL2` search icon, placeholder, `UpdateSourceTrigger=PropertyChanged`, caret
  and selection in accent colour. Two accessible 30×24-DIP scope toggles sit beside the four file-search
  toggles (`Aa` / `Abl` / `..\` / `.*`): app only, file only, or both for All. Clicking the last selected
  scope icon switches to the other single mode, never an empty scope. Hotkey and `Tab`/`Shift+Tab`
  mode transitions are unchanged. All six checked toggles use a theme-aware gray fill without
  resting outlines, with readable icon/text colours. Hover adds a thin gray outline to interactive
  controls without adding/changing their fill; checked/selected fills remain. The outline is a
  non-hit-testable overlay, so it cannot shift content or intercept clicks. Cards, menu/dropdown
  panels and tooltips stay borderless; rounded corners, shadows and menu separators remain.
  Keyboard focus stays visible through background highlights or menu/item outlines.
- **Application search**: cached catalog of Start Menu, Startup and Desktop shortcuts — `.lnk`
  **and `.url` Internet Shortcuts such as Steam's `Celeste.url`** — plus `shell:AppsFolder` entries
  (Shell automation, best effort), matched literally **and by pinyin initials** (`jsb` → 记事本).
  `.url` files are launched through the shell as the original `.url` file itself; a bounded,
  read-only metadata parser uses an unambiguous URL only for identity, without visiting it;
  their declared `IconFile`/`IconIndex` is used for the real icon, restricted to an existing local
  file so a shortcut can never make the shell fetch an icon over the network. When the declaration is
  rejected (for example a UNC path), the shortcut falls back to the generic `.url` extension icon,
  which is resolved without opening the shortcut — so the shell cannot re-read the rejected remote
  path either.
- **Real packaged-app icons**: `shell:AppsFolder\<AppUserModelId>` targets such as ChatGPT are
  resolved through `SHParseDisplayName` + `SHGetFileInfoW(SHGFI_PIDL)`, with the PIDL and `HICON`
  released on every path, so packaged apps show their own icon instead of one generic placeholder.
- **File search**: Everything through its documented IPC protocol (`WM_COPYDATA` + `QUERY2W` /
  `LIST2W`). No `Everything64.dll` is required, and there is deliberately **no full-disk scan
  fallback** — when Everything is not running the UI shows an explicit unavailable state instead.
- **File-search quick toggles**: the four toggles map 1:1 onto Everything's documented QUERY2
  `search_flags` — `Aa` → `MATCHCASE (0x1)`, `Abl` → `MATCHWHOLEWORD (0x2)`, `..\` →
  `MATCHPATH (0x4)`, `.*` → `REGEX (0x8)`. Everything defaults **off**, including match-path, so an
  unmodified query searches file/folder **names** case-insensitively. They only affect the file
  backend; application and history matching is always literal and case-insensitive, which every
  toggle tooltip states. Whole word is disabled while regex is on (Everything ignores it) and the
  whole-word wire bit is dropped in that mode.
- **Responsive, flicker-free results**: queries are debounced (120 ms), every query gets a
  generation number, stale results are dropped, and the previous rows stay on screen until the new
  ones arrive (a thin indeterminate bar indicates progress). Each keystroke also invalidates the
  in-flight generation and cancels its token **immediately**, so a slow earlier query cannot publish
  during the debounce window.
- **States and notices**: loading (results kept), empty-query application tiles plus pinned files/folders
  and recent history, and neutral "no matches" guidance stay in the card. Everything/hotkey/action and
  hidden-manager diagnostics appear only in one independent, theme-aware, non-activating floating
  notice near the launcher, clamped to its monitor. It disappears after about four seconds; hovering
  pauses the remaining time, leaving resumes it, and the close button dismisses it. New messages
  replace old ones (including renewing repeated actions), without stacking, resizing the launcher,
  taking focus or adding a taskbar button. No inline warning banner or footer is reserved.
- **Actions**: left-button double-click or `Enter` opens a result · `Ctrl+Enter` opens its containing
  folder · `Ctrl+C` copies its path (only when no search text is selected). A single click selects the hit row in All/app/file scopes while keeping query focus;
  double-clicking blank space or the scrollbar never opens the selected result. Right-click offers
  those actions plus **置顶到历史 / 取消置顶**, **隐藏此项**, and **QuickLook 预览**; row commands act on
  the clicked row even when another result is selected.
- **Hidden results**: hide an unwanted item from every scope and empty-query history without deleting
  its file, launch history, pin, or same-query preferences. Rules use verified launch identities and
  compatible shortcut aliases, not display names, and persist to the profile's `hidden.json`.
  The tray menu's **已隐藏** management entry remains accessible even with no results and supports
  restoring one item or all items. A failed save does not commit the change and shows a visible notice.
  Hidden candidates are excluded before application Top20 and merged Top60 truncation; Everything's
  existing 120-hit backend bound is unchanged, so hiding all returned hits does not fetch later pages.
- **QuickLook preview and launcher avoidance**: `Ctrl+Space` or the clicked row's **QuickLook 预览** sends
  the existing toggle request to [QuickLook](https://github.com/QL-Win/QuickLook); keyboard navigation
  and mouse selection of results, home history or pins use its non-toggling `Switch` command. An open
  manager preview follows the hit item while a closed preview stays closed. An accepted external-focus
  hide sends the pathless `Close` command (not Toggle or Quit); own menus/dialogs and refocus during the
  grace period do not close it, nor does a refused hide. Old requests are cancelled and serialized ahead
  of Close; later-show requests follow it, so an old completion cannot close a newly requested preview. After a successful send, the launcher makes a bounded, non-activating search for
  visible, restored, uncloaked top-level
  QuickLook preview windows (process plus preview-title checks) and moves **only its own
  window** beside their captured pixel bounds. It prefers right, then left; if the work area is too narrow
  it clamps the launcher and minimizes overlap, with a notice when full separation is impossible. The
  launcher keeps its own captured monitor/work area and size; normal show resets its anchor. No visible
  candidate means no move. Candidate discovery cannot distinguish QuickLook's manager window from a
  pinned preview, so it may avoid any visible QuickLook preview; it never writes to, resizes, raises or
  activates a QuickLook window. Late discovery is bounded and canceled by hide/newer request/shutdown.
  Only physical file/folder paths are accepted; shortcuts preview the original shortcut file rather than
  executing or resolving it. Rapid navigation is coalesced to the latest supported target. Connection
  and writes are bounded, unavailability is reported, and preview never records a launch or modifies
  usage preferences. The pipe has no acknowledgement, so success means the request was sent, not that
  a particular renderer succeeded. QuickLook is optional and is not bundled.
- **Notepad query editing**: with the search box focused, `Ctrl+G` opens the complete input in system
  Notepad. Pasting multiline text into the box (Ctrl+V, Shift+Insert, or its paste menu) first replaces
  the current selection, then uses the same editor flow; single-line paste is unchanged. There is no
  global clipboard watcher and no automatic clipboard check when showing the launcher.
  Save in Notepad (`Ctrl+S`), then use the launcher hotkey to return: saved text is read back without
  dropping line breaks or stealing focus. This does not depend on a Notepad process exiting: bounded
  UI Automation matches the launcher's unique draft filename to the corresponding Notepad tab, and
  requires repeated reliable absence before declaring that tab closed. Closing that tab reads the
  stable draft file (the last saved contents, never unsaved UI text), copies it to the clipboard, shows
  the launcher and ends the session. Closing an unrelated tab does nothing. One editing session is
  tracked at a time; query revisions/conflicts still prevent older saves from overwriting newer input.
  Notepad chooses its own window position: the launcher never moves it between monitors or virtual
  desktops and does not emit placement warnings. Only desktop identity is captured before hiding,
  to keep tab-close detection conservative when Windows/UIA cannot prove a reliable absence.
  UI Automation tracks the unique draft tab; if the tab is not observable after a grace period,
  auto-close tracking is reported unavailable while save monitoring and draft recovery continue.
  Closure requires consecutive reliable UIA absences and an observable, non-minimized, non-cloaked
  host on the expected desktop, or a destroyed observed host with a reliable scan ruling out a
  replacement tab. Hidden, cloaked, minimized, different-desktop, unknown-visibility and incomplete
  UIA states are inconclusive and do not end the session. Clipboard/launch/read
  failures leave the draft on disk and show a notice. The editing menu can still apply a saved draft or
  end the session manually. Plain-text drafts live under `%TEMP%\r1ng-launcher\query-editor` and are never
  automatically deleted. Drafts are limited to 1 MiB; readback accepts UTF-8 and BOM-marked UTF-16.
- **Keyboard**: `↑/↓` move selection, `PageUp/PageDown` page, `Home/End` first/last, `Esc` hide.
  Selection and Enter span home tiles followed by recent rows; tiles are also focusable buttons.
  When home has application tiles, plain `←/→` also move selection from an empty query box or a focused tile,
  wrapping across pins and history and following any open QuickLook preview. Query-text editing,
  modified arrows and context-menu navigation are not intercepted.
- **History and pins**: empty input shows only pinned applications in the horizontal tile strip.
  Pinned files/folders remain normal rows at the top of the list, followed by recent launches;
  application tiles are not duplicated in that list. All entries remain persisted to
  `%AppData%\r1ng-launcher\history.json`. The recent-launch capacity is 30; up to 30 independent pins
  survive recent-history eviction. A pin does not count as a launch. Unpinning retains real launch
  history, but removes an item that was only pinned and never opened. A full pin list rejects another
  pin with a visible hint rather than silently evicting an existing pin. Same-named files in different
  folders remain separate. Older array-format history files remain readable; verified local shortcut
  copies in the bounded home set are reconciled without waiting for a catalog scan.
- **Same-search memory**: successfully opened matching results are promoted on the same search, by
  usage count, then recency, then original relevance. Memory is independently persisted to
  `%AppData%\r1ng-launcher\preferences.json` (up to 200 search contexts, 8 results each), including
  scope, folder constraint and file-search options. It never inserts a non-matching result or a file
  absent from Everything's returned hits. Failed opens, pinning, copying, revealing locations and
  empty-input history launches do not count. It does not restore the last input on startup.
- **Reliable history icons**: all actual rows are loaded (not only the first 24), through four shared
  extraction slots. Concurrent requests for the same source share a load; transient failures expire
  after two seconds and each row can retry once. Retired rows and disposed windows cannot receive
  late icons. Old local `.url` history without icon metadata recovers a validated local declaration;
  remote/rejected declarations still use the safe generic extension icon.
- **Conservative shortcut deduplication**: equivalent local `.lnk` copies and exact `.url` identities
  share one result with searchable name aliases. Arguments, working directory, display/run flags and
  opaque resolution metadata must agree; unsupported, ambiguous, damaged or network links keep their
  path identities. Same display names alone never prove equivalence. The original shortcut remains
  the launch/reveal target, and **no shortcut files are deleted**.
- **Single instance**: a second launch signals the running launcher to come forward and exits. An
  explicit separate `--data-dir` uses an independent profile and guard; ordinary launches retain the
  original guard names, including compatibility with older resident builds.

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (or the .NET 8 SDK to build)
- [Everything](https://www.voidtools.com/) 1.4.1+ **running** for file search (optional; the app
  stays fully usable without it)

## Build

```powershell
# normal build (writes src\R1ng.Launcher\bin\...)
dotnet build R1ngLauncher.sln -c Release

# redirected build (does not touch a locked/older bin) - used for review builds
dotnet build R1ngLauncher.sln -c Release -p:BaseOutputPath=<repo>\artifacts\feature-build\
```

## Run

The reviewed build is published framework-dependent (.NET 8 Desktop Runtime) to
`artifacts\memory-release\R1ngLauncher.exe`; that folder is self-contained apart from the .NET 8 Desktop
Runtime and does not depend on the repository or on an older `bin` binary.

```powershell
# normal: starts hidden, waits for the hotkey
.\artifacts\memory-release\R1ngLauncher.exe

# show immediately (manual checks / scripting)
.\artifacts\memory-release\R1ngLauncher.exe --show

# show with a pre-filled query
.\artifacts\memory-release\R1ngLauncher.exe --query 记事本

# isolated local profile for manual testing (does not use your normal history/settings/preferences)
.\artifacts\memory-release\R1ngLauncher.exe --data-dir C:\Temp\r1ng-test-profile --show
```

> **Switching from an older build:** the launcher is single-instance. Exit the already-running
> instance first (tray menu **退出**, or sign out), otherwise a second start only signals the old process
> to come forward and the new executable exits immediately without taking over.

## Tests

```powershell
dotnet test R1ngLauncher.sln -c Release
```

Search/ranking/mapping/state code is unit-tested with injected fakes
(`SearchControllerTests`, `NameMatcherTests`, `PinyinInitialsTests`, `FileResultMapperTests`,
`AppCatalogTests`, `EverythingProtocolTests`, `EverythingReplyGateTests`,
`EverythingFileSearchProviderTests`, `LauncherViewModelTests`, `DeactivationGraceTests`,
`MainWindowCloseTests`, `HotKeyRegistrationStatusTests`, `SelectionModelTests`,
`SearchHistoryStoreTests`, `ScreenPlacementTests`). The feature additions add
`ShortcutScannerUrlTests` (`.url` discovery + safe/`None` icon fallbacks),
`AppsFolderTargetTests` (raw AUMID vs shell target vs absolute path vs URI),
`ShellIconProbeTests` (bounded real-shell probes: a system exe, a `.url` whose icon must equal its
`IconFile` and differ from the generic Internet Shortcut icon, the installed packaged apps and the
installed ChatGPT/Steam `.url` games when present), `FolderPrefixQueryTests` and
`FileQueryBuilderTests` (the exact Everything `path:`/`regex:` encodings and flag interactions),
`ExplorerFolderResolverTests` (virtual/URI/relative/missing folders rejected, and the same fully
qualified check rejects drive-relative `C:foo` and root-relative `\foo` paths),
`ScopeAndHotKeyTests` (`Tab`/`Shift+Tab` transitions, independent hotkey id and `0x4003` modifiers,
prefix replacement), `SettingsStoreTests` (round-trip + corrupt/unknown fallback),
`ThemePaletteTests` (both palettes define the same key set and swapping replaces exactly one
dictionary) and `MainWindowInputTests` (STA window wiring: hide keeps the query, a regular show
selects all, the file shortcut always shows 文件 scope, `Tab` cycles scope while keeping the keyboard
focus in the query box, and an injected Explorer folder prefixes the retained terms with only the
keywords selected and replaces — never stacks — a previous prefix).

`BoundedStaCallTests` pins the bounded-STA ownership contract (a worker that finishes after the
timeout completes normally with no disposed signal and no process crash), `ShellIconLoaderFallbackTests`
proves a timed-out shell-namespace extraction is never retried as a duplicate fallback and that a
`.url` is always loaded by extension (so a rejected remote `IconFile` cannot be re-read),
`PathContainmentTests` pins the whole-segment folder boundary used by the live Everything assertions
(rejecting siblings such as `system32-old`, including case variants), `ThemeRenderTests` drives the
real icon-only theme button on an STA thread, asserts its accessible name and absence of a tooltip,
checks dynamic palette swaps and persistence to a temp settings file, and renders dark/light result
frames under `artifacts\theme-render\`, and `ShortcutScannerUrlTests` adds the adversarial oversized
single-line `.url` case (rejected incrementally without materializing the line). `FolderPrefixQueryTests`
and `ScopeAndHotKeyTests` pin the folder prefix separator (kept even with no keywords) and the
fully-qualified path check (a drive-relative `"C:foo"` or root-relative `"\foo"` prefix is not a
folder constraint: the keywords are never stripped and no fake prefix is created), and
`MainWindowInputTests` covers the caret-after-space behavior and the pre-show foreground re-validation.

`MainWindowLayoutTests` exercises content-sized height, the capped virtualized viewport, keyboard
scrolling and wraparound, refresh-to-first-row behavior, header visibility, absence of inline banners and bottom-filling results, native placement,
and keyboard behavior around the theme button. `PinnedTilesLayoutTests` covers compact icon-only tiles,
shared home wheel scrolling, horizontal selection/wraparound/focus, selection/open/unpin routing,
unique home history, application-only tiles with file/folder pin routing, and collapse while searching. `MainWindowTrayTests` uses an
injected tray/menu fake to check reopen/manage/quit routing, no taskbar entry, hide-vs-shutdown lifetime and
visible tray-failure fallback. `ContextMenuRenderTests`
uses the production menu templates in both palettes to check rounded chrome without a system icon
gutter, themed separators, command bindings, highlighted/disabled states, and overflow scrolling.
`ToolbarAndScrollBarTests` checks the live two-icon scope bindings, unchanged show/Tab transitions,
gray-filled checked controls, borderless resting surfaces and outline-only hover without layout/hit-test changes
in both palettes, default scrollbars' 6-DIP geometry, live theme changes,
vertical/horizontal track paging, horizontal thumb dragging and retained result virtualization.

`LauncherMemoryTests`, `ShortcutIdentityTests`, `HistoryIconTests` and `SingleInstanceProfileTests`
cover independent pin capacity, old-history compatibility and merging, save failure, same-query usage
and pre-crop ranking, stale-row context attribution, safe shortcut parsing/aliases, all-row icons,
cache recovery/single-flight, retired/disposed rows, and profile guard isolation. Persistence fixtures
use temporary files; injected launch delegates do not execute real applications. `ContextMenuRenderTests`
also checks all six commands and the live pin/unpin label binding, and `ShellIconProbeTests` checks
legacy `.url` icon recovery through the real shell loader.

`HiddenResultStoreTests` and `HiddenResultFilteringTests` cover persistent hide/restore rules, aliases,
corrupt files, atomic-save rollback, concurrent mutation and filtering before result limits.
`QuickLookPreviewServiceTests` uses an independent test pipe (never the user's QuickLook), including
verified Toggle/Switch/Close wire messages. `QuickLookCloseOrderingTests` covers cancelled old requests,
close/new-show ordering and quiet failure. `TransientNoticeLifetimeTests` and
`LauncherNoticeAndPreviewLifetimeTests` check timed/hover/manual dismissal, replacement and stale timers,
non-activation, monitor clamping, unchanged card geometry, and accepted/suppressed/refused deactivation hides. `QuickLookWindowLocatorTests`, `ScreenPlacementTests` and
`MainWindowQuickLookAvoidanceTests` use fake candidate windows/placement to check conservative discovery,
side geometry, current-HWND ownership, late retries, generations and hide/dispose cancellation without
moving QuickLook. `NotepadQueryEditorTests` uses fake launchers and tab trackers for
stable final-file reads, matching closure, desktop-identity safeguards without positioning, encodings and lifetime races. `LauncherActionTests`
and `MainWindowActionTests` cover row navigation coalescing, clipboard fakes, shortcuts, multiline selection,
saved-query readback, closure completion and stale-edit protection.

`EverythingIpcIntegrationTests` additionally exercises the **real** backend: it probes Everything and
runs a live query round-trip when Everything is available — including a live **folder-constrained**
query and a live **folder-constrained regex** query that assert every hit is inside the folder's
actual subtree (whole-segment boundary) **and** that each hit's basename really matches the requested
name/regex — and falls back to the explicit unavailable-state contract otherwise, so the suite stays
green on machines without Everything.

`DeactivationGraceTests` drives the focus-loss grace period with a deterministic fake timer and also
exercises the real dispatcher-timer wiring on an STA thread; `LauncherViewModelTests` covers the
quick-toggle → `FileSearchOptions` wiring, the regex/whole-word rule, and immediate cancellation of
the in-flight query on input change.

`EverythingReplyGateTests` drives the query/reply correlation directly with synthetic LIST2 payloads
(unique non-zero ids, late replies after a timeout, stale replies from an older query), and
`EverythingProtocolTests` includes adversarial `ParseList2` inputs (`uint.MaxValue` offsets and
lengths, absurd item counts, invalid offsets, repeated data offsets, oversized paths and the
per-reply item/path/decoded-byte budgets) plus the `cbData` size cap, and pins a full legitimate
120-result reply round-trip. `MainWindowCloseTests`
exercises the real WPF window on an STA thread and proves a user close hides it while an explicit
shutdown closes it, and tray commands request explicit application shutdown while hiding alone retains
the tray icon. `HotKeyRegistrationStatusTests` pins the honest presentation of a successful fallback
versus a total registration failure (reason + UI exit hint, never a pretend hotkey). `AppCatalogTests`
covers mid-build cancellation: a cancelled waiter must not orphan the shared scan and a second waiter
must reuse it instead of starting a duplicate.

## Manual Windows checks

1. **Start hidden** — launch the exe with no arguments; no window appears and no normal taskbar entry exists; the notification-area icon is present.
2. **Hotkey** — press `Alt + Space`; the window appears centered on the monitor under the mouse
   cursor, in the upper third, with the caret in the search box. Press `Alt + Space` again to hide.
3. **Focus hide** — show the launcher, then click another application; the launcher hides after a
   short grace period (the timer always hides it, even though an early idle callback used to run
   first). Right-click a result first: the menu stays open and the launcher stays visible, then hides
   shortly after the menu closes while the window is still unfocused.
4. **Close = hide** — show the launcher and press `Alt+F4` (or otherwise request a close): the window
   hides and the process stays reachable — the `Alt + Space` hotkey and tray left-click still show it,
   and starting the exe again just signals the existing process (no second window). Only tray **退出**
   or signing out / shutting down closes it for real.
5. **System tray** — left-click and double-click the notification-area icon to show the launcher;
   right-click and choose **已隐藏** to open the existing manager, then choose **退出** from the tray
   menu in a separate run. The process exits, releasing the icon, hotkeys and single-instance guard.
6. **Hotkey unavailable** — with the launcher running, start a second program that grabs
   `Alt + Space` **and** `Ctrl + Shift + Space` (or temporarily reserve both), then start the exe:
   instead of staying invisibly resident, the launcher comes up with a floating failure notice stating that both
   combinations failed; the tray **退出** remains the exit route. With only
   `Alt + Space` taken, the floating notice reports the active fallback `Ctrl + Shift + Space` instead.
7. **Esc hide keeps the query** — type a query, press `Esc`; it hides. Open it again: the query is
   still there and fully selected so typing replaces it. The same holds after `Alt+F4`, focus loss and
   a successful launch.
8. **Second instance** — with the launcher running, start the exe again; the new process exits within
   milliseconds and the existing window comes forward.
9. **Application results** — type `记事本` or `jsb`: the Start Menu shortcut for 记事本 appears with a
   real shell icon, a `应用` badge and the shortcut folder as detail.
10. **Pinyin initials** — type the initials of a Chinese shortcut you have installed (for example
   `wps` for a WPS shortcut, `wx` for 微信) and confirm it matches.
11. **File results** — with Everything running, type at least two characters of a file name
   (for example `report`); file/folder rows appear with `文件`/`文件夹` badges and green/blue
   highlighted matched characters.
12. **Everything unavailable** — exit Everything, then type a file-ish query. A floating notice explains
   why (`Everything 未运行`) and no disk scan starts (verified: the UI stays instant).
13. **Response during typing** — type continuously; results never blank between keystrokes, a thin
    progress bar appears while searching, and only the newest query's results are shown. Home tiles
    collapse as soon as a nonblank query is entered.
14. **Keyboard/selection** — use a list longer than the viewport: `↓/↑`, `PageUp/PageDown`,
    `Home/End` must keep the highlighted row visible, including wrapping from first to last and back.
    Scroll away while the first row remains selected, then refresh: the first row is selected and the
    viewport returns to the top. Editing the query keeps old rows/selection until new results arrive.
15. **Actions** — `Enter` launches the selected item, `Ctrl+Enter` reveals it in Explorer, `Ctrl+C`
    copies the path, and the right-click menu offers those three actions plus pin/unpin, hide and
    QuickLook preview.
    In both themes, the menu has a uniform rounded background and separator, with no offset gray
    icon gutter or system-colored strip.
16. **Scope icons** — select only the file icon to search Everything (a floating notice reports unavailability),
    only the app icon to search the app catalog, or both to merge them. Clicking the sole selected icon
    switches to the other single mode. Check that selected icons and file-search flags show only their
    gray interior fill with no resting outline in both themes. Hover adds a gray outline but does
    not change/add a fill or shift the content; check buttons, tiles, rows and menu items too.
17. **Quick toggles** — with the file scope and Everything running, type `report`: matches are
    case-insensitive and match file names only by default. Toggle `Aa` and type `Report`/`report` to
    confirm case sensitivity; toggle `..\` to match a folder in the path; toggle `.*` and type a
    regex such as `^rep.*\.txt$` to confirm regex matching. `Abl` becomes disabled while `.*` is on,
    and its tooltip explains why.
18. **History** — launch a result, reopen the launcher with an empty query; the item appears on top.
    Restart the process and confirm it is still there (`%AppData%\r1ng-launcher\history.json`).
19. **Tab scope** — focus the search box and press `Tab`: 全部 → 应用 → 文件 → 应用. Press
    `Shift + Tab`: 全部 → 文件 → 应用. The icon fills update immediately and focus never leaves
    the input. Open a result's right-click menu  and press `Tab`: the menu
    navigates normally instead of switching scope.
20. **Steam `.url` game** — type `Celeste`; the Start Menu / Desktop `Celeste.url` shortcut appears in
    应用 with the game's real icon (its declared `IconFile`), not the generic Internet Shortcut icon,
    and `Enter` opens it through Steam.
21. **Packaged app icon** — type `ChatGPT`; the installed packaged ChatGPT app appears with its own
    icon (not a generic glyph) and opens.
22. **File shortcut** — open File Explorer at a folder, focus it, and press `Ctrl + Alt + Space`: the
    launcher appears in the 文件 scope with `"<that folder>"` followed by your last keywords, with the
    keywords selected. Invoke it again from another folder: the prefix is replaced, never stacked.
    Press it with a non-Explorer window (or a virtual folder such as 此电脑) in front: the file scope
    still opens with your keywords and no path is invented. Launching a result and re-invoking keeps
    the keywords.
23. **Folder constraint + toggles** — with the folder prefix present, type keywords: results come only
    from that folder. `Aa` (match case) and `..\` keep working. Toggling `.*` still searches only
    within the folder (Everything `path:` + `regex:` functions); whole-word stays disabled as usual
    and match-path does not combine with the regex form (see Limitations). Deleting the prefix removes
    the folder restriction.
24. **Theme** — click the small Sun/Moon header button: the palette switches immediately and focus
    returns to the query. The button has an accessible automation name and no tooltip. Restart the exe:
    the choice is restored from `%AppData%\r1ng-launcher\settings.json`. Clear or corrupt that file:
    the launcher still starts in the dark default.
25. **Resident tray** — close the launcher with `Alt+F4`; it hides while the notification-area icon stays
    available. Left-click or double-click reopens it; right-click opens **已隐藏** and **退出**. Choose
    退出 and verify the icon disappears. Start a second instance to occupy the launcher hotkey: the first
    instance remains reachable from the tray. Explorer restart should restore the icon.
26. **Mixed-DPI monitors** — place the cursor on each monitor and repeatedly hide/show the launcher
    (both hotkeys); check 100%/150%/200% scaling and monitors left of or above the primary display.
    The launcher stays horizontally centered in that monitor's work area, above its previous anchor,
    and inside the usable bounds. Moving the cursor after showing must not move the window on a
    result refresh.
27. **Adaptive height** — compare zero, one, several and many results, then reverse the sequence.
    The window expands/shrinks before the scrolling threshold and remains capped for long lists;
    the header stays above results, which extend to the card's bottom. Notices appear outside the card
    without changing its height. No result counts or shortcut hints occupy a footer. Repeat with an
    Everything-unavailable notice and a short monitor work area, including an active editing session.
    Check four-second dismissal, hover pause/resume, manual close, replacement and unchanged query focus. Scrollbars must remain thin
    and switch colours with the theme; check wheel, track clicks and thumb dragging on the vertical
    results/history bars and the horizontal pinned-tile bar.

28. **Pins** — right-click an application result and choose 置顶到历史. Clear the input: it appears
    as a compact icon-only tile in the single left-to-right pinned row, before but not duplicated in
    recent-history rows. Hover to identify it. Add more than one viewport of pins and scroll horizontally;
    wheel over tiles/history to check they scroll vertically together. With an empty query, use `←/→`
    to select across pins/history and wrap; focus a tile and check arrows followed by Enter open the new
    selection. Typed queries and modified arrows still edit text normally. Restart to check
    persistence; tile click opens it, and its context menu can unpin the clicked tile. Opening a pinned
    item preserves its pin. Pin a file and a folder: they stay as ordinary pinned rows above recent
    history, never as tiles. Switch to 文件 scope to confirm no empty tile band appears; open/unpin
    their actual rows and check that real launch history is retained.
29. **Same-query memory** — search for a term with several matches and successfully open a lower
    result. Search for the same term again, in the same scope/options/folder context: the opened
    result is promoted. Repeat after restarting. Another query/context must not inherit that boost.
30. **Cold history icons** — reopen a 30-item history after restarting and scroll to rows 25–30;
    real icons should also load there. Repeat with an old local `.url` record lacking IconPath metadata.
31. **Shortcut copies** — search using each name of two equivalent local shortcuts: one launch result
    remains and both names can match. Different arguments/working directories remain separate;
    the original shortcut files must still exist on disk.
32. **Mouse opening** — in All/app/file scopes, single-click a different row (including after scrolling)
    to select it without losing query focus, then double-click another actual row: only the
    double-clicked item opens, once. Blank space, the scrollbar and right-button double-click do not
    open an item. A successful open still records history and same-query preference once.
33. **Hide / restore** — hide a pinned or ordinary result through its right-click menu. It immediately
    disappears and stays absent across scopes, empty-query history and a restart. Use tray **已隐藏** to
    restore one or all entries, including when no rows remain. Restoring a previously pinned entry
    retains its pin and launch history; a same-named item at a different target is not hidden.
34. **QuickLook and launcher avoidance** — with QuickLook running and a real file selected, press
    `Ctrl+Space`; verify the launcher moves beside the visible preview without resizing, raising or
    activating QuickLook, and the query retains focus. Navigate with `↑/↓` or click another result/home
    history row: the already-open preview follows selection and the launcher stays beside the candidate
    window. Click an unrelated window: after the grace period both launcher and current preview close.
    Own menus/dialogs and clicking a notice's close button must not cause this; rapidly re-show and
    request a new preview to verify no stale Close closes it. Close the preview, navigate,
    and verify `Switch` neither reopens it nor triggers another move; `Ctrl+Space` remains the explicit
    toggle. Try pinned/multiple previews, rapid navigation and a short work area: preview windows never
    move, the latest supported target wins, and any unavoidable overlap is reported. Re-show the launcher
    to verify normal cursor-monitor placement resets the avoidance anchor. Unsupported rows, plain spaces,
    and a missing pipe/target remain bounded and safe.
35. **Notepad / multiline paste** — type text, press `Ctrl+G`, save in Notepad, and close only the
    launcher's matching draft tab. Verify the last saved text (not unsaved edits) is copied, the launcher
    returns, and the editing indicator clears; close another Notepad tab and verify it has no effect.
    Repeat with unchanged and empty saved text, a clipboard-busy condition, and a query edited in the
    launcher to confirm revision conflicts still win. Try both a new Notepad window and a pre-existing
    Windows 11 tabbed window: Windows chooses the position, the launcher does not reposition it, and
    no placement notice appears. Verify hidden, cloaked,
    minimized and incomplete-UIA hosts do not end the session, and that an unreliable destroyed old HWND
    does not close a rehosted draft. Verify the draft remains on disk after closing or ending a session. Multiline paste opens the merged text; single-line paste stays in the box.

## Architecture

```
src/R1ng.Launcher
├─ App.xaml(.cs)              composition root, single instance, catalog warm-up, --show/--query,
│                             theme load/apply and persistence
├─ MainWindow.xaml(.cs)       borderless window, hotkeys, placement, keys, hide rules, Tab scope,
│                             file-shortcut folder prefix, double-click, preview and query editing
├─ HiddenItemsWindow.xaml(.cs) themed hidden-result manager, single/all restore
├─ Controls/TitleHighlighter  attached property that paints matched characters
├─ Converters/                visibility converters
├─ Core/                      SearchResultItem/SearchSnapshot, FileSearchOptions, NameMatcher,
│                             PinyinInitials, SearchTextIndex, ResultRanker, SelectionModel,
│                             FolderPrefixQuery (prefix vs keywords)
├─ Themes/Controls.xaml       theme-aware tooltip/context-menu chrome (DynamicResource)
├─ Theming/                   AppTheme, ThemePalette (complete dark/light brushes), ThemeManager,
│                             LauncherSettings + SettingsStore (%AppData% JSON, corrupt-safe)
├─ Apps/                      AppCatalog (+cache), ShortcutScanner (.lnk + .url), UrlShortcut,
│                             AppFolders, AppsFolderEnumerator
├─ Files/                     EverythingProtocol (wire format + search-flag mapping),
│                             EverythingIpcClient (message window), EverythingReplyGate
│                             (per-query reply id correlation), IEverythingQueryClient
├─ Search/                    SearchController (generation guard), AppSearchProvider,
│                             EverythingFileSearchProvider, FileResultMapper, SearchHistoryStore,
│                             QueryPreferenceStore, HiddenResultStore
├─ Platform/                  GlobalHotKey (independent launcher + file ids), HotKeyRegistrationStatus
│                             (honest fallback/failure presentation), ApplicationShutdown (quit seam),
│                             DeactivationGrace (focus-loss timer), ScreenPlacement, SingleInstance,
│                             ShellIconLoader (SHGetFileInfo / SHParseDisplayName PIDL icons),
│                             BoundedStaCall (task-owned bounded STA shell call),
│                             ExplorerFolderResolver (bounded foreground-Explorer folder lookup),
│                             TargetLauncher, QuickLookPreviewService/QuickLookWindowLocator,
│                             LauncherWindowPlacement, NotepadQueryEditor
└─ ViewModels/                LauncherViewModel, ResultItemViewModel, RelayCommand
```

The Everything wire format and its parser are pure functions with no OS dependency, which is why they
are unit-tested directly.

## Limitations

- **Pinyin initials** cover common simplified Chinese (GB2312 level-1 order table plus a small
  polyphonic override list). One dominant reading per character is used, and characters outside
  GB2312 level-1 produce no initial.
- **File search is literal by default.** Everything performs the matching; pinyin-initial matching
  is not available for files (only for applications/history). The `Aa` / `Abl` / `..\` / `.*`
  quick toggles enable case-sensitive / whole-word / path / regex file matching; regex uses
  Everything's own regex engine and syntax. Application and history results always ignore these
  toggles (documented in each tooltip).
- **No file-search fallback.** If Everything is not running/too old/index loading, the file scope
  reports the reason and returns nothing. No unbounded disk scan is ever started.
- **Unit tests run against the live backend** only in `EverythingIpcIntegrationTests`, and they
  degrade to the unavailable contract when Everything is absent.
- `shell:AppsFolder` enumeration depends on Shell automation and is best effort.
- Resolving the folder of the foreground Explorer window is best effort. `ShellWindows` to HWND
  mapping is OS/build dependent (in particular across Windows 11 tabs), so on ambiguity or timeout the
  file shortcut keeps the keywords and adds **no** prefix rather than a stale/wrong path. The lookup
  runs on a background STA thread with a bounded wait; the completion signal is task-owned, so a
  worker that finishes after the timeout can never touch a disposed event or publish a stale folder,
  and a timed-out shell-namespace icon extraction is never retried as a duplicate call.
- The folder constraint is expressed with Everything's documented `path:` search function (functions
  are available in Everything 1.4.1+). A folder-constrained **regex** query carries the regex in the
  documented `regex:` function so the literal folder is never interpreted by the regex engine; in that
  one case the wire `REGEX`/`MATCHPATH` flags are omitted because the equivalent semantics live in the
  query text (verified live against Everything 1.4.1). No quick-toggle flag is ever silently cleared on
  a normal (non-regex) folder query.
- History now persists the icon source and index, so a `.url` re-listed from history keeps its
declared icon. `.url` files larger than the 64 Ki read cap (or containing one oversized line) are
rejected during a bounded stream read instead of being materialized, and a rejected remote `IconFile`
never causes the shell to read the shortcut (the generic `.url` extension icon is used instead).
- `.lnk` metadata is read only for conservative identity checks; it is never shell-resolved or
  rewritten. "Open location" still reveals the original shortcut, not its underlying executable.
- **Notification-area behavior.** The launcher is resident (hidden on start, no normal taskbar entry).
  `Esc` and `Alt+F4` hide it and leave the tray icon, hotkeys and single-instance guard alive. Tray
  **退出** performs clean explicit shutdown; sign-out / shutdown also removes the icon. If tray creation
  fails, the diagnostic stays visible and `Alt+F4` exits rather than stranding the process. There is no
  window context menu.
- The shell context menu is intentionally **not** implemented (CPOL-licensed implementations were
  deliberately avoided); the six first-party row actions (including pin/unpin, hide and preview) are
  offered instead.
- Window material is a solid dark card with a drop shadow (no acrylic/blur), and the UI strings are
  Chinese only.

## Third-party notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No third-party source files are vendored;
the Everything IPC wire format follows the publicly documented Everything SDK protocol.
