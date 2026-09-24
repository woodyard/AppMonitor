using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The user is asked to close an application only right before its install begins, and nothing is closed for them
/// any earlier either. Installs run one at a time; the blocking check, the prompt and any forced close happen when an
/// update holds the install lock (<see cref="PolicyEngine.DecideAtTurn"/>), and the policy tick holds its own close
/// prompt back while an install is ahead (<see cref="PolicyEngine.HoldPromptForTurn"/>). Before this, "Update all"
/// asked for every application at once and the user waited, application closed, while the others installed.
/// </summary>
public class InstallTurnTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 19, 34, 0, TimeSpan.Zero);

    private static PendingUpdate Update(Action<PendingUpdate>? configure = null)
    {
        var u = new PendingUpdate
        {
            AppId = "firefox",
            DisplayName = "Mozilla Firefox",
            InstalledVersion = "156.0",
            AvailableVersion = "156.0.1",
            Source = UpdateSource.Winget,
            Context = InstallContext.System,
            State = UpdateState.Scheduled,
            FirstDetectedUtc = Now.AddMinutes(-1),
            LastSeenUtc = Now,
            ProcessNames = ["firefox"],
            CloseGracePeriodMinutes = 15,
            InstallRequested = true,
        };
        configure?.Invoke(u);
        return u;
    }

    private static PendingUpdate PastDeadline(Action<PendingUpdate>? configure = null) => Update(x =>
    {
        x.Mandatory = true;
        x.FirstDetectedUtc = Now.AddHours(-30);
        x.DeadlineUtc = Now.AddHours(-6);
        x.ForceCloseAtDeadline = true;
        configure?.Invoke(x);
    });

    // ---------------------------------------------------------------- at the update's turn

    [Fact]
    public void Nothing_in_the_way_installs_whatever_else_is_queued()
    {
        Assert.Equal(TurnAction.Install, PolicyEngine.DecideAtTurn(Update(), Now, blockingProcessesRunning: false, anotherInstallReady: false));
        Assert.Equal(TurnAction.Install, PolicyEngine.DecideAtTurn(Update(), Now, blockingProcessesRunning: false, anotherInstallReady: true));
    }

    [Fact]
    public void A_blocked_update_asks_only_when_nothing_else_would_install_first()
    {
        Assert.Equal(TurnAction.PromptClose, PolicyEngine.DecideAtTurn(Update(), Now, blockingProcessesRunning: true, anotherInstallReady: false));
    }

    [Fact]
    public void A_blocked_update_behind_ready_installs_gives_up_its_turn_instead_of_asking()
    {
        // "Update all" with Firefox open: the others install first, the prompt comes when Firefox is next for real.
        Assert.Equal(TurnAction.Yield, PolicyEngine.DecideAtTurn(Update(), Now, blockingProcessesRunning: true, anotherInstallReady: true));
    }

    [Fact]
    public void Close_apps_and_update_closes_at_the_turn_even_with_others_queued()
    {
        // The user has already been asked and has already agreed; this update holds the turn, so it goes now.
        var u = Update(x => PolicyEngine.RequestForcedClose(x, Now.AddSeconds(-20)));

        Assert.Equal(TurnAction.CloseAndInstall, PolicyEngine.DecideAtTurn(u, Now, blockingProcessesRunning: true, anotherInstallReady: false));
        Assert.Equal(TurnAction.CloseAndInstall, PolicyEngine.DecideAtTurn(u, Now, blockingProcessesRunning: true, anotherInstallReady: true));
    }

    [Fact]
    public void Deadline_enforcement_closes_only_at_the_turn_and_only_after_the_grace_period()
    {
        var running = PastDeadline(x => x.ForceCloseAtUtc = Now.AddMinutes(5));
        Assert.Equal(TurnAction.PromptClose, PolicyEngine.DecideAtTurn(running, Now, blockingProcessesRunning: true, anotherInstallReady: false));
        Assert.Equal(TurnAction.Yield, PolicyEngine.DecideAtTurn(running, Now, blockingProcessesRunning: true, anotherInstallReady: true));

        var elapsed = PastDeadline(x => x.ForceCloseAtUtc = Now.AddMinutes(-1));
        Assert.Equal(TurnAction.CloseAndInstall, PolicyEngine.DecideAtTurn(elapsed, Now, blockingProcessesRunning: true, anotherInstallReady: true));
    }

    [Fact]
    public void A_new_automatic_update_with_its_app_open_is_never_closed_by_the_service()
    {
        // The Firefox case on the Surface Laptop: AutoInstall on (DefaultAutoInstall in the organization config), not
        // mandatory, no deadline, detected a minute ago. Nothing but the user's own "Close apps and update" can make
        // the service end Firefox; without it the answer at the turn is to ask.
        var u = Update(x => { x.State = UpdateState.Available; x.InstallRequested = false; x.AutoInstall = true; });

        Assert.Equal(PolicyActionKind.PromptClose, PolicyEngine.Decide(u, Now, TimeSpan.FromHours(4), blockingProcessesRunning: true).Kind);
        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
        Assert.Equal(TurnAction.PromptClose, PolicyEngine.DecideAtTurn(u, Now, blockingProcessesRunning: true, anotherInstallReady: false));
    }

    // ---------------------------------------------------------------- who counts as ahead

    [Fact]
    public void Only_an_install_that_would_really_start_counts_as_ahead()
    {
        Assert.True(PolicyEngine.IsReadyToInstall(Update(), Now, blockingProcessesRunning: false));
        Assert.True(PolicyEngine.IsReadyToInstall(Update(x => x.State = UpdateState.Installing), Now, blockingProcessesRunning: true));
        Assert.True(PolicyEngine.IsReadyToInstall(Update(x => PolicyEngine.RequestForcedClose(x, Now)), Now, blockingProcessesRunning: true));
        Assert.True(PolicyEngine.IsReadyToInstall(PastDeadline(x => x.ForceCloseAtUtc = Now.AddMinutes(-1)), Now, blockingProcessesRunning: true));

        // Blocked and waiting for its user: it would only ask, so it holds no one up.
        Assert.False(PolicyEngine.IsReadyToInstall(Update(), Now, blockingProcessesRunning: true));
    }

    [Fact]
    public void Two_blocked_updates_do_not_wait_for_each_other()
    {
        var chrome = Update(x => { x.AppId = "chrome"; x.ProcessNames = ["chrome"]; });
        var firefox = Update();

        var chromeReady = PolicyEngine.IsReadyToInstall(chrome, Now, blockingProcessesRunning: true);
        Assert.Equal(TurnAction.PromptClose, PolicyEngine.DecideAtTurn(firefox, Now, blockingProcessesRunning: true, anotherInstallReady: chromeReady));
    }

    // ---------------------------------------------------------------- the policy tick

    [Fact]
    public void The_tick_holds_its_close_prompt_while_an_install_is_ahead()
    {
        var prompt = new PolicyAction(PolicyActionKind.PromptClose, NotificationKind.CloseApplications);

        Assert.Equal(PolicyActionKind.None, PolicyEngine.HoldPromptForTurn(prompt, installAhead: true).Kind);
        Assert.Equal(prompt, PolicyEngine.HoldPromptForTurn(prompt, installAhead: false));
    }

    [Theory]
    [InlineData(PolicyActionKind.Install)]
    [InlineData(PolicyActionKind.ForceClose)]
    [InlineData(PolicyActionKind.Notify)]
    public void The_tick_holds_back_nothing_but_the_close_prompt(PolicyActionKind kind)
    {
        var action = new PolicyAction(kind);
        Assert.Equal(action, PolicyEngine.HoldPromptForTurn(action, installAhead: true));
    }

    // ---------------------------------------------------------------- licences from an earlier cycle

    [Fact]
    public void A_newer_version_does_not_inherit_the_close_licences_of_the_one_it_replaces()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan("155.0", "156.0");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, Now.AddMinutes(-40));
        var u = state.Values.Single();
        PolicyEngine.RequestInstall(u);
        PolicyEngine.RequestForcedClose(u, Now.AddMinutes(-30));
        u.ForceCloseAtUtc = Now.AddMinutes(-10);

        var newer = Scan("155.0", "156.0.1");
        PolicyEngine.Merge(state, [newer], new HashSet<string> { newer.Key }, Now.AddMinutes(-1));

        Assert.Equal("156.0.1", u.AvailableVersion);
        Assert.Null(u.ForceCloseRequestedUtc);
        Assert.Null(u.ForceCloseAtUtc);
        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
    }

    [Fact]
    public void A_new_cycle_after_an_install_starts_without_close_licences()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan("154.0", "155.0");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, Now.AddDays(-2));
        var u = state.Values.Single();
        PolicyEngine.RequestForcedClose(u, Now.AddDays(-2).AddMinutes(1));
        PolicyEngine.MarkInstalled(u, InstallResult.Ok("done", 0) with { InstalledVersion = "156.0" }, Now.AddDays(-2).AddMinutes(2));
        u.ForceCloseRequestedUtc = Now.AddDays(-2).AddMinutes(1); // as if an older agent had not cleared it

        var next = Scan("155.0", "156.0");
        PolicyEngine.Merge(state, [next], new HashSet<string> { next.Key }, Now.AddMinutes(-1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Null(u.ForceCloseRequestedUtc);
        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
    }

    [Fact]
    public void Timestamps_from_before_the_cycle_began_are_no_licence_and_are_forgotten()
    {
        // A state file written by an older agent that carried both over to a newer version.
        var u = PastDeadline(x =>
        {
            x.FirstDetectedUtc = Now.AddMinutes(-1);
            x.DeadlineUtc = Now.AddMinutes(-1);
            x.ForceCloseRequestedUtc = Now.AddMinutes(-5);
            x.ForceCloseAtUtc = Now.AddMinutes(-3);
        });

        Assert.False(PolicyEngine.HasForceCloseRequest(u, Now));
        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
        Assert.Equal(TurnAction.PromptClose, PolicyEngine.DecideAtTurn(u, Now, blockingProcessesRunning: true, anotherInstallReady: false));

        Assert.True(PolicyEngine.ClearStaleForceClose(u));
        Assert.Null(u.ForceCloseRequestedUtc);
        Assert.Null(u.ForceCloseAtUtc);
        Assert.False(PolicyEngine.ClearStaleForceClose(u));
    }

    [Fact]
    public void Licences_from_this_cycle_are_kept()
    {
        var u = Update(x =>
        {
            x.ForceCloseRequestedUtc = Now.AddSeconds(-10);
            x.ForceCloseAtUtc = Now.AddMinutes(10);
        });

        Assert.False(PolicyEngine.ClearStaleForceClose(u));
        Assert.Equal(Now.AddSeconds(-10), u.ForceCloseRequestedUtc);
        Assert.Equal(Now.AddMinutes(10), u.ForceCloseAtUtc);
        Assert.True(PolicyEngine.HasForceCloseRequest(u, Now));
    }

    private static ScanOutcome Scan(string installed, string available) => new(
        new UpdateCheckResult
        {
            AppId = "firefox",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = installed,
            AvailableVersion = available,
            UpdateAvailable = true,
            WingetId = "Mozilla.Firefox",
            ResolvedContext = InstallContext.System,
        },
        new AppPolicy { AppId = "firefox", DisplayName = "Mozilla Firefox", WingetId = "Mozilla.Firefox", AutoInstall = true, ProcessNames = ["firefox"] },
        InstallContext.System, null);
}
