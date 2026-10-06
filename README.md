# r1ng-launcher

A standalone, dependency-free Windows launcher (WPF, `net8.0-windows`, x64) with a floating
borderless search window inspired by the R1ng toolbar look: a prominent search box with trailing
quick toggles (`Aa` case · `Abl` whole word · `..\` path · `.*` regex), functional
**全部 / 应用 / 文件** scope chips, crisp result rows (icon, title, detail, type badge), and
open / open-location / copy-path / pin / hide / preview actions.

This project is an **independent new project**. It shares no projects, links or submodules with
`r1ng-toolbar/` or `tool_r1ng/`; those two repositories were only read as behavioral references.

## Features

- **Floating launcher**: borderless, rounded, always-on-top window that is **hidden on start** and
  appears only on the global hotkey (`Alt + Space`, falling back to `Ctrl + Shift + Space` when the
  first combination is taken), centered on the monitor that currently holds the mouse cursor.
  Each main-hotkey show switches back to **全部** while retaining the query; pressing it while the
  window is already visible still hides it. The independent file shortcut continues to open **文件**.
  Placement uses physical monitor coordinates and rechecks the actual window bounds after DPI/layout
  changes, including monitors with different scaling or negative desktop coordinates. The top anchor
  is at 18% of the work area, slightly above the previous 22% position.
- **Content-sized results**: the window grows and shrinks with the result rows, up to 520 DIPs (or the
  current monitor's smaller usable height); longer lists scroll internally while the footer and
  notices remain outside the scrolling region. Keyboard navigation keeps the selected row visible,
  and each completed result refresh selects the first row and returns the list to the top. Pending
  searches still preserve the previous rows and selection.
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
  the input and the scope chip updates immediately. `Tab` is ignored while the theme dropdown or a
  result context menu is active, so normal menu navigation is never hijacked.
- **Query retention**: hiding the launcher (`Esc`, the hotkey, `Alt+F4`, focus loss or a successful
  launch) now **preserves the query**; a regular show keeps it and selects the whole input so typing
  replaces it. Ordinary query retention is kept in-process only and is not restored on startup;
  explicitly editing in Notepad creates a temporary text draft (see below).
- **Theme selector**: a small palette dropdown in the footer switches between the complete **深色**
  (default) and **浅色** palettes. The choice is applied live through `DynamicResource` brushes —
  chips, toggles, result rows, footer, context menu, tooltip, dropdown, warning/failure banners,
  progress bar and icons all follow it — and is persisted best-effort to
  `%AppData%\r1ng-launcher\settings.json`. A missing/corrupt file keeps dark.
- **Hide rules**: `Esc`, `Alt+F4` (any window close request) or losing focus hides the launcher
  while preserving the query.
  The launcher is a resident hotkey app, so a close is intercepted and turned into a hide — the
  single-instance mutex and the global hotkey stay registered and the process stays reachable. Only an
  explicit shutdown (sign-out / session end) actually closes the window and releases the guard.
  Losing focus is deferred by a 150 ms grace period implemented with a dispatcher **timer** (not a
  background-priority callback), so the topmost window is reliably hidden after the grace period; the
  timer re-checks activity and keeps deferring while a result context menu is open, then hides once it
  closes. Launching a result hides it.
- **Explicit quit**: the footer has a small **退出** button (tooltip: releases the global hotkey and the
  single-instance lock). It calls `AllowClose()` + `Application.Shutdown()`, so it cleanly closes the
  resident process and releases the hotkey and the single-instance guard — no more hunting for the
  process in Task Manager.
- **Honest hotkey diagnostics**: if the preferred `Alt + Space` is taken, the UI banner says the
  fallback (`Ctrl + Shift + Space`) is active; if **both** combinations fail, the launcher comes up
  visible instead of staying hidden, the banner shows the failure reason plus the exit route, and the
  process is still reachable by starting the exe again. It never claims a hotkey that was not
  actually registered.
- **Search box**: `Segoe MDL2` search icon, placeholder, `UpdateSourceTrigger=PropertyChanged`, caret
  and selection in accent colour, scope chips as accessible radio buttons, `Tab`/`Shift+Tab` scope
  switching, and four trailing quick toggles inside the input region (`Aa` / `Abl` / `..\` / `.*`)
  with accessible tooltips.
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
- **States**: loading (results kept), empty-query recent history, "no matches", and an amber banner with
  the exact Everything reason (`Everything 未运行`, index not loaded, version too old, …).
- **Actions**: left-button double-click or `Enter` opens a result · `Ctrl+Enter` opens its containing
  folder · `Ctrl+C` copies its path (only when no search text is selected). A single click only selects;
  double-clicking blank space or the scrollbar never opens the selected result. Right-click offers
  those actions plus **置顶到历史 / 取消置顶**, **隐藏此项**, and **QuickLook 预览**; row commands act on
  the clicked row even when another result is selected.
- **Hidden results**: hide an unwanted item from every scope and empty-query history without deleting
  its file, launch history, pin, or same-query preferences. Rules use verified launch identities and
  compatible shortcut aliases, not display names, and persist to the profile's `hidden.json`.
  The footer's **已隐藏** management entry remains accessible even with no results and supports
  restoring one item or all items. A failed save does not commit the change and shows a visible notice.
  Hidden candidates are excluded before application Top20 and merged Top60 truncation; Everything's
  existing 120-hit backend bound is unchanged, so hiding all returned hits does not fetch later pages.
- **QuickLook preview**: `Ctrl+Space` or the clicked row's **QuickLook 预览** sends a toggle request
  to the currently running [QuickLook](https://github.com/QL-Win/QuickLook) instance through its
  current-user local named pipe. Only physical file/folder paths are accepted; shortcuts preview the
  original shortcut file rather than executing or resolving it. Normal space input is unaffected.
  Connection and writes are bounded, unavailability is reported, and preview never records a launch
  or modifies usage preferences. The pipe has no acknowledgement, so success means the request was
  sent, not that a particular preview renderer succeeded. QuickLook is optional and is not bundled.
- **Notepad query editing**: with the search box focused, `Ctrl+G` opens the complete input in system
  Notepad. Pasting multiline text into the box (Ctrl+V, Shift+Insert, or its paste menu) first replaces
  the current selection, then uses the same editor flow; single-line paste is unchanged. There is no
  global clipboard watcher and no automatic clipboard check when showing the launcher.
  Save in Notepad (`Ctrl+S`), then use the launcher hotkey to return: saved text is read back without
  dropping line breaks or stealing focus. This does not depend on a Notepad process exiting, so
  Windows 11 tabs are supported. One editing session is tracked at a time; changing the query in the
  launcher prevents older saves from overwriting it. The editing menu lets you apply the saved draft
  explicitly or end the session. Launch/read errors retain the input or draft and show a notice.
  Plain-text drafts live under `%TEMP%\r1ng-launcher\query-editor`; drafts handed to Notepad are never
  automatically deleted, even after ending an edit or quitting the launcher, because a tab may still
  be editing them. Remove unneeded drafts manually after closing their tabs. The launcher never closes
  your Notepad windows. Drafts are limited to 1 MiB; readback accepts UTF-8 and BOM-marked UTF-16.
- **Keyboard**: `↑/↓` move selection, `PageUp/PageDown` page, `Home/End` first/last, `Esc` hide.
- **History and pins**: empty input shows pinned results first, then recent launches, persisted to
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
> instance first (footer **退出**, or sign out), otherwise a second start only signals the old process
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
real theme selector on an STA thread (it loads the production `Themes/Controls.xaml` chrome, drives
the actual `ComboBox.SelectedValue` dark↔light, asserts the templated ComboBox `Chrome`/dropdown
panel and rendered 深色/浅色 label follow the `DynamicResource` palette, checks persistence to a temp
settings file, and renders offscreen dark/light frames with a result sample under
`artifacts\theme-render\`), and `ShortcutScannerUrlTests` adds the adversarial oversized
single-line `.url` case (rejected incrementally without materializing the line). `FolderPrefixQueryTests`
and `ScopeAndHotKeyTests` pin the folder prefix separator (kept even with no keywords) and the
fully-qualified path check (a drive-relative `"C:foo"` or root-relative `"\foo"` prefix is not a
folder constraint: the keywords are never stripped and no fake prefix is created), and
`MainWindowInputTests` covers the caret-after-space behavior and the pre-show foreground re-validation.

`MainWindowLayoutTests` exercises content-sized height, the capped virtualized viewport, keyboard
scrolling and wraparound, refresh-to-first-row behavior, footer/banner visibility, native placement,
and restoring query focus after the theme dropdown's closing animation. `ContextMenuRenderTests`
uses the production menu templates in both palettes to check rounded chrome without a system icon
gutter, themed separators, command bindings, highlighted/disabled states, and overflow scrolling.

`LauncherMemoryTests`, `ShortcutIdentityTests`, `HistoryIconTests` and `SingleInstanceProfileTests`
cover independent pin capacity, old-history compatibility and merging, save failure, same-query usage
and pre-crop ranking, stale-row context attribution, safe shortcut parsing/aliases, all-row icons,
cache recovery/single-flight, retired/disposed rows, and profile guard isolation. Persistence fixtures
use temporary files; injected launch delegates do not execute real applications. `ContextMenuRenderTests`
also checks all six commands and the live pin/unpin label binding, and `ShellIconProbeTests` checks
legacy `.url` icon recovery through the real shell loader.

`HiddenResultStoreTests` and `HiddenResultFilteringTests` cover persistent hide/restore rules, aliases,
corrupt files, atomic-save rollback, concurrent mutation and filtering before result limits.
`QuickLookPreviewServiceTests` uses an independent test pipe (never the user's QuickLook), and
`NotepadQueryEditorTests` uses a fake editor launcher for save detection, encodings and lifetime handling.
`LauncherActionTests` and `MainWindowActionTests` cover row actions, shortcuts, multiline selection
replacement, saved-query readback and stale-edit protection.

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
shutdown closes it, and that the footer **退出** command requests application shutdown and allows the
close through. `HotKeyRegistrationStatusTests` pins the honest presentation of a successful fallback
versus a total registration failure (reason + UI exit hint, never a pretend hotkey). `AppCatalogTests`
covers mid-build cancellation: a cancelled waiter must not orphan the shared scan and a second waiter
must reuse it instead of starting a duplicate.

## Manual Windows checks

1. **Start hidden** — launch the exe with no arguments; no window appears and no taskbar entry exists.
2. **Hotkey** — press `Alt + Space`; the window appears centered on the monitor under the mouse
   cursor, in the upper third, with the caret in the search box. Press `Alt + Space` again to hide.
3. **Focus hide** — show the launcher, then click another application; the launcher hides after a
   short grace period (the timer always hides it, even though an early idle callback used to run
   first). Right-click a result first: the menu stays open and the launcher stays visible, then hides
   shortly after the menu closes while the window is still unfocused.
4. **Close = hide** — show the launcher and press `Alt+F4` (or otherwise request a close): the window
   hides and the process stays reachable — the `Alt + Space` hotkey still shows it and starting the
   exe again just signals the existing process (no second window). Only the footer **退出** button or
   signing out / shutting down closes it for real.
5. **Explicit quit** — show the launcher and click the small **退出** button in the footer (right side):
   the window closes and the process exits, releasing the global hotkey and the single-instance guard.
   Starting the exe afterwards starts a fresh instance instead of signalling a stale one.
6. **Hotkey unavailable** — with the launcher running, start a second program that grabs
   `Alt + Space` **and** `Ctrl + Shift + Space` (or temporarily reserve both), then start the exe:
   instead of staying invisibly resident, the launcher comes up with a red banner stating that both
   combinations failed, and the footer **退出** button is the visible exit route. With only
   `Alt + Space` taken, the banner reports the active fallback `Ctrl + Shift + Space` instead.
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
12. **Everything unavailable** — exit Everything, then type a file-ish query. An amber banner explains
   why (`Everything 未运行`) and no disk scan starts (verified: the UI stays instant).
13. **Response during typing** — type continuously; results never blank between keystrokes, a thin
    progress bar appears while searching, and only the newest query's results are shown.
14. **Keyboard/selection** — use a list longer than the viewport: `↓/↑`, `PageUp/PageDown`,
    `Home/End` must keep the highlighted row visible, including wrapping from first to last and back.
    Scroll away while the first row remains selected, then refresh: the first row is selected and the
    viewport returns to the top. Editing the query keeps old rows/selection until new results arrive.
15. **Actions** — `Enter` launches the selected item, `Ctrl+Enter` reveals it in Explorer, `Ctrl+C`
    copies the path, and the right-click menu offers those three actions plus pin/unpin, hide and
    QuickLook preview.
    In both themes, the menu has a uniform rounded background and separator, with no offset gray
    icon gutter or system-colored strip.
16. **Scope chips** — `文件` searches only Everything (banner shows when unavailable), `应用` searches
    only the app catalog, `全部` merges both.
17. **Quick toggles** — with the file scope and Everything running, type `report`: matches are
    case-insensitive and match file names only by default. Toggle `Aa` and type `Report`/`report` to
    confirm case sensitivity; toggle `..\` to match a folder in the path; toggle `.*` and type a
    regex such as `^rep.*\.txt$` to confirm regex matching. `Abl` becomes disabled while `.*` is on,
    and its tooltip explains why.
18. **History** — launch a result, reopen the launcher with an empty query; the item appears on top.
    Restart the process and confirm it is still there (`%AppData%\r1ng-launcher\history.json`).
19. **Tab scope** — focus the search box and press `Tab`: 全部 → 应用 → 文件 → 应用. Press
    `Shift + Tab`: 全部 → 文件 → 应用. The chip highlights update immediately and focus never leaves
    the input. Open a result's right-click menu (or the theme dropdown) and press `Tab`: the menu
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
24. **Theme** — pick 浅色 in the footer dropdown; every surface (card, chips, toggles, rows, footer,
    context menu, tooltip, dropdown, banners, progress bar) turns light immediately. Restart the exe:
    the choice is restored from `%AppData%\r1ng-launcher\settings.json`. Clear or corrupt that file:
    the launcher still starts in the dark default.
25. **Mixed-DPI monitors** — place the cursor on each monitor and repeatedly hide/show the launcher
    (both hotkeys); check 100%/150%/200% scaling and monitors left of or above the primary display.
    The launcher stays horizontally centered in that monitor's work area, above its previous anchor,
    and inside the usable bounds. Moving the cursor after showing must not move the window on a
    result refresh.
26. **Adaptive height** — compare zero, one, several and many results, then reverse the sequence.
    The window expands/shrinks before the scrolling threshold and remains capped for long lists;
    footer and file/hotkey notices remain visible. Repeat with the Everything-unavailable banner and
    a short monitor work area.

27. **Pins** — right-click a non-selected result and choose 置顶到历史. Clear the input: it appears
    before recent history, marked 置顶. Restart to check persistence. The same row's menu now says
    取消置顶; unpinning does not erase a real recent launch. Opening a pinned row preserves its pin.
28. **Same-query memory** — search for a term with several matches and successfully open a lower
    result. Search for the same term again, in the same scope/options/folder context: the opened
    result is promoted. Repeat after restarting. Another query/context must not inherit that boost.
29. **Cold history icons** — reopen a 30-item history after restarting and scroll to rows 25–30;
    real icons should also load there. Repeat with an old local `.url` record lacking IconPath metadata.
30. **Shortcut copies** — search using each name of two equivalent local shortcuts: one launch result
    remains and both names can match. Different arguments/working directories remain separate;
    the original shortcut files must still exist on disk.
31. **Mouse opening** — single-click a row to select it, then double-click another actual row: only the
    double-clicked item opens, once. Blank space, the scrollbar and right-button double-click do not
    open an item. A successful open still records history and same-query preference once.
32. **Hide / restore** — hide a pinned or ordinary result through its right-click menu. It immediately
    disappears and stays absent across scopes, empty-query history and a restart. Use **已隐藏** to
    restore one or all entries, including when no rows remain. Restoring a previously pinned entry
    retains its pin and launch history; a same-named item at a different target is not hidden.
33. **QuickLook** — with QuickLook running, select a real file and press `Ctrl+Space`, then use the
    preview menu on another row. Verify it previews the clicked target without opening it or recording
    a launch. Plain spaces still type normally. With no pipe server or a missing target, the action
    finishes with a notice instead of blocking the search window.
34. **Notepad / multiline paste** — type text, press `Ctrl+G`, edit and save in Notepad, then return with
    the launcher hotkey: the saved text is intact. Replace a middle selection with CRLF/LF text using
    paste: the complete replacement opens in Notepad, while single-line paste stays in the search box.
    Try a second save, an existing Windows 11 Notepad window, repeated Ctrl+G, and a query edited in
    the launcher before a later Notepad save: no new uncontrolled session or silent overwrite occurs.
    Use the editing menu to apply the saved draft or end the session. Copying multiline text elsewhere
    never opens Notepad by itself.

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
├─ Themes/Controls.xaml       theme-aware tooltip/context-menu/dropdown chrome (DynamicResource)
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
│                             TargetLauncher, QuickLookPreviewService, NotepadQueryEditor
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
- **No tray icon.** The launcher is built to stay resident (hidden on start, no taskbar entry).
  `Esc` and `Alt+F4` hide it and leave the hotkey and single-instance guard alive; the footer **退出**
  button closes it cleanly (`AllowClose()` + `Application.Shutdown()`), as does sign-out / shutdown
  (session end is allowed through). There is deliberately no tray icon and no window context menu.
- The shell context menu is intentionally **not** implemented (CPOL-licensed implementations were
  deliberately avoided); the six first-party row actions (including pin/unpin, hide and preview) are
  offered instead.
- Window material is a solid dark card with a drop shadow (no acrylic/blur), and the UI strings are
  Chinese only.

## Third-party notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No third-party source files are vendored;
the Everything IPC wire format follows the publicly documented Everything SDK protocol.
