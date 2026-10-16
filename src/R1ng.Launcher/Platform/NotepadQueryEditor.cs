using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Edits the launcher's query text in an external editor through a draft file. The service only
/// owns the draft file and its save monitoring; input revisions, dispatching to the UI thread and
/// conflict handling belong to the caller.
/// </summary>
public interface IQueryEditor : IDisposable
{
    /// <summary>Id of the active editing session, or null when none is active.</summary>
    Guid? CurrentSessionId { get; }

    /// <summary>Draft file of the active session (kept on disk for recovery), or null.</summary>
    string? DraftPath { get; }

    /// <summary>
    /// Raised on a background thread when the draft was saved with different text. Subscribers must
    /// be quick, must not block on this editor, and should compare <see cref="QueryEditorSaved.SessionId"/>
    /// with <see cref="CurrentSessionId"/>. After <see cref="EndSession"/> or <see cref="IDisposable.Dispose"/>
    /// returns, no new notification is started (one already being delivered may still finish).
    /// </summary>
    event Action<QueryEditorSaved>? Saved;

    /// <summary>Raised (on a background thread, once per distinct problem) when the draft cannot be read back.</summary>
    event Action<QueryEditorError>? Failed;

    /// <summary>Raised when this session's specific Notepad document tab closes, with its final saved file text.</summary>
    event Action<QueryEditorClosed>? Closed;

    /// <summary>
    /// Opens the editor on <paramref name="text"/>. While a session is active this does not create or
    /// rewrite anything and returns the active session id; the caller decides what to tell the user.
    /// Concurrent calls are serialised. Failure and cancellation are returned, not thrown.
    /// </summary>
    Task<QueryEditorStartResult> StartAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Starts with the launcher's desktop identity for safe close detection; never positions Notepad.</summary>
    Task<QueryEditorStartResult> StartAsync(string text, QueryEditorLaunchContext? launchContext, CancellationToken cancellationToken = default) =>
        StartAsync(text, cancellationToken);

    /// <summary>
    /// Re-reads the active session's draft right now (for example when the launcher is shown again),
    /// delivering <see cref="Saved"/>/<see cref="Failed"/> as usual. Cancellation completes quietly.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring. The draft file is left in place; the user's editor window is never touched.</summary>
    void EndSession();
}

/// <summary>The draft of <see cref="SessionId"/> was saved with new text; <see cref="Sequence"/> starts at 1 per session.</summary>
public sealed record QueryEditorSaved(Guid SessionId, long Sequence, string Text);

/// <summary>The draft of <see cref="SessionId"/> could not be read back; the file is kept for recovery.</summary>
public sealed record QueryEditorError(Guid SessionId, string Message);

/// <summary><see cref="SessionId"/> is <see cref="Guid.Empty"/> when <see cref="Success"/> is false.</summary>
public sealed record QueryEditorStartResult(bool Success, Guid SessionId, string? Error);

/// <summary>The exact draft tab closed; <see cref="SavedText"/> is read from the stable draft file, never the unsaved editor buffer.</summary>
public sealed record QueryEditorClosed(Guid SessionId, string SavedText);

/// <summary>Launcher desktop identity captured before hiding, used only to rule out inconclusive tab absences.</summary>
public sealed record QueryEditorLaunchContext(Guid? VirtualDesktopId);

internal sealed record NotepadTabSnapshot(bool Reliable, IReadOnlyList<IntPtr> MatchingWindows);

internal enum NotepadWindowLife
{
    Unknown,
    Alive,
    Destroyed,
}

internal enum NotepadWindowVisibility
{
    Unknown,
    Visible,
    Hidden,
    Cloaked,
}

internal sealed record NotepadWindowState(
    NotepadWindowLife Life,
    bool IsMinimized,
    NotepadWindowVisibility Visibility,
    Guid? VirtualDesktopId);

internal interface INotepadTabTracker
{
    NotepadTabSnapshot FindDraftTabs(string draftPath);

