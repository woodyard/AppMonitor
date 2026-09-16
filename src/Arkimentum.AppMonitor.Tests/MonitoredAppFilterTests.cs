using System.Text.Json;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.State;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// "Monitored applications" in the tray must be about the device in front of the user, not about the fleet-wide
/// configuration: a policy that lists Slack, Zoom and VLC should not tell someone who has none of them installed
/// that they are being monitored.
/// </summary>
public class MonitoredAppFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private const string Alice = "S-1-5-21-1-2-3-1001";
    private const string Bob = "S-1-5-21-1-2-3-1002";

    private static AppPolicy App(string id, string? name = null) =>
        new() { AppId = id, DisplayName = name ?? id, Enabled = true };

    private static AppPresence Present(string appId, InstallContext context = InstallContext.System,
        string? sid = null, bool installed = true) =>
        new()
        {
            AppId = appId,
            DisplayName = appId,
            Context = context,
            UserSid = context == InstallContext.User ? sid : null,
            Installed = installed,
            CheckedUtc = Now,
        };

    private static readonly List<AppPolicy> Configured =
    [
        App("7zip", "7-Zip"), App("slack", "Slack"), App("vscode", "Visual Studio Code"), App("zoom", "Zoom"),
    ];

    // ---------------------------------------------------------------- the filter

    [Fact]
    public void Before_the_first_scan_everything_configured_is_listed()
    {
        // Knowing nothing is not the same as knowing nothing is installed; an empty panel would read as a broken agent.
        Assert.Equal(["7-Zip", "Slack", "Visual Studio Code", "Zoom"],
            MonitoredAppFilter.Relevant(Configured, [], Alice));
    }

    [Fact]
    public void Only_what_the_scan_found_installed_is_listed()
    {
        List<AppPresence> presence =
        [
            Present("7zip"),
            Present("slack", installed: false),
            Present("vscode"),
            Present("zoom", installed: false),
        ];

        Assert.Equal(["7-Zip", "Visual Studio Code"], MonitoredAppFilter.Relevant(Configured, presence, Alice));
    }

    [Fact]
    public void A_machine_wide_install_counts_for_every_user()
    {
        List<AppPresence> presence = [Present("7zip"), Present("slack", installed: false)];

        Assert.Equal(["7-Zip"], MonitoredAppFilter.Relevant(Configured, presence, Alice));
        Assert.Equal(["7-Zip"], MonitoredAppFilter.Relevant(Configured, presence, Bob));
        // Even for a connection whose user could not be read at all.
        Assert.Equal(["7-Zip"], MonitoredAppFilter.Relevant(Configured, presence, null));
    }

    [Fact]
    public void A_per_user_install_counts_only_for_that_user()
    {
        List<AppPresence> presence =
        [
            Present("vscode", InstallContext.User, Alice),
            Present("slack", InstallContext.User, Bob),
            Present("7zip", installed: false),
        ];

        Assert.Equal(["Visual Studio Code"], MonitoredAppFilter.Relevant(Configured, presence, Alice));
        Assert.Equal(["Slack"], MonitoredAppFilter.Relevant(Configured, presence, Bob));
        Assert.Empty(MonitoredAppFilter.Relevant(Configured, presence, null));
    }

    [Fact]
    public void A_user_sid_is_matched_without_regard_to_case()
    {
        List<AppPresence> presence = [Present("vscode", InstallContext.User, Alice.ToLowerInvariant())];

        Assert.Equal(["Visual Studio Code"], MonitoredAppFilter.Relevant(Configured, presence, Alice.ToUpperInvariant()));
    }

    [Fact]
    public void An_application_installed_both_ways_is_listed_once()
    {
        List<AppPresence> presence = [Present("vscode"), Present("vscode", InstallContext.User, Alice)];

        Assert.Equal(["Visual Studio Code"], MonitoredAppFilter.Relevant(Configured, presence, Alice));
    }

    [Fact]
    public void Presence_for_an_application_that_is_no_longer_configured_is_ignored()
    {
        List<AppPresence> presence = [Present("7zip"), Present("firefox")];

        Assert.Equal(["7-Zip"], MonitoredAppFilter.Relevant(Configured, presence, Alice));
    }

    [Fact]
    public void Nothing_installed_here_is_an_empty_list_not_the_whole_configuration()
    {
        List<AppPresence> presence = [Present("7zip", installed: false), Present("slack", installed: false)];

        Assert.Empty(MonitoredAppFilter.Relevant(Configured, presence, Alice));
    }

    [Fact]
    public void An_application_without_a_display_name_falls_back_to_its_id()
    {
        List<AppPolicy> apps = [new() { AppId = "7zip", Enabled = true }];

        Assert.Equal(["7zip"], MonitoredAppFilter.All(apps));
        Assert.Equal(["7zip"], MonitoredAppFilter.Relevant(apps, [Present("7zip")], Alice));
    }

    [Fact]
    public void Visibility_is_context_aware()
    {
        Assert.True(MonitoredAppFilter.IsVisibleTo(Present("7zip"), Alice));
        Assert.True(MonitoredAppFilter.IsVisibleTo(Present("vscode", InstallContext.User, Alice), Alice));
        Assert.False(MonitoredAppFilter.IsVisibleTo(Present("vscode", InstallContext.User, Bob), Alice));
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>The service persists its state with these options; a file written before 1.2 must still load.</summary>
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void The_presence_map_survives_a_restart()
    {
        var state = new ServiceState { LastScanUtc = Now };
        var entry = Present("vscode", InstallContext.User, Alice);
        entry.InstalledVersion = "1.94.0";
        state.AppPresence[entry.Key] = entry;

        var back = JsonSerializer.Deserialize<ServiceState>(JsonSerializer.Serialize(state, StateJson), StateJson)!;

        var loaded = Assert.Single(back.AppPresence).Value;
        Assert.Equal("vscode", loaded.AppId);
        Assert.Equal(InstallContext.User, loaded.Context);
        Assert.Equal(Alice, loaded.UserSid);
        Assert.True(loaded.Installed);
        Assert.Equal("1.94.0", loaded.InstalledVersion);
        Assert.Equal(@"vscode|User|" + Alice, loaded.Key);
    }

    [Fact]
    public void A_state_file_written_before_1_2_loads_with_no_presence()
    {
        const string json = """
        {
          "Updates": {},
          "LastScanUtc": "2026-09-16T09:00:00+00:00"
        }
        """;

        var back = JsonSerializer.Deserialize<ServiceState>(json, StateJson)!;

        Assert.NotNull(back.AppPresence);
        Assert.Empty(back.AppPresence);
        // ... and the fallback then shows what the tray always showed.
        Assert.Equal(4, MonitoredAppFilter.Relevant(Configured, back.AppPresence.Values, Alice).Count);
    }

    // ---------------------------------------------------------------- IPC contract

    [Fact]
    public void The_state_message_round_trips_the_configured_count()
    {
        var message = new StateMessage
        {
            Settings = new SettingsSummary
            {
                MonitoredApps = ["7-Zip", "Visual Studio Code"],
                MonitoredAppCount = 2,
                ConfiguredAppCount = 12,
            },
        };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.Equal(2, back.Settings.MonitoredAppCount);
        Assert.Equal(12, back.Settings.ConfiguredAppCount);
        Assert.Equal(["7-Zip", "Visual Studio Code"], back.Settings.MonitoredApps);
    }

    [Fact]
    public void An_older_service_reports_no_configured_count()
    {
        // 1.1.4 sent every enabled application as MonitoredApps and no ConfiguredAppCount; a newer tray must then
        // treat the list it gets as the whole configuration and say nothing about what "applies to this device".
        const string line = "{\"$type\":\"state\",\"updates\":[],\"scanInProgress\":false,\"serviceVersion\":\"1.1.4\"," +
                            "\"settings\":{\"monitoredAppCount\":4,\"monitoredApps\":[\"7-Zip\",\"Slack\"]}}";

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.Equal(0, back.Settings.ConfiguredAppCount);
        Assert.Equal(4, back.Settings.MonitoredAppCount);
    }
}
