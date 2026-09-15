using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Prerequisites;

namespace Arkimentum.AppMonitor.Cloud;

// =====================================================================================================================
// Wire contracts between the agent / admin console and the AppMonitor cloud API (Azure Functions).
// JSON: camelCase, enums as strings, nulls omitted. The backend must serialise/deserialise these shapes exactly;
// docs/cloud/openapi.yaml is generated from them. Version every breaking change through the /v1 path segment.
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
    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid - stable per Windows installation; used to re-attach a re-enrolling device.</summary>
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

/// <summary>Organization configuration as served to devices. <see cref="Settings"/> uses the exact SettingsDocument schema.</summary>
public sealed class DeviceConfigResponse
{
    public required Guid OrganizationId { get; set; }
    public required string OrganizationName { get; set; }
    /// <summary>Opaque version (ETag); send back as If-None-Match to get 304 when unchanged.</summary>
    public required string ConfigVersion { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public required SettingsDocument Settings { get; set; }
    /// <summary>
    /// Commands queued for this device by an administrator; acknowledge them in the next report. The server answers
    /// 304 Not Modified only when the ETag matches AND no unacknowledged command is pending, so commands always arrive.
    /// </summary>
    public List<DeviceCommand> Commands { get; set; } = [];
    /// <summary>Seconds until the device should poll again (server-controlled back-off).</summary>
    public int PollIntervalSeconds { get; set; } = 900;
}

public enum DeviceCommandKind
{
    ScanNow,
    RepairPrerequisites,
    UpdateAgent,
    ReportNow,
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

/// <summary>Posted by the service after every scan and after every install; the server keeps the latest snapshot per device plus history.</summary>
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
    /// <summary>Everything installed (registry inventory merged with winget), machine-wide and per user.</summary>
    public List<ReportedApp> InstalledApps { get; set; } = [];
    /// <summary>Tracked updates for monitored applications (Installed ones included with their state).</summary>
    public List<ReportedUpdate> Updates { get; set; } = [];
    /// <summary>Install outcomes since the previous report.</summary>
    public List<ReportedEvent> Events { get; set; } = [];
    /// <summary>Commands processed since the previous report.</summary>
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

public enum ReportedEventKind
{
    UpdateDetected,
    InstallSucceeded,
    InstallFailed,
    Deferred,
    ForcedClose,
    PrerequisiteRepaired,
    AgentUpdated,
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
    /// <summary>When the server has a newer configuration than <see cref="DeviceReport.ConfigVersionApplied"/>.</summary>
    public bool ConfigChanged { get; set; }
    public string? ConfigVersion { get; set; }
}

// ------------------------------------------------------------------------------------------------ agent releases

/// <summary>
/// Release manifest read by the self-updater. Published as <c>manifest.json</c> on each GitHub release (and optionally
/// mirrored by the API at /api/v1/device/release). The zip is the output of deploy\Build-Release.ps1.
/// </summary>
public sealed class ReleaseManifest
{
    public required string Version { get; set; }
    public string Channel { get; set; } = "stable";
    public required string PackageUrl { get; set; }
    /// <summary>Lower-case hex SHA-256 of the zip.</summary>
    public required string Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    /// <summary>Agents older than this must update before anything else (e.g. protocol change).</summary>
    public string? MinimumSupportedVersion { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}

// ------------------------------------------------------------------------------------------------ admin API

/// <summary>Unauthenticated: tells the admin console how to sign in.</summary>
public sealed class AuthConfigResponse
{
    /// <summary>Entra ID application (client) id of the admin console public client.</summary>
    public required string ClientId { get; set; }
    /// <summary>Authority, e.g. https://login.microsoftonline.com/organizations (multi-tenant) or a tenant id.</summary>
    public required string Authority { get; set; }
    /// <summary>Scope to request, e.g. api://{api-app-id}/AppMonitor.Admin.</summary>
    public required string Scope { get; set; }
    public string? ApiVersion { get; set; } = CloudRoutes.ApiVersion;
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

/// <summary>One application as seen across an organization's devices.</summary>
public sealed class OrganizationInventoryItem
{
    public required string DisplayName { get; set; }
    public string? WingetId { get; set; }
    public string? Publisher { get; set; }
    public string? CatalogAppId { get; set; }
    /// <summary>AppId in the organization configuration when this app is already monitored.</summary>
    public string? MonitoredAppId { get; set; }
    public int DeviceCount { get; set; }
    public int DevicesWithUpdateAvailable { get; set; }
    /// <summary>Distinct installed versions with device counts, newest first.</summary>
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
    /// <summary>Version the editor started from; the server rejects the write with 409 when it no longer matches.</summary>
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

/// <summary>Envelope for the paged admin list endpoints (devices, events).</summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>Body of POST /api/v1/admin/organizations (global administrators only).</summary>
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

/// <summary>One entry of GET /api/v1/admin/organizations/{id}/events (organization-wide event feed).</summary>
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
