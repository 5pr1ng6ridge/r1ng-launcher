namespace R1ng.Launcher.Platform;

/// <summary>
/// Single-instance guard. The first instance owns a named mutex and a named event; later instances
/// signal that event (which brings the existing launcher forward) and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\R1ngLauncher.SingleInstance";
    private const string ShowEventName = @"Local\R1ngLauncher.ShowRequested";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private string _showEventName = ShowEventName;
    private CancellationTokenSource? _listenerCancellation;
    private Thread? _listenerThread;

    public bool IsOwner { get; private set; }

    /// <summary>Acquires ownership, or signals the running instance when one exists.</summary>
    public static bool TryAcquire(out SingleInstance instance, string? instanceKey = null)
    {
        var suffix = InstanceSuffix(instanceKey);
        instance = new SingleInstance { _showEventName = ShowEventName + suffix };
        instance._mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out var createdNew);
        instance.IsOwner = createdNew;

        if (createdNew)
            return true;

        // Another instance is already running: ask it to show itself.
        try
        {
            if (EventWaitHandle.TryOpenExisting(instance._showEventName, out var existing))
            {
                using (existing)
                    existing.Set();
            }
        }
        catch (Exception)
        {
            // Not being able to signal is fine; the new process just exits.
        }

        return false;
    }

    // An explicit separate data directory is its own profile. The ordinary profile keeps the exact
    // original names, so second launches still signal older resident builds rather than taking over.
    internal static string InstanceSuffix(string? instanceKey) => string.IsNullOrEmpty(instanceKey)
        ? string.Empty : "." + Core.LaunchIdentity.Digest(instanceKey);

    /// <summary>Starts listening for show requests from later instances.</summary>
    public void Listen(Action onShowRequested)
    {
        if (!IsOwner)
            return;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _showEventName);
        _listenerCancellation = new CancellationTokenSource();
        var token = _listenerCancellation.Token;
        var showEvent = _showEvent;

        _listenerThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!showEvent.WaitOne(TimeSpan.FromMilliseconds(250)))
                        continue;

                    if (token.IsCancellationRequested)
                        return;

                    onShowRequested();
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "R1ng single-instance listener",
        };

        _listenerThread.SetApartmentState(ApartmentState.MTA);
        _listenerThread.Start();
    }

    public void Dispose()
    {
        try
        {
            _listenerCancellation?.Cancel();
            if (_listenerThread is { IsAlive: true })
                _listenerThread.Join(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
            // Shutting down; nothing to report.
        }

        _listenerCancellation?.Dispose();
        _showEvent?.Dispose();

        if (_mutex is not null)
        {
            try
            {
                if (IsOwner)
                    _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was not owned by this thread; ignore.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
