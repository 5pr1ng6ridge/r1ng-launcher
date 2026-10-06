using System.IO;
using System.Runtime.InteropServices;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Best-effort resolution of a captured foreground window handle to the physical folder it shows in
/// File Explorer (or on the desktop).
/// <para>
/// Shell automation is OS/build dependent and a blocked COM call cannot be interrupted, so the
/// lookup runs on a dedicated background STA thread with a bounded wait. On timeout the caller gets
/// "no folder" and the late worker result is ignored, which keeps the UI responsive and never
/// invents a path from a background, virtual or previous window. Virtual locations and URIs are
/// rejected, and UNC targets are validated off the UI thread.
/// </para>
/// </summary>
public static class ExplorerFolderResolver
{
    /// <summary>How long the UI waits for a folder lookup before falling back to "no prefix".</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Resolves the folder shown by the captured Explorer window, or null when unknown.</summary>
    public static string? TryResolve(IntPtr windowHandle, TimeSpan? timeout = null)
    {
        if (windowHandle == IntPtr.Zero)
            return null;

        return ResolveWith(() => ResolvePhysicalFolder(windowHandle), timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// Runs one resolver on a bounded background STA thread. Exposed internally so the timeout
    /// ownership contract (a late worker can never touch a disposed signal) can be tested without a
    /// real Explorer window.
    /// </summary>
    internal static string? ResolveWith(Func<string?> resolver, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var outcome = BoundedStaCall.Run(resolver, timeout, "R1ng Explorer folder");
        return outcome.Completed ? outcome.Value : null;
    }

    /// <summary>Walks <c>Shell.Application.Windows</c> looking for the exact captured handle.</summary>
    internal static string? ResolvePhysicalFolder(IntPtr windowHandle)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            if (windows is null)
                return null;

            dynamic dynamicWindows = windows;
            int count = dynamicWindows.Count;
            for (var index = 0; index < count; index++)
            {
                object? windowObject = null;
                try
                {
                    windowObject = dynamicWindows.Item(index);
                    if (windowObject is null)
                        continue;

                    dynamic window = windowObject;
                    long handle = window.HWND;
                    if (new IntPtr(handle) != windowHandle)
                        continue;

                    var path = ReadFolderPath(window);
                    var validated = ValidateFolder(path);
                    if (validated is not null)
                        return validated;
                }
                catch (Exception)
                {
                    // A window without a browsable folder is simply skipped.
                }
                finally
                {
                    Release(windowObject);
                }
            }
        }
        catch (Exception)
        {
            // Shell automation is best effort; any failure means "no folder".
        }
        finally
        {
            Release(windows);
            Release(shell);
        }

        return null;
    }

    private static string? ReadFolderPath(dynamic window)
    {
        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            document = window.Document;
            if (document is null)
                return null;

            dynamic dynamicDocument = document;
            folder = dynamicDocument.Folder;
            if (folder is null)
                return null;

            dynamic dynamicFolder = folder;
            self = dynamicFolder.Self;
            if (self is null)
                return null;

            dynamic dynamicSelf = self;
            return dynamicSelf.Path as string;
        }
        finally
        {
            Release(document);
            Release(folder);
            Release(self);
        }
    }

    /// <summary>
    /// Accepts only a rooted, existing, non-virtual, non-URI folder. Runs on the worker thread so a
    /// slow (for example UNC) existence check never touches the UI thread.
    /// </summary>
    internal static string? ValidateFolder(string? path)
    {
        if (!FolderPrefixQuery.IsPhysicalFolder(path))
            return null;

        var value = path!.Trim();
        try
        {
            if (!Directory.Exists(value))
                return null;
        }
        catch (Exception)
        {
            return null;
        }

        // Keep a drive root as "C:\", otherwise drop the trailing separator for the visible prefix.
        return value.Length > 3 ? value.TrimEnd('\\', '/') : value;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
