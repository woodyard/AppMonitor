using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Ipc;

/// <summary>
/// Messages exchanged between the service (pipe server) and tray agents (pipe clients) as newline-delimited JSON.
/// The "$type" discriminator selects the concrete class.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// client -> server
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(GetStateMessage), "getState")]
[JsonDerivedType(typeof(RequestScanMessage), "requestScan")]
[JsonDerivedType(typeof(InstallNowMessage), "installNow")]
[JsonDerivedType(typeof(DeferMessage), "defer")]
[JsonDerivedType(typeof(DismissMessage), "dismiss")]
[JsonDerivedType(typeof(UserInstallProgressMessage), "userInstallProgress")]
[JsonDerivedType(typeof(UserInstallResultMessage), "userInstallResult")]
[JsonDerivedType(typeof(UserScanResultMessage), "userScanResult")]
[JsonDerivedType(typeof(ProcessesClosedMessage), "processesClosed")]
[JsonDerivedType(typeof(RepairPrerequisitesMessage), "repairPrerequisites")]
// server -> client
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(NotifyMessage), "notify")]
[JsonDerivedType(typeof(PromptCloseMessage), "promptClose")]
[JsonDerivedType(typeof(RunUserInstallMessage), "runUserInstall")]
[JsonDerivedType(typeof(RunUserScanMessage), "runUserScan")]
[JsonDerivedType(typeof(CloseProcessesMessage), "closeProcesses")]
[JsonDerivedType(typeof(AckMessage), "ack")]
public abstract class IpcMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset SentUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ------------------------------------------------------------------ client -> server

public enum IpcClientKind
{
    /// <summary>The per-session tray agent: receives notifications, prompts and user-context work.</summary>
    Tray = 0,
    /// <summary>An administrative or diagnostic client: may read state and request scans, never receives user-context work.</summary>
    Admin = 1,
}

public sealed class HelloMessage : IpcMessage
{
    public int SessionId { get; set; }
    public string? UserSid { get; set; }
    public string? UserName { get; set; }
    public string? AgentVersion { get; set; }
    public IpcClientKind ClientKind { get; set; } = IpcClientKind.Tray;
}

public sealed class GetStateMessage : IpcMessage { }

public sealed class RequestScanMessage : IpcMessage { }

/// <summary>User asked to install one pending update now.</summary>
public sealed class InstallNowMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
}

public sealed class DeferMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
    public int Minutes { get; set; }
}

/// <summary>User dismissed a non-mandatory notification; it will be shown again after the notification interval.</summary>
public sealed class DismissMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
}

public sealed class UserInstallProgressMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
    public string? Status { get; set; }
}

/// <summary>Tray agent reports the outcome of a user-context install requested by the service.</summary>
public sealed class UserInstallResultMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
    public required InstallResult Result { get; set; }
}

/// <summary>Tray agent reports user-context scan results requested by the service.</summary>
public sealed class UserScanResultMessage : IpcMessage
{
    public string? ScanId { get; set; }
    public List<UpdateCheckResult> Results { get; set; } = [];
}

/// <summary>Tray agent reports the outcome of a <see cref="CloseProcessesMessage"/>.</summary>
public sealed class ProcessesClosedMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
    /// <summary>Process names still running after the attempt (empty = all closed).</summary>
    public List<string> StillRunning { get; set; } = [];
    /// <summary>True when the user declined (only possible when <see cref="CloseProcessesMessage.Force"/> was false).</summary>
    public bool Declined { get; set; }
}

/// <summary>Admin console asks the service to check and repair prerequisites (winget) now. Ignored from tray clients.</summary>
public sealed class RepairPrerequisitesMessage : IpcMessage { }

// ------------------------------------------------------------------ server -> client

/// <summary>Full snapshot of updates relevant to the receiving session (system updates + that user's updates).</summary>
public sealed class StateMessage : IpcMessage
{
    public List<PendingUpdate> Updates { get; set; } = [];
    public DateTimeOffset? LastScanUtc { get; set; }
    public DateTimeOffset? NextScanUtc { get; set; }
    public bool ScanInProgress { get; set; }
    public string? ServiceVersion { get; set; }
    public SettingsSummary Settings { get; set; } = new();
}

public sealed class SettingsSummary
{
    public int ScanIntervalMinutes { get; set; }
    public int NotificationIntervalMinutes { get; set; }
    public bool WingetEnabled { get; set; }
    public bool WebSourcesEnabled { get; set; }
    public bool NotificationsEnabled { get; set; }
    public string LogDirectory { get; set; } = string.Empty;
    public string LogLevel { get; set; } = string.Empty;
    public int MonitoredAppCount { get; set; }
    public List<string> MonitoredApps { get; set; } = [];
    public DateTimeOffset LoadedAtUtc { get; set; }
    /// <summary>Last prerequisite (winget) check result; null until the first check ran.</summary>
    public Prerequisites.PrerequisiteStatus? Prerequisites { get; set; }

    // ---- organization (cloud) connection, so the tray can tell the user who manages the device ----
    /// <summary>True when CloudServerUrl is configured, whether or not the device has enrolled yet.</summary>
    public bool CloudConfigured { get; set; }
    /// <summary>True once the device holds a device credential for the organization.</summary>
    public bool CloudEnrolled { get; set; }
    /// <summary>Display name of the organization, as the server reported it at enrollment or with the last configuration.</summary>
    public string? OrganizationName { get; set; }
}

/// <summary>Ask the tray agent to show a notification (toast).</summary>
public sealed class NotifyMessage : IpcMessage
{
    public NotificationKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>Update this notification refers to (null for informational messages).</summary>
    public PendingUpdate? Update { get; set; }
}

/// <summary>Blocking processes are running; ask the user to close them (or defer). Includes forced-close countdown when applicable.</summary>
public sealed class PromptCloseMessage : IpcMessage
{
    public required PendingUpdate Update { get; set; }
}

/// <summary>Instruct the tray agent to install a user-context update in its own session.</summary>
public sealed class RunUserInstallMessage : IpcMessage
{
    public required PendingUpdate Update { get; set; }
    public int TimeoutMinutes { get; set; } = 30;
}

/// <summary>Instruct the tray agent to check the given apps in user context (winget --scope user, HKCU inventory).</summary>
public sealed class RunUserScanMessage : IpcMessage
{
    public string ScanId { get; set; } = Guid.NewGuid().ToString("N");
    public List<AppPolicy> Apps { get; set; } = [];
    public bool WingetEnabled { get; set; } = true;
    public bool WebSourcesEnabled { get; set; } = true;
    public string? ProxyUrl { get; set; }
    public string? WingetGlobalArgs { get; set; }
    public bool WingetIncludeUnknown { get; set; }
}

/// <summary>
/// Ask the tray agent to close the given processes in its own session (window handles are session-bound, so only the
/// agent can close them gracefully). When <see cref="Force"/> is true the agent kills what does not exit within
/// <see cref="GracefulWaitSeconds"/>; the service kills any survivors itself afterwards.
/// </summary>
public sealed class CloseProcessesMessage : IpcMessage
{
    public required string UpdateKey { get; set; }
    public List<string> ProcessNames { get; set; } = [];
    public bool Force { get; set; }
    public int GracefulWaitSeconds { get; set; } = 30;
}

public sealed class AckMessage : IpcMessage
{
    public string? InReplyTo { get; set; }
    public bool Ok { get; set; } = true;
    public string? Message { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IpcMessage message) => JsonSerializer.Serialize(message, Options);

    public static IpcMessage? Deserialize(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<IpcMessage>(line, Options);
}