    NotepadWindowState InspectWindow(IntPtr window);
}

/// <summary>
/// <see cref="IQueryEditor"/> backed by the system Notepad. The session file is an independent
/// random UTF-8 .txt in a launcher-owned temp folder. Saves are detected by polling that one file,
/// never by waiting for the Notepad process (Windows 11 reuses windows and tabs, so the process
/// exit says nothing about the tab).
/// </summary>
public sealed class NotepadQueryEditor : IQueryEditor
{
    /// <summary>Largest draft read back (and written), in bytes.</summary>
    internal const int MaxDraftBytes = 1024 * 1024;

    private const string DraftPrefix = "r1ng-";
    private const string DraftExtension = ".txt";
    private const int TransientFailureLimit = 8;
    private const int BusyReadAttempts = 3;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private const string ClosedMessage = "记事本编辑器已关闭";
    private const string CancelledMessage = "已取消";
    private const string TooLargeMessage = "记事本内容超过 1 MiB，未读回；草稿文件已保留";
    private const string BadEncodingMessage = "记事本内容不是有效的 UTF-8/UTF-16 文本，未读回；请以 UTF-8 保存，草稿文件已保留";
    private const string BusyMessage = "暂时无法读取记事本草稿（文件被占用或无权限）";
    private const string MissingMessage = "记事本草稿文件已不存在或被移动";
    private const string UnexpectedMessage = "读取记事本草稿时出现意外错误";

    private readonly string _directory;
    private readonly Func<string, bool> _launchEditor;
    private readonly INotepadTabTracker? _tabTracker;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settleDelay;
    private readonly TimeSpan _busyRetryDelay;

    private readonly object _gate = new();
    // Never disposed on purpose: no wait handle is ever allocated and a late waiter must not fault.
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Session? _session;
    private bool _disposed;

    /// <summary>
    /// Creates the editor; nothing touches the disk until <see cref="StartAsync"/>. The parameters are
    /// test seams: a temp folder, an editor launcher (returns whether it started) and the poll period.
    /// </summary>
    public NotepadQueryEditor(string? temporaryDirectory = null, Func<string, bool>? launchEditor = null, TimeSpan? pollInterval = null)
        : this(temporaryDirectory, launchEditor, pollInterval, tabTracker: null)
    {
    }

