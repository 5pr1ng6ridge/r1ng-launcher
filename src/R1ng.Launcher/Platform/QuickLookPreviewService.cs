using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Asks a running QuickLook instance to preview a local file or folder. The preview target is only
/// ever handed to QuickLook as a path string; it is never opened, resolved or executed here.
/// </summary>
public interface IQuickLookPreviewService
{
    /// <summary>
    /// Cheap, side-effect free check that the row names an absolute physical path that can be sent
    /// over the QuickLook protocol. It does not touch the file system: whether the path really
    /// exists is decided by <see cref="PreviewAsync"/> on a background thread.
    /// </summary>
    bool CanPreview(SearchResultItem item);

    /// <summary>
    /// Sends a preview (toggle) request. Cancellation is reported as an unsuccessful result rather
    /// than an exception, so a caller never has to observe a faulted task.
    /// </summary>
    Task<QuickLookPreviewResult> PreviewAsync(SearchResultItem item, CancellationToken cancellationToken = default);

    /// <summary>Switches an already-open QuickLook preview to this item; it never opens or closes a preview.</summary>
    Task<QuickLookPreviewResult> SwitchAsync(SearchResultItem item, CancellationToken cancellationToken = default);

    /// <summary>Closes the current non-pinned preview; never toggles, opens a window, or quits QuickLook.</summary>
    Task<QuickLookPreviewResult> CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a preview request; <see cref="Message"/> is ready to show to the user.</summary>
public sealed record QuickLookPreviewResult(bool Success, string Message);

/// <summary>
/// QuickLook interop through its per-user local named pipe. The wire format is the documented
/// one-line text command <c>QuickLook.App.PipeMessages.Toggle|&lt;path&gt;|\n</c> (UTF-8, no BOM)
/// written to <c>QuickLook.App.Pipe.&lt;current user SID&gt;</c>. QuickLook sends no acknowledgement,
/// so a successful result only means the request was written, not that a window appeared.
/// </summary>
public sealed class QuickLookPreviewService : IQuickLookPreviewService
{
    internal const string PipeNamePrefix = "QuickLook.App.Pipe.";
    internal const string ToggleCommand = "QuickLook.App.PipeMessages.Toggle";
    internal const string SwitchCommand = "QuickLook.App.PipeMessages.Switch";
    internal const string CloseCommand = "QuickLook.App.PipeMessages.Close";

    private const int MaxPathLength = 32767;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    // Strict: a path that cannot be encoded losslessly must be rejected, not silently mangled.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    // Spelled out instead of Path.GetInvalidPathChars(), whose set differs between runtimes: control
    // characters (CR/LF/NUL included), the protocol delimiter '|' and the characters no Windows path allows.
    private static readonly char[] InvalidPathChars = Enumerable.Range(0, 32).Select(i => (char)i).Concat("\"<>|").ToArray();

    private static readonly QuickLookPreviewResult Sent = new(true, "预览请求已发送");
    private static readonly QuickLookPreviewResult Cancelled = new(false, "预览已取消");
    private static readonly QuickLookPreviewResult Unsupported = new(false, "只能预览本地文件或文件夹");
    private static readonly QuickLookPreviewResult Missing = new(false, "目标不存在或无法访问，无法预览");
    private static readonly QuickLookPreviewResult ExistenceTimeout = new(false, "确认目标是否存在超时，无法预览");
    private static readonly QuickLookPreviewResult UnknownUser = new(false, "无法确定当前用户，不能连接 QuickLook");
    private static readonly QuickLookPreviewResult ConnectTimeout = new(false, "未能连接到 QuickLook（连接超时），请确认 QuickLook 已运行后重试");
    private static readonly QuickLookPreviewResult WriteTimeout = new(false, "向 QuickLook 发送预览请求超时，请稍后重试");
    private static readonly QuickLookPreviewResult PipeBroken = new(false, "与 QuickLook 的连接已中断，预览请求可能未送达");
    private static readonly QuickLookPreviewResult AccessDenied = new(false, "没有权限访问 QuickLook 预览通道，请确认 QuickLook 与启动器以同一用户运行");

