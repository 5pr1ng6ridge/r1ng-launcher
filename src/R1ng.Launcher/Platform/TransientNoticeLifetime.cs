namespace R1ng.Launcher.Platform;

/// <summary>One replaceable notice, with a bounded lifetime and stale-timer protection.</summary>
internal sealed class TransientNoticeLifetime : IDisposable
{
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action _dismiss;
    private IDisposable? _timer;
    private DateTimeOffset _armedAt;
    private TimeSpan _remaining;
    private long _generation;
    private bool _paused;
    private bool _disposed;

    public TransientNoticeLifetime(Func<TimeSpan, Action, IDisposable> schedule, Action dismiss,
        Func<DateTimeOffset>? now = null)
    {
        _schedule = schedule;
        _dismiss = dismiss;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsVisible { get; private set; }

    public void Show()
    {
        if (_disposed) return;
        CancelTimer();
        IsVisible = true;
        _remaining = Duration;
        if (!_paused) Arm();
    }

    public void Pause()
    {
        if (_disposed || _paused) return;
        _paused = true;
        if (_timer is not null)
            _remaining = TimeSpan.FromTicks(Math.Max(0, (_remaining - (_now() - _armedAt)).Ticks));
        CancelTimer();
    }

    public void Resume()
    {
        if (_disposed || !_paused) return;
        _paused = false;
        if (IsVisible) Arm();
    }

    public void Dismiss()
    {
        if (_disposed || !IsVisible) return;
        CancelTimer();
        IsVisible = false;
        _paused = false;
        _dismiss();
    }

    private void Arm()
    {
        if (_remaining <= TimeSpan.Zero) { Dismiss(); return; }
        var generation = ++_generation;
        _armedAt = _now();
        _timer = _schedule(_remaining, () =>
        {
            if (!_disposed && !_paused && IsVisible && generation == _generation)
                Dismiss();
        });
    }

    private void CancelTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTimer();
        IsVisible = false;
    }
}
