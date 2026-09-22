using System.Text.Json;
using Xunit;
using ApiWire = global::Arkimentum.AppMonitor.Api.Contracts;
using CoreWire = global::Arkimentum.AppMonitor.Cloud;
using CoreConfig = global::Arkimentum.AppMonitor.Configuration;
using CoreModels = global::Arkimentum.AppMonitor.Models;
using CorePrereq = global::Arkimentum.AppMonitor.Prerequisites;

namespace Arkimentum.AppMonitor.Api.Tests;

/// <summary>
/// The contract tests. src/Arkimentum.AppMonitor.Core/Cloud/CloudContracts.cs is the source of truth; the API keeps
/// its own copy because it cannot reference a net10.0-windows assembly. Every DTO is serialised with the API's
/// converters, read back with Core's converters, re-serialised, and compared byte for byte - so a property that is
/// added, renamed, reordered or typed differently on either side fails here rather than in production.
/// </summary>
public sealed class ContractRoundTripTests
{
    private static readonly JsonSerializerOptions ApiJson = ApiWire.CloudJson.Options;
    private static readonly JsonSerializerOptions CoreJson = CoreWire.CloudJson.Options;

    private static void RoundTrip<TApi, TCore>(TApi value)
    {
        var apiJson = JsonSerializer.Serialize(value, ApiJson);

        var core = JsonSerializer.Deserialize<TCore>(apiJson, CoreJson);
        Assert.NotNull(core);
        var coreJson = JsonSerializer.Serialize(core, CoreJson);
        Assert.Equal(apiJson, coreJson);

        var back = JsonSerializer.Deserialize<TApi>(coreJson, ApiJson);
        Assert.NotNull(back);
        Assert.Equal(apiJson, JsonSerializer.Serialize(back, ApiJson));
    }

    // ------------------------------------------------------------------------------------------------ enrollment

    [Fact]
    public void EnrollRequest_RoundTrips() => RoundTrip<ApiWire.EnrollRequest, CoreWire.EnrollRequest>(new ApiWire.EnrollRequest
    {
        OrganizationId = Guid.Parse("7b6b2b1e-1f0e-4a1a-8b1e-0f0e1a2b3c4d"),
        EnrollmentKey = "Zm9vYmFyLWtleS12YWx1ZQ",
        DeviceName = "CONTOSO-LT-0041",
        MachineGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
        EntraTenantId = "22222222-2222-2222-2222-222222222222",
        EntraDeviceId = "44444444-4444-4444-4444-444444444444",
        OsVersion = "10.0.26100.2314",
        AgentVersion = "1.0.0",
    });

    [Fact]
    public void EnrollRequest_WithOnlyRequiredFields_RoundTrips() => RoundTrip<ApiWire.EnrollRequest, CoreWire.EnrollRequest>(new ApiWire.EnrollRequest
    {
        OrganizationId = Guid.NewGuid(),
        EnrollmentKey = "key",
        DeviceName = "PC",
        MachineGuid = "guid",
    });

    [Fact]
    public void EnrollResponse_RoundTrips() => RoundTrip<ApiWire.EnrollResponse, CoreWire.EnrollResponse>(new ApiWire.EnrollResponse
    {
        DeviceId = Guid.Parse("11112222-3333-4444-5555-666677778888"),
        DeviceKey = "c2VjcmV0LWRldmljZS1rZXk",
        OrganizationName = "Contoso A/S",
        ConfigVersion = "7-1a2b3c4d",
    });

    // ------------------------------------------------------------------------------------------------ configuration

    [Fact]
    public void DeviceConfigResponse_RoundTrips() => RoundTrip<ApiWire.DeviceConfigResponse, CoreWire.DeviceConfigResponse>(new ApiWire.DeviceConfigResponse
    {
        OrganizationId = Guid.NewGuid(),
        OrganizationName = "Contoso A/S",
        ConfigVersion = "12-aabbccdd",
        UpdatedUtc = new DateTimeOffset(2026, 2, 3, 9, 30, 0, TimeSpan.Zero),
        UpdatedBy = "it@contoso.com",
        Settings = SampleSettings(),
        PollIntervalSeconds = 600,
        Commands =
        [
            new ApiWire.DeviceCommand
            {
                CommandId = Guid.Parse("99998888-7777-6666-5555-444433332222"),
                Kind = ApiWire.DeviceCommandKind.RepairPrerequisites,
                Argument = "--force",
                IssuedUtc = new DateTimeOffset(2026, 2, 3, 10, 0, 0, TimeSpan.Zero),
                IssuedBy = "it@contoso.com",
            },
        ],
    });