    private readonly string? _pipeName;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates the service. <paramref name="pipeName"/> and <paramref name="timeout"/> exist so tests
    /// can talk to their own pipe instead of a running QuickLook; production passes neither.
    /// </summary>
    public QuickLookPreviewService(string? pipeName = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (pipeName is not null && string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("The pipe name must not be blank.", nameof(pipeName));

        _pipeName = pipeName ?? DefaultPipeName();
        _timeout = effectiveTimeout;
    }

    public bool CanPreview(SearchResultItem item) => item is not null && TryGetPreviewPath(item, out _);

    public Task<QuickLookPreviewResult> PreviewAsync(SearchResultItem item, CancellationToken cancellationToken = default) =>
        SendItemAsync(item, ToggleCommand, cancellationToken);

    public Task<QuickLookPreviewResult> SwitchAsync(SearchResultItem item, CancellationToken cancellationToken = default) =>
        SendItemAsync(item, SwitchCommand, cancellationToken);

    public Task<QuickLookPreviewResult> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(Cancelled);
        return _pipeName is null ? Task.FromResult(UnknownUser)
            : SendAsync(_pipeName, BuildMessage(string.Empty, CloseCommand), cancellationToken);
    }

    private async Task<QuickLookPreviewResult> SendItemAsync(SearchResultItem item, string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (cancellationToken.IsCancellationRequested)
            return Cancelled;
        if (!TryGetPreviewPath(item, out var path))
            return Unsupported;
        if (_pipeName is null)
            return UnknownUser;

        // File.Exists/Directory.Exists can stall on an unreachable network share, so the check runs
        // on the thread pool with the same bounded wait as the pipe. A late result is ignored.
        bool exists;
        try
        {
            exists = await Task.Run(() => PathExists(path)).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled;
        }
        catch (TimeoutException)
        {
            return ExistenceTimeout;
        }

        if (!exists)
            return Missing;

        return await SendAsync(_pipeName, BuildMessage(path, command), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pipe QuickLook listens on for the current Windows user, or null when the SID is unknown.</summary>
    internal static string? DefaultPipeName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            return string.IsNullOrEmpty(sid) ? null : PipeNamePrefix + sid;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The exact bytes of a protocol request for an already validated path: UTF-8, no BOM.</summary>
    internal static byte[] BuildMessage(string path, string command = ToggleCommand) => StrictUtf8.GetBytes(command + "|" + path + "|\n");

    /// <summary>
    /// Validates and normalises the row's target without any file system access. Shortcuts (.lnk,
    /// .url) are ordinary physical files here: the shortcut itself is previewed, never resolved.
    /// </summary>
    internal static bool TryGetPreviewPath(SearchResultItem item, out string path)
    {
        path = string.Empty;
        var target = item.Target;

        if (string.IsNullOrWhiteSpace(target) || target.Length > MaxPathLength)
            return false;
        if (TargetLauncher.IsAppsFolderTarget(target))
            return false;
        // '|' and CR/LF are protocol delimiters; none of the invalid path characters can be part of
        // a real file name either, so rejecting them up front also keeps the message unambiguous.
        if (target.IndexOfAny(InvalidPathChars) >= 0)
            return false;
        // Scheme URIs (http:, ms-settings:, ...) and drive-relative / rooted paths are not fully qualified.
        if (!Path.IsPathFullyQualified(target) || IsDevicePath(target))
            return false;

        try
        {
            var full = Path.GetFullPath(target);
            StrictUtf8.GetByteCount(full);
            path = full;
            return true;
        }
        catch (Exception)
        {
            // Includes EncoderFallbackException for unpaired surrogates.
            return false;
        }
    }

    // DOS device / NT namespace paths are not physical files. \\?\C:\ and \\?\UNC\ are the long-path
    // spellings of real locations and stay allowed.
    private static bool IsDevicePath(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return true;
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return false;

        var rest = path.AsSpan(4);
        var isDrive = rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\';
        var isUnc = rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
        return !isDrive && !isUnc;
    }

    private static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<QuickLookPreviewResult> SendAsync(string pipeName, byte[] message, CancellationToken cancellationToken)
    {
        try
        {
            // Identification keeps whoever owns the pipe from impersonating us; we only write.
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync((int)_timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

            using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeLimit.CancelAfter(_timeout);
            try
            {
                await client.WriteAsync(message, writeLimit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return WriteTimeout;
            }

            // Disposing the client closes the handle; bytes already in the pipe stay readable for the server.
            return Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled;
        }
        catch (TimeoutException)
        {
            // Not "not installed": the pipe may be missing, busy or owned by another session.
            return ConnectTimeout;
        }
        catch (UnauthorizedAccessException)
        {
            return AccessDenied;
        }
        catch (IOException)
        {
            return PipeBroken;
        }
        catch (Exception ex)
        {
            return new QuickLookPreviewResult(false, $"发送预览请求失败（{ex.GetType().Name}）");
        }
    }
}
