namespace R1ng.Launcher.Platform;

internal readonly record struct LauncherWindowSnapshot(
    IntPtr Handle,
    ScreenPlacement.PixelsRect Bounds,
    ScreenPlacement.MonitorSample Monitor);

/// <summary>Small native seam for capturing and moving only the launcher's own HWND.</summary>
internal interface ILauncherWindowPlacement
{
    bool TryCapture(IntPtr handle, out LauncherWindowSnapshot snapshot);

    bool TryMove(IntPtr handle, int x, int y);
}

internal sealed class Win32LauncherWindowPlacement : ILauncherWindowPlacement
{
    public bool TryCapture(IntPtr handle, out LauncherWindowSnapshot snapshot)
    {
        snapshot = default;
        if (!IsUsable(handle)
            || !ScreenPlacement.TryGetWindowBounds(handle, out var bounds)
            || !ScreenPlacement.TrySampleWindowMonitor(handle, out var monitor))
        {
            return false;
        }

        snapshot = new LauncherWindowSnapshot(handle, bounds, monitor);
        return true;
    }

    private bool IsUsable(IntPtr handle)
    {
        return handle != IntPtr.Zero
            && NativeMethods.IsWindow(handle)
            && NativeMethods.IsWindowVisible(handle)
            && !NativeMethods.IsIconic(handle)
            && NativeMethods.TryGetWindowCloaked(handle, out var cloaked)
            && !cloaked;
    }

    public bool TryMove(IntPtr handle, int x, int y) =>
        IsUsable(handle) && ScreenPlacement.TryMoveOwnWindow(handle, x, y);
}
