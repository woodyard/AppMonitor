using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// A tracked update must not outlive its application's configuration. Merge never sees an application that is no
/// longer configured, disabled, or rejected by the configuration reader (a "web" application without its URLs), so
/// without a separate pruning step such an update stayed in the state; one that had been scheduled kept the tray at
/// "Preparing updates" forever, because the install flow found no policy to run.
/// </summary>
public class UnconfiguredUpdatePruningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

    private static AppPolicy Policy(string appId, bool enabled = true) => new()
    {
        AppId = appId,
        DisplayName = appId,
        WingetId = $"Vendor.{appId}",
        Enabled = enabled,
        DeferralOptionsMinutes = [60],
    };

    private static PendingUpdate Tracked(Dictionary<string, PendingUpdate> state, string appId, UpdateState updateState)
    {
        var policy = Policy(appId);
        var result = new UpdateCheckResult
        {
            AppId = appId,
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = "1.0",
            AvailableVersion = "2.0",
            UpdateAvailable = true,
            WingetId = policy.WingetId,
            ResolvedContext = InstallContext.User,
        };
        var outcome = new ScanOutcome(result, policy, InstallContext.User, "S-1-5-21-1-2-3-1001");
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0);
        var u = state[outcome.Key];
        u.State = updateState;
        return u;
    }

    [Fact]
    public void A_scheduled_update_of_an_application_that_is_no_longer_configured_is_dropped()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        Tracked(state, "JanDeDobbeleer.OhMyPosh", UpdateState.Scheduled);

        var removed = PolicyEngine.PruneUnconfigured(state, [Policy("7zip")]);

        Assert.Equal(1, removed);
        Assert.Empty(state);
    }

    [Fact]
    public void Updates_of_configured_applications_stay()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        Tracked(state, "7zip", UpdateState.Available);
        Tracked(state, "vlc", UpdateState.Scheduled);

        var removed = PolicyEngine.PruneUnconfigured(state, [Policy("7zip"), Policy("VLC")]);

        Assert.Equal(0, removed);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void The_enabled_list_is_what_counts_so_a_disabled_application_is_treated_as_unconfigured()
    {
        // The coordinator hands over only the enabled applications, exactly as it does for the presence map.
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        Tracked(state, "7zip", UpdateState.Available);

        var enabled = new[] { Policy("7zip", enabled: false) }.Where(p => p.Enabled);
        var removed = PolicyEngine.PruneUnconfigured(state, enabled);

        Assert.Equal(1, removed);
        Assert.Empty(state);
    }

    [Fact]
    public void An_install_in_flight_is_left_alone()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        Tracked(state, "7zip", UpdateState.Installing);

        var removed = PolicyEngine.PruneUnconfigured(state, []);

        Assert.Equal(0, removed);
        Assert.Single(state);
    }
}
