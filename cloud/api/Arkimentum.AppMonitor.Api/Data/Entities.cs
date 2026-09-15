using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Api.Data;

/// <summary>A customer organization. Every other row carries an OrganizationId (directly or through its device).</summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Entra tenant whose administrators may manage this organization. Unique when set.</summary>
    public string? EntraTenantId { get; set; }
    /// <summary>SHA-256 of the enrollment key. The key itself is never stored.</summary>
    public byte[] EnrollmentKeyHash { get; set; } = [];
    public DateTimeOffset? EnrollmentKeyRotatedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public bool IsActive { get; set; } = true;

    public OrganizationConfig? Config { get; set; }
    public List<Device> Devices { get; set; } = [];
}

/// <summary>Current organization configuration; one row per organization.</summary>
public sealed class OrganizationConfig
{
    public Guid OrganizationId { get; set; }
    public string ConfigVersion { get; set; } = string.Empty;
    /// <summary>A serialised <see cref="SettingsDocument"/> (nvarchar(max)).</summary>
    public string SettingsJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public Organization? Organization { get; set; }
}

public sealed class OrganizationConfigHistory
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string ConfigVersion { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public string? Comment { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid - re-attaches a re-installed machine.</summary>
    public string MachineGuid { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string? EntraTenantId { get; set; }
    public string? EntraDeviceId { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    /// <summary>SHA-256 of the per-device key issued at enrollment.</summary>
    public byte[] DeviceKeyHash { get; set; } = [];
    public DateTimeOffset EnrolledUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public string? ConfigVersionApplied { get; set; }
    public string? LastLogonUser { get; set; }
    /// <summary>Serialised <see cref="PrerequisiteStatus"/>.</summary>
    public string? PrerequisitesJson { get; set; }
    public int PendingUpdateCount { get; set; }
    public int FailedUpdateCount { get; set; }
    public bool IsDeleted { get; set; }

    public Organization? Organization { get; set; }
    public List<DeviceApp> Apps { get; set; } = [];
    public List<DeviceUpdate> Updates { get; set; } = [];
    public List<DeviceCommandRow> Commands { get; set; } = [];
}

/// <summary>Latest installed-application snapshot for a device; replaced wholesale on every report.</summary>
public sealed class DeviceApp
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OrganizationId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? WingetId { get; set; }
    public string? AvailableVersion { get; set; }
    public InstallContext Context { get; set; }
    public string? CatalogAppId { get; set; }
    public string? MonitoredAppId { get; set; }

    public Device? Device { get; set; }
}

/// <summary>Latest tracked-update snapshot for a device; replaced wholesale on every report.</summary>
public sealed class DeviceUpdate
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OrganizationId { get; set; }
    public string AppId { get; set; } = string.Empty;
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

    public Device? Device { get; set; }
}

/// <summary>Append-only install/deferral history.</summary>
public sealed class DeviceEvent
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public ReportedEventKind Kind { get; set; }
    public string? AppId { get; set; }
    public string? Message { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }

    public Device? Device { get; set; }
}

/// <summary>A command queued for a device by an administrator; delivered with the config and acknowledged in a report.</summary>
public sealed class DeviceCommandRow
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OrganizationId { get; set; }
    public DeviceCommandKind Kind { get; set; }
    public string? Argument { get; set; }
    public DateTimeOffset IssuedUtc { get; set; }
    public string? IssuedBy { get; set; }
    /// <summary>First time the command was handed to the device on a GET /device/config. Bookkeeping only.</summary>
    public DateTimeOffset? DeliveredUtc { get; set; }
    /// <summary>Set when the device lists the command in DeviceReport.AcknowledgedCommands; stops re-delivery.</summary>
    public DateTimeOffset? AcknowledgedUtc { get; set; }

    public Device? Device { get; set; }
}

/// <summary>Release manifest mirror; one row per channel.</summary>
public sealed class ReleaseManifestRow
{
    public string Channel { get; set; } = "stable";
    /// <summary>A serialised <see cref="ReleaseManifest"/>.</summary>
    public string Json { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string? Details { get; set; }
}
