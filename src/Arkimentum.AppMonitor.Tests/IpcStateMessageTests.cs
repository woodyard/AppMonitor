using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class IpcStateMessageTests
{
    [Fact]
    public void State_message_round_trips_the_organization_fields()
    {
        var message = new StateMessage
        {
            ServiceVersion = "1.1.2",
            Settings = new SettingsSummary
            {
                ScanIntervalMinutes = 240,
                CloudConfigured = true,
                CloudEnrolled = true,
                OrganizationName = "cloudonly.dk",
            },
        };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.True(back.Settings.CloudConfigured);
        Assert.True(back.Settings.CloudEnrolled);
        Assert.Equal("cloudonly.dk", back.Settings.OrganizationName);
        Assert.Equal(240, back.Settings.ScanIntervalMinutes);
    }

    [Fact]
    public void State_message_from_an_older_service_reads_as_stand_alone()
    {
        // A 1.1.1 service never sends the organization fields; the tray must treat that as "no organization".
        const string line = "{\"$type\":\"state\",\"updates\":[],\"scanInProgress\":false,\"serviceVersion\":\"1.1.1\",\"settings\":{\"scanIntervalMinutes\":240,\"monitoredApps\":[]}}";

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.False(back.Settings.CloudConfigured);
        Assert.False(back.Settings.CloudEnrolled);
        Assert.Null(back.Settings.OrganizationName);
        // No NotificationMode either: the tray falls back to the product default instead of the noisy mode.
        Assert.Equal(NotificationMode.Quiet, back.Settings.NotificationMode);
    }

    [Fact]
    public void State_message_round_trips_the_agent_update_status()
    {
        var checkedUtc = DateTimeOffset.Parse("2026-02-03T10:22:00Z");
        var message = new StateMessage
        {
            ServiceVersion = "1.1.3",
            AgentUpdate = new AgentUpdateStatus
            {
                RunningVersion = "1.1.3",
                LatestVersion = "1.1.4",
                UpdateAvailable = true,
                InProgress = false,
                LastCheckUtc = checkedUtc,
                LastError = null,
                Enabled = true,
            },
        };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.NotNull(back.AgentUpdate);
        Assert.Equal("1.1.3", back.AgentUpdate!.RunningVersion);
        Assert.Equal("1.1.4", back.AgentUpdate.LatestVersion);
        Assert.True(back.AgentUpdate.UpdateAvailable);
        Assert.False(back.AgentUpdate.InProgress);
        Assert.Equal(checkedUtc, back.AgentUpdate.LastCheckUtc);
        Assert.Null(back.AgentUpdate.LastError);
        Assert.True(back.AgentUpdate.Enabled);
    }

    [Fact]
    public void State_message_from_an_older_service_has_no_agent_update_status()
    {
        // A service that predates client-initiated self-update omits the field; the tray then shows the plain version.
        const string line = "{\"$type\":\"state\",\"updates\":[],\"scanInProgress\":false,\"serviceVersion\":\"1.1.1\",\"settings\":{\"scanIntervalMinutes\":240,\"monitoredApps\":[]}}";

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.Null(back.AgentUpdate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Update_agent_message_round_trips(bool checkOnly)
    {
        var message = new UpdateAgentMessage { CheckOnly = checkOnly };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<UpdateAgentMessage>(IpcJson.Deserialize(line));

        Assert.Equal(checkOnly, back.CheckOnly);
        Assert.Equal(message.MessageId, back.MessageId);
    }

    [Fact]
    public void An_older_agent_that_does_not_know_update_agent_is_not_broken_by_the_new_discriminator()
    {
        // The type is additive: every message an older client sends still deserialises unchanged.
        const string line = "{\"$type\":\"requestScan\",\"messageId\":\"abc\",\"sentUtc\":\"2026-02-03T10:22:00+00:00\"}";

        var back = Assert.IsType<RequestScanMessage>(IpcJson.Deserialize(line));

        Assert.Equal("abc", back.MessageId);
    }

    [Fact]
    public void State_message_round_trips_the_notification_mode()
    {
        var message = new StateMessage { Settings = new SettingsSummary { NotificationMode = NotificationMode.Reminders } };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.Equal(NotificationMode.Reminders, back.Settings.NotificationMode);
    }
}
