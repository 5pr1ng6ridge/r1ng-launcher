using System.Windows.Threading;

namespace R1ng.Launcher.Platform;

/// <summary>
/// One-shot deferral timer abstraction so <see cref="DeactivationGrace"/> can be unit-tested with a
/// deterministic fake instead of a live WPF dispatcher.
/// </summary>
public interface IGraceTimer : IDisposable
{
    /// <summary>True while the deferral is armed and waiting to elapse.</summary>
    bool IsEnabled { get; }

    event EventHandler? Elapsed;

    void Start();

    void Stop();
}

/// <summary>Production timer: a <see cref="DispatcherTimer"/> owned by the window's dispatcher.</summary>
public sealed class DispatcherGraceTimer : IGraceTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherGraceTimer(Dispatcher dispatcher, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => Elapsed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsEnabled => _timer.IsEnabled;

    public event EventHandler? Elapsed;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}

/// <summary>
/// Reliable time-based deferral for "hide when the launcher loses focus".
/// <para>
/// A dispatcher callback at <see cref="DispatcherPriority.Background"/> runs as soon as the queue is
/// idle, i.e. almost always *before* the grace period is over. Checking the elapsed time and then
/// returning would silently drop the hide. This type arms a timer for the full grace period instead,
/// and when it elapses it always hides the window if it is still inactive — re-arming while a
/// suppression condition (an open context menu) is active, so the hide happens right after the menu
/// closes. The logic is free of WPF types (all decisions are injected delegates) so it is directly
/// testable.
/// </para>
/// </summary>
public sealed class DeactivationGrace : IDisposable
{
    private readonly IGraceTimer _timer;
    private readonly Func<bool> _shouldHide;
    private readonly Func<bool> _isSuppressed;
    private readonly Action _hide;
    private bool _disposed;

    /// <param name="timer">The deferral timer.</param>
    /// <param name="shouldHide">True when hiding is warranted at all (window visible and not active).</param>
    /// <param name="isSuppressed">True while hiding must be deferred (an open context menu).</param>
    /// <param name="hide">The action that hides the window.</param>
    public DeactivationGrace(IGraceTimer timer, Func<bool> shouldHide, Func<bool> isSuppressed, Action hide)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _shouldHide = shouldHide ?? throw new ArgumentNullException(nameof(shouldHide));
        _isSuppressed = isSuppressed ?? throw new ArgumentNullException(nameof(isSuppressed));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
        _timer.Elapsed += OnElapsed;
    }

    /// <summary>True while a hide is pending (the grace period is running).</summary>
    public bool IsPending => _timer.IsEnabled;

    /// <summary>Arms (or re-arms) the grace period. Call this from the window's Deactivated event.</summary>
    public void NotifyDeactivated()
    {
        if (_disposed)
            return;

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Cancels a pending hide, e.g. when the window is shown again.</summary>
    public void Cancel()
    {
        if (_disposed)
            return;

        _timer.Stop();
    }

    private void OnElapsed(object? sender, EventArgs e)
    {
        _timer.Stop();

        if (_disposed || !_shouldHide())
            return;

        // A context menu keeps the window "inactive"; defer again and hide once it closes.
        if (_isSuppressed())
        {
            _timer.Start();
            return;
        }

        _hide();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Elapsed -= OnElapsed;
        _timer.Stop();
        _timer.Dispose();
    }
}
