using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

public sealed class SingleInstanceProfileTests
{
    [Fact]
    public void Default_namespace_is_unchanged_and_profile_names_are_stable_and_opaque()
    {
        Assert.Equal(string.Empty, SingleInstance.InstanceSuffix(null));
        Assert.Equal(string.Empty, SingleInstance.InstanceSuffix(""));
        var first = SingleInstance.InstanceSuffix(@"C:\TEMP\PROFILE");
        Assert.Equal(first, SingleInstance.InstanceSuffix(@"C:\TEMP\PROFILE"));
        Assert.NotEqual(first, SingleInstance.InstanceSuffix(@"C:\TEMP\OTHER"));
        Assert.DoesNotContain("PROFILE", first);
    }

    [Fact]
    public void Separate_profiles_acquire_independent_guards_and_same_profile_signals_only_its_owner()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryAcquire(out var owner, key));
        using (owner)
        using (var received = new ManualResetEventSlim())
        {
            owner.Listen(() => received.Set());
            Assert.True(SingleInstance.TryAcquire(out var other, key + "-other"));
            using (other)
                Assert.False(received.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(SingleInstance.TryAcquire(out var second, key));
            using (second)
                Assert.True(received.Wait(TimeSpan.FromSeconds(2)));
        }
    }
}
