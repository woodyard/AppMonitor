using System.Text.Json;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Covers "Close apps and update" reaching processes no tray agent can close - elevated, or in another session.
/// Nothing here starts a real process: the decision to kill is a pure function and the contract is JSON.
/// </summary>
public class ForcedCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

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
            FirstDetectedUtc = Now.AddHours(-2),
            LastSeenUtc = Now,
            ProcessNames = ["pwsh"],
            BlockingProcesses = ["pwsh"],
            CloseGracePeriodMinutes = 15,
        };
        configure?.Invoke(u);
        return u;
    }

    // ---------------------------------------------------------------- MayServiceForceClose

    [Fact]
    public void The_service_does_not_kill_anything_on_its_own()
    {
        // No user request and no deadline: blocking processes are the user's business, so we keep prompting.
        Assert.False(PolicyEngine.MayServiceForceClose(Update(), Now));
    }

    [Fact]
    public void The_service_kills_when_the_user_chose_close_apps_and_update()
    {
        var u = Update(x => { PolicyEngine.RequestInstall(x); PolicyEngine.RequestForcedClose(x, Now); });

        Assert.Equal(Now, u.ForceCloseRequestedUtc);
        Assert.True(PolicyEngine.MayServiceForceClose(u, Now));
        Assert.True(PolicyEngine.MayServiceForceClose(u, Now.AddMinutes(30)));
    }

    [Fact]
    public void A_stale_request_stops_being_a_licence_to_kill()
    {
        var u = Update(x => PolicyEngine.RequestForcedClose(x, Now));

        Assert.True(PolicyEngine.MayServiceForceClose(u, Now + PolicyEngine.ForceCloseRequestWindow));
        Assert.False(PolicyEngine.MayServiceForceClose(u, Now + PolicyEngine.ForceCloseRequestWindow + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Deadline_enforcement_kills_only_after_the_grace_period()
    {
        var u = Update(x =>
        {
            x.Mandatory = true;
            x.DeadlineUtc = Now.AddMinutes(-5);
            x.ForceCloseAtDeadline = true;
            x.ForceCloseAtUtc = Now.AddMinutes(10);
        });

        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
        Assert.True(PolicyEngine.MayServiceForceClose(u, Now.AddMinutes(10)));
    }

    [Fact]
    public void Deadline_enforcement_never_kills_when_ForceCloseAtDeadline_is_off()
    {
        var u = Update(x =>
        {
            x.Mandatory = true;
            x.DeadlineUtc = Now.AddHours(-1);
            x.ForceCloseAtDeadline = false;
            x.ForceCloseAtUtc = Now.AddMinutes(-30);
        });

        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
    }

    [Fact]
    public void A_deadline_that_has_not_passed_never_kills()
    {
        var u = Update(x =>
        {
            x.Mandatory = true;
            x.DeadlineUtc = Now.AddHours(1);
            x.ForceCloseAtDeadline = true;
            x.ForceCloseAtUtc = Now.AddMinutes(-1);
        });

        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
    }

    [Theory]
    [InlineData(UpdateState.Installing)]
    [InlineData(UpdateState.Installed)]
    public void An_install_that_is_already_running_or_done_never_kills(UpdateState state)
    {
        var u = Update(x => { x.ForceCloseRequestedUtc = Now; x.State = state; });

        Assert.False(PolicyEngine.MayServiceForceClose(u, Now));
    }

    // ---------------------------------------------------------------- the flag's lifetime

    [Fact]
    public void The_request_is_cleared_when_the_install_finishes()
    {
        var u = Update(x => PolicyEngine.RequestForcedClose(x, Now));

        PolicyEngine.MarkInstalled(u, InstallResult.Ok("7.5.1"), Now);

        Assert.Null(u.ForceCloseRequestedUtc);
        Assert.Empty(u.BlockingDetails);
    }

    [Fact]
    public void The_request_is_cleared_when_the_install_fails()
    {
        var u = Update(x => PolicyEngine.RequestForcedClose(x, Now));

        PolicyEngine.MarkFailed(u, "Could not close pwsh (pid 9, session 0, NT AUTHORITY\\SYSTEM, elevated)", Now);

        Assert.Null(u.ForceCloseRequestedUtc);
        Assert.Equal(UpdateState.Failed, u.State);
    }

    [Fact]
    public void Deferring_or_dismissing_withdraws_the_request()
    {
        var deferred = Update(x => PolicyEngine.RequestForcedClose(x, Now));
        Assert.True(PolicyEngine.TryDefer(deferred, 60, Now, out _));
        Assert.Null(deferred.ForceCloseRequestedUtc);

        var dismissed = Update(x => { x.State = UpdateState.WaitingForClose; PolicyEngine.RequestForcedClose(x, Now); });
        PolicyEngine.Dismiss(dismissed, Now);
        Assert.Null(dismissed.ForceCloseRequestedUtc);
    }

    // ---------------------------------------------------------------- blocking detail

    [Fact]
    public void MarkWaitingForClose_carries_the_detail_the_tray_cannot_read_itself()
    {
        var u = Update();
        var details = new List<BlockingProcessInfo>
        {
            new() { ProcessName = "pwsh", ProcessId = 4242, SessionId = 3, UserName = @"H-SURFACELAP5\bob", Elevated = false },
            new() { ProcessName = "pwsh", ProcessId = 99, SessionId = 1, UserName = @"H-SURFACELAP5\henrik", Elevated = true },
        };

        PolicyEngine.MarkWaitingForClose(u, ["pwsh"], Now, scheduleForcedClose: false, details);

        Assert.Equal(UpdateState.WaitingForClose, u.State);
        Assert.Equal(2, u.BlockingDetails.Count);
        Assert.Equal(4242, u.BlockingDetails[0].ProcessId);
        // Passing no detail must leave what is already there alone rather than wiping it.
        PolicyEngine.MarkWaitingForClose(u, ["pwsh"], Now, scheduleForcedClose: false);
        Assert.Equal(2, u.BlockingDetails.Count);
    }

    [Fact]
    public void Describe_names_the_process_its_session_and_its_owner()
    {
        var info = new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 4242, SessionId = 3, UserName = @"H-SURFACELAP5\bob", Elevated = true };

        Assert.Equal(@"pwsh (pid 4242, session 3, H-SURFACELAP5\bob, elevated)", info.Describe());
        // Whatever the caller could not read simply drops out of the description.
        Assert.Equal("pwsh (pid 7, session 1)", new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 7, SessionId = 1 }.Describe());
        Assert.Equal(@"pwsh (pid 4242, session 3, H-SURFACELAP5\bob, elevated); pwsh (pid 7, session 1)",
            BlockingProcessInfo.Describe([info, new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 7, SessionId = 1 }]));
    }

    // ---------------------------------------------------------------- IPC round trip

    [Fact]
    public void Install_now_round_trips_the_close_blocking_processes_flag()
    {
        var message = new InstallNowMessage { UpdateKey = "powershell|System|-", CloseBlockingProcesses = true };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<InstallNowMessage>(IpcJson.Deserialize(line));

        Assert.True(back.CloseBlockingProcesses);
        Assert.Equal("powershell|System|-", back.UpdateKey);
        Assert.Equal(message.MessageId, back.MessageId);
    }

    [Fact]
    public void Install_now_from_an_older_tray_never_asks_the_service_to_kill()
    {
        // A 1.1.1 tray does not know the field; the service must then keep prompting instead of terminating anything.
        const string line = "{\"$type\":\"installNow\",\"updateKey\":\"powershell|System|-\",\"messageId\":\"abc\"}";

        var back = Assert.IsType<InstallNowMessage>(IpcJson.Deserialize(line));

        Assert.False(back.CloseBlockingProcesses);
        Assert.False(PolicyEngine.MayServiceForceClose(Update(), Now));
    }

    [Fact]
    public void Install_now_without_the_flag_serialises_the_way_an_older_service_reads_it()
    {
        var line = IpcJson.Serialize(new InstallNowMessage { UpdateKey = "app|System|-" });

        // The flag defaults to false, so an older service simply ignores an unknown property it never sees set.
        var back = Assert.IsType<InstallNowMessage>(IpcJson.Deserialize(line));
        Assert.False(back.CloseBlockingProcesses);
    }

    [Fact]
    public void Prompt_close_round_trips_the_blocking_detail()
    {
        var update = Update(x =>
        {
            x.ForceCloseRequestedUtc = Now;
            x.BlockingDetails =
            [
                new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 4242, SessionId = 3, UserName = @"H-SURFACELAP5\bob", Elevated = false },
            ];
        });

        var line = IpcJson.Serialize(new PromptCloseMessage { Update = update });
        var back = Assert.IsType<PromptCloseMessage>(IpcJson.Deserialize(line));

        Assert.Equal(Now, back.Update.ForceCloseRequestedUtc);
        var detail = Assert.Single(back.Update.BlockingDetails);
        Assert.Equal(4242, detail.ProcessId);
        Assert.Equal(3, detail.SessionId);
        Assert.Equal(@"H-SURFACELAP5\bob", detail.UserName);
        Assert.False(detail.Elevated);
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>The service persists tracked updates with these options; state written before 1.2 must still load.</summary>
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void A_pending_update_round_trips_the_new_fields()
    {
        var u = Update(x =>
        {
            x.ForceCloseRequestedUtc = Now;
            x.BlockingDetails =
            [
                new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 4242, SessionId = 0, UserName = @"NT AUTHORITY\SYSTEM", Elevated = true },
            ];
        });

        var back = JsonSerializer.Deserialize<PendingUpdate>(JsonSerializer.Serialize(u, StateJson), StateJson)!;

        Assert.Equal(Now, back.ForceCloseRequestedUtc);
        var detail = Assert.Single(back.BlockingDetails);
        Assert.Equal(@"NT AUTHORITY\SYSTEM", detail.UserName);
        Assert.True(detail.Elevated);
        Assert.Equal(0, detail.SessionId);
    }

    [Fact]
    public void A_pending_update_written_before_1_2_still_loads()
    {
        const string json = """
        {
          "AppId": "powershell",
          "DisplayName": "PowerShell 7",
          "State": "WaitingForClose",
          "Context": "System",
          "ProcessNames": [ "pwsh" ],
          "BlockingProcesses": [ "pwsh" ]
        }
        """;

        var back = JsonSerializer.Deserialize<PendingUpdate>(json, StateJson)!;

        Assert.Null(back.ForceCloseRequestedUtc);
        Assert.Empty(back.BlockingDetails);
        Assert.Equal(["pwsh"], back.BlockingProcesses);
        Assert.False(PolicyEngine.MayServiceForceClose(back, Now));
    }

    [Fact]
    public void Clone_copies_the_blocking_detail_rather_than_sharing_it()
    {
        var original = Update(x => x.BlockingDetails =
        [
            new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 1, SessionId = 1 },
        ]);

        var clone = original.Clone();
        clone.BlockingDetails[0].ProcessId = 99;
        clone.BlockingDetails.Add(new BlockingProcessInfo { ProcessName = "pwsh", ProcessId = 2, SessionId = 2 });

        Assert.Single(original.BlockingDetails);
        Assert.Equal(1, original.BlockingDetails[0].ProcessId);
    }
}
