using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace R1ng.Launcher.Platform;

/// <summary>UI-thread-owned notification-area lifecycle and command seam.</summary>
internal interface ILauncherTrayIcon : IDisposable
{
    event Action? ShowRequested;
    event Action? ManageHiddenRequested;
    event Action? QuitRequested;
    event Action<bool, string?>? AvailabilityChanged;

    string? FailureMessage { get; }

    bool Initialize(IntPtr windowHandle);

    bool HandleWindowMessage(int message, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// Minimal Shell_NotifyIcon implementation. The native notification-area menu is intentionally
/// limited to the existing hidden-items manager and explicit shutdown actions.
/// </summary>
internal sealed class LauncherTrayIcon : ILauncherTrayIcon
{
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int WmApp = 0x8000;
    private const int TrayCallbackMessage = WmApp + 0x36A;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint MenuString = 0x00000000;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackRightButton = 0x0002;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint DefaultSize = 0x0040;
    private const uint MfByPosition = 0x00000400;
    private const uint MfString = 0x00000000;
    private const uint ShowHiddenItemsCommand = 1;
    private const uint QuitCommand = 2;

    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private int _ownerThreadId;
    private bool _added;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ManageHiddenRequested;
    public event Action? QuitRequested;
    public event Action<bool, string?>? AvailabilityChanged;

    public string? FailureMessage { get; private set; }

    public bool Initialize(IntPtr windowHandle)
    {
        EnsureNotDisposed();
        if (_windowHandle != IntPtr.Zero)
            throw new InvalidOperationException("The launcher tray icon is already initialized.");

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _windowHandle = windowHandle;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        _iconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
        if (_iconHandle == IntPtr.Zero)
        {
            FailureMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        if (AddIcon())
            return true;

        DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
        return false;
    }

    public bool HandleWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        EnsureOwnerThread();
        if (_disposed)
            return false;

        if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            if (_iconHandle != IntPtr.Zero)
                AddIcon();
            return true;
        }

        if (message != TrayCallbackMessage)
            return false;

        switch (unchecked((int)lParam.ToInt64()))
        {
            case WmLButtonUp:
            case WmLButtonDoubleClick:
                ShowRequested?.Invoke();
                break;
            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                break;
        }

        return true;
    }

    private bool AddIcon()
    {
        if (_windowHandle == IntPtr.Zero || _iconHandle == IntPtr.Zero)
            return false;

        var data = CreateNotifyData();
        var wasAdded = _added;
        var previousFailure = FailureMessage;
        _added = Shell_NotifyIcon(NimAdd, ref data);
        FailureMessage = _added ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        if (wasAdded != _added || previousFailure != FailureMessage)
            AvailabilityChanged?.Invoke(_added, FailureMessage);
        return _added;
    }

    private NotifyIconData CreateNotifyData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = TrayCallbackMessage,
        IconHandle = _iconHandle,
        Tooltip = "R1ng Launcher",
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = Guid.Empty,
    };

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MfByPosition | MfString, ShowHiddenItemsCommand, "已隐藏");
            AppendMenu(menu, MfByPosition | MfString, QuitCommand, "退出");
            SetForegroundWindow(_windowHandle);
            GetCursorPos(out var point);
            var command = TrackPopupMenu(menu, TrackReturnCommand | TrackRightButton,
                point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
            PostMessage(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == ShowHiddenItemsCommand)
                ManageHiddenRequested?.Invoke();
            else if (command == QuitCommand)
                QuitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        EnsureOwnerThread();
        _disposed = true;
        if (_added && _windowHandle != IntPtr.Zero)
        {
            var data = CreateNotifyData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        _windowHandle = IntPtr.Zero;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LauncherTrayIcon));
    }

    private void EnsureOwnerThread()
    {
        if (_ownerThreadId != 0 && Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("The launcher tray icon must be accessed on its owning UI thread.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr owner, IntPtr rectangle);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
