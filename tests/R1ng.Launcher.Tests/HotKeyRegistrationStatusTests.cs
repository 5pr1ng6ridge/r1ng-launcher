using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The presentation of the hotkey registration outcome must stay honest: a fallback is never shown
/// as the preferred hotkey, and a total failure always carries a reason plus a usable exit route.
/// </summary>
public class HotKeyRegistrationStatusTests
{
    [Fact]
    public void Preferred_hotkey_is_reported_without_a_notice()
    {
        var status = HotKeyRegistrationStatus.From(true, HotKeyDefinition.AltSpace, failureMessage: null);

        Assert.Equal("Alt + Space", status.Summary);
        Assert.Null(status.Notice);
        Assert.False(status.IsFailure);
    }

    [Fact]
    public void A_successful_fallback_is_reported_honestly_and_never_as_the_preferred_hotkey()
    {
        const string failure = "Alt + Space 已被占用，已改用 Ctrl + Shift + Space";
        var status = HotKeyRegistrationStatus.From(true, HotKeyDefinition.CtrlShiftSpace, failure);

        Assert.Contains("Ctrl + Shift + Space", status.Summary);
        Assert.DoesNotContain("已改用 Alt", status.Summary);
        Assert.Equal(failure, status.Notice);
        Assert.False(status.IsFailure);
    }

    [Fact]
    public void A_total_failure_is_flagged_with_the_reason_and_the_ui_exit_route()
    {
        const string failure = "Alt + Space 和 Ctrl + Shift + Space 均注册失败，可能被其他程序占用";
        var status = HotKeyRegistrationStatus.From(false, active: null, failure);

        Assert.True(status.IsFailure);
        Assert.Equal(failure, status.Summary);
        Assert.NotNull(status.Notice);
        Assert.Contains(failure, status.Notice);
        Assert.Contains(HotKeyRegistrationStatus.UnavailableNotice, status.Notice);
    }

    [Fact]
    public void A_missing_failure_message_still_produces_an_actionable_notice()
    {
        var status = HotKeyRegistrationStatus.From(false, active: null, failureMessage: null);

        Assert.True(status.IsFailure);
        Assert.Equal("注册失败", status.Summary);
        Assert.NotNull(status.Notice);
        Assert.Contains(HotKeyRegistrationStatus.UnavailableNotice, status.Notice);
    }
}
