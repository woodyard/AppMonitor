namespace Arkimentum.AppMonitor.Models;

/// <summary>
/// Tracked state of an update that has been detected but not yet installed. Persisted by the service and
/// mirrored to the tray agent over IPC.
/// </summary>
public sealed class PendingUpdate
{
    public required string AppId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? AvailableVersion { get; set; }
    public UpdateSource Source { get; set; }
    /// <summary>Resolved to System or User (never Auto).</summary>
    public InstallContext Context { get; set; }
    /// <summary>Owner SID for user-context updates.</summary>
    public string? UserSid { get; set; }
    public UpdateState State { get; set; } = UpdateState.Available;

    public DateTimeOffset FirstDetectedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public DateTimeOffset? DeferredUntilUtc { get; set; }
    public DateTimeOffset? LastNotifiedUtc { get; set; }
    /// <summary>When set, blocking processes will be terminated at this time.</summary>
    public DateTimeOffset? ForceCloseAtUtc { get; set; }
    /// <summary>
    /// Set when a user pressed "Close apps and update" (added after 1.1.1; older state files simply lack it).
    /// The tray closes and then kills what it can reach in its own session; whatever still blocks is elevated or
    /// in another session, so the service - running as LocalSystem - terminates it instead of prompting again,
    /// which is what used to make the dialog reappear forever. Cleared when the install finishes or fails.
    /// </summary>
    public DateTimeOffset? ForceCloseRequestedUtc { get; set; }
    public DateTimeOffset? InstalledAtUtc { get; set; }

    public bool Mandatory { get; set; }
    public int DeferralCount { get; set; }
    public int MaxDeferrals { get; set; }
    public bool AutoInstall { get; set; }
    public bool ForceCloseAtDeadline { get; set; }
    public int CloseGracePeriodMinutes { get; set; }
    public List<int> DeferralOptionsMinutes { get; set; } = [];
    /// <summary>Configured process names that must be closed.</summary>
    public List<string> ProcessNames { get; set; } = [];
    /// <summary>Process names currently detected running (subset of <see cref="ProcessNames"/>).</summary>
    public List<string> BlockingProcesses { get; set; } = [];
    /// <summary>
    /// Per-instance detail for <see cref="BlockingProcesses"/> as the service (SYSTEM) sees them, so the tray can
    /// tell the user which of them it cannot close itself. Optional (added after 1.1.1): an older service leaves
    /// it empty and the dialog falls back to the bare names.
    /// </summary>
    public List<BlockingProcessInfo> BlockingDetails { get; set; } = [];
    public string? LastError { get; set; }
    public int FailureCount { get; set; }
    /// <summary>Set when the user chose "Dismiss" on a non-mandatory update; re-notified after the interval.</summary>
    public bool Dismissed { get; set; }
    /// <summary>
    /// Set once the user has been told this update exists. In <see cref="NotificationMode.Quiet"/> that toast is shown
    /// only once per update, so this flag - not <see cref="LastNotifiedUtc"/>, which deferrals and prompts also move -
    /// decides whether the availability toast may be shown again. Reset when a new version supersedes this one.
    /// </summary>
    public bool Announced { get; set; }
    /// <summary>Set when the user asked to install; keeps the intent alive while blocking apps are being closed.</summary>
    public bool InstallRequested { get; set; }

    // installer plan (so the tray agent can perform user-context installs)
    /// <summary>The winget id the update was detected under.</summary>
    public string? WingetId { get; set; }
    /// <summary>Every configured winget id alternative for this app ("A;B"); the installer falls back through them.</summary>
    public string? WingetIdAlternatives { get; set; }
    public string? WingetSourceName { get; set; }
    public string? WingetExtraArgs { get; set; }
    /// <summary>Opt-in take-over (uninstall + install) when winget refuses the upgrade with a technology mismatch.</summary>
    public bool WingetReplaceOnMismatch { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InstallerArgs { get; set; }
    public InstallerType InstallerType { get; set; }
    public string? Sha256 { get; set; }

    public bool CanDefer(DateTimeOffset now)
    {
        if (State is UpdateState.Installing or UpdateState.Installed or UpdateState.Scheduled) return false;
        if (IsPastDeadline(now)) return false;
        return MaxDeferrals <= 0 || DeferralCount < MaxDeferrals;
    }

    public bool IsPastDeadline(DateTimeOffset now) => Mandatory && DeadlineUtc is { } d && now >= d;

    public bool IsDeferred(DateTimeOffset now) => DeferredUntilUtc is { } u && now < u;

    public bool IsActive => State is not (UpdateState.Installed);

    public string Key => MakeKey(AppId, Context, UserSid);

    public static string MakeKey(string appId, InstallContext context, string? userSid) =>
        $"{appId}|{context}|{(context == InstallContext.User ? userSid ?? "-" : "-")}";

    public PendingUpdate Clone()
    {
        var c = (PendingUpdate)MemberwiseClone();
        c.DeferralOptionsMinutes = [.. DeferralOptionsMinutes];
        c.ProcessNames = [.. ProcessNames];
        c.BlockingProcesses = [.. BlockingProcesses];
        c.BlockingDetails = [.. BlockingDetails.Select(d => d.Clone())];
        return c;
    }
}
