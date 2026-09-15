using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arkimentum.AppMonitor.Api.Tests;

public sealed class AdminServiceTests : IDisposable
{
    private readonly TestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // ------------------------------------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task GlobalAdmin_SeesEveryOrganization()
    {
        await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        var outcome = await _harness.Admin.ListOrganizationsAsync(TestHarness.GlobalAdmin, default);

        Assert.Equal(["Contoso", "Fabrikam"], outcome.Value!.Select(o => o.Name));
    }

    [Fact]
    public async Task CustomerAdmin_SeesOnlyTheOrganizationMappedToItsTenant()
    {
        await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        var outcome = await _harness.Admin.ListOrganizationsAsync(TestHarness.CustomerAdmin, default);

        var only = Assert.Single(outcome.Value!);
        Assert.Equal("Contoso", only.Name);
    }

    [Fact]
    public async Task GlobalAdminRole_FromAnotherTenant_IsIgnored()
    {
        await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        // Same roles as the operator, but the token was issued by the customer's tenant.
        var impostor = new AdminPrincipal(TestHarness.CustomerTenant, "attacker@contoso.com", null, null,
            [AdminPrincipal.RoleGlobalAdmin, AdminPrincipal.RoleAdmin]);

        Assert.False(_harness.Admin.IsGlobalAdmin(impostor));
        var outcome = await _harness.Admin.ListOrganizationsAsync(impostor, default);
        Assert.Equal(["Contoso"], outcome.Value!.Select(o => o.Name));
    }

