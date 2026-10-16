using R1ng.Launcher.Theming;

namespace R1ng.Launcher.Platform;

internal sealed record LauncherNotice(string Message, bool IsFailure = false);
internal readonly record struct NoticeAnchor(ScreenPlacement.PixelsRect Bounds, ScreenPlacement.MonitorSample Monitor);

internal interface ILauncherNoticePresenter : IDisposable
{
    void Show(LauncherNotice notice, NoticeAnchor anchor, AppTheme theme);
    void UpdateTheme(AppTheme theme);
}

/// <summary>Creates at most one non-activating window, outside the launcher's layout and lifetime of a show.</summary>
internal sealed class LauncherNoticePresenter : ILauncherNoticePresenter
{
    private FloatingNoticeWindow? _window;
    private bool _disposed;
    public void Show(LauncherNotice notice, NoticeAnchor anchor, AppTheme theme)
    {
        if (_disposed) return;
        _window ??= new FloatingNoticeWindow();
        _window.ShowNotice(notice, anchor, theme);
    }

    public void UpdateTheme(AppTheme theme) => _window?.UpdateTheme(theme);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window?.Close();
        _window = null;
    }
}

/// <summary>Tests without an Application never create a notification HWND by accident.</summary>
internal sealed class NullLauncherNoticePresenter : ILauncherNoticePresenter
{
    public void Show(LauncherNotice notice, NoticeAnchor anchor, AppTheme theme) { }
    public void UpdateTheme(AppTheme theme) { }
    public void Dispose() { }
}
