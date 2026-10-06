using System.Runtime.InteropServices;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Computes where the floating launcher should appear: centered horizontally on the monitor that
/// currently holds the mouse cursor, anchored in the upper part of its work area vertically.
/// <para>
/// Everything that talks to the OS is in <b>device pixels</b>. Work areas, window rectangles and the
/// result of <see cref="Place"/> are only comparable in the same unit, and monitors of a mixed-DPI
/// setup do not share one device-independent coordinate space, so the window is positioned in real
/// pixels and only its size limits are expressed in DIPs (<see cref="FitSize"/>).
/// </para>
/// </summary>
public static class ScreenPlacement
{
    /// <summary>Vertical anchor inside the work area (fraction of its height).</summary>
    public const double VerticalAnchor = 0.18;

    private const int LastResortWidth = 1024;
    private const int LastResortHeight = 768;

    /// <summary>
    /// Places a window of the given size inside <paramref name="workArea"/> (same units). The top is
    /// derived from the work area and the (largest) window height only, so it never depends on how
    /// much content the window currently shows.
    /// </summary>
    public static (double Left, double Top) Place(double windowWidth, double windowHeight, PixelsRect workArea)
    {
        var left = workArea.Left + ((workArea.Right - workArea.Left) - windowWidth) / 2.0;
        var top = workArea.Top + ((workArea.Bottom - workArea.Top) * VerticalAnchor);

        // Never let the window leave the work area on very small or narrow monitors.
        left = Math.Max(workArea.Left, left);
        if (top + windowHeight > workArea.Bottom)
            top = Math.Max(workArea.Top, workArea.Bottom - windowHeight);

        return (Math.Round(left), Math.Round(top));
    }

    /// <summary>
    /// Largest size, in DIPs, the window may take on a monitor rendering at <paramref name="dpiScale"/>
    /// (1.0 = 96 DPI): the design size, capped so that its real pixel size never exceeds the work area.
    /// </summary>
    public static (double Width, double Height) FitSize(
        double designWidth,
        double designHeight,
        PixelsRect workArea,
        double dpiScale
    )
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        return (
            Math.Min(designWidth, Math.Max(1, Math.Floor(workArea.Width / scale))),
            Math.Min(designHeight, Math.Max(1, Math.Floor(workArea.Height / scale)))
        );
    }

    /// <summary>Work area (in device pixels) of the monitor under the cursor.</summary>
    public static PixelsRect GetCursorWorkArea() => SampleCursorMonitor().WorkArea;

    /// <summary>
    /// Samples the monitor under the cursor: its work area in device pixels (negative coordinates and
    /// a taskbar on any edge included) and the scale it renders at. Falls back to the primary monitor.
    /// </summary>
    public static MonitorSample SampleCursorMonitor()
    {
        var monitor = NativeMethods.GetCursorPos(out var cursor)
            ? NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest)
            : IntPtr.Zero;

        if (monitor == IntPtr.Zero)
            monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MonitorDefaultToPrimary);

        var info = new NativeMethods.MonitorInfo { cbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return new MonitorSample(
                new PixelsRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                GetMonitorDpiScale(monitor)
            );
        }

        return new MonitorSample(FallbackWorkArea(), GetSystemDpiScale());
    }

    /// <summary>
    /// Work area of the primary monitor in real pixels, for when the cursor monitor cannot be
    /// determined. Never derived from <c>SystemParameters</c>, which reports DIPs.
    /// </summary>
    internal static PixelsRect FallbackWorkArea()
    {
        var work = default(NativeMethods.Rect);
        if (NativeMethods.SystemParametersInfoW(NativeMethods.SpiGetWorkArea, 0, ref work, 0)
            && work.Right > work.Left
            && work.Bottom > work.Top)
        {
            return new PixelsRect(work.Left, work.Top, work.Right, work.Bottom);
        }

        var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen);
        return width > 0 && height > 0
            ? new PixelsRect(0, 0, width, height)
            : new PixelsRect(0, 0, LastResortWidth, LastResortHeight);
    }

    /// <summary>The scale a window actually renders at right now; <paramref name="fallback"/> if unknown.</summary>
    internal static double GetWindowDpiScale(IntPtr handle, double fallback)
    {
        try
        {
            var dpi = NativeMethods.GetDpiForWindow(handle);
            if (dpi > 0)
                return dpi / (double)NativeMethods.DefaultDpi;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1607: keep the caller's estimate.
        }

        return fallback > 0 ? fallback : 1.0;
    }

    /// <summary>Current window rectangle in device pixels.</summary>
    internal static bool TryGetWindowBounds(IntPtr handle, out PixelsRect bounds)
    {
        if (NativeMethods.GetWindowRect(handle, out var rect))
        {
            bounds = new PixelsRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Moves the native window to a device-pixel position. Size, z-order and activation are left
    /// alone, so WPF stays in charge of sizing and of its own DPI-change handling.
    /// </summary>
    internal static bool TryMoveWindow(IntPtr handle, int x, int y) => NativeMethods.SetWindowPos(
        handle,
        IntPtr.Zero,
        x,
        y,
        0,
        0,
        NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate
    );

    private static double GetMonitorDpiScale(IntPtr monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / (double)NativeMethods.DefaultDpi;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Windows 8.1: fall through to the system DPI.
        }

        return GetSystemDpiScale();
    }

    private static double GetSystemDpiScale()
    {
        try
        {
            var dpi = NativeMethods.GetDpiForSystem();
            if (dpi > 0)
                return dpi / (double)NativeMethods.DefaultDpi;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1607: assume 100 %.
        }

        return 1.0;
    }

    /// <summary>A monitor work area in device pixels and the scale (1.0 = 96 DPI) it renders at.</summary>
    public readonly record struct MonitorSample(PixelsRect WorkArea, double DpiScale);

    /// <summary>Device-pixel rectangle where Right/Bottom are exclusive.</summary>
    public readonly record struct PixelsRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }
}
