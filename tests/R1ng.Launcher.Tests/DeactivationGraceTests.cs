using R1ng.Launcher.Platform;
using System.Windows.Threading;

namespace R1ng.Launcher.Tests;

public class DeactivationGraceTests
{
    [Fact]
    public void Hide_waits_for_the_grace_period_instead_of_an_early_callback()
    {
        var timer = new FakeGraceTimer();
        var hideCalls = 0;
        using var grace = new DeactivationGrace(timer, () => true, () => false, () => hideCalls++);

        grace.NotifyDeactivated();

        // Nothing happens until the timer actually elapses (the old Background callback ran early
        // and then gave up, leaving the topmost window visible).
        Assert.True(grace.IsPending);
        Assert.True(timer.IsEnabled);
        Assert.Equal(0, hideCalls);

        timer.RaiseElapsed();

        Assert.Equal(1, hideCalls);
        Assert.False(grace.IsPending);
    }

    [Fact]
    public void Context_menu_suppression_defers_then_hides_once_it_closes()
    {
        var timer = new FakeGraceTimer();
        var suppressed = true;
        var hideCalls = 0;
        using var grace = new DeactivationGrace(timer, () => true, () => suppressed, () => hideCalls++);

        grace.NotifyDeactivated();
        timer.RaiseElapsed();

        Assert.Equal(0, hideCalls);
        Assert.True(timer.IsEnabled); // re-armed while the menu is open

        suppressed = false;
        timer.RaiseElapsed();

        Assert.Equal(1, hideCalls);
    }

    [Fact]
    public void An_active_window_is_left_alone()
    {
        var timer = new FakeGraceTimer();
        var hideCalls = 0;
        using var grace = new DeactivationGrace(timer, () => false, () => false, () => hideCalls++);

        grace.NotifyDeactivated();
        timer.RaiseElapsed();

        Assert.Equal(0, hideCalls);
        Assert.False(timer.IsEnabled);
    }

    [Fact]
    public void Cancel_stops_a_pending_hide()
    {
        var timer = new FakeGraceTimer();
        var hideCalls = 0;
        using var grace = new DeactivationGrace(timer, () => true, () => false, () => hideCalls++);

        grace.NotifyDeactivated();
        grace.Cancel();

        Assert.False(grace.IsPending);
        timer.RaiseElapsed();
        Assert.Equal(0, hideCalls);
    }

    [Fact]
    public void Dispose_unsubscribes_and_stops_the_timer()
    {
        var timer = new FakeGraceTimer();
        var hideCalls = 0;
        var grace = new DeactivationGrace(timer, () => true, () => false, () => hideCalls++);

        grace.NotifyDeactivated();
        grace.Dispose();

        Assert.False(timer.IsEnabled);
        Assert.False(timer.HasSubscribers);

        // After disposal, notifications are ignored.
        grace.NotifyDeactivated();
        Assert.False(timer.IsEnabled);
        Assert.Equal(0, hideCalls);
    }

    [Fact]
    public void Dispatcher_timer_wiring_hides_after_the_interval_on_an_sta_thread()
    {
        var hideCalled = new ManualResetEventSlim(false);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var timer = new DispatcherGraceTimer(dispatcher, TimeSpan.FromMilliseconds(30));
                var frame = new DispatcherFrame();
                using var grace = new DeactivationGrace(
                    timer,
                    () => true,
                    () => false,
                    () =>
                    {
                        hideCalled.Set();
                        frame.Continue = false;
                    }
                );

                grace.NotifyDeactivated();

                // Bounded safety valve so a broken timer cannot hang the suite.
                var safety = new DispatcherTimer(
                    TimeSpan.FromSeconds(5),
                    DispatcherPriority.Normal,
                    (_, _) => frame.Continue = false,
                    dispatcher
                );
                safety.Start();
                Dispatcher.PushFrame(frame);
                safety.Stop();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            Assert.True(hideCalled.Wait(TimeSpan.FromSeconds(6)), "the dispatcher timer never fired the hide");
            Assert.Null(failure);
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Deterministic stand-in for the dispatcher timer.</summary>
    private sealed class FakeGraceTimer : IGraceTimer
    {
        public bool IsEnabled { get; private set; }

        public bool HasSubscribers => Elapsed is not null;

        public event EventHandler? Elapsed;

        public void Start() => IsEnabled = true;

        public void Stop() => IsEnabled = false;

        public void Dispose() => IsEnabled = false;

        public void RaiseElapsed()
        {
            if (!IsEnabled)
                return;

            Elapsed?.Invoke(this, EventArgs.Empty);
        }
    }
}
