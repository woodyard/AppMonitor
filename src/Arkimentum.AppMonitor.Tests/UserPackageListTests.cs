using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The inventory runs as LocalSystem, whose "winget list --scope user" only ever lists SYSTEM's own packages, so a
/// per-user install such as GitHub Desktop reached the inventory without a package id. The tray agents now report
/// their own user-scope listing over the pipe and discovery matches it per user SID.
/// </summary>
public class UserPackageListDiscoveryTests
{
    private const string SidA = "S-1-5-21-111-222-333-1001";
    private const string SidB = "S-1-5-21-111-222-333-1002";

    private static InstalledApp UserEntry(string name, string sid, string? version = "1.0.0") => new()
    {
        DisplayName = name,
        DisplayVersion = version,
        Publisher = "Contoso",
        Context = InstallContext.User,
        UserSid = sid,
    };

    private static WingetRow Row(string name, string id, string version = "1.0.0", string available = "", string source = "winget") =>
        new(name, id, version, available, source);

    [Fact]
    public void Per_user_entry_takes_its_winget_id_from_that_users_rows()
    {
        var userPackages = new Dictionary<string, IReadOnlyList<WingetRow>>
        {
            [SidA] = [Row("GitHub Desktop", "GitHub.GitHubDesktop", "3.4.13", "3.5.0")],
        };

        var list = InstalledAppDiscovery.Combine(
            [UserEntry("GitHub Desktop", SidA, "3.4.13")],
            wingetRows: [],
            userPackages,
            configured: [],
            catalog: []);

        var app = Assert.Single(list);
        Assert.Equal("GitHub Desktop", app.DisplayName);
        Assert.Equal("GitHub.GitHubDesktop", app.WingetId);
        Assert.Equal("3.5.0", app.AvailableVersion);
        Assert.Equal(InstallContext.User, app.Context);
        Assert.Equal("winget+registry", app.Origin);
    }

    [Fact]
    public void Rows_of_one_user_never_attach_to_another_users_entry()
    {
        // Only the first user's tray answered. The second user's identically named install must stay without a package
        // id rather than borrow one from a listing that describes a different session.
        var userPackages = new Dictionary<string, IReadOnlyList<WingetRow>>
        {
            [SidA] = [Row("Bicep CLI", "Microsoft.Bicep", "0.38.5")],
        };

        var list = InstalledAppDiscovery.Combine(
            [UserEntry("Bicep CLI", SidA, "0.38.5"), UserEntry("Bicep CLI", SidB, "0.30.0")],
            wingetRows: [],
            userPackages,
            configured: [],
            catalog: []);

        var withId = Assert.Single(list, a => a.WingetId is not null);
        Assert.Equal("Microsoft.Bicep", withId.WingetId);
        Assert.Equal("0.38.5", withId.Version);
        Assert.Contains(list, a => a.Version == "0.30.0" && a.WingetId is null && a.Origin == "registry");
    }

    [Fact]
    public void A_machine_wide_entry_does_not_take_a_users_row()
    {
        var userPackages = new Dictionary<string, IReadOnlyList<WingetRow>>
        {
            [SidA] = [Row("7-Zip", "7zip.7zip", "26.02")],
        };
        var machine = new InstalledApp { DisplayName = "7-Zip", DisplayVersion = "26.02", Context = InstallContext.System };

        var list = InstalledAppDiscovery.Combine([machine], wingetRows: [], userPackages, configured: [], catalog: []);

        var system = Assert.Single(list, a => a.Context == InstallContext.System);
        Assert.Null(system.WingetId);
        // The unmatched tray row is still offered, but as the per-user package it is.
        var user = Assert.Single(list, a => a.Context == InstallContext.User);
        Assert.Equal("7zip.7zip", user.WingetId);
        Assert.Equal("winget", user.Origin);
    }

    [Fact]
    public void A_user_row_nothing_claims_becomes_a_winget_only_entry()
    {
        var userPackages = new Dictionary<string, IReadOnlyList<WingetRow>>
        {
            [SidA] = [Row("Bicep CLI", "Microsoft.Bicep", "0.38.5", "0.39.0")],
        };

        var list = InstalledAppDiscovery.Combine([], wingetRows: [], userPackages, configured: [], catalog: []);

        var app = Assert.Single(list);
        Assert.Equal("Microsoft.Bicep", app.WingetId);
        Assert.Equal(InstallContext.User, app.Context);
        Assert.Equal("winget", app.Origin);
        Assert.True(app.CanQuickAdd);
    }

