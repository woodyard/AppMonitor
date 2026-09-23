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

    /// <summary>Resolved to System or User (never Auto) - the context the install ran in.</summary>
    public InstallContext Context { get; set; }

    /// <summary>Owner SID for a user-context install; null for a machine-wide one.</summary>
    public string? UserSid { get; set; }

    /// <summary>Why the install failed, shortened; null for a success.</summary>
    public string? Message { get; set; }

    /// <summary>The tracked update's <see cref="PendingUpdate.IconPath"/> when the install finished; null for older or backfilled entries.</summary>
    public string? IconPath { get; set; }
}
