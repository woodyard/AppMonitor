namespace Arkimentum.AppMonitor.Models;

/// <summary>
/// One finished install attempt of a monitored application, as the service remembers it after the tracked update
/// itself is gone (installed updates are purged after the retention period). Persisted in the service state, newest
/// first, and sent to the tray in <see cref="Ipc.StateMessage.RecentInstalls"/> for the "Recent updates" list.
/// </summary>
public sealed class InstallHistoryEntry
{
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The version that was installed before the attempt, when known.</summary>
    public string? FromVersion { get; set; }

    /// <summary>The version the install ended at when the provider read it back, else the version it aimed for.</summary>
    public string? ToVersion { get; set; }

    public bool Succeeded { get; set; }

    public DateTimeOffset CompletedUtc { get; set; }

    /// <summary>
    /// When the installer was started; with <see cref="CompletedUtc"/> it times the install (the wait for the user to
    /// close applications is not included). Null for entries recorded before 1.1.42 and for backfilled ones.
    /// </summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>Resolved to System or User (never Auto) - the context the install ran in.</summary>
    public InstallContext Context { get; set; }

    /// <summary>Owner SID for a user-context install; null for a machine-wide one.</summary>
    public string? UserSid { get; set; }

    /// <summary>Why the install failed, shortened; null for a success.</summary>
    public string? Message { get; set; }

    /// <summary>The tracked update's <see cref="PendingUpdate.IconPath"/> when the install finished; null for older or backfilled entries.</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// A successful install that finishes the next time the application starts (see
    /// <see cref="InstallResult.AppRestartPending"/>); cleared once a scan shows the new version or the pending state
    /// ends otherwise. Added after 1.1.44; older entries and an older service leave it false.
    /// </summary>
    public bool AppRestartPending { get; set; }
}
