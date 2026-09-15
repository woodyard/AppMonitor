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
    public void State_message_round_trips_the_notification_mode()
    {
        var message = new StateMessage { Settings = new SettingsSummary { NotificationMode = NotificationMode.Reminders } };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.Equal(NotificationMode.Reminders, back.Settings.NotificationMode);
    }
}