    [Fact]
    public void Without_tray_rows_the_system_run_listing_is_used_as_before()
    {
        // The admin console's Discover dialog and the command line pass no dictionary at all: nothing may change there.
        var systemRun = new List<(WingetRow Row, InstallContext Context)>
        {
            (Row("GitHub Desktop", "GitHub.GitHubDesktop", "3.4.13", "3.5.0"), InstallContext.User),
        };

        var list = InstalledAppDiscovery.Combine(
            [UserEntry("GitHub Desktop", SidA, "3.4.13")],
            systemRun,
            userPackagesBySid: null,
            configured: [],
            catalog: []);

        var app = Assert.Single(list);
        Assert.Equal("GitHub.GitHubDesktop", app.WingetId);
        Assert.Equal("winget+registry", app.Origin);
    }

    [Fact]
    public void A_user_without_rows_still_falls_back_to_the_system_run_listing()
    {
        // One tray answered, another user's did not: that user keeps the pre-existing behaviour instead of losing ids.
        var systemRun = new List<(WingetRow Row, InstallContext Context)>
        {
            (Row("Bicep CLI", "Microsoft.Bicep", "0.30.0"), InstallContext.User),
        };
        var userPackages = new Dictionary<string, IReadOnlyList<WingetRow>>
        {
            [SidA] = [Row("GitHub Desktop", "GitHub.GitHubDesktop", "3.4.13")],
        };

        var list = InstalledAppDiscovery.Combine(
            [UserEntry("GitHub Desktop", SidA, "3.4.13"), UserEntry("Bicep CLI", SidB, "0.30.0")],
            systemRun,
            userPackages,
            configured: [],
            catalog: []);

        Assert.Equal("GitHub.GitHubDesktop", Assert.Single(list, a => a.DisplayName == "GitHub Desktop").WingetId);
        Assert.Equal("Microsoft.Bicep", Assert.Single(list, a => a.DisplayName == "Bicep CLI").WingetId);
    }
}

/// <summary>
/// The two messages that carry the per-user winget listing must survive a round trip, and a tray or a service that
/// predates them must degrade quietly rather than break the connection.
/// </summary>
public class UserPackageListIpcTests
{
    [Fact]
    public void Run_user_package_list_round_trips()
    {
        var message = new RunUserPackageListMessage { ListId = "abc123", WingetPath = @"C:\winget\winget.exe" };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<RunUserPackageListMessage>(IpcJson.Deserialize(line));

        Assert.Contains("\"$type\":\"runUserPackageList\"", line);
        Assert.Contains("\"listId\":\"abc123\"", line);
        Assert.Equal("abc123", back.ListId);
        Assert.Equal(@"C:\winget\winget.exe", back.WingetPath);
    }

    [Fact]
    public void User_package_list_result_round_trips_every_row_field()
    {
        var message = new UserPackageListResultMessage
        {
            ListId = "abc123",
            Rows =
            [
                new UserPackageRow
                {
                    Name = "GitHub Desktop",
                    Id = "GitHub.GitHubDesktop",
                    Version = "3.4.13",
                    Available = "3.5.0",
                    Source = "winget",
                    IsTruncated = false,
                },
                new UserPackageRow { Name = "Bicep CL…", Id = "Microsoft.Bice…", Version = "0.38.5", IsTruncated = true },
            ],
        };

        var line = IpcJson.Serialize(message);
        var back = Assert.IsType<UserPackageListResultMessage>(IpcJson.Deserialize(line));

        Assert.Contains("\"$type\":\"userPackageListResult\"", line);
        Assert.Equal("abc123", back.ListId);
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal("GitHub Desktop", back.Rows[0].Name);
        Assert.Equal("GitHub.GitHubDesktop", back.Rows[0].Id);
        Assert.Equal("3.4.13", back.Rows[0].Version);
        Assert.Equal("3.5.0", back.Rows[0].Available);
        Assert.Equal("winget", back.Rows[0].Source);
        Assert.False(back.Rows[0].IsTruncated);
        Assert.True(back.Rows[1].IsTruncated);
        Assert.Null(back.Error);
    }

    [Fact]
    public void A_failed_listing_round_trips_as_no_rows_plus_an_error()
    {
        var message = new UserPackageListResultMessage { ListId = "x", Error = "winget.exe was not found in this user's session" };

        var back = Assert.IsType<UserPackageListResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.Empty(back.Rows);
        Assert.Equal("winget.exe was not found in this user's session", back.Error);
    }

    [Fact]
    public void An_older_agent_sees_an_unknown_discriminator_and_never_answers()
    {
        // PipeClient/PipeServer catch this, log the line as a bad message and keep reading, so the connection survives
        // and the requesting side falls back to its timeout.
        const string line = "{\"$type\":\"runUserPackageList\",\"listId\":\"abc123\"}";

        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<LegacyIpcMessage>(line, IpcJson.Options));
    }

    /// <summary>Stand-in for the message hierarchy of an agent built before the package-list messages existed.</summary>
    [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(LegacyAck), "ack")]
    public abstract class LegacyIpcMessage { }

    public sealed class LegacyAck : LegacyIpcMessage { }
}
