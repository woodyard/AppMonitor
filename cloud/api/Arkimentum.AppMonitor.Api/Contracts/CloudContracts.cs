using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkimentum.AppMonitor.Api.Contracts;

// =====================================================================================================================
// Wire contracts - ported verbatim from src/Arkimentum.AppMonitor.Core/Cloud/CloudContracts.cs, which is the source of
// truth. The API cannot reference Core (net10.0-windows vs. net10.0), so the shapes are duplicated and
// Arkimentum.AppMonitor.Api.Tests round-trips every DTO through both copies to prove they agree.
//
// JSON: camelCase, enums as camelCase strings, nulls omitted.
// =====================================================================================================================

public static class CloudJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new SettingValueJsonConverter() },
    };
}

public static class CloudRoutes
{
    public const string ApiVersion = "v1";
    public const string Enroll = "/api/v1/device/enroll";
    public const string Config = "/api/v1/device/config";
    public const string Report = "/api/v1/device/report";
    public const string Release = "/api/v1/device/release";
    public const string AuthConfig = "/api/v1/public/auth-config";
    public const string AdminMe = "/api/v1/admin/me";
    public const string AdminOrganizations = "/api/v1/admin/organizations";
    /// <summary>Header carrying the device credential: <c>Authorization: Device {deviceId}:{deviceKey}</c>.</summary>
    public const string DeviceAuthScheme = "Device";
}

// ------------------------------------------------------------------------------------------------ enrollment

