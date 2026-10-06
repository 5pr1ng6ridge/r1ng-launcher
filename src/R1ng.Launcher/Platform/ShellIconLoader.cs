using R1ng.Launcher.Apps;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Resolves shell icons for shortcuts, executables, folders and shell namespace items
/// (<c>shell:AppsFolder\&lt;AppUserModelId&gt;</c>) through SHGetFileInfo / SHParseDisplayName.
/// Requests for the same path/index/folder hint share an extraction; failures cool down briefly
/// rather than being cached for the entire process. Loading runs off the UI thread and returns
/// frozen images.
/// </summary>
public static class ShellIconLoader
{
    private const uint CoInitApartmentThreaded = 0x2;

    /// <summary>How long a shell-namespace extraction may occupy the STA helper before it is dropped.</summary>
    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(3);

    private static readonly IconCache Cache = new();

    /// <summary>Returns a frozen icon, or null when the shell has nothing for the path.</summary>
    /// <param name="path">File path or <c>shell:</c> namespace target.</param>
    /// <param name="isFolder">Hint for paths that do not exist on disk yet.</param>
    /// <param name="iconIndex">Icon index inside the file; -1 selects the file's default icon.</param>
    public static ImageSource? Load(string? path, bool isFolder = false, int iconIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            // Old history may only know the shortcut path. Safely recover its local declared icon
            // here, off the UI thread; rejected declarations still get the attributes-only icon.
            var reference = ShellLinkReader.IsLocalAbsolute(path) ? UrlShortcut.ReadIcon(path) : UrlShortcut.IconReference.None;
            if (reference.File is not null)
            {
                path = reference.File;
                iconIndex = reference.Index;
            }
            else
            {
                iconIndex = -1;
            }
        }
        return Cache.Load(path, isFolder, iconIndex, () => ExtractIcon(path, isFolder, iconIndex));
    }

    private static ImageSource? ExtractIcon(string path, bool isFolder, int iconIndex)
    {
        // Parsing a shell namespace target (an AppUserModelId) is most reliable on an STA thread.
        // The pool thread reached through Task.Run is usually MTA, so run it on a dedicated STA
        // thread there.
        if (IsShellNamespace(path))
        {
            return ResolveShellNamespaceIcon(
                () => ExtractCore(path, isFolder, iconIndex),
                StaTimeout,
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            );
        }

        return ExtractCore(path, isFolder, iconIndex);
    }

    /// <summary>
    /// Runs a shell-namespace extraction on an STA thread when the caller is not already STA.
    /// <para>
    /// A timed-out attempt returns null and is deliberately <em>not</em> retried on the caller's
    /// apartment: the original worker is still inside COM, so a duplicate call could hang the caller
    /// and would only race the same shell state. A completed-but-empty attempt may still fall back,
    /// matching the original best-effort behavior. Internal so the timeout/fallback contract can be
    /// tested without a real shell namespace target.
    /// </para>
    /// </summary>
    internal static ImageSource? ResolveShellNamespaceIcon(
        Func<ImageSource?> extraction,
        TimeSpan timeout,
        bool currentApartmentIsSta
    )
    {
        ArgumentNullException.ThrowIfNull(extraction);

        if (currentApartmentIsSta)
            return extraction();

        var outcome = BoundedStaCall.Run(extraction, timeout, "R1ng shell icon (STA)");
        if (!outcome.Completed)
            return null;

        return outcome.Value ?? extraction();
    }

    private static ImageSource? ExtractCore(string path, bool isFolder, int iconIndex)
    {
        // SHGetFileInfo/SHParseDisplayName need COM. A pool thread that is already MTA reports
        // RPC_E_CHANGED_MODE; that is not a failure (COM is initialized), and a successful or
        // S_FALSE call is the only case where this method owns a CoUninitialize.
        var comInitialized = TryComInitialize();
        try
        {
            if (IsShellNamespace(path))
                return ExtractFromShellNamespace(path);

            if (iconIndex >= 0 && TryExtractIndexed(path, iconIndex, out var indexed))
                return indexed;

            return ExtractFromFile(path, isFolder);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (comInitialized)
                NativeMethods.CoUninitialize();
        }
    }

    private static ImageSource? ExtractFromShellNamespace(string path)
    {
        var parseResult = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
        if (parseResult != 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            var info = new NativeMethods.ShellFileInfo();
            var flags = NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon | NativeMethods.ShgfiPidl;

            // SHGFI_PIDL must not be combined with SHGFI_USEFILEATTRIBUTES.
            var result = NativeMethods.SHGetFileInfoW(
                pidl,
                0,
                ref info,
                Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
                flags
            );

            return FromHIcon(result == IntPtr.Zero ? IntPtr.Zero : info.hIcon);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static ImageSource? ExtractFromFile(string path, bool isFolder)
    {
        var info = new NativeMethods.ShellFileInfo();
        var flags = NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon;

        if (ShouldUseFileAttributes(path, SafeExists(path)))
            flags |= NativeMethods.ShgfiUseFileAttributes;

        var attributes = isFolder ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal;
        var result = NativeMethods.SHGetFileInfoW(
            path,
            attributes,
            ref info,
            Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
            flags
        );

        return FromHIcon(result == IntPtr.Zero ? IntPtr.Zero : info.hIcon);
    }

    private static bool TryExtractIndexed(string path, int iconIndex, out ImageSource? icon)
    {
        icon = null;
        try
        {
            if (!File.Exists(path))
                return false;

            var large = new IntPtr[1];
            var extracted = NativeMethods.ExtractIconExW(path, iconIndex, large, null, 1);
            if (extracted == 0 || large[0] == IntPtr.Zero)
                return false;

            icon = FromHIcon(large[0]);
            return icon is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions()
            );
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static bool TryComInitialize()
    {
        var result = NativeMethods.CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);

        // S_OK (0) or S_FALSE (1): this call owns the matching CoUninitialize.
        if (result == 0 || result == 1)
            return true;

        // RPC_E_CHANGED_MODE (0x80010106): COM is already initialized as MTA. Any other error is
        // reported by the shell call itself; either way this call must not CoUninitialize an
        // apartment it did not initialize.
        return false;
    }

    private static bool IsShellNamespace(string path) =>
        path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("::", StringComparison.Ordinal);

    private static bool SafeExists(string path)
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

    /// <summary>
    /// True when the generic extension icon must be used instead of reading the file. A missing path
    /// is obviously generic; so is a <c>.url</c> shortcut, because the shell's InternetShortcut
    /// handler parses the file's declared <c>IconFile</c> and could fetch a remote (UNC) icon over the
    /// network even though <c>UrlShortcut</c> already rejected it. Using the file attributes avoids
    /// opening the shortcut at all.
    /// </summary>
    internal static bool ShouldUseFileAttributes(string path, bool exists) =>
        !exists || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
}
