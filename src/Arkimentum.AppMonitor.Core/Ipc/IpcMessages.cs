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
[JsonDerivedType(typeof(UserPackageListResultMessage), "userPackageListResult")]
[JsonDerivedType(typeof(ProcessesClosedMessage), "processesClosed")]
[JsonDerivedType(typeof(RepairPrerequisitesMessage), "repairPrerequisites")]
[JsonDerivedType(typeof(UpdateAgentMessage), "updateAgent")]
// server -> client
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(NotifyMessage), "notify")]
[JsonDerivedType(typeof(PromptCloseMessage), "promptClose")]
[JsonDerivedType(typeof(RunUserInstallMessage), "runUserInstall")]
[JsonDerivedType(typeof(RunUserScanMessage), "runUserScan")]
[JsonDerivedType(typeof(RunUserPackageListMessage), "runUserPackageList")]
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

    /// <summary>
    /// Optional (added after 1.1.1): the user pressed "Close apps and update", so the service may terminate the
    /// blocking processes the tray agent could not reach - elevated ones, and ones in other sessions. An older
    /// tray never sets it, so an older tray against a newer service keeps the pre-1.2 prompt-and-wait behaviour.
    /// </summary>
    public bool CloseBlockingProcesses { get; set; }
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

/// <summary>
/// One row of the tray agent's <c>winget list --scope user</c> table. It mirrors <c>WingetRow</c> instead of reusing
/// it, because the wire contract must stay stable even if the parser's record gains members, and because a JSON
/// payload needs settable properties rather than a positional record.
/// </summary>
public sealed class UserPackageRow
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Available { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    /// <summary>True when winget printed a shortened (…) cell; such rows cannot be used as package ids.</summary>
    public bool IsTruncated { get; set; }
}

/// <summary>
/// Tray agent reports the packages <c>winget list --scope user</c> knows about in its own session (added after 1.1.8).
/// <see cref="Rows"/> is empty and <see cref="Error"/> set when winget is missing or failed.
/// </summary>
public sealed class UserPackageListResultMessage : IpcMessage
{
    public string ListId { get; set; } = string.Empty;
    public List<UserPackageRow> Rows { get; set; } = [];
    public string? Error { get; set; }
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

/// <summary>
/// A user asked the agent to update itself now instead of waiting for the scheduled check (added after 1.1.1).
/// Tray and admin clients may both send it; the service still owns the update itself (it downloads, verifies and
/// starts the installer as SYSTEM). An older service does not know the type and simply logs it as ignored.
/// </summary>
public sealed class UpdateAgentMessage : IpcMessage
{
    /// <summary>True: only read the feed and report what it says. False: install the release when it is newer.</summary>
    public bool CheckOnly { get; set; }
}

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

    /// <summary>
    /// Optional (added after 1.1.1): what the agent's self-updater last concluded, so the tray and the console can
    /// show the agent's own update state and offer to update now. Null from an older service - the client then
    /// shows the plain version without the status line or the buttons.
    /// </summary>
    public AgentUpdateStatus? AgentUpdate { get; set; }

    /// <summary>
    /// Optional (added after 1.1.20): the most recent finished installs this client may see, newest first - machine-wide
    /// ones for everyone, per-user ones only for their own user - at most 10. Null from an older service; the tray then
    /// shows its "Recent updates" list as empty.
    /// </summary>
    public List<InstallHistoryEntry>? RecentInstalls { get; set; }
}

/// <summary>The self-updater's last outcome, as the service reports it to its clients.</summary>
public sealed class AgentUpdateStatus
{
    /// <summary>The version of the service that is running right now.</summary>
    public string RunningVersion { get; set; } = string.Empty;

    /// <summary>The version the feed offers, when a check has resolved a manifest; null when no check has succeeded.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>True when <see cref="LatestVersion"/> is newer than <see cref="RunningVersion"/>.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>True while a check or an install of the agent itself is running (the agent restarts by itself).</summary>
    public bool InProgress { get; set; }

    /// <summary>When the last check ran; null until one has.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>Why the last check or update failed, when it did.</summary>
    public string? LastError { get; set; }

    /// <summary>The effective <c>AgentAutoUpdate</c>: false means the administrator owns the binaries and requests are refused.</summary>
    public bool Enabled { get; set; }
}

public sealed class SettingsSummary
{
    public int ScanIntervalMinutes { get; set; }
    public int NotificationIntervalMinutes { get; set; }
    /// <summary>Optional (added after 1.1.1): lets the tray collapse a burst of "update available" toasts in Quiet mode. An older service omits it and the tray assumes the default.</summary>
    public NotificationMode NotificationMode { get; set; } = NotificationMode.Quiet;
    public bool WingetEnabled { get; set; }
    public bool WebSourcesEnabled { get; set; }
    public bool NotificationsEnabled { get; set; }
    public string LogDirectory { get; set; } = string.Empty;
    public string LogLevel { get; set; } = string.Empty;
    /// <summary>
    /// How many of the configured applications apply to the receiving session: machine-wide installs for everyone,
    /// per-user installs only for that connection's user. Always the length of <see cref="MonitoredApps"/>.
    /// </summary>
    public int MonitoredAppCount { get; set; }

    /// <summary>
    /// The applications found installed for the receiving session, by display name. Before the first scan has
    /// produced any result the service falls back to every enabled application, so the panel is never empty.
    /// </summary>
    public List<string> MonitoredApps { get; set; } = [];

    /// <summary>
    /// Optional (added after 1.1.4): every enabled application in the configuration, whether or not it is installed
    /// here, so a client can say "3 of 12 monitored applications apply to this device". 0 from an older service -
    /// the client then treats <see cref="MonitoredAppCount"/> as the whole story, which is what it used to be.
    /// </summary>
    public int ConfiguredAppCount { get; set; }
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
/// Ask the tray agent to list the packages winget knows about in its own session (added after 1.1.8). The service runs
/// as LocalSystem, so its own <c>winget list --scope user</c> only ever sees SYSTEM's packages; the per-user installs
/// of real users are visible only to the agent running in that user's session. An older tray does not know the
/// discriminator, logs the line as a bad message and never answers, so the caller must rely on its timeout.
/// </summary>
public sealed class RunUserPackageListMessage : IpcMessage
{
    public string ListId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Explicit winget.exe path from the configuration, when one is set; null lets the agent locate winget itself.</summary>
    public string? WingetPath { get; set; }
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