public sealed class EnrollRequest
{
    public required Guid OrganizationId { get; set; }
    public required string EnrollmentKey { get; set; }
    public required string DeviceName { get; set; }
    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid - stable per Windows installation.</summary>
    public required string MachineGuid { get; set; }
    public string? EntraTenantId { get; set; }
    public string? EntraDeviceId { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
}

public sealed class EnrollResponse
{
    public required Guid DeviceId { get; set; }
    /// <summary>Secret shown once; the agent stores it DPAPI-protected. Rotated by re-enrolling.</summary>
    public required string DeviceKey { get; set; }
    public required string OrganizationName { get; set; }
    public string? ConfigVersion { get; set; }
}

// ------------------------------------------------------------------------------------------------ configuration

public sealed class DeviceConfigResponse
{
    public required Guid OrganizationId { get; set; }
    public required string OrganizationName { get; set; }
    public required string ConfigVersion { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public required SettingsDocument Settings { get; set; }
    public List<DeviceCommand> Commands { get; set; } = [];
    public int PollIntervalSeconds { get; set; } = 900;
}

public sealed class DeviceCommand
{
    public required Guid CommandId { get; set; }
    public required DeviceCommandKind Kind { get; set; }
    public string? Argument { get; set; }
    public DateTimeOffset IssuedUtc { get; set; }
    public string? IssuedBy { get; set; }
}

// ------------------------------------------------------------------------------------------------ reporting

public sealed class DeviceReport
{
    public required DateTimeOffset ReportedUtc { get; set; }
    public required string AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? DeviceName { get; set; }
    public string? LastLogonUser { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public string? ConfigVersionApplied { get; set; }
    public PrerequisiteStatus? Prerequisites { get; set; }
    public List<ReportedApp> InstalledApps { get; set; } = [];
    public List<ReportedUpdate> Updates { get; set; } = [];
    public List<ReportedEvent> Events { get; set; } = [];
    public List<Guid> AcknowledgedCommands { get; set; } = [];
}

public sealed class ReportedApp
{
    public required string DisplayName { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? WingetId { get; set; }
    public string? AvailableVersion { get; set; }
    public InstallContext Context { get; set; }
    public string? CatalogAppId { get; set; }
    public string? MonitoredAppId { get; set; }
}

public sealed class ReportedUpdate
{
    public required string AppId { get; set; }
    public string? DisplayName { get; set; }
    public string? InstalledVersion { get; set; }
    public string? AvailableVersion { get; set; }
    public UpdateState State { get; set; }
    public InstallContext Context { get; set; }
    public bool Mandatory { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public DateTimeOffset? DeferredUntilUtc { get; set; }
    public int DeferralCount { get; set; }
    public DateTimeOffset FirstDetectedUtc { get; set; }
    public DateTimeOffset? InstalledAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class ReportedEvent
{
    public required DateTimeOffset OccurredUtc { get; set; }
    public required ReportedEventKind Kind { get; set; }
    public string? AppId { get; set; }
    public string? Message { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
}

public sealed class ReportResponse
{
    public bool Accepted { get; set; } = true;
    public bool ConfigChanged { get; set; }
    public string? ConfigVersion { get; set; }
}

// ------------------------------------------------------------------------------------------------ agent releases

public sealed class ReleaseManifest
{
    public required string Version { get; set; }
    public string Channel { get; set; } = "stable";
    public required string PackageUrl { get; set; }
    /// <summary>Lower-case hex SHA-256 of the zip.</summary>
    public required string Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}

// ------------------------------------------------------------------------------------------------ admin API

public sealed class AuthConfigResponse
{
    public required string ClientId { get; set; }
    public required string Authority { get; set; }
    public required string Scope { get; set; }
    public string? ApiVersion { get; set; } = CloudRoutes.ApiVersion;
    /// <summary>Public URL of the browser-based admin console, when the server hosts one; null otherwise.</summary>
    public string? WebAdminUrl { get; set; }
}

public sealed class AdminMeResponse
{
    public required string UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public required string TenantId { get; set; }
    public bool IsGlobalAdmin { get; set; }
    public List<OrganizationSummary> Organizations { get; set; } = [];
}

public sealed class OrganizationSummary
{
    public required Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public string? EntraTenantId { get; set; }
    public int DeviceCount { get; set; }
    public int DevicesWithPendingUpdates { get; set; }
    public int DevicesNotSeenIn7Days { get; set; }
    public string? ConfigVersion { get; set; }
    public DateTimeOffset? ConfigUpdatedUtc { get; set; }
}

public sealed class DeviceSummary
{
    public required Guid DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public string? LastLogonUser { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset EnrolledUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public string? ConfigVersionApplied { get; set; }
    public int PendingUpdateCount { get; set; }
    public int FailedUpdateCount { get; set; }
    public bool PrerequisitesHealthy { get; set; } = true;
}

public sealed class DeviceDetail
{
    public required DeviceSummary Summary { get; set; }
    public string? MachineGuid { get; set; }
    public string? EntraDeviceId { get; set; }
    public PrerequisiteStatus? Prerequisites { get; set; }
    public List<ReportedApp> InstalledApps { get; set; } = [];
    public List<ReportedUpdate> Updates { get; set; } = [];
    public List<ReportedEvent> RecentEvents { get; set; } = [];
    public List<DeviceCommand> PendingCommands { get; set; } = [];
}

public sealed class OrganizationInventoryItem
{
    public required string DisplayName { get; set; }
    public string? WingetId { get; set; }
    public string? Publisher { get; set; }
    public string? CatalogAppId { get; set; }
    public string? MonitoredAppId { get; set; }
    public int DeviceCount { get; set; }
    public int DevicesWithUpdateAvailable { get; set; }
    public List<VersionCount> Versions { get; set; } = [];
    public InstallContext PredominantContext { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class VersionCount
{
    public required string Version { get; set; }
    public int DeviceCount { get; set; }
}

public sealed class OrganizationConfigResponse
{
    public required Guid OrganizationId { get; set; }
    public required string ConfigVersion { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public required SettingsDocument Settings { get; set; }
}

public sealed class OrganizationConfigUpdateRequest
{
    public string? BaseConfigVersion { get; set; }
    public required SettingsDocument Settings { get; set; }
    public string? Comment { get; set; }
}

public sealed class EnrollmentInfoResponse
{
    public required Guid OrganizationId { get; set; }
    /// <summary>Only returned right after creation or rotation.</summary>
    public string? EnrollmentKey { get; set; }
    public DateTimeOffset? KeyRotatedUtc { get; set; }
    public required string ServerUrl { get; set; }
}

public sealed class DeviceCommandRequest
{
    public required DeviceCommandKind Kind { get; set; }
    public string? Argument { get; set; }
}

public sealed class ApiError
{
    public required string Code { get; set; }
    public required string Message { get; set; }
    public string? TraceId { get; set; }
}

// ------------------------------------------------------------------------------------------------ API-only shapes
// These are not in Core's CloudContracts.cs; they are the envelopes the admin console needs for the paged and
// list-returning admin endpoints. See "Recommended contract additions" in cloud/README.md.

/// <summary>Envelope for the paged admin list endpoints (devices, events).</summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>Body of POST /api/v1/admin/organizations.</summary>
public sealed class CreateOrganizationRequest
{
    public required string Name { get; set; }
    public string? EntraTenantId { get; set; }
}

/// <summary>Response of POST /api/v1/admin/organizations - the organization plus its one-time enrollment key.</summary>
public sealed class CreateOrganizationResponse
{
    public required OrganizationSummary Organization { get; set; }
    public required EnrollmentInfoResponse Enrollment { get; set; }
}

/// <summary>One entry of GET /api/v1/admin/organizations/{id}/config/history.</summary>
public sealed class ConfigHistoryEntry
{
    public required long Id { get; set; }
    public required string ConfigVersion { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public string? Comment { get; set; }
}

/// <summary>One entry of GET /api/v1/admin/organizations/{id}/events.</summary>
public sealed class OrganizationEvent
{
    public required Guid DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public required DateTimeOffset OccurredUtc { get; set; }
    public required ReportedEventKind Kind { get; set; }
    public string? AppId { get; set; }
    public string? Message { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
}
