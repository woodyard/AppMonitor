using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The post-install grace period exists for stale readings: an installer that needs a reboot, or a cache that lags.
/// It must not hide a real change. When the install read the new version back from the same source the scan uses
/// and no reboot is pending, a later scan showing a lower version means someone put an older build back, and that is
/// a fresh update to report, not a failure of ours. (Seen on H-PARALLELSVM: Notepad++ updated to 8.9.8 by the agent,
/// 8.9.1 reinstalled by hand five minutes later, nothing reported for two hours.)
/// </summary>
public class PostInstallGraceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static AppPolicy Policy() => new()
    {
        AppId = "notepadplusplus",
        DisplayName = "Notepad++",
        WingetId = "Notepad++.Notepad++",
        AutoInstall = true,
        DeferralOptionsMinutes = [60],
    };

    private static ScanOutcome Scan(string installed, string available = "8.9.8") => new(
        new UpdateCheckResult
        {
            AppId = "notepadplusplus",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = installed,
            AvailableVersion = available,
            UpdateAvailable = true,
            WingetId = "Notepad++.Notepad++",
            ResolvedContext = InstallContext.System,
        },
        Policy(), InstallContext.System, null);

    private static (Dictionary<string, PendingUpdate> state, PendingUpdate u) InstalledAt(DateTimeOffset at, InstallResult result)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan("8.9.1");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, at.AddMinutes(-1));
        var u = state.Values.Single();
        PolicyEngine.MarkInstalled(u, result, at);
        return (state, u);
    }

    [Fact]
    public void A_verified_install_followed_by_a_lower_version_is_a_new_update_not_a_stale_reading()
    {
        var (state, u) = InstalledAt(T0, InstallResult.Ok("done", 0) with { InstalledVersion = "8.9.8" });
        Assert.True(u.InstallVerified);

        var again = Scan("8.9.1");
        var summary = PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0.AddMinutes(5));

        Assert.Equal(1, summary.Updated);
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("8.9.1", u.InstalledVersion);
        Assert.Equal(0, u.FailureCount);
        Assert.False(u.InstallVerified);
    }

    [Fact]
    public void A_scan_that_started_before_the_install_finished_does_not_undo_it()
    {
        // The scan began at T0 and read 8.9.1; the install finished (verified) while it ran; the merge comes after.
        var (state, u) = InstalledAt(T0.AddSeconds(30), InstallResult.Ok("done", 0) with { InstalledVersion = "8.9.8" });
        Assert.True(u.InstallVerified);

        var stale = Scan("8.9.1");
        PolicyEngine.Merge(state, [stale], new HashSet<string> { stale.Key }, T0);

        Assert.Equal(UpdateState.Installed, u.State);
        Assert.Equal("8.9.8", u.InstalledVersion);
        Assert.True(u.InstallVerified);
    }

    [Fact]
    public void An_unverified_install_keeps_the_grace_period()
    {
        // The provider could not read the version back; a stale reading of the old version is still plausible.
        var (state, u) = InstalledAt(T0, InstallResult.Ok("done", 0));
        Assert.False(u.InstallVerified);

        var again = Scan("8.9.1");
        PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0.AddMinutes(5));

        Assert.Equal(UpdateState.Installed, u.State);
    }

    [Fact]
    public void A_pending_reboot_keeps_the_grace_period_even_when_verified()
    {
        var (state, u) = InstalledAt(T0, InstallResult.Ok("done", 0) with { InstalledVersion = "8.9.8", RebootRequired = true });
        Assert.True(u.InstallVerified);
        Assert.True(u.RebootPending);

        var again = Scan("8.9.1");
        PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0.AddMinutes(5));

        Assert.Equal(UpdateState.Installed, u.State);
    }

    [Fact]
    public void After_the_grace_period_the_old_rule_still_counts_a_repeat_as_a_failure()
    {
        var (state, u) = InstalledAt(T0, InstallResult.Ok("done", 0));

        var again = Scan("8.9.1");
        PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0 + PolicyEngine.PostInstallGrace + TimeSpan.FromMinutes(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(1, u.FailureCount);
    }
}
