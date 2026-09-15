using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arkimentum.AppMonitor.Api.Tests;

public sealed class DeviceServiceTests : IDisposable
{
    private readonly TestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // ------------------------------------------------------------------------------------------------ enrollment

    [Fact]
    public async Task Enroll_WithTheRightKey_CreatesADeviceAndReturnsTheKeyOnce()
    {
        var (organization, key) = await _harness.SeedOrganizationAsync();

        var outcome = await _harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key), "203.0.113.5", default);

        Assert.True(outcome.IsSuccess);
        var response = outcome.Value!;
        Assert.Equal("Contoso", response.OrganizationName);
        Assert.NotEqual(Guid.Empty, response.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(response.DeviceKey));

        var device = await _harness.Db.Devices.SingleAsync();
        Assert.Equal(organization.Id, device.OrganizationId);
        Assert.Equal(32, device.DeviceKeyHash.Length);
        // Only the hash is stored: the plaintext key must not be recoverable from the row.
        Assert.NotEqual(response.DeviceKey, System.Text.Encoding.UTF8.GetString(device.DeviceKeyHash));
        Assert.True(Secrets.Verify(response.DeviceKey, device.DeviceKeyHash));
    }

    [Fact]
    public async Task Enroll_WithAWrongKey_IsRejectedWithoutRevealingWhichPartWasWrong()
    {
        var (organization, _) = await _harness.SeedOrganizationAsync();

        var wrongKey = await _harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, "wrong"), "203.0.113.5", default);
        var wrongOrganization = await _harness.Devices.EnrollAsync(NewEnrollRequest(Guid.NewGuid(), "wrong"), "203.0.113.5", default);

        Assert.Equal(401, wrongKey.Status);
        Assert.Equal(401, wrongOrganization.Status);
        Assert.Equal(ErrorCodes.InvalidEnrollmentKey, wrongKey.Error!.Code);
        Assert.Equal(wrongKey.Error.Message, wrongOrganization.Error!.Message);
        Assert.Empty(_harness.Db.Devices);
    }

    [Fact]
    public async Task Enroll_Twice_ReattachesTheSameMachineAndIssuesANewKey()
    {
        var (organization, key) = await _harness.SeedOrganizationAsync();
        var request = NewEnrollRequest(organization.Id, key);

        var first = await _harness.Devices.EnrollAsync(request, "203.0.113.5", default);
        var second = await _harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key), "203.0.113.5", default);

        Assert.Equal(first.Value!.DeviceId, second.Value!.DeviceId);
        Assert.NotEqual(first.Value.DeviceKey, second.Value.DeviceKey);
        Assert.Equal(1, await _harness.Db.Devices.CountAsync());

        // The old key stops working the moment a new one is issued.
        Assert.Null(await _harness.Authenticator.AuthenticateAsync($"Device {first.Value.DeviceId}:{first.Value.DeviceKey}", default));
        Assert.NotNull(await _harness.Authenticator.AuthenticateAsync($"Device {second.Value.DeviceId}:{second.Value.DeviceKey}", default));
    }

    [Fact]
    public async Task Enroll_FromTheSameMachineGuidInTwoOrganizations_CreatesTwoDevices()
    {
        var (contoso, contosoKey) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var (fabrikam, fabrikamKey) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        var a = await _harness.Devices.EnrollAsync(NewEnrollRequest(contoso.Id, contosoKey), "203.0.113.5", default);
        var b = await _harness.Devices.EnrollAsync(NewEnrollRequest(fabrikam.Id, fabrikamKey), "203.0.113.6", default);

        Assert.NotEqual(a.Value!.DeviceId, b.Value!.DeviceId);
        Assert.Equal(2, await _harness.Db.Devices.CountAsync());
    }

    [Fact]
    public async Task Enroll_IsRateLimitedPerIp()
    {
        var options = new ServerOptions { EnrollLimitPerIp = 3, EnrollLimitPerOrganization = 1000, EnrollWindowMinutes = 10 };
        using var harness = new TestHarness(options);
        var (organization, key) = await harness.SeedOrganizationAsync();

        for (var i = 0; i < 3; i++)
            Assert.True((await harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key, $"PC-{i}", $"guid-{i}"), "203.0.113.5", default)).IsSuccess);

        var blocked = await harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key, "PC-4", "guid-4"), "203.0.113.5", default);
        Assert.Equal(429, blocked.Status);
        Assert.Equal(ErrorCodes.RateLimited, blocked.Error!.Code);
        Assert.NotNull(blocked.RetryAfterSeconds);

        // Another address is unaffected, and the window eventually reopens.
        Assert.True((await harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key, "PC-5", "guid-5"), "198.51.100.9", default)).IsSuccess);
        harness.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.True((await harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key, "PC-6", "guid-6"), "203.0.113.5", default)).IsSuccess);
    }

    [Fact]
    public async Task Enroll_IntoAnInactiveOrganization_IsForbidden()
    {
        var (organization, key) = await _harness.SeedOrganizationAsync();
        organization.IsActive = false;
        await _harness.Db.SaveChangesAsync();

        var outcome = await _harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key), "203.0.113.5", default);

        Assert.Equal(403, outcome.Status);
        Assert.Equal(ErrorCodes.OrganizationInactive, outcome.Error!.Code);
    }

    [Fact]
    public async Task Enroll_WithoutRequiredFields_IsABadRequest()
    {
        var (organization, key) = await _harness.SeedOrganizationAsync();
        var request = NewEnrollRequest(organization.Id, key);
        request.MachineGuid = "  ";

        var outcome = await _harness.Devices.EnrollAsync(request, "203.0.113.5", default);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(ErrorCodes.BadRequest, outcome.Error!.Code);
    }

    // ------------------------------------------------------------------------------------------------ device auth

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("Device")]
    [InlineData("Device no-colon")]
    [InlineData("Device not-a-guid:key")]
    [InlineData("Device 11112222-3333-4444-5555-666677778888:")]
    public async Task Authenticate_RejectsMalformedHeaders(string? header) =>
        Assert.Null(await _harness.Authenticator.AuthenticateAsync(header, default));

    [Fact]
    public async Task Authenticate_IsCaseInsensitiveOnTheScheme()
    {
        var identity = await EnrolledDeviceAsync();
        Assert.NotNull(await _harness.Authenticator.AuthenticateAsync($"device {identity.DeviceId}:{identity.Key}", default));
    }

    [Fact]
    public async Task Authenticate_RejectsADeletedDevice()
    {
        var identity = await EnrolledDeviceAsync();
        var device = await _harness.Db.Devices.SingleAsync();
        device.IsDeleted = true;
        await _harness.Db.SaveChangesAsync();

        Assert.Null(await _harness.Authenticator.AuthenticateAsync($"Device {identity.DeviceId}:{identity.Key}", default));
    }

    [Fact]
    public async Task Authenticate_RejectsADeviceOfAnInactiveOrganization()
    {
        var identity = await EnrolledDeviceAsync();
        var organization = await _harness.Db.Organizations.SingleAsync();
        organization.IsActive = false;
        await _harness.Db.SaveChangesAsync();

        Assert.Null(await _harness.Authenticator.AuthenticateAsync($"Device {identity.DeviceId}:{identity.Key}", default));
    }

    // ------------------------------------------------------------------------------------------------ configuration

    [Fact]
    public async Task GetConfig_ReturnsTheOrganizationConfigurationWithAnETag()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "5-abcdabcd");

        var outcome = await _harness.Devices.GetConfigAsync(identity.Identity, null, default);

        Assert.Equal(200, outcome.Status);
        Assert.Equal("5-abcdabcd", outcome.ETag);
        Assert.Equal("5-abcdabcd", outcome.Value!.ConfigVersion);
        Assert.Equal(600, outcome.Value.PollIntervalSeconds);
        Assert.Equal(240, outcome.Value.Settings.Global["ScanIntervalMinutes"].AsInt());
    }

    [Fact]
    public async Task GetConfig_WithAMatchingIfNoneMatch_Returns304()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "5-abcdabcd");

        Assert.Equal(304, (await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default)).Status);
        Assert.Equal(304, (await _harness.Devices.GetConfigAsync(identity.Identity, "\"5-abcdabcd\"", default)).Status);
        Assert.Equal(304, (await _harness.Devices.GetConfigAsync(identity.Identity, "W/\"5-abcdabcd\"", default)).Status);
        Assert.Equal(200, (await _harness.Devices.GetConfigAsync(identity.Identity, "4-abcdabcd", default)).Status);
    }

    [Fact]
    public async Task GetConfig_WithAPendingCommand_Returns200EvenWhenTheETagMatches()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "5-abcdabcd");
        _harness.Db.DeviceCommands.Add(new DeviceCommandRow
        {
            Id = Guid.NewGuid(),
            DeviceId = identity.DeviceId,
            OrganizationId = identity.OrganizationId,
            Kind = DeviceCommandKind.ScanNow,
            IssuedUtc = _harness.Time.GetUtcNow(),
            IssuedBy = "it@contoso.com",
        });
        await _harness.Db.SaveChangesAsync();

        var outcome = await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default);

        Assert.Equal(200, outcome.Status);
        Assert.Equal("5-abcdabcd", outcome.Value!.ConfigVersion);
        var command = Assert.Single(outcome.Value.Commands);
        Assert.Equal(DeviceCommandKind.ScanNow, command.Kind);

        // Delivery is recorded, but the command keeps coming back until the device acknowledges it.
        var row = await _harness.Db.DeviceCommands.AsNoTracking().SingleAsync();
        Assert.Equal(_harness.Time.GetUtcNow(), row.DeliveredUtc);
        Assert.Null(row.AcknowledgedUtc);
    }

    [Fact]
    public async Task GetConfig_RedeliversAnUnacknowledgedCommandAndStopsOnceAcknowledged()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "5-abcdabcd");
        var commandId = Guid.NewGuid();
        _harness.Db.DeviceCommands.Add(new DeviceCommandRow
        {
            Id = commandId,
            DeviceId = identity.DeviceId,
            OrganizationId = identity.OrganizationId,
            Kind = DeviceCommandKind.RepairPrerequisites,
            IssuedUtc = _harness.Time.GetUtcNow(),
        });
        await _harness.Db.SaveChangesAsync();

        var firstDelivery = _harness.Time.GetUtcNow();
        Assert.Single((await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default)).Value!.Commands);

        _harness.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Single((await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default)).Value!.Commands);

        // DeliveredUtc records the first delivery only.
        Assert.Equal(firstDelivery, (await _harness.Db.DeviceCommands.AsNoTracking().SingleAsync()).DeliveredUtc);

        var report = NewReport();
        report.AcknowledgedCommands = [commandId];
        await _harness.Devices.ReportAsync(identity.Identity, report, "{}", default);

        // Acknowledged: the config response is now a plain 304 again.
        Assert.Equal(304, (await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default)).Status);
    }

    [Fact]
    public async Task GetConfig_DeliversAtMostTwentyCommandsAtATime()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "5-abcdabcd");
        for (var i = 0; i < 25; i++)
            _harness.Db.DeviceCommands.Add(new DeviceCommandRow
            {
                Id = Guid.NewGuid(),
                DeviceId = identity.DeviceId,
                OrganizationId = identity.OrganizationId,
                Kind = DeviceCommandKind.ScanNow,
                Argument = i.ToString(),
                IssuedUtc = _harness.Time.GetUtcNow().AddMinutes(i),
            });
        await _harness.Db.SaveChangesAsync();

        var outcome = await _harness.Devices.GetConfigAsync(identity.Identity, "5-abcdabcd", default);

        Assert.Equal(20, outcome.Value!.Commands.Count);
        Assert.Equal("0", outcome.Value.Commands[0].Argument);      // oldest first
        Assert.Equal(20, await _harness.Db.DeviceCommands.CountAsync(c => c.DeliveredUtc != null));
    }

    [Fact]
    public async Task GetConfig_UpdatesLastSeen()
    {
        var identity = await EnrolledDeviceAsync();
        _harness.Time.Advance(TimeSpan.FromHours(3));

        await _harness.Devices.GetConfigAsync(identity.Identity, null, default);

        var device = await _harness.Db.Devices.AsNoTracking().SingleAsync();
        Assert.Equal(_harness.Time.GetUtcNow(), device.LastSeenUtc);
    }

    [Fact]
    public async Task GetConfig_ForAnOrganizationWithoutAConfiguration_ReturnsTheInitialVersion()
    {
        var identity = await EnrolledDeviceAsync();

        var outcome = await _harness.Devices.GetConfigAsync(identity.Identity, null, default);

        Assert.Equal(ConfigVersioning.Initial, outcome.Value!.ConfigVersion);
        Assert.True(outcome.Value.Settings.IsEmpty);
    }

    // ------------------------------------------------------------------------------------------------ reporting

    [Fact]
    public async Task Report_StoresTheSnapshotArchivesTheBodyAndReportsConfigDrift()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "9-11223344");

        var report = NewReport(configVersionApplied: "8-00000000");
        var raw = JsonSerializer.Serialize(report, CloudJson.Options);

        var outcome = await _harness.Devices.ReportAsync(identity.Identity, report, raw, default);

        Assert.Equal(200, outcome.Status);
        Assert.True(outcome.Value!.Accepted);
        Assert.True(outcome.Value.ConfigChanged);
        Assert.Equal("9-11223344", outcome.Value.ConfigVersion);

        var device = await _harness.Db.Devices.AsNoTracking().SingleAsync();
        Assert.Equal("CONTOSO-LT-0041", device.DeviceName);
        Assert.Equal("1.1.0", device.AgentVersion);
        Assert.Equal(1, device.PendingUpdateCount);
        Assert.Equal(1, device.FailedUpdateCount);
        Assert.Equal(_harness.Time.GetUtcNow(), device.LastSeenUtc);
        Assert.NotNull(device.PrerequisitesJson);

        Assert.Equal(2, await _harness.Db.DeviceApps.CountAsync());
        Assert.Equal(2, await _harness.Db.DeviceUpdates.CountAsync());
        Assert.Equal(1, await _harness.Db.DeviceEvents.CountAsync());

        var archived = Assert.Single(_harness.Archive.Entries);
        Assert.Equal(identity.OrganizationId, archived.OrganizationId);
        Assert.Equal(identity.DeviceId, archived.DeviceId);
        Assert.Equal(raw, archived.Json);
    }

    [Fact]
    public async Task Report_WithTheCurrentConfigVersion_DoesNotSignalAChange()
    {
        var identity = await EnrolledDeviceAsync();
        await SetConfigAsync(identity.OrganizationId, "9-11223344");

        var outcome = await _harness.Devices.ReportAsync(identity.Identity, NewReport("9-11223344"), "{}", default);

        Assert.False(outcome.Value!.ConfigChanged);
    }

    [Fact]
    public async Task Report_ReplacesThePreviousSnapshotButAppendsEvents()
    {
        var identity = await EnrolledDeviceAsync();
        await _harness.Devices.ReportAsync(identity.Identity, NewReport(), "{}", default);

        var second = NewReport();
        second.InstalledApps = [new ReportedApp { DisplayName = "Only one left", Context = InstallContext.System }];
        second.Updates = [];
        await _harness.Devices.ReportAsync(identity.Identity, second, "{}", default);

        Assert.Equal(1, await _harness.Db.DeviceApps.CountAsync());
        Assert.Equal(0, await _harness.Db.DeviceUpdates.CountAsync());
        Assert.Equal(2, await _harness.Db.DeviceEvents.CountAsync());

        var device = await _harness.Db.Devices.AsNoTracking().SingleAsync();
        Assert.Equal(0, device.PendingUpdateCount);
        Assert.Equal(0, device.FailedUpdateCount);
    }

    [Fact]
    public async Task Report_AcknowledgesPendingCommands()
    {
        var identity = await EnrolledDeviceAsync();
        var commandId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        _harness.Db.DeviceCommands.AddRange(
            new DeviceCommandRow { Id = commandId, DeviceId = identity.DeviceId, OrganizationId = identity.OrganizationId, Kind = DeviceCommandKind.ScanNow, IssuedUtc = _harness.Time.GetUtcNow() },
            new DeviceCommandRow { Id = otherId, DeviceId = identity.DeviceId, OrganizationId = identity.OrganizationId, Kind = DeviceCommandKind.ReportNow, IssuedUtc = _harness.Time.GetUtcNow() });
        await _harness.Db.SaveChangesAsync();

        var report = NewReport();
        report.AcknowledgedCommands = [commandId];
        await _harness.Devices.ReportAsync(identity.Identity, report, "{}", default);

        Assert.NotNull((await _harness.Db.DeviceCommands.AsNoTracking().SingleAsync(c => c.Id == commandId)).AcknowledgedUtc);
        Assert.Null((await _harness.Db.DeviceCommands.AsNoTracking().SingleAsync(c => c.Id == otherId)).AcknowledgedUtc);
    }

    [Fact]
    public async Task Report_PrunesOldEvents()
    {
        var options = new ServerOptions { MaxEventsPerDevice = 50 };
        using var harness = new TestHarness(options);
        var identity = await EnrolledDeviceAsync(harness);

        for (var round = 0; round < 3; round++)
        {
            var report = NewReport();
            report.Events = [.. Enumerable.Range(0, 40).Select(i => new ReportedEvent
            {
                OccurredUtc = harness.Time.GetUtcNow().AddMinutes(-i),
                Kind = ReportedEventKind.UpdateDetected,
                AppId = $"app-{round}-{i}",
            })];
            await harness.Devices.ReportAsync(identity.Identity, report, "{}", default);
        }

        Assert.Equal(50, await harness.Db.DeviceEvents.CountAsync());
    }

    [Fact]
    public async Task Report_WithAnAbsurdlyLargePayload_IsRejected()
    {
        var identity = await EnrolledDeviceAsync();
        var report = NewReport();
        report.InstalledApps = [.. Enumerable.Range(0, 5001).Select(i => new ReportedApp { DisplayName = $"App {i}" })];

        var outcome = await _harness.Devices.ReportAsync(identity.Identity, report, "{}", default);

        Assert.Equal(400, outcome.Status);
        Assert.Empty(_harness.Db.DeviceApps);
    }

    // ------------------------------------------------------------------------------------------------ release mirror

    [Fact]
    public async Task GetRelease_WithoutAPublishedManifest_Is404()
    {
        var outcome = await _harness.Devices.GetReleaseAsync("stable", default);
        Assert.Equal(404, outcome.Status);
        Assert.Equal(ErrorCodes.NotFound, outcome.Error!.Code);
    }

    [Fact]
    public async Task GetRelease_ReturnsThePublishedManifestForTheChannel()
    {
        var manifest = new ReleaseManifest
        {
            Version = "1.3.0",
            Channel = "preview",
            PackageUrl = "https://example.invalid/appmonitor-1.3.0.zip",
            Sha256 = new string('b', 64),
        };
        _harness.Db.ReleaseManifests.Add(new ReleaseManifestRow
        {
            Channel = "preview",
            Json = JsonSerializer.Serialize(manifest, CloudJson.Options),
            UpdatedUtc = _harness.Time.GetUtcNow(),
        });
        await _harness.Db.SaveChangesAsync();

        Assert.Equal("1.3.0", (await _harness.Devices.GetReleaseAsync("preview", default)).Value!.Version);
        Assert.Equal("1.3.0", (await _harness.Devices.GetReleaseAsync("PREVIEW", default)).Value!.Version);
        Assert.Equal(404, (await _harness.Devices.GetReleaseAsync(null, default)).Status);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private sealed record Enrolled(DeviceIdentity Identity, string Key)
    {
        public Guid DeviceId => Identity.DeviceId;
        public Guid OrganizationId => Identity.OrganizationId;
    }

    private Task<Enrolled> EnrolledDeviceAsync() => EnrolledDeviceAsync(_harness);

    private static async Task<Enrolled> EnrolledDeviceAsync(TestHarness harness)
    {
        var (organization, key) = await harness.SeedOrganizationAsync();
        var response = (await harness.Devices.EnrollAsync(NewEnrollRequest(organization.Id, key), "203.0.113.5", default)).Value!;
        var identity = await new DeviceAuthenticator(harness.Db)
            .AuthenticateAsync($"Device {response.DeviceId}:{response.DeviceKey}", default);
        return new Enrolled(identity!, response.DeviceKey);
    }

    private async Task SetConfigAsync(Guid organizationId, string version)
    {
        var settings = new SettingsDocument();
        settings.Global["ScanIntervalMinutes"] = SettingValue.From(240);
        _harness.Db.OrganizationConfigs.Add(new OrganizationConfig
        {
            OrganizationId = organizationId,
            ConfigVersion = version,
            SettingsJson = settings.ToCompactJson(),
            UpdatedUtc = _harness.Time.GetUtcNow(),
            UpdatedBy = "it@contoso.com",
        });
        await _harness.Db.SaveChangesAsync();
    }

    private static EnrollRequest NewEnrollRequest(Guid organizationId, string key, string deviceName = "CONTOSO-LT-0041", string machineGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301") => new()
    {
        OrganizationId = organizationId,
        EnrollmentKey = key,
        DeviceName = deviceName,
        MachineGuid = machineGuid,
        OsVersion = "10.0.26100.2314",
        AgentVersion = "1.0.0",
        EntraTenantId = TestHarness.CustomerTenant,
    };

    private DeviceReport NewReport(string? configVersionApplied = null) => new()
    {
        ReportedUtc = _harness.Time.GetUtcNow(),
        AgentVersion = "1.1.0",
        OsVersion = "10.0.26100.2314",
        DeviceName = "CONTOSO-LT-0041",
        LastLogonUser = "CONTOSO\\hsk",
        LastScanUtc = _harness.Time.GetUtcNow().AddMinutes(-5),
        ConfigVersionApplied = configVersionApplied,
        Prerequisites = new PrerequisiteStatus { WingetAvailable = true, WingetMeetsMinimum = true, WingetVersion = "1.9.0" },
        InstalledApps =
        [
            new ReportedApp { DisplayName = "Google Chrome", Version = "131.0", WingetId = "Google.Chrome", AvailableVersion = "132.0", Context = InstallContext.System },
            new ReportedApp { DisplayName = "7-Zip", Version = "24.09", WingetId = "7zip.7zip", Context = InstallContext.System },
        ],
        Updates =
        [
            new ReportedUpdate { AppId = "chrome", State = UpdateState.Available, InstalledVersion = "131.0", AvailableVersion = "132.0", FirstDetectedUtc = _harness.Time.GetUtcNow() },
            new ReportedUpdate { AppId = "firefox", State = UpdateState.Failed, LastError = "1603", FirstDetectedUtc = _harness.Time.GetUtcNow() },
        ],
        Events =
        [
            new ReportedEvent { OccurredUtc = _harness.Time.GetUtcNow(), Kind = ReportedEventKind.UpdateDetected, AppId = "chrome" },
        ],
    };
}
