using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Theming;

namespace R1ng.Launcher;

/// <summary>A replaceable toast: no taskbar entry, no keyboard activation, and no launcher-layout impact.</summary>
internal partial class FloatingNoticeWindow : Window
{
    private readonly TransientNoticeLifetime _lifetime;
    private HwndSource? _source;
    private NoticeAnchor? _anchor;

    public FloatingNoticeWindow()
    {
        InitializeComponent();
        _lifetime = new TransientNoticeLifetime(Schedule, Hide);
        MouseEnter += (_, _) => _lifetime.Pause();
        MouseLeave += (_, _) => _lifetime.Resume();
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessageHook);
        };
        SizeChanged += (_, _) => PositionNotice();
    }

    internal void ShowNotice(LauncherNotice notice, NoticeAnchor anchor, AppTheme theme)
    {
        _anchor = anchor;
        MessageText.Text = notice.Message;
        System.Windows.Automation.AutomationProperties.SetName(MessageText, notice.Message);
        UpdateTheme(theme);
        MessageText.SetResourceReference(TextBlock.ForegroundProperty,
            notice.IsFailure ? "FailureTextBrush" : "WarningTextBrush");
        var size = ScreenPlacement.FitSize(360, 260, anchor.Monitor.WorkArea, anchor.Monitor.DpiScale);
        Width = size.Width;
        MaxHeight = size.Height;
        MessageScroll.MaxHeight = Math.Max(1, Math.Min(200, size.Height - 76));
        // Move the hidden HWND to the anchor's monitor first, allowing WPF to adopt its actual DPI.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        ScreenPlacement.TryMoveOwnWindow(handle, anchor.Bounds.Left, anchor.Bounds.Bottom);
        Show();
        UpdateLayout();
        PositionNotice();
        _lifetime.Show();
        if (IsMouseOver) _lifetime.Pause();
    }

    internal void UpdateTheme(AppTheme theme) => ThemeManager.Apply(theme, Resources);
    private void OnDismissClick(object sender, RoutedEventArgs e) => _lifetime.Dismiss();

    private IDisposable Schedule(TimeSpan delay, Action elapsed) => new NoticeTimer(Dispatcher, delay, elapsed);

    private void PositionNotice()
    {
        if (_anchor is not { } anchor || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
        var handle = new WindowInteropHelper(this).Handle;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (x, y) = Place(anchor, (int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale));
        ScreenPlacement.TryMoveOwnWindow(handle, x, y);
    }

    internal static (int X, int Y) Place(NoticeAnchor anchor, int width, int height)
    {
        var work = anchor.Monitor.WorkArea;
        var gap = (int)Math.Round(8 * anchor.Monitor.DpiScale);
        var x = Math.Clamp((long)anchor.Bounds.Right - width, work.Left, Math.Max((long)work.Left, (long)work.Right - width));
        var below = (long)anchor.Bounds.Bottom + gap;
        var y = below + height <= work.Bottom ? below : (long)anchor.Bounds.Top - height - gap;
        y = Math.Clamp(y, work.Top, Math.Max((long)work.Top, (long)work.Bottom - height));
        return ((int)x, (int)y);
    }

    // ShowActivated=False only prevents initial activation; WM_MOUSEACTIVATE also prevents clicks
    // on the close button from taking keyboard focus and hiding the launcher/closing QuickLook.
    internal static IntPtr MouseActivationResult(int message, ref bool handled)
    {
        if (message != NativeMethods.WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(NativeMethods.MaNoActivate);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        MouseActivationResult(message, ref handled);

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        base.OnClosed(e);
    }

    private sealed class NoticeTimer : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _elapsed;
        public NoticeTimer(Dispatcher dispatcher, TimeSpan delay, Action elapsed)
        {
            _elapsed = elapsed;
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        private void OnTick(object? sender, EventArgs e)
        {
            Dispose();
            _elapsed();
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
