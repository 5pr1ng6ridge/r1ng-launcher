using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>Deterministic tray lifecycle/menu seam; it never calls the native notification-area API.</summary>
internal sealed class TestLauncherTrayIcon(bool initializeResult = true) : ILauncherTrayIcon
{
    private Action<bool, string?>? _availabilityChanged;

    public event Action? ShowRequested;
    public event Action? ManageHiddenRequested;
    public event Action? QuitRequested;
    public event Action<bool, string?>? AvailabilityChanged
    {
        add => _availabilityChanged += value;
        remove => _availabilityChanged -= value;
    }

    public int AvailabilityChangedSubscribers => _availabilityChanged?.GetInvocationList().Length ?? 0;
    public string? FailureMessage { get; private set; }
    public bool InitializeResult { get; set; } = initializeResult;
    public int InitializeCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public IntPtr WindowHandle { get; private set; }
    public bool IsDisposed => DisposeCalls > 0;

    public bool Initialize(IntPtr windowHandle)
    {
        InitializeCalls++;
        WindowHandle = windowHandle;
        FailureMessage = InitializeResult ? null : "injected tray initialization failure";
        return InitializeResult;
    }

    public bool HandleWindowMessage(int message, IntPtr wParam, IntPtr lParam) => false;

    public void RaiseShow() => ShowRequested?.Invoke();
    public void RaiseManageHidden() => ManageHiddenRequested?.Invoke();
    public void RaiseQuit() => QuitRequested?.Invoke();

    public void SimulateTaskbarRestart(bool addSucceeded, string? failureMessage = null)
    {
        InitializeResult = addSucceeded;
        FailureMessage = addSucceeded ? null : failureMessage ?? "injected taskbar re-registration failure";
        _availabilityChanged?.Invoke(addSucceeded, FailureMessage);
    }

    public void Dispose() => DisposeCalls++;
}
