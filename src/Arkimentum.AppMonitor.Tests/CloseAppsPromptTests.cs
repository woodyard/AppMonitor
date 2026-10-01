using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The close-apps prompt is a toast that waits half a minute for an answer. No answer is "Not now" - the very same
/// "Not now" as the button - and that must never weaken a scheduled forced close or hide what the agent cannot close.
/// </summary>
public class CloseAppsPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const int MySession = 2;

    private static PendingUpdate Update(Action<PendingUpdate>? configure = null)
    {
        var u = new PendingUpdate
        {
            AppId = "powershell",
            DisplayName = "PowerShell 7",
            InstalledVersion = "7.5.0",
            AvailableVersion = "7.5.1",
            Source = UpdateSource.Winget,
            Context = InstallContext.System,
            State = UpdateState.WaitingForClose,
            FirstDetectedUtc = Now.AddHours(-2),
            LastSeenUtc = Now,
            ProcessNames = ["pwsh"],
            BlockingProcesses = ["pwsh"],
            DeferralOptionsMinutes = [60, 240],
            MaxDeferrals = 3,
            CloseGracePeriodMinutes = 15,
        };
        configure?.Invoke(u);
        return u;
    }

    /// <summary>A mandatory update past its deadline with its forced close scheduled, as MarkWaitingForClose leaves it.</summary>
    private static PendingUpdate ForcedClosePending() => Update(x =>
    {
        x.Mandatory = true;
        x.DeadlineUtc = Now.AddMinutes(-5);
        x.ForceCloseAtDeadline = true;
        x.ForceCloseAtUtc = Now.AddMinutes(10);
    });

    private static CloseAppsPromptContent Compose(PendingUpdate u) => CloseAppsPrompt.Compose(u, MySession, name => name, Now);

    // ---------------------------------------------------------------- no answer is "Not now"

    [Fact]
    public void The_tray_waits_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), CloseAppsPrompt.AnswerTimeout);
        // Windows' own expiry is only a backstop behind the tray's timer, never what ends the prompt.
        Assert.True(CloseAppsPrompt.ToastExpiry > CloseAppsPrompt.AnswerTimeout);
    }

    [Theory]
    [InlineData(CloseAppsPromptEnd.Timeout)]
    [InlineData(CloseAppsPromptEnd.ToastClosed)]
    [InlineData(CloseAppsPromptEnd.OpenedWindow)]
    [InlineData(CloseAppsPromptEnd.NotShown)]
    public void Every_end_without_a_button_is_not_now(CloseAppsPromptEnd end)
    {
        Assert.Equal(CloseAppsChoice.NotNow, CloseAppsPrompt.ChoiceFor(end, clicked: null));
        // Even a choice somehow attached to it does not count: only a button is the user choosing.
        Assert.Equal(CloseAppsChoice.NotNow, CloseAppsPrompt.ChoiceFor(end, CloseAppsChoice.CloseAndUpdate));
    }

    [Theory]
    [InlineData(CloseAppsChoice.CloseAndUpdate)]
    [InlineData(CloseAppsChoice.Defer)]
    [InlineData(CloseAppsChoice.NotNow)]
    public void A_button_is_what_it_says(CloseAppsChoice clicked) =>
        Assert.Equal(clicked, CloseAppsPrompt.ChoiceFor(CloseAppsPromptEnd.Button, clicked));

    [Fact]
    public void Not_now_dismisses_an_optional_update()
    {
        var u = Update();
        Assert.True(CloseAppsPrompt.NotNowSendsDismissal(u));

        // What the service does with that dismissal: the update leaves WaitingForClose and the notification interval
        // starts over, so the prompt does not come straight back.
        PolicyEngine.Dismiss(u, Now);
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(Now, u.LastNotifiedUtc);
        Assert.Equal(PolicyAction.None, PolicyEngine.Decide(u, Now.AddSeconds(31), TimeSpan.FromHours(4), blockingProcessesRunning: true));
    }

    [Fact]
    public void Not_now_sends_nothing_for_a_mandatory_update()
    {
        Assert.False(CloseAppsPrompt.NotNowSendsDismissal(Update(x => { x.Mandatory = true; x.DeadlineUtc = Now.AddDays(1); })));
    }

    [Fact]
    public void An_unanswered_mandatory_prompt_is_not_repeated_before_the_notification_interval()
    {
        // The service stamped LastNotifiedUtc when it prompted; a timeout sends nothing for a mandatory update, so the
        // next prompt waits for the interval like it did after the dialog's "Not now".
        var u = Update(x => { x.Mandatory = true; x.DeadlineUtc = Now.AddDays(1); x.LastNotifiedUtc = Now; });
        var interval = TimeSpan.FromHours(4);

        Assert.Equal(PolicyAction.None, PolicyEngine.Decide(u, Now + CloseAppsPrompt.AnswerTimeout, interval, blockingProcessesRunning: true));
        Assert.Equal(PolicyActionKind.PromptClose, PolicyEngine.Decide(u, Now + interval, interval, blockingProcessesRunning: true).Kind);
    }

    // ---------------------------------------------------------------- forced close

    [Fact]
    public void A_timeout_never_cancels_a_forced_close()
    {
        var u = ForcedClosePending();

        // Only ever scheduled for a mandatory update, so the timeout's "Not now" sends nothing at all ...
        Assert.False(CloseAppsPrompt.NotNowSendsDismissal(u));
        // ... and even a dismissal from an older tray would leave it in place past the deadline.
        PolicyEngine.Dismiss(u, Now);
        Assert.Equal(Now.AddMinutes(10), u.ForceCloseAtUtc);
        Assert.Equal(PolicyActionKind.ForceClose, PolicyEngine.Decide(u, Now.AddMinutes(10), TimeSpan.FromHours(4), blockingProcessesRunning: true).Kind);
    }

    [Fact]
    public void A_forced_close_prompt_says_when_and_offers_no_way_around_it()
    {
        var content = Compose(ForcedClosePending());

        Assert.Equal(Now.AddMinutes(10), content.ForceCloseAtUtc);
        Assert.NotNull(content.Detail);
        Assert.StartsWith(Strings.CloseAppsForcedCloseAt(TimeFormat.Clock(Now.AddMinutes(10))), content.Detail);
        Assert.False(content.ShowNotNow);
        Assert.Empty(content.DeferralMinutes);
    }

    [Fact]
    public void A_forced_close_that_is_due_says_so()
    {
        var content = Compose(Update(x =>
        {
            x.Mandatory = true;
            x.DeadlineUtc = Now.AddMinutes(-20);
            x.ForceCloseAtDeadline = true;
            x.ForceCloseAtUtc = Now.AddMinutes(-1);
        }));

        Assert.StartsWith(Strings.CloseAppsCountdownElapsed, content.Detail);
    }

    [Fact]
    public void A_timed_out_forced_close_prompt_leaves_a_notice()
    {
        var u = ForcedClosePending();

        Assert.True(CloseAppsPrompt.LeavesForcedCloseNotice(u, CloseAppsPromptEnd.Timeout, Now));
        // The user who closed the toast or opened the window has read it; nothing to leave once the close is due.
        Assert.False(CloseAppsPrompt.LeavesForcedCloseNotice(u, CloseAppsPromptEnd.ToastClosed, Now));
        Assert.False(CloseAppsPrompt.LeavesForcedCloseNotice(u, CloseAppsPromptEnd.OpenedWindow, Now));
        Assert.False(CloseAppsPrompt.LeavesForcedCloseNotice(u, CloseAppsPromptEnd.Timeout, Now.AddMinutes(10)));
        Assert.False(CloseAppsPrompt.LeavesForcedCloseNotice(Update(), CloseAppsPromptEnd.Timeout, Now));
    }

    // ---------------------------------------------------------------- what the toast says and offers

    [Fact]
    public void An_ordinary_prompt_offers_close_defer_and_not_now()
    {
        var content = Compose(Update());

        Assert.Equal(Strings.CloseAppsTitle("PowerShell 7"), content.Title);
        Assert.Equal(Strings.CloseAppsToastBody("pwsh"), content.Body);
        Assert.Contains(Strings.CloseAppsSaveHint, content.Body);
        Assert.Null(content.Detail);
        Assert.True(content.ShowNotNow);
        Assert.Equal([60, 240], content.DeferralMinutes);
        Assert.True(content.DeferAsSelection);
    }

    [Fact]
    public void A_single_deferral_is_a_button_and_none_is_no_button()
    {
        Assert.False(Compose(Update(x => x.DeferralOptionsMinutes = [60])).DeferAsSelection);
        Assert.Equal([60], Compose(Update(x => x.DeferralOptionsMinutes = [60])).DeferralMinutes);
        Assert.Empty(Compose(Update(x => x.DeferralCount = 3)).DeferralMinutes);
        Assert.Empty(Compose(Update(x => x.DeferralOptionsMinutes = [])).DeferralMinutes);
    }

    [Fact]
    public void Deferral_options_fit_a_selection_box()
    {
        var content = Compose(Update(x => x.DeferralOptionsMinutes = [15, 30, 60, 60, 120, 240, 480, 0]));
        Assert.Equal([15, 30, 60, 120, 240], content.DeferralMinutes);
    }

    [Fact]
    public void Processes_the_agent_cannot_close_are_marked_and_explained()
    {
        var content = Compose(Update(x =>
        {
            x.ProcessNames = ["pwsh", "code"];
            x.BlockingProcesses = ["pwsh", "code"];
            x.BlockingDetails =
            [
                new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 10, SessionId = MySession, Elevated = true },
                new BlockingProcessInfo { ProcessName = "code", ProcessId = 11, SessionId = 5, UserName = @"CONTOSO\bob" },
            ];
        }));

        Assert.Contains(Strings.CloseAppsProcessWithMarkers("pwsh", Strings.CloseAppsElevated), content.Body);
        Assert.Contains(Strings.CloseAppsProcessWithMarkers("code", Strings.CloseAppsOtherSessionAs(@"CONTOSO\bob")), content.Body);
        Assert.Equal(Strings.CloseAppsServiceCloses, content.Detail);
    }

    [Fact]
    public void Processes_in_my_own_session_carry_no_markers()
    {
        var content = Compose(Update(x => x.BlockingDetails =
            [new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 10, SessionId = MySession, Elevated = false }]));

        Assert.Equal(Strings.CloseAppsToastBody("pwsh"), content.Body);
        Assert.Null(content.Detail);
    }

    [Fact]
    public void The_forced_close_comes_before_the_service_hint()
    {
        var u = ForcedClosePending();
        u.BlockingDetails = [new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 10, SessionId = 7 }];
        var content = Compose(u);

        Assert.StartsWith(Strings.CloseAppsForcedCloseAt(TimeFormat.Clock(Now.AddMinutes(10))), content.Detail);
        Assert.EndsWith(Strings.CloseAppsServiceCloses, content.Detail);
    }

    [Fact]
    public void A_friendly_name_that_throws_falls_back_to_the_process_name()
    {
        var content = CloseAppsPrompt.Compose(Update(), MySession, _ => throw new InvalidOperationException(), Now);
        Assert.Equal(Strings.CloseAppsToastBody("pwsh"), content.Body);
    }

    [Fact]
    public void An_unchanged_prompt_has_the_same_signature_and_a_scheduled_forced_close_changes_it()
    {
        Assert.Equal(Compose(Update()).Signature, Compose(Update()).Signature);
        Assert.NotEqual(Compose(Update()).Signature, Compose(ForcedClosePending()).Signature);
        Assert.NotEqual(Compose(Update()).Signature, Compose(Update(x => x.BlockingProcesses = ["pwsh", "code"])).Signature);
    }

    // ---------------------------------------------------------------- withdrawn while on screen

    [Theory]
    [InlineData(UpdateState.Installing)]
    [InlineData(UpdateState.Installed)]
    public void A_prompt_whose_update_moved_on_is_withdrawn(UpdateState state) =>
        Assert.True(CloseAppsPrompt.IsWithdrawn(Update(x => x.State = state), Now));

    [Fact]
    public void A_prompt_whose_update_is_gone_or_deferred_meanwhile_is_withdrawn()
    {
        Assert.True(CloseAppsPrompt.IsWithdrawn(null, Now));
        Assert.True(CloseAppsPrompt.IsWithdrawn(Update(x => { x.State = UpdateState.Deferred; x.DeferredUntilUtc = Now.AddHours(1); }), Now));
    }

    [Fact]
    public void A_prompt_still_waiting_or_with_a_run_out_deferral_stays()
    {
        Assert.False(CloseAppsPrompt.IsWithdrawn(Update(), Now));
        // A state message from before the prompt cannot take it down: the service only prompts once a deferral ran out.
        Assert.False(CloseAppsPrompt.IsWithdrawn(Update(x => { x.State = UpdateState.Deferred; x.DeferredUntilUtc = Now.AddMinutes(-1); }), Now));
    }
}
