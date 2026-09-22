using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// A running deferral blocks further deferrals. Before this, every click on "Defer 1 hour" went through: each one
/// moved the end of the deferral and used up another of the allowed deferrals, because the rule only looked at the
/// state, the deadline and the count. The same rule hides the buttons in the tray, the toast and the close-apps
/// dialog, so the service refusal is the backstop, not the user's experience.
/// </summary>
public class DeferWhileDeferredTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static PendingUpdate Update(int maxDeferrals = 3) => new()
    {
        AppId = "app",
        DisplayName = "App",
        Context = InstallContext.System,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        State = UpdateState.Available,
        MaxDeferrals = maxDeferrals,
        DeferralOptionsMinutes = [60, 240],
    };

    [Fact]
    public void A_second_deferral_during_the_first_is_refused_and_counted_once()
    {
        var u = Update();

        Assert.True(PolicyEngine.TryDefer(u, 60, T0, out _));
        Assert.Equal(1, u.DeferralCount);
        Assert.Equal(T0.AddMinutes(60), u.DeferredUntilUtc);

        Assert.False(u.CanDefer(T0.AddMinutes(5)));
        Assert.False(PolicyEngine.TryDefer(u, 60, T0.AddMinutes(5), out var reason));
        Assert.Contains("Already deferred until", reason);
        Assert.Equal(1, u.DeferralCount);
        Assert.Equal(T0.AddMinutes(60), u.DeferredUntilUtc);
    }

    [Fact]
    public void Deferring_is_allowed_again_once_the_period_is_over()
    {
        var u = Update();
        Assert.True(PolicyEngine.TryDefer(u, 60, T0, out _));

        Assert.True(u.CanDefer(T0.AddMinutes(60)));
        Assert.True(PolicyEngine.TryDefer(u, 240, T0.AddMinutes(61), out _));
        Assert.Equal(2, u.DeferralCount);
        Assert.Equal(T0.AddMinutes(61 + 240), u.DeferredUntilUtc);
    }

    [Fact]
    public void An_expired_deferral_does_not_block_when_deferrals_are_unlimited()
    {
        var u = Update(maxDeferrals: 0);
        Assert.True(PolicyEngine.TryDefer(u, 60, T0, out _));

        Assert.False(u.CanDefer(T0.AddMinutes(59)));
        Assert.True(u.CanDefer(T0.AddMinutes(60)));
    }
}