    [Fact]
    public async Task AnotherTenantsOrganization_IsForbiddenAndIndistinguishableFromNotFound()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);

        var foreignOrganization = await _harness.Admin.GetConfigAsync(TestHarness.OtherCustomerAdmin, contoso.Id, default);
        var missingOrganization = await _harness.Admin.GetConfigAsync(TestHarness.OtherCustomerAdmin, Guid.NewGuid(), default);

        Assert.Equal(403, foreignOrganization.Status);
        Assert.Equal(403, missingOrganization.Status);
        Assert.Equal(foreignOrganization.Error!.Message, missingOrganization.Error!.Message);
    }

    [Fact]
    public async Task AUserWithoutTheAdminRole_IsForbidden()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();

        var outcome = await _harness.Admin.GetConfigAsync(TestHarness.RolelessUser, contoso.Id, default);

        Assert.Equal(403, outcome.Status);
        Assert.Contains(AdminPrincipal.RoleAdmin, outcome.Error!.Message);
        Assert.Empty((await _harness.Admin.ListOrganizationsAsync(TestHarness.RolelessUser, default)).Value!);
    }

    [Fact]
    public async Task AnOrganizationWithoutATenantMapping_IsReachableOnlyByTheGlobalAdmin()
    {
        var (unmapped, _) = await _harness.SeedOrganizationAsync("Unmapped", tenantId: null);

        Assert.Equal(200, (await _harness.Admin.GetConfigAsync(TestHarness.GlobalAdmin, unmapped.Id, default)).Status);
        Assert.Equal(403, (await _harness.Admin.GetConfigAsync(TestHarness.CustomerAdmin, unmapped.Id, default)).Status);
    }

    // ------------------------------------------------------------------------------------------------ me / create

    [Fact]
    public async Task Me_ReportsTheCallerAndTheirOrganizationsWithCounts()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        await EnrollAsync(contoso.Id, key, "PC-1", "guid-1", pendingUpdates: 3);
        await EnrollAsync(contoso.Id, key, "PC-2", "guid-2", pendingUpdates: 0);

        var outcome = await _harness.Admin.MeAsync(TestHarness.CustomerAdmin, default);

        Assert.Equal("it@contoso.com", outcome.Value!.UserPrincipalName);
        Assert.False(outcome.Value.IsGlobalAdmin);
        var organization = Assert.Single(outcome.Value.Organizations);
        Assert.Equal(2, organization.DeviceCount);
        Assert.Equal(1, organization.DevicesWithPendingUpdates);
        Assert.Equal(0, organization.DevicesNotSeenIn7Days);

        _harness.Time.Advance(TimeSpan.FromDays(8));
        var later = await _harness.Admin.MeAsync(TestHarness.CustomerAdmin, default);
        Assert.Equal(2, later.Value!.Organizations[0].DevicesNotSeenIn7Days);
    }

    [Fact]
    public async Task CreateOrganization_IsGlobalAdminOnlyAndReturnsTheKeyOnce()
    {
        var forbidden = await _harness.Admin.CreateOrganizationAsync(TestHarness.CustomerAdmin,
            new CreateOrganizationRequest { Name = "Nope" }, default);
        Assert.Equal(403, forbidden.Status);

        var created = await _harness.Admin.CreateOrganizationAsync(TestHarness.GlobalAdmin,
            new CreateOrganizationRequest { Name = "Contoso A/S", EntraTenantId = TestHarness.CustomerTenant }, default);

        Assert.Equal(201, created.Status);
        var key = created.Value!.Enrollment.EnrollmentKey;
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.Equal("https://appmon-test.azurewebsites.net", created.Value.Enrollment.ServerUrl);

        // The key works exactly once as a secret: it is never returned again.
        var later = await _harness.Admin.GetEnrollmentAsync(TestHarness.GlobalAdmin, created.Value.Organization.OrganizationId, default);
        Assert.Null(later.Value!.EnrollmentKey);
        Assert.NotNull(later.Value.KeyRotatedUtc);

        // ... but it does enroll a device.
        var enrolled = await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = created.Value.Organization.OrganizationId,
            EnrollmentKey = key!,
            DeviceName = "PC",
            MachineGuid = "guid",
        }, "203.0.113.1", default);
        Assert.True(enrolled.IsSuccess);
    }

    [Fact]
    public async Task CreateOrganization_RejectsADuplicateTenantMapping()
    {
        await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);

        var outcome = await _harness.Admin.CreateOrganizationAsync(TestHarness.GlobalAdmin,
            new CreateOrganizationRequest { Name = "Contoso again", EntraTenantId = TestHarness.CustomerTenant }, default);

        Assert.Equal(409, outcome.Status);
    }

    [Fact]
    public async Task CreateOrganization_RejectsANonGuidTenantId()
    {
        var outcome = await _harness.Admin.CreateOrganizationAsync(TestHarness.GlobalAdmin,
            new CreateOrganizationRequest { Name = "Contoso", EntraTenantId = "contoso.com" }, default);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(ErrorCodes.ValidationFailed, outcome.Error!.Code);
    }

    // ------------------------------------------------------------------------------------------------ configuration

    [Fact]
    public async Task PutConfig_ValidatesAgainstTheSchema()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var settings = new SettingsDocument();
        settings.Global["ScanIntervalMinutes"] = SettingValue.From(1);       // below the minimum of 5

        var outcome = await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest { Settings = settings }, null, default);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(ErrorCodes.ValidationFailed, outcome.Error!.Code);
        Assert.Contains("between 5 and 10080", outcome.Error.Message);
        Assert.Empty(_harness.Db.OrganizationConfigs);
    }

    [Fact]
    public async Task PutConfig_WritesAHistoryRowAndBumpsTheVersion()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();

        var first = await PutValidConfigAsync(contoso.Id, 240, comment: "initial");
        var second = await PutValidConfigAsync(contoso.Id, 300, comment: "faster scans", ifMatch: first.Value!.ConfigVersion);

        Assert.Equal(1, ConfigVersioning.ParseSequence(first.Value!.ConfigVersion));
        Assert.Equal(2, ConfigVersioning.ParseSequence(second.Value!.ConfigVersion));
        Assert.NotEqual(first.Value.ConfigVersion, second.Value.ConfigVersion);
        Assert.Equal(second.Value.ConfigVersion, second.ETag);

        var history = await _harness.Admin.ConfigHistoryAsync(TestHarness.CustomerAdmin, contoso.Id, 1, 50, default);
        Assert.Equal(2, history.Value!.Total);
        Assert.Equal("faster scans", history.Value.Items[0].Comment);
        Assert.Equal("it@contoso.com", history.Value.Items[0].UpdatedBy);
    }

    [Fact]
    public async Task PutConfig_WithAStaleVersion_Is409()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var first = await PutValidConfigAsync(contoso.Id, 240);
        await PutValidConfigAsync(contoso.Id, 300, ifMatch: first.Value!.ConfigVersion);

        var stale = await PutValidConfigAsync(contoso.Id, 360, ifMatch: first.Value.ConfigVersion);

        Assert.Equal(409, stale.Status);
        Assert.Equal(ErrorCodes.Conflict, stale.Error!.Code);
    }

    [Fact]
    public async Task PutConfig_AcceptsTheBaseVersionInTheBodyToo()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var first = await PutValidConfigAsync(contoso.Id, 240);

        var settings = new SettingsDocument();
        settings.Global["ScanIntervalMinutes"] = SettingValue.From(300);

        var ok = await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest { Settings = settings, BaseConfigVersion = first.Value!.ConfigVersion }, null, default);
        Assert.Equal(200, ok.Status);

        var stale = await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest { Settings = settings, BaseConfigVersion = first.Value.ConfigVersion }, null, default);
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task PutConfig_WithoutAnyVersion_Overwrites()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        await PutValidConfigAsync(contoso.Id, 240);

        var outcome = await PutValidConfigAsync(contoso.Id, 300);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(300, outcome.Value!.Settings.Global["ScanIntervalMinutes"].AsInt());
    }

    [Fact]
    public async Task PutConfig_RejectsAnUnknownSchema()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var settings = new SettingsDocument { Schema = "arkimentum-appmonitor-settings/9" };

        var outcome = await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest { Settings = settings }, null, default);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(ErrorCodes.ValidationFailed, outcome.Error!.Code);
    }

    [Fact]
    public async Task PutConfig_IsImmediatelyVisibleToDevices()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var device = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");

        var saved = await PutValidConfigAsync(contoso.Id, 300);
        var config = await _harness.Devices.GetConfigAsync(device, null, default);

        Assert.Equal(saved.Value!.ConfigVersion, config.Value!.ConfigVersion);
        Assert.Equal(300, config.Value.Settings.Global["ScanIntervalMinutes"].AsInt());
    }

    // ------------------------------------------------------------------------------------------------ devices

    [Fact]
    public async Task ListDevices_SearchesAndPages()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        for (var i = 0; i < 7; i++) await EnrollAsync(contoso.Id, key, $"CONTOSO-LT-{i:00}", $"guid-{i}");
        await EnrollAsync(contoso.Id, key, "CONTOSO-SRV-01", "guid-srv");

        var all = await _harness.Admin.ListDevicesAsync(TestHarness.CustomerAdmin, contoso.Id, null, 1, 5, default);
        Assert.Equal(8, all.Value!.Total);
        Assert.Equal(5, all.Value.Items.Count);

        var page2 = await _harness.Admin.ListDevicesAsync(TestHarness.CustomerAdmin, contoso.Id, null, 2, 5, default);
        Assert.Equal(3, page2.Value!.Items.Count);

        var search = await _harness.Admin.ListDevicesAsync(TestHarness.CustomerAdmin, contoso.Id, "SRV", 1, 50, default);
        Assert.Equal("CONTOSO-SRV-01", Assert.Single(search.Value!.Items).DeviceName);
    }

    [Fact]
    public async Task ListDevices_NeverCrossesOrganizations()
    {
        var (contoso, contosoKey) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var (fabrikam, fabrikamKey) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);
        await EnrollAsync(contoso.Id, contosoKey, "CONTOSO-LT-01", "guid-c");
        await EnrollAsync(fabrikam.Id, fabrikamKey, "FABRIKAM-LT-01", "guid-f");

        var outcome = await _harness.Admin.ListDevicesAsync(TestHarness.CustomerAdmin, contoso.Id, null, 1, 50, default);

        Assert.Equal("CONTOSO-LT-01", Assert.Single(outcome.Value!.Items).DeviceName);
    }

    [Fact]
    public async Task GetDevice_ReturnsTheFullSnapshot()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var device = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");
        await _harness.Devices.ReportAsync(device, new DeviceReport
        {
            ReportedUtc = _harness.Time.GetUtcNow(),
            AgentVersion = "1.1.0",
            Prerequisites = new PrerequisiteStatus { WingetAvailable = true, WingetMeetsMinimum = true },
            InstalledApps = [new ReportedApp { DisplayName = "7-Zip", Version = "24.09", WingetId = "7zip.7zip" }],
            Updates = [new ReportedUpdate { AppId = "chrome", State = UpdateState.Available, FirstDetectedUtc = _harness.Time.GetUtcNow() }],
            Events = [new ReportedEvent { OccurredUtc = _harness.Time.GetUtcNow(), Kind = ReportedEventKind.UpdateDetected }],
        }, "{}", default);

        var outcome = await _harness.Admin.GetDeviceAsync(TestHarness.CustomerAdmin, contoso.Id, device.DeviceId, default);

        Assert.Equal(200, outcome.Status);
        Assert.Equal("guid-1", outcome.Value!.MachineGuid);
        Assert.True(outcome.Value.Summary.PrerequisitesHealthy);
        Assert.Single(outcome.Value.InstalledApps);
        Assert.Single(outcome.Value.Updates);
        Assert.Single(outcome.Value.RecentEvents);
    }

    [Fact]
    public async Task DeleteDevice_SoftDeletesAndRevokesTheDeviceKey()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var enrollment = await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = contoso.Id, EnrollmentKey = key, DeviceName = "PC-1", MachineGuid = "guid-1",
        }, "203.0.113.1", default);
        var deviceId = enrollment.Value!.DeviceId;
        await _harness.Devices.ReportAsync(
            (await _harness.Authenticator.AuthenticateAsync($"Device {deviceId}:{enrollment.Value.DeviceKey}", default))!,
            new DeviceReport { ReportedUtc = _harness.Time.GetUtcNow(), AgentVersion = "1.0.0", InstalledApps = [new ReportedApp { DisplayName = "7-Zip" }] },
            "{}", default);

        var outcome = await _harness.Admin.DeleteDeviceAsync(TestHarness.CustomerAdmin, contoso.Id, deviceId, default);

        Assert.Equal(204, outcome.Status);
        Assert.True((await _harness.Db.Devices.AsNoTracking().SingleAsync()).IsDeleted);
        Assert.Empty(_harness.Db.DeviceApps);
        Assert.Null(await _harness.Authenticator.AuthenticateAsync($"Device {deviceId}:{enrollment.Value.DeviceKey}", default));
        Assert.Equal(404, (await _harness.Admin.GetDeviceAsync(TestHarness.CustomerAdmin, contoso.Id, deviceId, default)).Status);
        Assert.Equal(404, (await _harness.Admin.DeleteDeviceAsync(TestHarness.CustomerAdmin, contoso.Id, deviceId, default)).Status);
    }

    [Fact]
    public async Task DeleteDevice_FromAnotherOrganization_IsForbidden()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);
        var device = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");

        var outcome = await _harness.Admin.DeleteDeviceAsync(TestHarness.OtherCustomerAdmin, contoso.Id, device.DeviceId, default);

        Assert.Equal(403, outcome.Status);
        Assert.False((await _harness.Db.Devices.AsNoTracking().SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task CreateCommand_QueuesItForTheDevice()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var device = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");

        var outcome = await _harness.Admin.CreateCommandAsync(TestHarness.CustomerAdmin, contoso.Id, device.DeviceId,
            new DeviceCommandRequest { Kind = DeviceCommandKind.ScanNow }, default);

        Assert.Equal(201, outcome.Status);
        Assert.Equal("it@contoso.com", outcome.Value!.IssuedBy);

        var config = await _harness.Devices.GetConfigAsync(device, null, default);
        Assert.Equal(outcome.Value.CommandId, Assert.Single(config.Value!.Commands).CommandId);
    }

    [Fact]
    public async Task CreateCommand_ForAnUnknownDevice_Is404()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();

        var outcome = await _harness.Admin.CreateCommandAsync(TestHarness.CustomerAdmin, contoso.Id, Guid.NewGuid(),
            new DeviceCommandRequest { Kind = DeviceCommandKind.ScanNow }, default);

        Assert.Equal(404, outcome.Status);
    }

    [Fact]
    public async Task CreateCommand_WithAnUnknownKind_IsRejected()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var device = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");

        var outcome = await _harness.Admin.CreateCommandAsync(TestHarness.CustomerAdmin, contoso.Id, device.DeviceId,
            new DeviceCommandRequest { Kind = (DeviceCommandKind)99 }, default);

        Assert.Equal(400, outcome.Status);
    }

    // ------------------------------------------------------------------------------------- one historical revision

    [Fact]
    public async Task GetConfigRevision_ReturnsTheSettingsAsTheyWereAtThatVersion()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var first = await PutValidConfigAsync(contoso.Id, 240, comment: "initial");
        _harness.Time.Advance(TimeSpan.FromHours(1));
        var second = await PutValidConfigAsync(contoso.Id, 300, comment: "faster scans", ifMatch: first.Value!.ConfigVersion);

        var history = (await _harness.Admin.ConfigHistoryAsync(TestHarness.CustomerAdmin, contoso.Id, 1, 50, default)).Value!;
        var oldest = history.Items.Single(h => h.ConfigVersion == first.Value.ConfigVersion);

        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, contoso.Id, oldest.Id, default);

        Assert.Equal(200, outcome.Status);
        var revision = outcome.Value!;
        Assert.Equal(contoso.Id, revision.OrganizationId);
        Assert.Equal(first.Value.ConfigVersion, revision.ConfigVersion);     // the historical version, not the current one
        Assert.Equal(oldest.UpdatedUtc, revision.UpdatedUtc);
        Assert.Equal("it@contoso.com", revision.UpdatedBy);
        Assert.Equal(240, revision.Settings.Global["ScanIntervalMinutes"].AsInt());

        // The current configuration is untouched and still the newer one.
        var current = await _harness.Admin.GetConfigAsync(TestHarness.CustomerAdmin, contoso.Id, default);
        Assert.Equal(second.Value!.ConfigVersion, current.Value!.ConfigVersion);
        Assert.Equal(300, current.Value.Settings.Global["ScanIntervalMinutes"].AsInt());
    }

    [Fact]
    public async Task GetConfigRevision_ReturnsNoETagSoARestoreCannotSendAHistoricalIfMatch()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        await PutValidConfigAsync(contoso.Id, 240);
        var entry = (await _harness.Admin.ConfigHistoryAsync(TestHarness.CustomerAdmin, contoso.Id, 1, 50, default)).Value!.Items[0];

        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, contoso.Id, entry.Id, default);

        Assert.Null(outcome.ETag);
    }

    [Fact]
    public async Task GetConfigRevision_CanBeRestoredByPuttingItBackAgainstTheCurrentVersion()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var first = await PutValidConfigAsync(contoso.Id, 240);
        var second = await PutValidConfigAsync(contoso.Id, 300, ifMatch: first.Value!.ConfigVersion);

        var entry = (await _harness.Admin.ConfigHistoryAsync(TestHarness.CustomerAdmin, contoso.Id, 1, 50, default))
            .Value!.Items.Single(h => h.ConfigVersion == first.Value.ConfigVersion);
        var revision = (await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, contoso.Id, entry.Id, default)).Value!;

        var restored = await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest
            {
                Settings = revision.Settings,
                Comment = $"Restored {revision.ConfigVersion}",
            }, second.Value!.ConfigVersion, default);

        Assert.Equal(200, restored.Status);
        Assert.Equal(240, restored.Value!.Settings.Global["ScanIntervalMinutes"].AsInt());
        Assert.Equal(3, ConfigVersioning.ParseSequence(restored.Value.ConfigVersion));   // a new revision, not a rewind
    }

    [Fact]
    public async Task GetConfigRevision_WithAnUnknownId_Is404()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        await PutValidConfigAsync(contoso.Id, 240);

        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, contoso.Id, 999_999, default);

        Assert.Equal(404, outcome.Status);
        Assert.Equal(ErrorCodes.NotFound, outcome.Error!.Code);
    }

    [Fact]
    public async Task GetConfigRevision_OfAnotherOrganizationsRevision_Is404()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var (fabrikam, _) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        await PutValidConfigAsync(contoso.Id, 240);
        var fabrikamSettings = new SettingsDocument();
        fabrikamSettings.Global["ScanIntervalMinutes"] = SettingValue.From(600);
        await _harness.Admin.PutConfigAsync(TestHarness.OtherCustomerAdmin, fabrikam.Id,
            new OrganizationConfigUpdateRequest { Settings = fabrikamSettings }, null, default);

        var fabrikamEntry = (await _harness.Admin.ConfigHistoryAsync(TestHarness.OtherCustomerAdmin, fabrikam.Id, 1, 50, default)).Value!.Items[0];

        // Contoso's administrator asks for Contoso, but with Fabrikam's history id.
        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, contoso.Id, fabrikamEntry.Id, default);

        Assert.Equal(404, outcome.Status);

        // ... and cannot reach it through Fabrikam's own organization id either.
        Assert.Equal(403, (await _harness.Admin.GetConfigRevisionAsync(TestHarness.CustomerAdmin, fabrikam.Id, fabrikamEntry.Id, default)).Status);
    }

    [Fact]
    public async Task GetConfigRevision_IsReachableByTheGlobalAdminForEveryOrganization()
    {
        var (fabrikam, _) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);
        var settings = new SettingsDocument();
        settings.Global["ScanIntervalMinutes"] = SettingValue.From(600);
        await _harness.Admin.PutConfigAsync(TestHarness.OtherCustomerAdmin, fabrikam.Id,
            new OrganizationConfigUpdateRequest { Settings = settings }, null, default);
        var entry = (await _harness.Admin.ConfigHistoryAsync(TestHarness.GlobalAdmin, fabrikam.Id, 1, 50, default)).Value!.Items[0];

        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.GlobalAdmin, fabrikam.Id, entry.Id, default);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(600, outcome.Value!.Settings.Global["ScanIntervalMinutes"].AsInt());
    }

    [Fact]
    public async Task GetConfigRevision_WithoutAnAppRole_IsForbidden()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        await PutValidConfigAsync(contoso.Id, 240);
        var entry = (await _harness.Admin.ConfigHistoryAsync(TestHarness.CustomerAdmin, contoso.Id, 1, 50, default)).Value!.Items[0];

        var outcome = await _harness.Admin.GetConfigRevisionAsync(TestHarness.RolelessUser, contoso.Id, entry.Id, default);

        Assert.Equal(403, outcome.Status);
    }

    // ------------------------------------------------------------------------------------------------ enrollment key

    [Fact]
    public async Task RotateEnrollmentKey_InvalidatesTheOldKeyAndReturnsTheNewOneOnce()
    {
        var (contoso, oldKey) = await _harness.SeedOrganizationAsync();

        var rotated = await _harness.Admin.RotateEnrollmentKeyAsync(TestHarness.CustomerAdmin, contoso.Id, default);

        Assert.Equal(200, rotated.Status);
        var newKey = rotated.Value!.EnrollmentKey!;
        Assert.NotEqual(oldKey, newKey);

        var withOld = await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = contoso.Id, EnrollmentKey = oldKey, DeviceName = "PC", MachineGuid = "guid-old",
        }, "203.0.113.1", default);
        Assert.Equal(401, withOld.Status);

        var withNew = await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = contoso.Id, EnrollmentKey = newKey, DeviceName = "PC", MachineGuid = "guid-new",
        }, "203.0.113.1", default);
        Assert.True(withNew.IsSuccess);

        // An already enrolled device keeps working: it holds a device key, not the enrollment key.
        Assert.Null((await _harness.Admin.GetEnrollmentAsync(TestHarness.CustomerAdmin, contoso.Id, default)).Value!.EnrollmentKey);
    }

    // ------------------------------------------------------------------------------------------------ release mirror

    [Fact]
    public async Task PutRelease_IsGlobalAdminOnlyAndValidatesTheManifest()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync();
        var manifest = new ReleaseManifest
        {
            Version = "1.3.0",
            Channel = "stable",
            PackageUrl = "https://example.invalid/appmonitor-1.3.0.zip",
            Sha256 = new string('c', 64),
        };

        Assert.Equal(403, (await _harness.Admin.PutReleaseAsync(TestHarness.CustomerAdmin, contoso.Id, "stable", manifest, default)).Status);

        var bad = await _harness.Admin.PutReleaseAsync(TestHarness.GlobalAdmin, contoso.Id, "stable",
            new ReleaseManifest { Version = "1.3.0", PackageUrl = "http://insecure.invalid/a.zip", Sha256 = "short" }, default);
        Assert.Equal(400, bad.Status);
        Assert.Contains("https", bad.Error!.Message);

        Assert.Equal(200, (await _harness.Admin.PutReleaseAsync(TestHarness.GlobalAdmin, contoso.Id, "stable", manifest, default)).Status);
        Assert.Equal("1.3.0", (await _harness.Admin.GetReleaseAsync(TestHarness.CustomerAdmin, contoso.Id, "stable", default)).Value!.Version);
        Assert.Equal("1.3.0", (await _harness.Devices.GetReleaseAsync("stable", default)).Value!.Version);
    }

    // ------------------------------------------------------------------------------------------------ events

    [Fact]
    public async Task Events_AreScopedPagedAndFilterableBySince()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var (fabrikam, fabrikamKey) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);
        var contosoDevice = await EnrollAsync(contoso.Id, key, "CONTOSO-LT-01", "guid-c");
        var fabrikamDevice = await EnrollAsync(fabrikam.Id, fabrikamKey, "FABRIKAM-LT-01", "guid-f");

        var cutoff = _harness.Time.GetUtcNow();
        await ReportEventsAsync(contosoDevice, cutoff.AddHours(-2), cutoff.AddMinutes(5));
        await ReportEventsAsync(fabrikamDevice, cutoff.AddMinutes(5));

        var all = await _harness.Admin.EventsAsync(TestHarness.CustomerAdmin, contoso.Id, null, 1, 50, default);
        Assert.Equal(2, all.Value!.Total);
        Assert.All(all.Value.Items, e => Assert.Equal("CONTOSO-LT-01", e.DeviceName));

        var since = await _harness.Admin.EventsAsync(TestHarness.CustomerAdmin, contoso.Id, cutoff, 1, 50, default);
        Assert.Equal(1, since.Value!.Total);
    }

    // ------------------------------------------------------------------------------------------------ audit

    [Fact]
    public async Task EveryMutatingCall_WritesAnAuditRow()
    {
        var created = await _harness.Admin.CreateOrganizationAsync(TestHarness.GlobalAdmin,
            new CreateOrganizationRequest { Name = "Contoso", EntraTenantId = TestHarness.CustomerTenant }, default);
        var organizationId = created.Value!.Organization.OrganizationId;

        await PutValidConfigAsync(organizationId, 240);
        await _harness.Admin.RotateEnrollmentKeyAsync(TestHarness.CustomerAdmin, organizationId, default);

        var actions = await _harness.Db.AuditLog.AsNoTracking().Select(a => a.Action).ToListAsync();
        Assert.Contains("organization.created", actions);
        Assert.Contains("config.updated", actions);
        Assert.Contains("enrollment.rotated", actions);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<ApiOutcome<OrganizationConfigResponse>> PutValidConfigAsync(
        Guid organizationId, int scanInterval, string? comment = null, string? ifMatch = null)
    {
        var settings = new SettingsDocument();
        settings.Global["ScanIntervalMinutes"] = SettingValue.From(scanInterval);
        return await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, organizationId,
            new OrganizationConfigUpdateRequest { Settings = settings, Comment = comment }, ifMatch, default);
    }

    private async Task<DeviceIdentity> EnrollAsync(Guid organizationId, string key, string name, string machineGuid, int pendingUpdates = 0)
    {
        var response = (await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = organizationId,
            EnrollmentKey = key,
            DeviceName = name,
            MachineGuid = machineGuid,
        }, "203.0.113.1", default)).Value!;

        if (pendingUpdates > 0)
        {
            var device = await _harness.Db.Devices.SingleAsync(d => d.Id == response.DeviceId);
            device.PendingUpdateCount = pendingUpdates;
            await _harness.Db.SaveChangesAsync();
        }

        return (await _harness.Authenticator.AuthenticateAsync($"Device {response.DeviceId}:{response.DeviceKey}", default))!;
    }

    private Task ReportEventsAsync(DeviceIdentity device, params DateTimeOffset[] occurredUtc) =>
        _harness.Devices.ReportAsync(device, new DeviceReport
        {
            ReportedUtc = _harness.Time.GetUtcNow(),
            AgentVersion = "1.0.0",
            Events = [.. occurredUtc.Select(o => new ReportedEvent { OccurredUtc = o, Kind = ReportedEventKind.InstallSucceeded, AppId = "chrome" })],
        }, "{}", default);
}
