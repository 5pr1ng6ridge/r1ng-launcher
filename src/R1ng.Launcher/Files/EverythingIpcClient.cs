using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Files;

/// <summary>
/// File search backend that talks to a running Everything instance through its documented IPC
/// protocol. No native Everything64.dll is required, and there is deliberately no disk-scan
/// fallback because an unbounded scan would make the launcher slow.
/// <para>
/// All window work happens on one dedicated message-pump thread. Queries are queued to that thread
/// and completed from there, so the UI thread never blocks on IPC.
/// </para>
/// </summary>
public sealed class EverythingIpcClient : IEverythingQueryClient, IDisposable
{
    private readonly TimeSpan _queryTimeout;
    private readonly ConcurrentQueue<PendingQuery> _pending = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _stateGate = new();
    private readonly EverythingReplyGate _replyGate = new();

    private EverythingInterop.WndProcDelegate? _wndProc;
    private Thread? _thread;
    private IntPtr _window;
    private string? _className;
    private volatile bool _stopping;
    private bool _disposed;

    public EverythingIpcClient(TimeSpan? queryTimeout = null)
    {
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>True once the reply window exists.</summary>
    public bool IsStarted => _window != IntPtr.Zero;

    /// <summary>Starts the IPC thread. Safe to call repeatedly.</summary>
    public void Start()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
                return;

            _stopping = false;
            _ready.Reset();
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "R1ng Everything IPC",
            };

            _thread = thread;
            thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Checks whether Everything is running, new enough and finished indexing.</summary>
    public FileSearchAvailability Probe()
    {
        if (_disposed)
            return FileSearchAvailability.Unavailable("文件搜索已停止");

        try
        {
            Start();
        }
        catch (Exception exception)
        {
            return FileSearchAvailability.Unavailable($"无法启动 Everything IPC：{exception.Message}");
        }

        if (!IsStarted)
            return FileSearchAvailability.Unavailable("无法创建 Everything IPC 应答窗口");

        var everythingWindow = EverythingInterop.FindWindowW(EverythingProtocol.NotificationWindowClass, null);
        if (everythingWindow == IntPtr.Zero)
            return FileSearchAvailability.Unavailable("Everything 未运行");

        var major = SendIpc(everythingWindow, EverythingProtocol.GetMajorVersion);
        var minor = SendIpc(everythingWindow, EverythingProtocol.GetMinorVersion);
        var revision = SendIpc(everythingWindow, EverythingProtocol.GetRevision);

        if (!EverythingProtocol.SupportsQuery2(major, minor, revision))
        {
            return FileSearchAvailability.Unavailable(
                $"Everything {major}.{minor}.{revision} 版本过低，需要 1.4.1 或更高版本"
            );
        }

        if (SendIpc(everythingWindow, EverythingProtocol.IsDbLoaded) == 0)
            return FileSearchAvailability.Unavailable("Everything 索引尚未加载完成");

        return FileSearchAvailability.Ready($"Everything {major}.{minor}.{revision}");
    }

    /// <summary>Runs one query. Never throws; a timeout or cancellation yields an empty list.</summary>
    public Task<IReadOnlyList<EverythingRawItem>> QueryAsync(
        string search,
        int maxResults,
        CancellationToken cancellationToken,
        FileSearchOptions options = default
    )
    {
        if (_disposed || string.IsNullOrWhiteSpace(search) || maxResults <= 0)
            return Task.FromResult<IReadOnlyList<EverythingRawItem>>(Array.Empty<EverythingRawItem>());

        try
        {
            Start();
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<EverythingRawItem>>(Array.Empty<EverythingRawItem>());
        }

        if (!IsStarted)
            return Task.FromResult<IReadOnlyList<EverythingRawItem>>(Array.Empty<EverythingRawItem>());

        var work = new PendingQuery(search, maxResults, options, cancellationToken);
        _pending.Enqueue(work);
        return work.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stopping = true;

        var thread = _thread;
        if (thread is { IsAlive: true })
            thread.Join(TimeSpan.FromSeconds(2));

        while (_pending.TryDequeue(out var work))
            work.Complete(Array.Empty<EverythingRawItem>());

        _ready.Dispose();
    }

    private void ThreadMain()
    {
        var instance = EverythingInterop.GetModuleHandleW(null);
        _className = "R1ngLauncherEverythingReply_" + Guid.NewGuid().ToString("N");
        _wndProc = WindowProc;

        var windowClass = new EverythingInterop.WndClassEx
        {
            cbSize = Marshal.SizeOf<EverythingInterop.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = _className,
        };

        if (EverythingInterop.RegisterClassExW(ref windowClass) == 0)
        {
            _ready.Set();
            return;
        }

        // A plain hidden popup window (not a message-only window) so Everything can reach it with a
        // regular cross-process SendMessage.
        _window = EverythingInterop.CreateWindowExW(
            0,
            _className,
            "R1ngLauncherEverythingReply",
            EverythingInterop.WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero
        );

        _ready.Set();

        if (_window == IntPtr.Zero)
        {
            EverythingInterop.UnregisterClassW(_className, instance);
            return;
        }

        try
        {
            while (!_stopping)
            {
                while (EverythingInterop.PeekMessageW(out var message, IntPtr.Zero, 0, 0, EverythingInterop.PmRemove))
                {
                    EverythingInterop.TranslateMessage(ref message);
                    EverythingInterop.DispatchMessageW(ref message);
                }

                if (_pending.TryDequeue(out var work))
                {
                    Execute(work);
                    continue;
                }

                EverythingInterop.MsgWaitForMultipleObjects(0, null, false, 20, EverythingInterop.QsAllInput);
            }
        }
        finally
        {
            EverythingInterop.DestroyWindow(_window);
            _window = IntPtr.Zero;
            EverythingInterop.UnregisterClassW(_className, instance);
        }
    }

