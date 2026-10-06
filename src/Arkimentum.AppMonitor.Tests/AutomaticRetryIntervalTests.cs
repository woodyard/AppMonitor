using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// H-SURFACELAP5 (2026-10-06): a failed update went back to "available" at every scan, and the tray's scans came seconds
/// apart, so all three automatic retries of one Firefox update failed within three minutes. An automatic retry of the
/// same version now waits <see cref="PolicyEngine.AutomaticRetryInterval"/> after the failure (added after 1.1.46);
/// "Install now" does not.
/// </summary>
public class AutomaticRetryIntervalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 18, 50, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private static readonly AppPolicy Policy = new() { AppId = "firefox", DisplayName = "Mozilla Firefox", WingetId = "Mozilla.Firefox", AutoInstall = true };

    private static ScanOutcome Outcome(string available = "157.0.1") => new(new UpdateCheckResult
    {
        AppId = "firefox",
        Source = UpdateSource.Winget,
        IsInstalled = true,
        InstalledVersion = "157.0.0.0",
        AvailableVersion = available,
        UpdateAvailable = true,
        WingetId = "Mozilla.Firefox",
        ResolvedContext = InstallContext.User,
    }, Policy, InstallContext.User, "S-1-5-21-1");

    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) FailedAt(DateTimeOffset failed)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = Outcome();
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0);
        var u = state.Values.Single();
        PolicyEngine.MarkFailed(u, "Installer failed with exit code: 0x80070002", failed);
        return (state, u);
    }

    private static void Scan(Dictionary<string, PendingUpdate> state, DateTimeOffset at, string available = "157.0.1")
    {
        var outcome = Outcome(available);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, at);
    }

    [Fact]
    public void The_failure_time_is_recorded()
    {
        var (_, u) = FailedAt(T0.AddMinutes(2));

        Assert.Equal(T0.AddMinutes(2), u.FailedAtUtc);
        Assert.Equal(1, u.FailureCount);
    }

    [Fact]
    public void A_scan_a_minute_after_the_failure_does_not_retry_it()
    {
        var failed = T0.AddMinutes(2);
        var (state, u) = FailedAt(failed);

        Scan(state, failed.AddMinutes(1));

        Assert.Equal(UpdateState.Failed, u.State);
        Assert.NotEqual(PolicyActionKind.Install, PolicyEngine.Decide(u, failed.AddMinutes(1), Interval, false).Kind);
    }

    [Fact]
    public void A_scan_after_the_interval_retries_it()
    {
        var failed = T0.AddMinutes(2);
        var (state, u) = FailedAt(failed);

        Scan(state, failed.Add(PolicyEngine.AutomaticRetryInterval).AddMinutes(-1));
        Assert.Equal(UpdateState.Failed, u.State);

        var at = failed.Add(PolicyEngine.AutomaticRetryInterval);
        Scan(state, at);
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, at, Interval, false).Kind);
    }

    [Fact]
    public void An_hourly_scan_still_retries_at_every_turn()
    {
        // The scan's time is its start; the install fails some minutes later, and the next scan is an hour after the first.
        Assert.True(PolicyEngine.AutomaticRetryInterval <= TimeSpan.FromMinutes(60) - TimeSpan.FromMinutes(10));
        var (state, u) = FailedAt(T0.AddMinutes(10));

        Scan(state, T0.AddHours(1));

        Assert.Equal(UpdateState.Available, u.State);
    }

    [Fact]
    public void Install_now_retries_at_once()
    {
        var failed = T0.AddMinutes(2);
        var (_, u) = FailedAt(failed);

        PolicyEngine.RequestInstall(u);

        Assert.Equal(UpdateState.Scheduled, u.State);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, failed.AddSeconds(30), Interval, false).Kind);
    }

    [Fact]
    public void A_newer_version_is_offered_at_once()
    {
        var failed = T0.AddMinutes(2);
        var (state, u) = FailedAt(failed);

        Scan(state, failed.AddMinutes(1), available: "157.0.2");

        Assert.Equal((UpdateState.Available, 0), (u.State, u.FailureCount));
    }

    [Fact]
    public void A_failure_recorded_by_an_older_agent_is_retried_as_before()
    {
        var (state, u) = FailedAt(T0.AddMinutes(2));
        u.FailedAtUtc = null; // a state file without the field

        Scan(state, T0.AddMinutes(3));

        Assert.Equal(UpdateState.Available, u.State);
    }
}