    internal NotepadQueryEditor(
        string? temporaryDirectory,
        Func<string, bool>? launchEditor,
        TimeSpan? pollInterval,
        INotepadTabTracker? tabTracker)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        _directory = string.IsNullOrWhiteSpace(temporaryDirectory) ? DefaultDirectory : Path.GetFullPath(temporaryDirectory);
        _launchEditor = launchEditor ?? LaunchNotepad;
        _tabTracker = tabTracker ?? (launchEditor is null ? new WindowsNotepadTabTracker() : null);
        _pollInterval = interval;
        // Two reads this far apart must agree before a save is believed (a writer may still be mid-write).
        _settleDelay = TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds, 10, 100));
        _busyRetryDelay = TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds / 4, 5, 50));
    }

    public event Action<QueryEditorSaved>? Saved;

    public event Action<QueryEditorError>? Failed;

    public event Action<QueryEditorClosed>? Closed;

    /// <summary>Launcher-owned folder under the user's temp path, not the launcher profile/data directory.</summary>
    internal static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "r1ng-launcher", "query-editor");

    /// <summary>The trusted system Notepad; never resolved through PATH or the shell.</summary>
    internal static string NotepadPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    public Guid? CurrentSessionId
    {
        get
        {
            lock (_gate)
                return _session?.Id;
        }
    }

    public string? DraftPath
    {
        get
        {
            lock (_gate)
                return _session?.FilePath;
        }
    }

    public Task<QueryEditorStartResult> StartAsync(string text, CancellationToken cancellationToken = default) =>
        StartAsync(text, null, cancellationToken);

    public async Task<QueryEditorStartResult> StartAsync(string text, QueryEditorLaunchContext? launchContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(CancelledMessage);
        }

        try
        {
            return await StartCoreAsync(text, launchContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_gate)
            session = _session;
        if (session is null)
            return;

        try
        {
            // Hop to the thread pool first so a UI-thread caller never does file IO or bounded UIA inline.
            await Task.Run(async () =>
            {
                await PollAsync(session, cancellationToken).ConfigureAwait(false);
                await ObserveTabAsync(session, cancellationToken).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled refresh: nothing was consumed, the monitor keeps going.
        }
    }

    public void EndSession()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        Stop(session);
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        Stop(session);
    }

    private async Task<QueryEditorStartResult> StartCoreAsync(string text, QueryEditorLaunchContext? launchContext, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
                return Failure(ClosedMessage);
            if (_session is { } active)
                return new QueryEditorStartResult(true, active.Id, null);
        }

        if (cancellationToken.IsCancellationRequested)
            return Failure(CancelledMessage);

        byte[] payload;
        try
        {
            var body = StrictUtf8.GetBytes(text);
            payload = new byte[Utf8Bom.Length + body.Length];
            Utf8Bom.CopyTo(payload, 0);
            body.CopyTo(payload, Utf8Bom.Length);
        }
        catch (EncoderFallbackException)
        {
            return Failure("输入包含无法保存为 UTF-8 的字符，未打开记事本");
        }

        if (payload.Length > MaxDraftBytes)
            return Failure("输入内容超过 1 MiB，未打开记事本");

        var id = Guid.NewGuid();
        var path = Path.Combine(_directory, DraftFileName(id));
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_directory);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(payload, 0, payload.Length);
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryDelete(path);
            return Failure("无法创建记事本草稿文件");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryDelete(path);
            return Failure(CancelledMessage);
        }

        // Publish and monitor the session before dispatching the app. This closes the race where a
        // fast user closes the newly-created tab before StartAsync returns. The file remains recoverable.
        var session = new Session(id, path, text, payload, launchContext);
        lock (_gate)
        {
            if (_disposed)
            {
                TryDelete(path);
                return Failure(ClosedMessage);
            }
            _session = session;
        }
        _ = Task.Run(() => RunLoopAsync(session));

        // The initial file is the baseline: only a later, different save is reported. Once the editor
        // has been launched the call is not abandoned half-way, so cancellation is not observed again.
        bool launched;
        try
        {
            launched = await Task.Run(() => _launchEditor(path)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            launched = false;
        }

        if (!launched)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                    _session = null;
            }
            Stop(session);
            // Never handed to an editor, so the file is ours to remove.
            TryDelete(path);
            return Failure("无法启动记事本");
        }

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_session, session))
                return Failure(ClosedMessage); // Already handed to the editor: the file is kept, never deleted.
        }

        return new QueryEditorStartResult(true, id, null);
    }

    private async Task RunLoopAsync(Session session)
    {
        var token = session.Cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await PollAsync(session, token).ConfigureAwait(false);
                await ObserveTabAsync(session, token).ConfigureAwait(false);
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // EndSession/Dispose.
        }
        catch (Exception)
        {
            // Last resort: this fire-and-forget task must never fault unobserved.
        }
        finally
        {
            session.Cts.Dispose();
        }
    }

    // One poll at a time per session (timer tick and RefreshAsync serialise here), so the session's
    // monitoring state needs no further locking and events leave in sequence order.
    private async Task PollAsync(Session session, CancellationToken cancellationToken)
    {
        await session.PollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Ended)
                return;
            await PollCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Transient(session, UnexpectedMessage);
        }
        finally
        {
            session.PollGate.Release();
        }
    }

    private const int TabTrackingGraceObservations = 20;

    private async Task ObserveTabAsync(Session session, CancellationToken cancellationToken)
    {
        if (_tabTracker is null)
            return;

        await session.PollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Ended || session.ClosedRaised)
                return;

            NotepadTabSnapshot snapshot;
            try
            {
                snapshot = _tabTracker.FindDraftTabs(session.FilePath);
            }
            catch (Exception)
            {
                snapshot = new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
            }

            session.TrackingObservations++;
            var matchingWindows = snapshot.MatchingWindows
                .Where(handle => handle != IntPtr.Zero)
                .Distinct()
                .Where(handle => InspectWindow(handle).Life != NotepadWindowLife.Destroyed)
                .ToArray();
            if (matchingWindows.Length > 0)
            {
                session.HasObservedTab = true;
                session.MissingTabObservations = 0;
                foreach (var window in matchingWindows)
                {
                    session.ObservedWindows.Add(window);
                    var state = InspectWindow(window);
                    if (state.VirtualDesktopId is { } desktopId)
                        session.ObservedDesktopIds[window] = desktopId;

                }

                return;
            }

            if (!snapshot.Reliable)
            {
                // An incomplete tree cannot rule out a replacement matching tab. Keep the debounce
                // consecutive across reliable scans only, even if every previously seen HWND died.
                session.MissingTabObservations = 0;
                WarnIfTrackingGraceElapsed(session);
                return;
            }

            var states = session.ObservedWindows.ToDictionary(window => window, InspectWindow);

            if (!session.HasObservedTab)
            {
                WarnIfTrackingGraceElapsed(session);
                return;
            }

            // Reliable UIA absence may mean the document tab closed, but not if its known host is
            // minimized, hidden/cloaked, or has moved away from its observed desktop.
            foreach (var (window, state) in states)
            {
                if (state.Life == NotepadWindowLife.Unknown
                    || state.IsMinimized
                    || state.Life == NotepadWindowLife.Alive && state.Visibility != NotepadWindowVisibility.Visible)
                {
                    session.MissingTabObservations = 0;
                    WarnIfTrackingGraceElapsed(session);
                    return;
                }

                if (state.Life == NotepadWindowLife.Alive)
                {
                    var expectedDesktop = session.LaunchContext?.VirtualDesktopId;
                    if (expectedDesktop is null && session.ObservedDesktopIds.TryGetValue(window, out var observedDesktop))
                        expectedDesktop = observedDesktop;
                    if (expectedDesktop is null || state.VirtualDesktopId != expectedDesktop)
                    {
                        session.MissingTabObservations = 0;
                        WarnIfTrackingGraceElapsed(session);
                        return;
                    }
                }
            }

            if (++session.MissingTabObservations < 2)
                return;

            await RaiseClosedFromStableDraftAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.PollGate.Release();
        }
    }

    private NotepadWindowState InspectWindow(IntPtr window)
    {
        try
        {
            return _tabTracker!.InspectWindow(window);
        }
        catch (Exception)
        {
            return new NotepadWindowState(NotepadWindowLife.Unknown, false, NotepadWindowVisibility.Unknown, null);
        }
    }

    private void WarnIfTrackingGraceElapsed(Session session)
    {
        if (session.TrackingObservations >= TabTrackingGraceObservations)
            WarnOnce(session, "无法可靠跟踪此记事本标签的自动关闭；草稿仍在监控并保留，可用启动器热键返回或手动结束编辑");
    }

    private void WarnOnce(Session session, string message)
    {
        if (session.Ended || !session.WarningMessages.Add(message))
            return;
        RaiseSafely(Failed, new QueryEditorError(session.Id, message));
    }

    private async Task RaiseClosedFromStableDraftAsync(Session session, CancellationToken cancellationToken)
    {
        var first = await ReadBoundedAsync(session.FilePath, cancellationToken).ConfigureAwait(false);
        if (first.Kind != ReadKind.Ok)
        {
            ApplyReadFailure(session, first.Kind);
            return;
        }

        await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        var second = await ReadBoundedAsync(session.FilePath, cancellationToken).ConfigureAwait(false);
        if (second.Kind != ReadKind.Ok)
        {
            ApplyReadFailure(session, second.Kind);
            return;
        }

        if (!first.Bytes!.AsSpan().SequenceEqual(second.Bytes!))
        {
            Transient(session, BusyMessage);
            return;
        }

        if (!TryDecode(second.Bytes!, out var text))
        {
            Permanent(session, second.Bytes, BadEncodingMessage);
            return;
        }

        // The stable draft file is the source of truth. Unsaved Notepad UI text is never read.
        session.ProcessedBytes = second.Bytes;
        session.TransientCount = 0;
        session.ErrorKey = null;
        if (!string.Equals(text, session.LastText, StringComparison.Ordinal))
        {
            session.LastText = text;
            RaiseSafely(Saved, new QueryEditorSaved(session.Id, ++session.Sequence, text));
        }

        if (session.Ended)
            return;

        session.ClosedRaised = true;
        RaiseSafely(Closed, new QueryEditorClosed(session.Id, text));
    }

    private async Task PollCoreAsync(Session session, CancellationToken cancellationToken)
    {
        // Cheap size check first: a missing file is a transient condition, an oversized one is not
        // worth reading at all.
        var length = StatLength(session.FilePath);
        if (length is null)
        {
            Transient(session, MissingMessage);
            return;
        }

        if (length.Value > MaxDraftBytes)
        {
            Permanent(session, null, TooLargeMessage);
            return;
        }

        // Always compared by content, whatever the size: file times are coarse, so a quick rewrite
        // can keep identical size and timestamps. The draft is bounded (1 MiB) and only one file is read.
        var first = await ReadBoundedAsync(session.FilePath, cancellationToken).ConfigureAwait(false);
        if (first.Kind != ReadKind.Ok)
        {
            ApplyReadFailure(session, first.Kind);
            return;
        }

        if (session.ProcessedBytes is { } handled && first.Bytes!.AsSpan().SequenceEqual(handled))
        {
            // Same bytes as the baseline or the last stable read: nothing to report.
            ClearTransientState(session);
            return;
        }

        await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);

        var second = await ReadBoundedAsync(session.FilePath, cancellationToken).ConfigureAwait(false);
        if (second.Kind != ReadKind.Ok)
        {
            ApplyReadFailure(session, second.Kind);
            return;
        }

        if (!first.Bytes!.AsSpan().SequenceEqual(second.Bytes!))
        {
            // Still being written; try again on the next poll.
            Transient(session, BusyMessage);
            return;
        }

        if (!TryDecode(second.Bytes!, out var text))
        {
            Permanent(session, second.Bytes, BadEncodingMessage);
            return;
        }

        session.ProcessedBytes = second.Bytes;
        session.TransientCount = 0;
        session.ErrorKey = null;

        if (string.Equals(text, session.LastText, StringComparison.Ordinal))
            return;

        session.LastText = text;
        if (session.Ended)
            return;

        RaiseSafely(Saved, new QueryEditorSaved(session.Id, ++session.Sequence, text));
    }

    private static void ClearTransientState(Session session)
    {
        if (session.TransientCount == 0)
            return;

        session.TransientCount = 0;
        if (session.ErrorIsTransient)
            session.ErrorKey = null;
    }

    private async Task<ReadResult> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var result = TryRead(path);
        for (var attempt = 1; attempt < BusyReadAttempts && result.Kind == ReadKind.Busy; attempt++)
        {
            await Task.Delay(_busyRetryDelay, cancellationToken).ConfigureAwait(false);
            result = TryRead(path);
        }

        return result;
    }

    private void ApplyReadFailure(Session session, ReadKind kind)
    {
        switch (kind)
        {
            case ReadKind.TooLarge:
                Permanent(session, null, TooLargeMessage);
                break;
            case ReadKind.Missing:
                Transient(session, MissingMessage);
                break;
            default:
                Transient(session, BusyMessage);
                break;
        }
    }

    // The same bytes will fail the same way, so they count as handled: no re-decoding every tick.
    // The error is reported once and the next successful read clears it.
    private void Permanent(Session session, byte[]? bytes, string message)
    {
        session.ProcessedBytes = bytes;
        session.TransientCount = 0;
        ReportError(session, message, transient: false);
    }

    // Sharing violations and half-written files usually clear within a poll or two; only a failure
    // that lasts several polls in a row is worth telling the user about.
    private void Transient(Session session, string message)
    {
        if (session.TransientCount < TransientFailureLimit)
            session.TransientCount++;
        if (session.TransientCount >= TransientFailureLimit)
            ReportError(session, message, transient: true);
    }

    private void ReportError(Session session, string message, bool transient)
    {
        if (session.Ended || string.Equals(session.ErrorKey, message, StringComparison.Ordinal))
            return;

        session.ErrorKey = message;
        session.ErrorIsTransient = transient;
        RaiseSafely(Failed, new QueryEditorError(session.Id, message));
    }

    // A faulty subscriber must not take the monitor (or the process) down with it.
    private static void RaiseSafely<T>(Action<T>? handlers, T argument)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(argument);
            }
            catch (Exception)
            {
                // Ignored on purpose.
            }
        }
    }

    private static void Stop(Session? session)
    {
        if (session is null)
            return;

        session.Ended = true;
        try
        {
            session.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The monitor loop already ended.
        }
    }

    private static QueryEditorStartResult Failure(string message) => new(false, Guid.Empty, message);

    private static bool LaunchNotepad(string path)
    {
        // Direct start of the trusted system binary with an argument list: no shell, no quoting of the path.
        var startInfo = new ProcessStartInfo(NotepadPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    internal static string DraftFileName(Guid id) => DraftPrefix + id.ToString("N") + DraftExtension;

    private static long? StatLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort; a stray empty draft in the temp folder is harmless.
        }
    }

    // Reads with full sharing (and delete sharing) so we never block an editor that saves by
    // replacing the file, and stops as soon as the size limit is exceeded.
    private static ReadResult TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxDraftBytes)
                    return new ReadResult(ReadKind.TooLarge, null);
                buffer.Write(chunk, 0, read);
            }

            return new ReadResult(ReadKind.Ok, buffer.ToArray());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadResult(ReadKind.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReadResult(ReadKind.Busy, null);
        }
    }

    // BOM UTF-8 / UTF-16 are recognised; anything else must be valid UTF-8 (strict, no guessing).
    private static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = StrictUtf16Le.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                text = StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2);
            else
                text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private enum ReadKind
    {
        Ok,
        Missing,
        Busy,
        TooLarge,
    }

    private readonly record struct ReadResult(ReadKind Kind, byte[]? Bytes);

    private sealed class Session
    {
        public Session(
            Guid id,
            string filePath,
            string initialText,
            byte[] initialBytes,
            QueryEditorLaunchContext? launchContext)
        {
            Id = id;
            FilePath = filePath;
            LastText = initialText;
            ProcessedBytes = initialBytes;
            LaunchContext = launchContext;
        }

        public Guid Id { get; }

        public string FilePath { get; }

        public QueryEditorLaunchContext? LaunchContext { get; }

        public CancellationTokenSource Cts { get; } = new();

        public SemaphoreSlim PollGate { get; } = new(1, 1);

        // Set by EndSession/Dispose from any thread, read by the monitor before every delivery.
        public volatile bool Ended;

        // Everything below is only touched while holding PollGate.
        public byte[]? ProcessedBytes;
        public string LastText;
        public long Sequence;
        public string? ErrorKey;
        public bool ErrorIsTransient;
        public int TransientCount;
        public bool HasObservedTab;
        public int MissingTabObservations;
        public int TrackingObservations;
        public bool ClosedRaised;
        public HashSet<IntPtr> ObservedWindows { get; } = new();
        public Dictionary<IntPtr, Guid> ObservedDesktopIds { get; } = new();
        public HashSet<string> WarningMessages { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Best-effort Windows 11 tab ownership by the launcher's unpredictable draft filename. UI Automation
/// is queried on a bounded STA worker; an incomplete tree is never treated as evidence of closure.
/// </summary>
internal sealed class WindowsNotepadTabTracker : INotepadTabTracker
{
    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromMilliseconds(700);
    private int _scanActive;

    public NotepadTabSnapshot FindDraftTabs(string draftPath)
    {
        if (Interlocked.CompareExchange(ref _scanActive, 1, 0) != 0)
            return new NotepadTabSnapshot(false, Array.Empty<IntPtr>());

        var outcome = BoundedStaCall.Run(
            () => ScanDraftTabs(Path.GetFileName(draftPath)),
            UiAutomationTimeout,
            "R1ng Notepad tab UIA");
        if (outcome.Completed)
            Interlocked.Exchange(ref _scanActive, 0);
        else
            _ = outcome.Completion.ContinueWith(
                _ => Interlocked.Exchange(ref _scanActive, 0),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        return outcome.Completed && outcome.Value is { } snapshot
            ? snapshot
            : new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
    }

    public NotepadWindowState InspectWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return new NotepadWindowState(NotepadWindowLife.Unknown, false, NotepadWindowVisibility.Unknown, null);
        if (!NativeMethods.IsWindow(window))
            return new NotepadWindowState(NotepadWindowLife.Destroyed, false, NotepadWindowVisibility.Unknown, null);

        var visibility = !NativeMethods.IsWindowVisible(window)
            ? NotepadWindowVisibility.Hidden
            : NativeMethods.TryGetWindowCloaked(window, out var cloaked)
                ? cloaked ? NotepadWindowVisibility.Cloaked : NotepadWindowVisibility.Visible
                : NotepadWindowVisibility.Unknown;
        return new NotepadWindowState(
            NotepadWindowLife.Alive,
            NativeMethods.IsIconic(window),
            visibility,
            ScreenPlacement.TryGetWindowDesktopId(window));
    }

    private static NotepadTabSnapshot ScanDraftTabs(string draftFileName)
    {
        var matches = new HashSet<IntPtr>();
        try
        {
            var root = AutomationElement.RootElement;
            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement window in windows)
            {
                int processId;
                int nativeHandle;
                string windowName;
                try
                {
                    processId = window.Current.ProcessId;
                    nativeHandle = window.Current.NativeWindowHandle;
                    windowName = window.Current.Name;
                }
                catch (ElementNotAvailableException)
                {
                    return new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
                }

                if (!IsNotepadProcess(processId))
                    continue;

                var hwnd = new IntPtr(nativeHandle);
                if (MatchesDraftName(windowName, draftFileName) && hwnd != IntPtr.Zero)
                    matches.Add(hwnd);

                AutomationElementCollection descendants;
                try
                {
                    descendants = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                }
                catch (Exception)
                {
                    return new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
                }

                foreach (AutomationElement element in descendants)
                {
                    try
                    {
                        var current = element.Current;
                        if (current.ControlType == ControlType.TabItem
                            && MatchesDraftName(current.Name, draftFileName)
                            && hwnd != IntPtr.Zero)
                        {
                            matches.Add(hwnd);
                        }
                    }
                    catch (ElementNotAvailableException)
                    {
                        return new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
                    }
                }
            }

            return new NotepadTabSnapshot(true, matches.ToArray());
        }
        catch (Exception)
        {
            return new NotepadTabSnapshot(false, Array.Empty<IntPtr>());
        }
    }

    private static bool IsNotepadProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, "Notepad", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool MatchesDraftName(string? automationName, string draftFileName)
    {
        if (string.IsNullOrWhiteSpace(automationName))
            return false;

        var stem = Path.GetFileNameWithoutExtension(draftFileName);
        return automationName.Contains(draftFileName, StringComparison.OrdinalIgnoreCase)
            || automationName.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }
}
