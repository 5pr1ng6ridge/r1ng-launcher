using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The bounded STA helper must keep ownership of its completion signal. A worker that finishes after
/// the caller already stopped waiting must be able to complete normally — with the old
/// <c>using ManualResetEventSlim</c> pattern its late <c>Set()</c> threw an unhandled
/// <see cref="ObjectDisposedException"/> and crashed the process.
/// </summary>
public class BoundedStaCallTests
{
    [Fact]
    public async Task Work_that_finishes_after_the_timeout_completes_without_touching_a_disposed_signal()
    {
        using var gate = new ManualResetEventSlim(false);

        var outcome = BoundedStaCall.Run(
            () =>
            {
                // Blocks well past the bounded wait, then completes normally.
                gate.Wait(TimeSpan.FromSeconds(5));
                return "late";
            },
            TimeSpan.FromMilliseconds(30),
            "R1ng test late completion"
        );

        // The caller stopped waiting and returned no value; the worker is still running.
        Assert.False(outcome.Completed);
        Assert.Null(outcome.Value);

        // Release the worker after the caller already returned. Its completion must be a safe no-op.
        gate.Set();

        var value = await outcome.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("late", value);
    }

    [Fact]
    public async Task A_completing_worker_is_observed_exactly_once()
    {
        var outcome = BoundedStaCall.Run(() => 7, TimeSpan.FromSeconds(5), "R1ng test fast");

        Assert.True(outcome.Completed);
        Assert.Equal(7, outcome.Value);
        Assert.Equal(7, await outcome.Completion);
    }

    [Fact]
    public void The_worker_runs_on_a_background_STA_thread()
    {
        ApartmentState? apartment = null;
        var background = false;

        var outcome = BoundedStaCall.Run(
            () =>
            {
                apartment = Thread.CurrentThread.GetApartmentState();
                background = Thread.CurrentThread.IsBackground;
                return 0;
            },
            TimeSpan.FromSeconds(5),
            "R1ng test apartment"
        );

        Assert.True(outcome.Completed);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(background, "a stuck COM call must not keep the process alive");
    }

    [Fact]
    public async Task A_throwing_worker_yields_the_default_value_instead_of_crashing()
    {
        var outcome = BoundedStaCall.Run<int>(
            () => throw new InvalidOperationException("boom"),
            TimeSpan.FromSeconds(5),
            "R1ng test fault"
        );

        Assert.True(outcome.Completed);
        Assert.Equal(0, outcome.Value);
        Assert.Equal(0, await outcome.Completion);
    }

    [Fact]
    public void A_negative_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedStaCall.Run(() => 0, TimeSpan.FromMilliseconds(-1), "R1ng test bad timeout")
        );
    }
}
