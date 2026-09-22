using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Whether the "Installing ..." toast is shown is a per-application decision with a global fallback:
/// <c>auto</c> keeps the historical behaviour (only Reminders announces the start of an install), while
/// <c>always</c>/<c>never</c> settle it regardless of the notification style.
/// </summary>
public sealed class NotifyInstallingRuleTests
{
    private static AppPolicy App(NotifyInstallingMode? notify, NotificationMode? mode = null) =>
        new() { AppId = "app", DisplayName = "App", NotifyInstalling = notify, NotificationMode = mode };

    private static AgentSettings Global(NotifyInstallingMode notify, NotificationMode mode) =>
        new() { DefaultNotifyInstalling = notify, NotificationMode = mode };

    [Fact]
    public void Auto_follows_the_notification_style()
    {
        Assert.True(PolicyEngine.NotifyInstallingFor(App(NotifyInstallingMode.Auto), Global(NotifyInstallingMode.Auto, NotificationMode.Reminders)));
        Assert.False(PolicyEngine.NotifyInstallingFor(App(NotifyInstallingMode.Auto), Global(NotifyInstallingMode.Auto, NotificationMode.Quiet)));
    }

    [Fact]
    public void Auto_follows_the_apps_own_notification_style_when_it_sets_one()
    {
        // The app is louder than the global style, so auto means "show it".
        Assert.True(PolicyEngine.NotifyInstallingFor(
            App(NotifyInstallingMode.Auto, NotificationMode.Reminders),
            Global(NotifyInstallingMode.Auto, NotificationMode.Quiet)));
    }

    [Fact]
    public void An_app_set_to_always_wins_over_a_global_never_and_over_quiet()
    {
        Assert.True(PolicyEngine.NotifyInstallingFor(
            App(NotifyInstallingMode.Always),
            Global(NotifyInstallingMode.Never, NotificationMode.Quiet)));
    }

    [Fact]
    public void An_app_set_to_never_wins_over_a_global_always()
    {
        Assert.False(PolicyEngine.NotifyInstallingFor(
            App(NotifyInstallingMode.Never),
            Global(NotifyInstallingMode.Always, NotificationMode.Reminders)));
    }

    [Fact]
    public void An_app_without_a_value_inherits_the_global_always()
    {
        Assert.True(PolicyEngine.NotifyInstallingFor(App(null), Global(NotifyInstallingMode.Always, NotificationMode.Quiet)));
    }

    [Fact]
    public void An_app_without_a_value_inherits_the_global_never()
    {
        Assert.False(PolicyEngine.NotifyInstallingFor(App(null), Global(NotifyInstallingMode.Never, NotificationMode.Reminders)));
    }

    [Fact]
    public void The_defaults_keep_the_old_behaviour()
    {
        // Unconfigured: Quiet plus auto, i.e. no install toast, exactly as before the setting existed.
        Assert.False(PolicyEngine.NotifyInstallingFor(null, new AgentSettings()));
        Assert.True(PolicyEngine.NotifyInstallingFor(null, new AgentSettings { NotificationMode = NotificationMode.Reminders }));
    }
}
