using System.Windows.Input;

namespace R1ng.Launcher.Platform;

/// <summary>A global hotkey definition (modifiers + virtual key).</summary>
public readonly record struct HotKeyDefinition(uint Modifiers, uint VirtualKey, string DisplayName)
{
    public static HotKeyDefinition AltSpace { get; } = new(
        NativeMethods.ModAlt | NativeMethods.ModNoRepeat,
        (uint)KeyInterop.VirtualKeyFromKey(Key.Space),
        "Alt + Space"
    );

    public static HotKeyDefinition CtrlShiftSpace { get; } = new(
        NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
        (uint)KeyInterop.VirtualKeyFromKey(Key.Space),
        "Ctrl + Shift + Space"
    );

    /// <summary>The independent file-scope shortcut. Modifiers are Alt+Ctrl+NoRepeat (0x4003).</summary>
    public static HotKeyDefinition CtrlAltSpace { get; } = new(
        NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat,
        (uint)KeyInterop.VirtualKeyFromKey(Key.Space),
        "Ctrl + Alt + Space"
    );
}

/// <summary>
/// Registers system-wide hotkeys on an existing window handle. The launcher toggle (Alt + Space with
/// a Ctrl + Shift + Space fallback) and the independent file-scope shortcut (Ctrl + Alt + Space) use
/// separate ids, so one failing (or being disposed) never affects the other.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    /// <summary>Id of the preferred launcher toggle hotkey.</summary>
    public const int LauncherHotKeyId = 0x5B01;

    /// <summary>Id of the launcher toggle fallback hotkey.</summary>
    public const int LauncherFallbackHotKeyId = 0x5B02;

    /// <summary>Id of the independent file-scope hotkey.</summary>
    public const int FileHotKeyId = 0x5B03;

    private readonly IntPtr _windowHandle;
    private readonly HashSet<int> _registered = new();
    private bool _disposed;

    public GlobalHotKey(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>The launcher-toggle combination that is actually active, or null when registration failed.</summary>
    public HotKeyDefinition? Active { get; private set; }

    /// <summary>The file-scope combination that is actually active, or null when registration failed.</summary>
    public HotKeyDefinition? FileActive { get; private set; }

    public string? FailureMessage { get; private set; }

    public string? FileFailureMessage { get; private set; }

    /// <summary>Tries the preferred launcher combination, then the fallback.</summary>
    public bool RegisterLauncher(HotKeyDefinition primary, HotKeyDefinition? fallback = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Register(LauncherHotKeyId, primary))
        {
            Active = primary;
            FailureMessage = null;
            return true;
        }

        if (fallback is { } alternative && Register(LauncherFallbackHotKeyId, alternative))
        {
            Active = alternative;
            FailureMessage = $"{primary.DisplayName} 已被占用，已改用 {alternative.DisplayName}";
            return true;
        }

        Active = null;
        FailureMessage = fallback is { } alsoTried
            ? $"{primary.DisplayName} 和 {alsoTried.DisplayName} 均注册失败，可能被其他程序占用"
            : $"{primary.DisplayName} 注册失败，可能被其他程序占用";
        return false;
    }

    /// <summary>
    /// Registers the independent file-scope shortcut. Failure is reported through
    /// <see cref="FileFailureMessage"/> and never affects the launcher toggle.
    /// </summary>
    public bool RegisterFileHotKey(HotKeyDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Register(FileHotKeyId, definition))
        {
            FileActive = definition;
            FileFailureMessage = null;
            return true;
        }

        FileActive = null;
        FileFailureMessage = $"{definition.DisplayName} 注册失败，可能被其他程序占用";
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var id in _registered)
            NativeMethods.UnregisterHotKey(_windowHandle, id);

        _registered.Clear();
    }

    private bool Register(int id, HotKeyDefinition definition)
    {
        if (!NativeMethods.RegisterHotKey(_windowHandle, id, definition.Modifiers, definition.VirtualKey))
            return false;

        _registered.Add(id);
        return true;
    }
}
