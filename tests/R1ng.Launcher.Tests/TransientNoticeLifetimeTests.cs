using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

public sealed class TransientNoticeLifetimeTests
{
    [Fact]
    public void A_notice_expires_after_four_seconds_and_late_callbacks_are_ignored()
    {
        var jobs = new List<Job>();
        var dismissed = 0;
        using var lifetime = new TransientNoticeLifetime((delay, callback) => Add(jobs, delay, callback), () => dismissed++);
        lifetime.Show();
        Assert.True(lifetime.IsVisible);
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(jobs).Delay);
        jobs[0].Fire();
        Assert.False(lifetime.IsVisible);
        Assert.Equal(1, dismissed);
        jobs[0].Fire();
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void Hover_pauses_and_leaving_resumes_only_the_remaining_time()
    {
        var jobs = new List<Job>();
        var now = DateTimeOffset.UtcNow;
        var dismissed = 0;
        using var lifetime = new TransientNoticeLifetime((delay, callback) => Add(jobs, delay, callback), () => dismissed++, () => now);
        lifetime.Show();
        now += TimeSpan.FromSeconds(1.5);
        lifetime.Pause();
        Assert.True(jobs[0].Disposed);
        jobs[0].Fire();
        Assert.Equal(0, dismissed);
        now += TimeSpan.FromMinutes(2);
        lifetime.Resume();
        Assert.Equal(TimeSpan.FromSeconds(2.5), jobs[1].Delay);
        jobs[1].Fire();
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void Replacing_a_notice_restarts_its_lifetime_and_invalidates_the_old_timer()
    {
        var jobs = new List<Job>();
        var dismissed = 0;
        using var lifetime = new TransientNoticeLifetime((delay, callback) => Add(jobs, delay, callback), () => dismissed++);
        lifetime.Show();
        lifetime.Show();
        Assert.True(jobs[0].Disposed);
        Assert.Equal(TimeSpan.FromSeconds(4), jobs[1].Delay);
        jobs[0].Fire();
        Assert.True(lifetime.IsVisible);
        jobs[1].Fire();
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void Replacing_while_hovered_keeps_the_new_notice_paused_for_a_fresh_four_seconds()
    {
        var jobs = new List<Job>();
        using var lifetime = new TransientNoticeLifetime((delay, callback) => Add(jobs, delay, callback), () => { });
        lifetime.Show();
        lifetime.Pause();
        lifetime.Show();
        Assert.Single(jobs);
        lifetime.Resume();
        Assert.Equal(TimeSpan.FromSeconds(4), jobs[1].Delay);
    }

    [Fact]
    public void Manual_dismiss_and_disposal_stop_timers_and_cannot_expire_a_later_notice()
    {
        var jobs = new List<Job>();
        var dismissed = 0;
        var lifetime = new TransientNoticeLifetime((delay, callback) => Add(jobs, delay, callback), () => dismissed++);
        lifetime.Show();
        lifetime.Dismiss();
        lifetime.Show();
        jobs[0].Fire();
        Assert.True(lifetime.IsVisible);
        Assert.Equal(1, dismissed);
        lifetime.Dispose();
        jobs[1].Fire();
        lifetime.Show();
        Assert.Equal(2, jobs.Count);
        Assert.False(lifetime.IsVisible);
        Assert.Equal(1, dismissed);
    }

    private static Job Add(List<Job> jobs, TimeSpan delay, Action callback)
    {
        var job = new Job(delay, callback);
        jobs.Add(job);
        return job;
    }
    private sealed class Job(TimeSpan delay, Action callback) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public bool Disposed { get; private set; }
        public void Fire() => callback(); // Deliberately allows firing a cancelled job.
        public void Dispose() => Disposed = true;
    }
}