    [Theory]
    [InlineData(ApiWire.DeviceCommandKind.ScanNow)]
    [InlineData(ApiWire.DeviceCommandKind.RepairPrerequisites)]
    [InlineData(ApiWire.DeviceCommandKind.UpdateAgent)]
    [InlineData(ApiWire.DeviceCommandKind.ReportNow)]
    public void DeviceCommand_RoundTripsEveryKind(ApiWire.DeviceCommandKind kind) =>
        RoundTrip<ApiWire.DeviceCommand, CoreWire.DeviceCommand>(new ApiWire.DeviceCommand
        {
            CommandId = Guid.NewGuid(),
            Kind = kind,
            IssuedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });

    [Fact]
    public void DeviceCommandKind_SerialisesAsCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new ApiWire.DeviceCommandRequest { Kind = ApiWire.DeviceCommandKind.RepairPrerequisites }, ApiJson);
        Assert.Equal("{\"kind\":\"repairPrerequisites\"}", json);
    }

    [Fact]
    public void DeviceCommandRequest_RoundTrips() => RoundTrip<ApiWire.DeviceCommandRequest, CoreWire.DeviceCommandRequest>(
        new ApiWire.DeviceCommandRequest { Kind = ApiWire.DeviceCommandKind.ScanNow, Argument = "now" });

    // ------------------------------------------------------------------------------------------------ reporting

    [Fact]
    public void DeviceReport_RoundTrips() => RoundTrip<ApiWire.DeviceReport, CoreWire.DeviceReport>(SampleReport());

    [Fact]
    public void DeviceReport_Empty_RoundTrips() => RoundTrip<ApiWire.DeviceReport, CoreWire.DeviceReport>(new ApiWire.DeviceReport
    {
        ReportedUtc = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero),
        AgentVersion = "1.0.0",
    });

    [Fact]
    public void ReportedApp_RoundTrips() => RoundTrip<ApiWire.ReportedApp, CoreWire.ReportedApp>(new ApiWire.ReportedApp
    {
        DisplayName = "Mozilla Firefox (x64 en-US)",
        Version = "135.0.1",
        Publisher = "Mozilla",
        WingetId = "Mozilla.Firefox",
        AvailableVersion = "136.0",
        Context = ApiWire.InstallContext.System,
        CatalogAppId = "firefox",
        MonitoredAppId = "firefox",
    });

    [Theory]
    [InlineData(ApiWire.UpdateState.Available, ApiWire.InstallContext.Auto)]
    [InlineData(ApiWire.UpdateState.Deferred, ApiWire.InstallContext.System)]
    [InlineData(ApiWire.UpdateState.Scheduled, ApiWire.InstallContext.User)]
    [InlineData(ApiWire.UpdateState.WaitingForClose, ApiWire.InstallContext.Auto)]
    [InlineData(ApiWire.UpdateState.Installing, ApiWire.InstallContext.System)]
    [InlineData(ApiWire.UpdateState.Installed, ApiWire.InstallContext.User)]
    [InlineData(ApiWire.UpdateState.Failed, ApiWire.InstallContext.Auto)]
    public void ReportedUpdate_RoundTripsEveryState(ApiWire.UpdateState state, ApiWire.InstallContext context) =>
        RoundTrip<ApiWire.ReportedUpdate, CoreWire.ReportedUpdate>(new ApiWire.ReportedUpdate
        {
            AppId = "chrome",
            DisplayName = "Google Chrome",
            InstalledVersion = "131.0.6778.86",
            AvailableVersion = "132.0.6834.83",
            State = state,
            Context = context,
            Mandatory = true,
            DeadlineUtc = new DateTimeOffset(2026, 2, 10, 12, 0, 0, TimeSpan.Zero),
            DeferredUntilUtc = new DateTimeOffset(2026, 2, 9, 12, 0, 0, TimeSpan.Zero),
            DeferralCount = 2,
            FirstDetectedUtc = new DateTimeOffset(2026, 2, 7, 12, 0, 0, TimeSpan.Zero),
            InstalledAtUtc = null,
            LastError = "The installer returned 1603.",
        });

    [Theory]
    [InlineData(ApiWire.ReportedEventKind.UpdateDetected)]
    [InlineData(ApiWire.ReportedEventKind.InstallSucceeded)]
    [InlineData(ApiWire.ReportedEventKind.InstallFailed)]
    [InlineData(ApiWire.ReportedEventKind.Deferred)]
    [InlineData(ApiWire.ReportedEventKind.ForcedClose)]
    [InlineData(ApiWire.ReportedEventKind.PrerequisiteRepaired)]
    [InlineData(ApiWire.ReportedEventKind.AgentUpdated)]
    public void ReportedEvent_RoundTripsEveryKind(ApiWire.ReportedEventKind kind) =>
        RoundTrip<ApiWire.ReportedEvent, CoreWire.ReportedEvent>(new ApiWire.ReportedEvent
        {
            OccurredUtc = new DateTimeOffset(2026, 2, 8, 7, 6, 5, TimeSpan.Zero),
            Kind = kind,
            AppId = "chrome",
            Message = "Installed",
            FromVersion = "131.0",
            ToVersion = "132.0",
        });

    [Fact]
    public void ReportResponse_RoundTrips() => RoundTrip<ApiWire.ReportResponse, CoreWire.ReportResponse>(
        new ApiWire.ReportResponse { Accepted = true, ConfigChanged = true, ConfigVersion = "13-deadbeef" });

    [Fact]
    public void PrerequisiteStatus_RoundTrips() => RoundTrip<ApiWire.PrerequisiteStatus, CorePrereq.PrerequisiteStatus>(SamplePrerequisites());

    [Fact]
    public void PrerequisiteStatus_Unhealthy_RoundTrips() => RoundTrip<ApiWire.PrerequisiteStatus, CorePrereq.PrerequisiteStatus>(
        new ApiWire.PrerequisiteStatus { WingetAvailable = false, MinimumVersion = "1.6.0", LastError = "winget was not found." });

    // ------------------------------------------------------------------------------------------------ releases

    [Fact]
    public void ReleaseManifest_RoundTrips() => RoundTrip<ApiWire.ReleaseManifest, CoreWire.ReleaseManifest>(new ApiWire.ReleaseManifest
    {
        Version = "1.2.0",
        Channel = "stable",
        PackageUrl = "https://example.invalid/Arkimentum.AppMonitor-1.2.0.zip",
        Sha256 = new string('a', 64),
        SizeBytes = 52_428_800,
        PublishedUtc = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        MinimumSupportedVersion = "1.0.0",
        ReleaseNotesUrl = "https://example.invalid/notes",
    });

    // ------------------------------------------------------------------------------------------------ admin API

    [Fact]
    public void AuthConfigResponse_RoundTrips() => RoundTrip<ApiWire.AuthConfigResponse, CoreWire.AuthConfigResponse>(new ApiWire.AuthConfigResponse
    {
        ClientId = "bbbbbbbb-0000-0000-0000-000000000002",
        Authority = "https://login.microsoftonline.com/organizations",
        Scope = "api://aaaaaaaa-0000-0000-0000-000000000001/AppMonitor.Access",
        ApiVersion = "v1",
        WebAdminUrl = "https://appmon-prod-web-ab12cd.azurestaticapps.net",
    });

    [Fact]
    public void AdminMeResponse_RoundTrips() => RoundTrip<ApiWire.AdminMeResponse, CoreWire.AdminMeResponse>(new ApiWire.AdminMeResponse
    {
        UserPrincipalName = "it@contoso.com",
        DisplayName = "Contoso IT",
        TenantId = "22222222-2222-2222-2222-222222222222",
        IsGlobalAdmin = false,
        Organizations = [SampleOrganizationSummary()],
    });

    [Fact]
    public void OrganizationSummary_RoundTrips() => RoundTrip<ApiWire.OrganizationSummary, CoreWire.OrganizationSummary>(SampleOrganizationSummary());

    [Fact]
    public void DeviceSummary_RoundTrips() => RoundTrip<ApiWire.DeviceSummary, CoreWire.DeviceSummary>(SampleDeviceSummary());

    [Fact]
    public void DeviceDetail_RoundTrips() => RoundTrip<ApiWire.DeviceDetail, CoreWire.DeviceDetail>(new ApiWire.DeviceDetail
    {
        Summary = SampleDeviceSummary(),
        MachineGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
        EntraDeviceId = "44444444-4444-4444-4444-444444444444",
        Prerequisites = SamplePrerequisites(),
        InstalledApps = [.. SampleReport().InstalledApps],
        Updates = [.. SampleReport().Updates],
        RecentEvents = [.. SampleReport().Events],
        PendingCommands =
        [
            new ApiWire.DeviceCommand { CommandId = Guid.NewGuid(), Kind = ApiWire.DeviceCommandKind.ScanNow, IssuedUtc = DateTimeOffset.UnixEpoch },
        ],
    });

    [Fact]
    public void OrganizationInventoryItem_RoundTrips() => RoundTrip<ApiWire.OrganizationInventoryItem, CoreWire.OrganizationInventoryItem>(
        new ApiWire.OrganizationInventoryItem
        {
            DisplayName = "7-Zip 24.09 (x64)",
            WingetId = "7zip.7zip",
            Publisher = "Igor Pavlov",
            CatalogAppId = "7zip",
            MonitoredAppId = "sevenzip",
            DeviceCount = 42,
            DevicesWithUpdateAvailable = 7,
            Versions = [new ApiWire.VersionCount { Version = "24.09", DeviceCount = 35 }, new ApiWire.VersionCount { Version = "23.01", DeviceCount = 7 }],
            PredominantContext = ApiWire.InstallContext.System,
            LastSeenUtc = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
        });

    [Fact]
    public void VersionCount_RoundTrips() => RoundTrip<ApiWire.VersionCount, CoreWire.VersionCount>(
        new ApiWire.VersionCount { Version = "1.0", DeviceCount = 3 });

    [Fact]
    public void OrganizationConfigResponse_RoundTrips() => RoundTrip<ApiWire.OrganizationConfigResponse, CoreWire.OrganizationConfigResponse>(
        new ApiWire.OrganizationConfigResponse
        {
            OrganizationId = Guid.NewGuid(),
            ConfigVersion = "3-0f0f0f0f",
            UpdatedUtc = new DateTimeOffset(2026, 7, 7, 7, 7, 7, TimeSpan.Zero),
            UpdatedBy = "it@contoso.com",
            Settings = SampleSettings(),
        });

    [Fact]
    public void OrganizationConfigUpdateRequest_RoundTrips() => RoundTrip<ApiWire.OrganizationConfigUpdateRequest, CoreWire.OrganizationConfigUpdateRequest>(
        new ApiWire.OrganizationConfigUpdateRequest
        {
            BaseConfigVersion = "3-0f0f0f0f",
            Settings = SampleSettings(),
            Comment = "Added Chrome with a 72 hour deadline",
        });

    [Fact]
    public void EnrollmentInfoResponse_RoundTrips() => RoundTrip<ApiWire.EnrollmentInfoResponse, CoreWire.EnrollmentInfoResponse>(
        new ApiWire.EnrollmentInfoResponse
        {
            OrganizationId = Guid.NewGuid(),
            EnrollmentKey = "a-one-time-key",
            KeyRotatedUtc = new DateTimeOffset(2026, 8, 8, 8, 8, 8, TimeSpan.Zero),
            ServerUrl = "https://appmon-prod-api.azurewebsites.net",
        });

    // ------------------------------------------------------------------- shapes added to Core for the admin console

    [Fact]
    public void PagedResultOfDeviceSummary_RoundTrips() =>
        RoundTrip<ApiWire.PagedResult<ApiWire.DeviceSummary>, CoreWire.PagedResult<CoreWire.DeviceSummary>>(
            new ApiWire.PagedResult<ApiWire.DeviceSummary>
            {
                Items = [SampleDeviceSummary()],
                Total = 137,
                Page = 3,
                PageSize = 50,
            });

    [Fact]
    public void PagedResultOfConfigHistory_RoundTrips() =>
        RoundTrip<ApiWire.PagedResult<ApiWire.ConfigHistoryEntry>, CoreWire.PagedResult<CoreWire.ConfigHistoryEntry>>(
            new ApiWire.PagedResult<ApiWire.ConfigHistoryEntry> { Items = [SampleConfigHistoryEntry()], Total = 1 });

    [Fact]
    public void PagedResultOfOrganizationEvent_RoundTrips() =>
        RoundTrip<ApiWire.PagedResult<ApiWire.OrganizationEvent>, CoreWire.PagedResult<CoreWire.OrganizationEvent>>(
            new ApiWire.PagedResult<ApiWire.OrganizationEvent> { Items = [SampleOrganizationEvent()], Total = 1 });

    [Fact]
    public void EmptyPagedResult_RoundTrips() =>
        RoundTrip<ApiWire.PagedResult<ApiWire.DeviceSummary>, CoreWire.PagedResult<CoreWire.DeviceSummary>>(
            new ApiWire.PagedResult<ApiWire.DeviceSummary>());

    [Fact]
    public void CreateOrganizationRequest_RoundTrips() =>
        RoundTrip<ApiWire.CreateOrganizationRequest, CoreWire.CreateOrganizationRequest>(
            new ApiWire.CreateOrganizationRequest { Name = "Contoso A/S", EntraTenantId = "22222222-2222-2222-2222-222222222222" });

    [Fact]
    public void CreateOrganizationRequest_WithoutATenant_RoundTrips() =>
        RoundTrip<ApiWire.CreateOrganizationRequest, CoreWire.CreateOrganizationRequest>(
            new ApiWire.CreateOrganizationRequest { Name = "Contoso A/S" });

    [Fact]
    public void CreateOrganizationResponse_RoundTrips() =>
        RoundTrip<ApiWire.CreateOrganizationResponse, CoreWire.CreateOrganizationResponse>(
            new ApiWire.CreateOrganizationResponse
            {
                Organization = SampleOrganizationSummary(),
                Enrollment = new ApiWire.EnrollmentInfoResponse
                {
                    OrganizationId = Guid.Parse("7b6b2b1e-1f0e-4a1a-8b1e-0f0e1a2b3c4d"),
                    EnrollmentKey = "ek_live_kQ7fZ2x1Tn8sV4bR6mJpL9wC3yA0dE5uH7gK2iN1oQs",
                    KeyRotatedUtc = new DateTimeOffset(2026, 8, 8, 8, 8, 8, TimeSpan.Zero),
                    ServerUrl = "https://appmon-prod-func-ab12cd.azurewebsites.net",
                },
            });

    [Fact]
    public void ConfigHistoryEntry_RoundTrips() =>
        RoundTrip<ApiWire.ConfigHistoryEntry, CoreWire.ConfigHistoryEntry>(SampleConfigHistoryEntry());

    [Fact]
    public void OrganizationEvent_RoundTrips() =>
        RoundTrip<ApiWire.OrganizationEvent, CoreWire.OrganizationEvent>(SampleOrganizationEvent());

    [Fact]
    public void ApiError_RoundTrips() => RoundTrip<ApiWire.ApiError, CoreWire.ApiError>(
        new ApiWire.ApiError { Code = "validation_failed", Message = "'chrome\\DeadlineHours' must be between 0 and 8760.", TraceId = "00-abc-def-01" });

    // ------------------------------------------------------------------------------------------------ settings document

    [Fact]
    public void SettingsDocument_RoundTrips() => RoundTrip<ApiWire.SettingsDocument, CoreConfig.SettingsDocument>(SampleSettings());

    [Fact]
    public void SettingsDocument_SurvivesCoreToJsonAndBack()
    {
        var core = CoreSampleSettings();
        var api = ApiWire.SettingsDocument.FromJson(core.ToJson());

        Assert.Equal(core.Global.Count, api.Global.Count);
        Assert.Equal(core.Apps.Count, api.Apps.Count);
        Assert.Equal(core.Global["ScanIntervalMinutes"].AsInt(), api.Global["ScanIntervalMinutes"].AsInt());
        Assert.Equal(core.Apps["chrome"]["ProcessNames"].AsStringList(), api.Apps["chrome"]["ProcessNames"].AsStringList());

        // and the other way round
        var backToCore = CoreConfig.SettingsDocument.FromJson(api.ToJson());
        Assert.Equal(core.ToJson(), backToCore.ToJson());
    }

    [Fact]
    public void SettingValue_KeepsItsJsonType()
    {
        var document = new ApiWire.SettingsDocument();
        document.Global["ScanOnStartup"] = ApiWire.SettingValue.From(true);
        document.Global["ScanIntervalMinutes"] = ApiWire.SettingValue.From(240);
        document.Global["LogLevel"] = ApiWire.SettingValue.From("Information");
        document.GetOrAddApp("chrome")["ProcessNames"] = ApiWire.SettingValue.From(new[] { "chrome", "chrome_proxy" });

        // DTO property names are camelCased, but the dictionary keys are registry value names and stay verbatim -
        // that is what the agent writes to HKLM, so the casing must not be touched.
        var json = JsonSerializer.Serialize(document, ApiJson);
        Assert.Contains("\"ScanOnStartup\":true", json);
        Assert.Contains("\"ScanIntervalMinutes\":240", json);
        Assert.Contains("\"LogLevel\":\"Information\"", json);
        Assert.Contains("[\"chrome\",\"chrome_proxy\"]", json);
        Assert.Contains("\"global\":{", json);
        Assert.Contains("\"apps\":{", json);

        var core = JsonSerializer.Deserialize<CoreConfig.SettingsDocument>(json, CoreJson)!;
        Assert.True(core.Global["ScanOnStartup"].AsBool());
        Assert.Equal(240, core.Global["ScanIntervalMinutes"].AsInt());
        Assert.Equal(["chrome", "chrome_proxy"], core.Apps["chrome"]["ProcessNames"].AsStringList());
    }

    // ------------------------------------------------------------------------------------------------ routes and conventions

    [Fact]
    public void Routes_MatchCore()
    {
        Assert.Equal(CoreWire.CloudRoutes.ApiVersion, ApiWire.CloudRoutes.ApiVersion);
        Assert.Equal(CoreWire.CloudRoutes.Enroll, ApiWire.CloudRoutes.Enroll);
        Assert.Equal(CoreWire.CloudRoutes.Config, ApiWire.CloudRoutes.Config);
        Assert.Equal(CoreWire.CloudRoutes.Report, ApiWire.CloudRoutes.Report);
        Assert.Equal(CoreWire.CloudRoutes.Release, ApiWire.CloudRoutes.Release);
        Assert.Equal(CoreWire.CloudRoutes.AuthConfig, ApiWire.CloudRoutes.AuthConfig);
        Assert.Equal(CoreWire.CloudRoutes.AdminMe, ApiWire.CloudRoutes.AdminMe);
        Assert.Equal(CoreWire.CloudRoutes.AdminOrganizations, ApiWire.CloudRoutes.AdminOrganizations);
        Assert.Equal(CoreWire.CloudRoutes.DeviceAuthScheme, ApiWire.CloudRoutes.DeviceAuthScheme);
    }

    [Fact]
    public void EveryFunctionRoute_IsCoveredByCoreRoutes()
    {
        // The Functions use "v1/..." because host.json adds the "api" prefix; Core spells the full path.
        Assert.Equal("/api/" + "v1/device/enroll", ApiWire.CloudRoutes.Enroll);
        Assert.Equal("/api/" + "v1/device/config", ApiWire.CloudRoutes.Config);
        Assert.Equal("/api/" + "v1/device/report", ApiWire.CloudRoutes.Report);
        Assert.Equal("/api/" + "v1/device/release", ApiWire.CloudRoutes.Release);
        Assert.Equal("/api/" + "v1/public/auth-config", ApiWire.CloudRoutes.AuthConfig);
        Assert.Equal("/api/" + "v1/admin/me", ApiWire.CloudRoutes.AdminMe);
        Assert.Equal("/api/" + "v1/admin/organizations", ApiWire.CloudRoutes.AdminOrganizations);
    }

    [Fact]
    public void Enums_HaveTheSameMembersAndValuesAsCore()
    {
        AssertEnumParity<ApiWire.InstallContext, CoreModels.InstallContext>();
        AssertEnumParity<ApiWire.UpdateState, CoreModels.UpdateState>();
        AssertEnumParity<ApiWire.UpdateSource, CoreModels.UpdateSource>();
        AssertEnumParity<ApiWire.InstallerType, CoreModels.InstallerType>();
        AssertEnumParity<ApiWire.DeviceCommandKind, CoreWire.DeviceCommandKind>();
        AssertEnumParity<ApiWire.ReportedEventKind, CoreWire.ReportedEventKind>();
    }

    [Fact]
    public void NullProperties_AreOmitted()
    {
        var json = JsonSerializer.Serialize(new ApiWire.ReportResponse { Accepted = true }, ApiJson);
        Assert.DoesNotContain("configVersion", json);
        Assert.Contains("\"accepted\":true", json);
    }

    private static void AssertEnumParity<TApi, TCore>() where TApi : struct, Enum where TCore : struct, Enum
    {
        var api = Enum.GetNames<TApi>().Select(n => (Name: n, Value: Convert.ToInt64(Enum.Parse<TApi>(n)))).OrderBy(x => x.Value).ToList();
        var core = Enum.GetNames<TCore>().Select(n => (Name: n, Value: Convert.ToInt64(Enum.Parse<TCore>(n)))).OrderBy(x => x.Value).ToList();
        Assert.Equal(core, api);
    }

    // ------------------------------------------------------------------------------------------------ samples

    private static ApiWire.SettingsDocument SampleSettings()
    {
        var document = new ApiWire.SettingsDocument
        {
            Description = "Contoso baseline",
            ExportedUtc = new DateTimeOffset(2026, 2, 3, 9, 30, 0, TimeSpan.Zero),
            ExportedBy = "it@contoso.com",
            ExportedFrom = "cloud",
            ProductVersion = "1.0.0",
        };
        document.Global["ScanIntervalMinutes"] = ApiWire.SettingValue.From(240);
        document.Global["ScanOnStartup"] = ApiWire.SettingValue.From(true);
        document.Global["LogLevel"] = ApiWire.SettingValue.From("Information");
        document.Global["DefaultDeferralOptions"] = ApiWire.SettingValue.From("60,240,1440");

        var chrome = document.GetOrAddApp("chrome");
        chrome["Source"] = ApiWire.SettingValue.From("winget");
        chrome["WingetId"] = ApiWire.SettingValue.From("Google.Chrome");
        chrome["Mandatory"] = ApiWire.SettingValue.From(true);
        chrome["DeadlineHours"] = ApiWire.SettingValue.From(72);
        chrome["ProcessNames"] = ApiWire.SettingValue.From(new[] { "chrome", "chrome_proxy" });
        return document;
    }

    private static CoreConfig.SettingsDocument CoreSampleSettings()
    {
        var document = new CoreConfig.SettingsDocument
        {
            Description = "Contoso baseline",
            ExportedUtc = new DateTimeOffset(2026, 2, 3, 9, 30, 0, TimeSpan.Zero),
            ExportedBy = "it@contoso.com",
            ExportedFrom = "cloud",
            ProductVersion = "1.0.0",
        };
        document.Global["ScanIntervalMinutes"] = CoreConfig.SettingValue.From(240);
        document.Global["ScanOnStartup"] = CoreConfig.SettingValue.From(true);
        document.Global["LogLevel"] = CoreConfig.SettingValue.From("Information");
        document.Global["DefaultDeferralOptions"] = CoreConfig.SettingValue.From("60,240,1440");

        var chrome = document.GetOrAddApp("chrome");
        chrome["Source"] = CoreConfig.SettingValue.From("winget");
        chrome["WingetId"] = CoreConfig.SettingValue.From("Google.Chrome");
        chrome["Mandatory"] = CoreConfig.SettingValue.From(true);
        chrome["DeadlineHours"] = CoreConfig.SettingValue.From(72);
        chrome["ProcessNames"] = CoreConfig.SettingValue.From(new[] { "chrome", "chrome_proxy" });
        return document;
    }

    private static ApiWire.PrerequisiteStatus SamplePrerequisites() => new()
    {
        WingetAvailable = true,
        WingetPath = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.9.25180.0_x64__8wekyb3d8bbwe\winget.exe",
        WingetVersion = "1.9.25180",
        MinimumVersion = "1.6.0",
        WingetMeetsMinimum = true,
        AppInstallerProvisioned = true,
        AppInstallerProvisionedVersion = "1.25.180.0",
        AutoInstallEnabled = true,
        CheckedUtc = new DateTimeOffset(2026, 2, 3, 6, 0, 0, TimeSpan.Zero),
        LastAction = "Repair-WinGetPackageManager -AllUsers -Latest",
        LastError = null,
        RepairInProgress = false,
    };

    private static ApiWire.OrganizationSummary SampleOrganizationSummary() => new()
    {
        OrganizationId = Guid.Parse("7b6b2b1e-1f0e-4a1a-8b1e-0f0e1a2b3c4d"),
        Name = "Contoso A/S",
        EntraTenantId = "22222222-2222-2222-2222-222222222222",
        DeviceCount = 120,
        DevicesWithPendingUpdates = 18,
        DevicesNotSeenIn7Days = 4,
        ConfigVersion = "12-aabbccdd",
        ConfigUpdatedUtc = new DateTimeOffset(2026, 2, 3, 9, 30, 0, TimeSpan.Zero),
    };

    private static ApiWire.ConfigHistoryEntry SampleConfigHistoryEntry() => new()
    {
        Id = 42,
        ConfigVersion = "12-aabbccdd",
        UpdatedUtc = new DateTimeOffset(2026, 2, 3, 9, 30, 0, TimeSpan.Zero),
        UpdatedBy = "it@contoso.com",
        Comment = "Added Chrome with a 72 hour deadline",
    };

    private static ApiWire.OrganizationEvent SampleOrganizationEvent() => new()
    {
        DeviceId = Guid.Parse("11112222-3333-4444-5555-666677778888"),
        DeviceName = "CONTOSO-LT-0041",
        OccurredUtc = new DateTimeOffset(2026, 2, 3, 9, 41, 0, TimeSpan.Zero),
        Kind = ApiWire.ReportedEventKind.InstallSucceeded,
        AppId = "chrome",
        Message = "Installed Chrome 132.0.6834.83",
        FromVersion = "131.0.6778.86",
        ToVersion = "132.0.6834.83",
    };

    private static ApiWire.DeviceSummary SampleDeviceSummary() => new()
    {
        DeviceId = Guid.Parse("11112222-3333-4444-5555-666677778888"),
        DeviceName = "CONTOSO-LT-0041",
        LastLogonUser = "CONTOSO\\hsk",
        OsVersion = "10.0.26100.2314",
        AgentVersion = "1.0.0",
        EnrolledUtc = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
        LastSeenUtc = new DateTimeOffset(2026, 2, 3, 9, 45, 0, TimeSpan.Zero),
        LastScanUtc = new DateTimeOffset(2026, 2, 3, 9, 40, 0, TimeSpan.Zero),
        ConfigVersionApplied = "12-aabbccdd",
        PendingUpdateCount = 2,
        FailedUpdateCount = 1,
        PrerequisitesHealthy = true,
    };

    private static ApiWire.DeviceReport SampleReport() => new()
    {
        ReportedUtc = new DateTimeOffset(2026, 2, 3, 9, 45, 0, TimeSpan.Zero),
        AgentVersion = "1.0.0",
        OsVersion = "10.0.26100.2314",
        DeviceName = "CONTOSO-LT-0041",
        LastLogonUser = "CONTOSO\\hsk",
        LastScanUtc = new DateTimeOffset(2026, 2, 3, 9, 40, 0, TimeSpan.Zero),
        ConfigVersionApplied = "12-aabbccdd",
        Prerequisites = SamplePrerequisites(),
        InstalledApps =
        [
            new ApiWire.ReportedApp
            {
                DisplayName = "Google Chrome",
                Version = "131.0.6778.86",
                Publisher = "Google LLC",
                WingetId = "Google.Chrome",
                AvailableVersion = "132.0.6834.83",
                Context = ApiWire.InstallContext.System,
                CatalogAppId = "chrome",
                MonitoredAppId = "chrome",
            },
            new ApiWire.ReportedApp { DisplayName = "Visual Studio Code (User)", Version = "1.97.0", Context = ApiWire.InstallContext.User },
        ],
        Updates =
        [
            new ApiWire.ReportedUpdate
            {
                AppId = "chrome",
                DisplayName = "Google Chrome",
                InstalledVersion = "131.0.6778.86",
                AvailableVersion = "132.0.6834.83",
                State = ApiWire.UpdateState.Available,
                Context = ApiWire.InstallContext.System,
                Mandatory = true,
                DeadlineUtc = new DateTimeOffset(2026, 2, 6, 9, 0, 0, TimeSpan.Zero),
                DeferralCount = 1,
                FirstDetectedUtc = new DateTimeOffset(2026, 2, 3, 9, 0, 0, TimeSpan.Zero),
            },
        ],
        Events =
        [
            new ApiWire.ReportedEvent
            {
                OccurredUtc = new DateTimeOffset(2026, 2, 3, 9, 41, 0, TimeSpan.Zero),
                Kind = ApiWire.ReportedEventKind.UpdateDetected,
                AppId = "chrome",
                Message = "Chrome 132.0.6834.83 is available",
                FromVersion = "131.0.6778.86",
                ToVersion = "132.0.6834.83",
            },
        ],
        AcknowledgedCommands = [Guid.Parse("99998888-7777-6666-5555-444433332222")],
    };
}
