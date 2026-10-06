using System.Runtime.InteropServices;

namespace R1ng.Launcher.Platform;

/// <summary>Win32 declarations for hotkeys, monitor placement and shell icons.</summary>
internal static class NativeMethods
{
    // Hotkey modifiers
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    public const int WmHotKey = 0x0312;

    public const uint MonitorDefaultToPrimary = 0x00000001;
    public const uint MonitorDefaultToNearest = 0x00000002;

    // SetWindowPos flags
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public const uint SpiGetWorkArea = 0x0030;
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;

    /// <summary>GetDpiForMonitor: the DPI the monitor effectively renders at (includes user scaling).</summary>
    public const int MdtEffectiveDpi = 0;

    public const uint DefaultDpi = 96;

    // SHGetFileInfo flags
    public const uint ShgfiIcon = 0x000000100;
    public const uint ShgfiLargeIcon = 0x000000000;
    public const uint ShgfiSmallIcon = 0x000000001;
    public const uint ShgfiUseFileAttributes = 0x000000010;
    public const uint ShgfiPidl = 0x000000008;

    public const int FileAttributeDirectory = 0x00000010;
    public const int FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoW(uint action, uint param, ref Rect rect, uint winIni);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        string path,
        int fileAttributes,
        ref ShellFileInfo fileInfo,
        int fileInfoSize,
        uint flags
    );

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        IntPtr pidl,
        int fileAttributes,
        ref ShellFileInfo fileInfo,
        int fileInfoSize,
        uint flags
    );

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut
    );

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconExW(
        string file,
        int iconIndex,
        IntPtr[]? largeIcons,
        IntPtr[]? smallIcons,
        uint iconCount
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();
}