    private void Execute(PendingQuery work)
    {
        if (work.IsCancelled)
        {
            work.Complete(Array.Empty<EverythingRawItem>());
            return;
        }

        var everythingWindow = EverythingInterop.FindWindowW(EverythingProtocol.NotificationWindowClass, null);
        if (everythingWindow == IntPtr.Zero)
        {
            work.Complete(Array.Empty<EverythingRawItem>());
            return;
        }

        // A unique, non-zero id per query: Everything echoes it back as the reply's dwData, which is
        // how a late reply from a timed-out query is kept apart from a newer query.
        var requestId = _replyGate.Begin();

        var payload = EverythingProtocol.BuildQuery2(
            work.Search,
            work.MaxResults,
            unchecked((uint)_window.ToInt64()),
            requestId,
            searchFlags: EverythingProtocol.ToSearchFlags(work.Options)
        );

        var payloadPointer = Marshal.AllocHGlobal(payload.Length);
        var copyDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<EverythingInterop.CopyDataStruct>());
        try
        {
            Marshal.Copy(payload, 0, payloadPointer, payload.Length);
            var copyData = new EverythingInterop.CopyDataStruct
            {
                dwData = (IntPtr)EverythingProtocol.CopyDataQuery2W,
                cbData = payload.Length,
                lpData = payloadPointer,
            };

            Marshal.StructureToPtr(copyData, copyDataPointer, false);

            var sent = EverythingInterop.SendMessageTimeoutW(
                everythingWindow,
                EverythingInterop.WmCopyData,
                _window,
                copyDataPointer,
                EverythingInterop.SmtoAbortIfHung | EverythingInterop.SmtoNormal,
                (uint)_queryTimeout.TotalMilliseconds,
                out var queryResult
            );

            if (sent != IntPtr.Zero && queryResult != IntPtr.Zero)
            {
                // Everything usually answers from inside the SendMessage above; pump the queue
                // briefly in case the reply was posted instead.
                var deadline = Environment.TickCount64 + (long)_queryTimeout.TotalMilliseconds;
                while (Environment.TickCount64 < deadline && !work.IsCancelled && !_replyGate.HasReply(requestId))
                {
                    EverythingInterop.MsgWaitForMultipleObjects(0, null, false, 15, EverythingInterop.QsAllInput);
                    while (EverythingInterop.PeekMessageW(out var message, IntPtr.Zero, 0, 0, EverythingInterop.PmRemove))
                    {
                        EverythingInterop.TranslateMessage(ref message);
                        EverythingInterop.DispatchMessageW(ref message);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(payloadPointer);
            Marshal.FreeHGlobal(copyDataPointer);
        }

        // Take() retires the request even on a timeout/cancellation, so a reply that arrives later is
        // dropped instead of being mistaken for the next query's answer.
        work.Complete(_replyGate.Take(requestId) ?? Array.Empty<EverythingRawItem>());
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == EverythingInterop.WmCopyData && lParam != IntPtr.Zero)
        {
            try
            {
                var copyData = Marshal.PtrToStructure<EverythingInterop.CopyDataStruct>(lParam);
                var replyId = unchecked((uint)copyData.dwData.ToInt64());

                if (EverythingProtocol.IsAcceptableReplySize(copyData.cbData) && copyData.lpData != IntPtr.Zero)
                {
                    var buffer = new byte[copyData.cbData];
                    Marshal.Copy(copyData.lpData, buffer, 0, copyData.cbData);
                    if (_replyGate.TryAccept(replyId, buffer))
                        return (IntPtr)1;
                }
            }
            catch (Exception)
            {
                // A malformed or oversized WM_COPYDATA payload must never crash the IPC message pump.
            }
        }

        return EverythingInterop.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private static uint SendIpc(IntPtr window, uint ipcMessage)
    {
        var sent = EverythingInterop.SendMessageTimeoutW(
            window,
            EverythingProtocol.WmIpc,
            (IntPtr)ipcMessage,
            IntPtr.Zero,
            EverythingInterop.SmtoAbortIfHung,
            1000,
            out var result
        );

        return sent == IntPtr.Zero ? 0 : unchecked((uint)result.ToInt64());
    }

    /// <summary>One queued query plus the promise handed back to the caller.</summary>
    private sealed class PendingQuery
    {
        private readonly TaskCompletionSource<IReadOnlyList<EverythingRawItem>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationToken _cancellationToken;

        public PendingQuery(string search, int maxResults, FileSearchOptions options, CancellationToken cancellationToken)
        {
            Search = search;
            MaxResults = maxResults;
            Options = options;
            _cancellationToken = cancellationToken;
        }

        public string Search { get; }

        public int MaxResults { get; }

        public FileSearchOptions Options { get; }

        public bool IsCancelled => _cancellationToken.IsCancellationRequested;

        public Task<IReadOnlyList<EverythingRawItem>> Task => _completion.Task;

        public void Complete(IReadOnlyList<EverythingRawItem> items) => _completion.TrySetResult(items);
    }
}
