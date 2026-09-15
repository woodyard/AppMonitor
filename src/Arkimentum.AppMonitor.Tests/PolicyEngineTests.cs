using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class PolicyEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private static AppPolicy Policy(bool mandatory = false, int deadlineHours = 0, int maxDeferrals = 3, bool autoInstall = false, params string[] processes) => new()
    {
        AppId = "app",
        DisplayName = "App",
        WingetId = "Vendor.App",
        Mandatory = mandatory,
        DeadlineHours = deadlineHours,
        MaxDeferrals = maxDeferrals,
        AutoInstall = autoInstall,
        ProcessNames = processes,
        DeferralOptionsMinutes = [60, 240, 1440],
        CloseGracePeriodMinutes = 15,
        ForceCloseAtDeadline = true,
    };

    private static UpdateCheckResult Result(string installed = "1.0", string? available = "2.0") => new()
    {
        AppId = "app",
        Source = UpdateSource.Winget,
        IsInstalled = true,
        InstalledVersion = installed,
        AvailableVersion = available,
        UpdateAvailable = available is not null,
        WingetId = "Vendor.App",
        ResolvedContext = InstallContext.System,
    };

    private static (Dictionary<string, PendingUpdate> state, PendingUpdate u) Detect(AppPolicy policy, DateTimeOffset at)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = new ScanOutcome(Result(), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, at);
        Assert.Equal(1, summary.Added);
        return (state, state.Values.Single());
    }

    [Fact]
    public void Merge_adds_new_update_with_deadline_for_mandatory_app()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 72), T0);
        Assert.Equal(UpdateState.Available, u.State);
        Assert.True(u.Mandatory);
        Assert.Equal(T0.AddHours(72), u.DeadlineUtc);
        Assert.Equal("1.0", u.InstalledVersion);
        Assert.Equal("2.0", u.AvailableVersion);
        Assert.Equal("app|System|-", u.Key);
    }

    [Fact]
    public void Merge_without_deadline_hours_has_no_deadline()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 0), T0);
        Assert.Null(u.DeadlineUtc);
    }

    [Fact]
    public void Merge_keeps_first_detected_and_deferral_across_scans()
    {
        var policy = Policy(mandatory: true, deadlineHours: 48);
        var (state, u) = Detect(policy, T0);
        Assert.True(PolicyEngine.TryDefer(u, 240, T0.AddMinutes(5), out _));

        var later = T0.AddHours(1);
        var outcome = new ScanOutcome(Result(), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, later);

        Assert.Equal(1, summary.Updated);
        var again = state.Values.Single();
        Assert.Equal(T0, again.FirstDetectedUtc);
        Assert.Equal(T0.AddHours(48), again.DeadlineUtc);
        Assert.Equal(UpdateState.Deferred, again.State);
        Assert.Equal(1, again.DeferralCount);
    }

    [Fact]
    public void Merge_marks_installed_when_scan_no_longer_reports_update()
    {
        var policy = Policy();
        var (state, _) = Detect(policy, T0);
        var outcome = new ScanOutcome(Result(installed: "2.0", available: null), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0.AddHours(4));
        Assert.Equal(1, summary.Resolved);
        Assert.Equal(UpdateState.Installed, state.Values.Single().State);

        // and purged after retention
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0.AddHours(4) + PolicyEngine.InstalledRetention + TimeSpan.FromMinutes(1));
        Assert.Empty(state);
    }

    [Fact]
    public void Merge_removes_update_when_app_uninstalled_and_when_no_longer_configured()
    {
        var policy = Policy();
        var (state, _) = Detect(policy, T0);
        var gone = new ScanOutcome(UpdateCheckResult.NotInstalled("app", UpdateSource.Winget), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [gone], new HashSet<string> { gone.Key }, T0.AddHours(1));
        Assert.Equal(1, summary.Removed);
        Assert.Empty(state);

        (state, _) = Detect(policy, T0);
        // checked this round but produced no outcome at all (e.g. policy removed the app)
        summary = PolicyEngine.Merge(state, [], new HashSet<string> { "app|System|-" }, T0.AddHours(1));
        Assert.Equal(1, summary.Removed);
        Assert.Empty(state);
    }

    [Fact]
    public void Merge_leaves_unchecked_contexts_alone()
    {
        var policy = Policy();
        var (state, _) = Detect(policy, T0);
        var summary = PolicyEngine.Merge(state, [], new HashSet<string>(), T0.AddHours(1));
        Assert.Equal(0, summary.Removed);
        Assert.Single(state);
    }

    [Fact]
    public void Merge_ignores_errors_and_keeps_existing_state()
    {
        var policy = Policy();
        var (state, _) = Detect(policy, T0);
        var err = new ScanOutcome(UpdateCheckResult.Failed("app", UpdateSource.Winget, "boom"), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [err], new HashSet<string> { err.Key }, T0.AddHours(1));
        Assert.Equal(0, summary.Removed);
        Assert.Single(state);
    }

    [Fact]
    public void Merge_reapplies_changed_policy()
    {
        var (state, u) = Detect(Policy(mandatory: false), T0);
        Assert.False(u.Mandatory);
        var stricter = Policy(mandatory: true, deadlineHours: 24);
        var outcome = new ScanOutcome(Result(), stricter, InstallContext.System, null);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0.AddHours(1));
        var again = state.Values.Single();
        Assert.True(again.Mandatory);
        Assert.Equal(T0.AddHours(24), again.DeadlineUtc);
    }

    [Fact]
    public void Merge_recently_installed_is_not_reset_by_a_stale_scan()
    {
        var policy = Policy();
        var (state, u) = Detect(policy, T0);
        PolicyEngine.MarkInstalled(u, InstallResult.Ok(), T0.AddMinutes(10));
        var stale = new ScanOutcome(Result(), policy, InstallContext.System, null);
        PolicyEngine.Merge(state, [stale], new HashSet<string> { stale.Key }, T0.AddMinutes(30));
        Assert.Equal(UpdateState.Installed, state.Values.Single().State);

        PolicyEngine.Merge(state, [stale], new HashSet<string> { stale.Key }, T0.AddMinutes(10) + PolicyEngine.PostInstallGrace + TimeSpan.FromMinutes(1));
        var reset = state.Values.Single();
        Assert.Equal(UpdateState.Available, reset.State);
        Assert.Equal(1, reset.FailureCount);
    }

    [Fact]
    public void Merge_does_not_honour_post_install_grace_when_install_reported_old_version()
    {
        var policy = Policy();
        var (state, u) = Detect(policy, T0);
        // an "install" that returned success but read back the old version (the bug that was fixed in the provider)
        PolicyEngine.MarkInstalled(u, InstallResult.Ok() with { InstalledVersion = "1.0" }, T0.AddMinutes(10));
        Assert.Equal(UpdateState.Installed, u.State);

        var scan = new ScanOutcome(Result(), policy, InstallContext.System, null);
        var summary = PolicyEngine.Merge(state, [scan], new HashSet<string> { scan.Key }, T0.AddMinutes(20));
        var again = state.Values.Single();
        Assert.Equal(UpdateState.Available, again.State);
        Assert.Equal(1, summary.Updated);
    }

    [Fact]
    public void Decide_notifies_new_update_then_waits_for_interval()
    {
        var (_, u) = Detect(Policy(), T0);
        var a = PolicyEngine.Decide(u, T0, Interval, blockingProcessesRunning: false);
        Assert.Equal(PolicyActionKind.Notify, a.Kind);
        Assert.Equal(NotificationKind.UpdateAvailable, a.Notification);

        u.LastNotifiedUtc = T0;
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(1), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false).Kind);
    }

    [Fact]
    public void Decide_uses_deadline_approaching_within_24h()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 30), T0);
        Assert.Equal(NotificationKind.UpdateAvailable, PolicyEngine.Decide(u, T0, Interval, false).Notification);
        u.LastNotifiedUtc = null;
        Assert.Equal(NotificationKind.DeadlineApproaching, PolicyEngine.Decide(u, T0.AddHours(7), Interval, false).Notification);
    }

    [Fact]
    public void Decide_installs_immediately_after_deadline_when_nothing_blocks()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 24), T0);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddHours(24), Interval, false).Kind);
    }

    [Fact]
    public void Decide_past_deadline_with_blocking_processes_prompts_then_forces_after_grace()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 24, processes: "chrome"), T0);
        var t = T0.AddHours(24);
        var first = PolicyEngine.Decide(u, t, Interval, blockingProcessesRunning: true);
        Assert.Equal(PolicyActionKind.PromptClose, first.Kind);

        // the coordinator marks it waiting and schedules the forced close
        PolicyEngine.MarkWaitingForClose(u, ["chrome"], t, scheduleForcedClose: true);
        Assert.Equal(t.AddMinutes(15), u.ForceCloseAtUtc);
        u.LastNotifiedUtc = t;

        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, t.AddMinutes(5), Interval, true).Kind);
        Assert.Equal(PolicyActionKind.ForceClose, PolicyEngine.Decide(u, t.AddMinutes(15), Interval, true).Kind);
        // user closed the app themselves in the meantime
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, t.AddMinutes(10), Interval, false).Kind);
    }

    [Fact]
    public void Decide_past_deadline_without_forced_close_keeps_prompting_at_cadence()
    {
        var policy = Policy(mandatory: true, deadlineHours: 24, processes: "chrome") with { ForceCloseAtDeadline = false };
        var (_, u) = Detect(policy, T0);
        var t = T0.AddHours(24);
        Assert.Equal(PolicyActionKind.PromptClose, PolicyEngine.Decide(u, t, Interval, true).Kind);
        PolicyEngine.MarkWaitingForClose(u, ["chrome"], t, scheduleForcedClose: true);
        Assert.Null(u.ForceCloseAtUtc);
        u.LastNotifiedUtc = t;
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, t.AddHours(1), Interval, true).Kind);
        Assert.Equal(PolicyActionKind.PromptClose, PolicyEngine.Decide(u, t.AddHours(4), Interval, true).Kind);
    }

    [Fact]
    public void Decide_respects_deferral_until_it_expires()
    {
        var (_, u) = Detect(Policy(), T0);
        Assert.True(PolicyEngine.TryDefer(u, 60, T0, out _));
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddMinutes(30), Interval, false).Kind);
        // deferral expired and the notification interval has passed -> notify again
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false).Kind);
    }

    [Fact]
    public void Deferral_is_capped_at_deadline_and_by_max_deferrals()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 2, maxDeferrals: 2), T0);
        Assert.True(PolicyEngine.TryDefer(u, 1440, T0, out _));
        Assert.Equal(T0.AddHours(2), u.DeferredUntilUtc);
        Assert.True(PolicyEngine.TryDefer(u, 60, T0.AddMinutes(1), out _));
        Assert.False(PolicyEngine.TryDefer(u, 60, T0.AddMinutes(2), out var reason));
        Assert.Contains("No more deferrals", reason);
        Assert.False(u.CanDefer(T0.AddMinutes(2)));
    }

    [Fact]
    public void Deferral_rejects_unknown_option_and_past_deadline()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 1), T0);
        Assert.False(PolicyEngine.TryDefer(u, 17, T0, out var reason));
        Assert.Contains("not allowed", reason);
        Assert.False(PolicyEngine.TryDefer(u, 60, T0.AddHours(1), out reason));
        Assert.Contains("deadline", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unlimited_deferrals_when_max_is_zero()
    {
        var (_, u) = Detect(Policy(maxDeferrals: 0), T0);
        for (var i = 0; i < 10; i++) Assert.True(PolicyEngine.TryDefer(u, 60, T0.AddMinutes(i), out _));
    }

    [Fact]
    public void Auto_install_runs_silently_when_no_blocking_process_otherwise_prompts()
    {
        var (_, u) = Detect(Policy(autoInstall: true, processes: "notepad++"), T0);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0, Interval, false).Kind);
        var prompt = PolicyEngine.Decide(u, T0, Interval, true);
        Assert.Equal(PolicyActionKind.PromptClose, prompt.Kind);
        Assert.Equal(NotificationKind.CloseApplications, prompt.Notification);
    }

    [Fact]
    public void Install_request_survives_waiting_for_close()
    {
        var (_, u) = Detect(Policy(processes: "chrome"), T0);
        PolicyEngine.RequestInstall(u);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0, Interval, false).Kind);
        PolicyEngine.MarkWaitingForClose(u, ["chrome"], T0, scheduleForcedClose: true);
        Assert.Null(u.ForceCloseAtUtc); // not past a deadline: never forced
        Assert.True(u.InstallRequested);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(1), Interval, false).Kind);
    }

    [Fact]
    public void Failed_install_is_not_retried_until_next_scan()
    {
        var policy = Policy(autoInstall: true);
        var (state, u) = Detect(policy, T0);
        PolicyEngine.MarkFailed(u, "exit 1603", T0);
        Assert.Equal(NotificationKind.Failed, PolicyEngine.Decide(u, T0, Interval, false).Notification);
        u.LastNotifiedUtc = T0;
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddMinutes(1), Interval, false).Kind);

        var outcome = new ScanOutcome(Result(), policy, InstallContext.System, null);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0.AddHours(4));
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false).Kind);

        for (var i = 0; i < 2; i++) PolicyEngine.MarkFailed(u, "again", T0);
        Assert.Equal(PolicyEngine.MaxAutomaticRetries, u.FailureCount);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0.AddHours(8));
        Assert.Equal(UpdateState.Failed, u.State); // exhausted: stays failed until a new version appears

        var newer = new ScanOutcome(Result(available: "3.0"), policy, InstallContext.System, null);
        PolicyEngine.Merge(state, [newer], new HashSet<string> { newer.Key }, T0.AddHours(12));
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(0, u.FailureCount);
    }

    [Fact]
    public void Dismiss_resets_notification_clock_only()
    {
        var (_, u) = Detect(Policy(), T0);
        PolicyEngine.Dismiss(u, T0);
        Assert.True(u.Dismissed);
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(1), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false).Kind);
    }

    // ---------------------------------------------------------------- quiet notifications

    private const NotificationMode Quiet = NotificationMode.Quiet;
    private const NotificationMode Reminders = NotificationMode.Reminders;

    /// <summary>Stands in for the coordinator, which records that the user was told after the toast was delivered.</summary>
    private static void MarkNotified(PendingUpdate u, DateTimeOffset at)
    {
        u.LastNotifiedUtc = at;
        u.Announced = true;
    }

    [Fact]
    public void Quiet_announces_an_update_once_and_then_stays_silent()
    {
        var (_, u) = Detect(Policy(), T0);
        var first = PolicyEngine.Decide(u, T0, Interval, false, Quiet);
        Assert.Equal(PolicyActionKind.Notify, first.Kind);
        Assert.Equal(NotificationKind.UpdateAvailable, first.Notification);

        MarkNotified(u, T0);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false, Quiet).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddDays(7), Interval, false, Quiet).Kind);
    }

    [Fact]
    public void Reminders_repeats_the_same_update_after_the_interval()
    {
        var (_, u) = Detect(Policy(), T0);
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0, Interval, false, Reminders).Kind);
        MarkNotified(u, T0);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(1), Interval, false, Reminders).Kind);
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddHours(4), Interval, false, Reminders).Kind);
    }

    [Fact]
    public void Quiet_still_warns_when_the_deadline_approaches()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 30), T0);
        MarkNotified(u, T0);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(1), Interval, false, Quiet).Kind);

        var warning = PolicyEngine.Decide(u, T0.AddHours(7), Interval, false, Quiet);
        Assert.Equal(PolicyActionKind.Notify, warning.Kind);
        Assert.Equal(NotificationKind.DeadlineApproaching, warning.Notification);
    }

    [Fact]
    public void Quiet_still_prompts_to_close_blocking_applications()
    {
        var (_, u) = Detect(Policy(mandatory: true, deadlineHours: 30, processes: "chrome"), T0);
        MarkNotified(u, T0);
        var prompt = PolicyEngine.Decide(u, T0.AddHours(7), Interval, blockingProcessesRunning: true, Quiet);
        Assert.Equal(PolicyActionKind.PromptClose, prompt.Kind);
        Assert.Equal(NotificationKind.CloseApplications, prompt.Notification);
    }

    [Fact]
    public void Quiet_reports_a_failure_once_per_failure()
    {
        var (_, u) = Detect(Policy(), T0);
        MarkNotified(u, T0);
        PolicyEngine.MarkFailed(u, "boom", T0.AddMinutes(1));

        var reported = PolicyEngine.Decide(u, T0.AddMinutes(2), Interval, false, Quiet);
        Assert.Equal(PolicyActionKind.Notify, reported.Kind);
        Assert.Equal(NotificationKind.Failed, reported.Notification);

        u.LastNotifiedUtc = T0.AddMinutes(2);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(8), Interval, false, Quiet).Kind);
        // Reminders keeps nagging about the same failure.
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddHours(8), Interval, false, Reminders).Kind);
    }

    [Fact]
    public void Quiet_does_not_re_announce_when_a_deferral_expires()
    {
        var (_, u) = Detect(Policy(), T0);
        MarkNotified(u, T0);
        Assert.True(PolicyEngine.TryDefer(u, 60, T0.AddMinutes(1), out _));
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddMinutes(30), Interval, false, Quiet).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(6), Interval, false, Quiet).Kind);
    }

    [Fact]
    public void Quiet_does_not_re_announce_after_a_dismissal()
    {
        var (_, u) = Detect(Policy(), T0);
        MarkNotified(u, T0);
        PolicyEngine.Dismiss(u, T0.AddMinutes(1));
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(6), Interval, false, Quiet).Kind);
    }

    [Fact]
    public void Quiet_announces_a_newer_version_again()
    {
        var policy = Policy();
        var (state, u) = Detect(policy, T0);
        MarkNotified(u, T0);

        var newer = new ScanOutcome(Result(available: "3.0"), policy, InstallContext.System, null);
        PolicyEngine.Merge(state, [newer], new HashSet<string> { newer.Key }, T0.AddDays(30));

        Assert.False(u.Announced);
        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddDays(30), Interval, false, Quiet).Kind);
    }

    [Fact]
    public void NotificationModeFor_prefers_the_application_override()
    {
        var settings = new AgentSettings();
        Assert.Equal(NotificationMode.Quiet, settings.NotificationMode);
        Assert.Equal(NotificationMode.Quiet, PolicyEngine.NotificationModeFor(null, settings));
        Assert.Equal(NotificationMode.Quiet, PolicyEngine.NotificationModeFor(Policy(), settings));
        Assert.Equal(NotificationMode.Reminders, PolicyEngine.NotificationModeFor(Policy() with { NotificationMode = Reminders }, settings));
        Assert.Equal(NotificationMode.Quiet,
            PolicyEngine.NotificationModeFor(Policy() with { NotificationMode = Quiet }, settings with { NotificationMode = Reminders }));
    }

    [Fact]
    public void User_context_updates_are_keyed_per_user()
    {
        var policy = Policy() with { Context = InstallContext.User };
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var a = new ScanOutcome(Result(), policy, InstallContext.User, "S-1-5-21-1");
        var b = new ScanOutcome(Result(), policy, InstallContext.User, "S-1-5-21-2");
        PolicyEngine.Merge(state, [a, b], new HashSet<string> { a.Key, b.Key }, T0);
        Assert.Equal(2, state.Count);
        Assert.All(state.Values, u => Assert.Equal(InstallContext.User, u.Context));
        Assert.Contains(state.Values, u => u.UserSid == "S-1-5-21-2");
    }
}
