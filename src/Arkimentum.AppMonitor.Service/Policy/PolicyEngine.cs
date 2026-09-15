using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;

namespace Arkimentum.AppMonitor.Service.Policy;

public enum PolicyActionKind
{
    None,
    /// <summary>Send an "update available" / "deadline approaching" notification.</summary>
    Notify,
    /// <summary>Start the install now (no blocking processes, or install requested by the user).</summary>
    Install,
    /// <summary>Blocking processes are running: ask the user to close them (with countdown when forced close is scheduled).</summary>
    PromptClose,
    /// <summary>Grace period elapsed: close the blocking processes and install.</summary>
    ForceClose,
}

public readonly record struct PolicyAction(PolicyActionKind Kind, NotificationKind Notification = NotificationKind.Info)
{
    public static readonly PolicyAction None = new(PolicyActionKind.None);
}

/// <summary>A scan result together with the policy it was produced for and the user it applies to.</summary>
public sealed record ScanOutcome(UpdateCheckResult Result, AppPolicy Policy, InstallContext Context, string? UserSid)
{
    public string Key => PendingUpdate.MakeKey(Policy.AppId, Context, UserSid);
}

public sealed record MergeSummary(int Added, int Updated, int Resolved, int Removed, IReadOnlyList<PendingUpdate> NewUpdates);

/// <summary>
/// Pure decision logic for update lifecycle: merging scan results into tracked state, deciding what to do on each tick,
/// and applying user actions. No I/O, fully unit-testable.
/// </summary>
public static class PolicyEngine
{
    public const int MaxAutomaticRetries = 3;
    public static readonly TimeSpan InstalledRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan DeadlineWarningWindow = TimeSpan.FromHours(24);
    /// <summary>After a successful install, ignore a scan that still reports the old version for this long (pending reboot etc.).</summary>
    public static readonly TimeSpan PostInstallGrace = TimeSpan.FromHours(2);

    public static TimeSpan NotificationIntervalFor(PendingUpdate update, AppPolicy? policy, AgentSettings settings) =>
        policy?.NotificationIntervalMinutes is > 0 and var m ? TimeSpan.FromMinutes(m) : settings.NotificationInterval;

    /// <summary>The app's own notification mode when it sets one, otherwise the global mode.</summary>
    public static NotificationMode NotificationModeFor(AppPolicy? policy, AgentSettings settings) =>
        policy?.NotificationMode ?? settings.NotificationMode;

    /// <summary>
    /// Merges scan outcomes into <paramref name="state"/>. <paramref name="checkedKeys"/> is the set of (app, context, user)
    /// combinations that were actually checked this round; tracked updates outside that set are left untouched
    /// (e.g. a user who is not logged on right now).
    /// </summary>
    public static MergeSummary Merge(IDictionary<string, PendingUpdate> state, IReadOnlyList<ScanOutcome> outcomes,
        ISet<string> checkedKeys, DateTimeOffset now)
    {
        int added = 0, updated = 0, resolved = 0, removed = 0;
        var newUpdates = new List<PendingUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in outcomes)
        {
            var r = o.Result;
            var key = o.Key;
            if (r.Error is not null) { seen.Add(key); continue; } // keep whatever we knew; transient failure

            state.TryGetValue(key, out var existing);

            if (!r.IsInstalled)
            {
                if (existing is not null && existing.State != UpdateState.Installing) { state.Remove(key); removed++; }
                seen.Add(key);
                continue;
            }

            if (!r.UpdateAvailable)
            {
                if (existing is not null)
                {
                    if (existing.State is UpdateState.Installing) { }
                    else if (existing.State == UpdateState.Installed) { existing.LastSeenUtc = now; }
                    else
                    {
                        // Updated outside of us (or a previous install finished): show as installed briefly, then purge.
                        existing.State = UpdateState.Installed;
                        existing.InstalledAtUtc ??= now;
                        existing.InstalledVersion = r.InstalledVersion ?? existing.InstalledVersion;
                        existing.LastSeenUtc = now;
                        resolved++;
                    }
                }
                seen.Add(key);
                continue;
            }

            seen.Add(key);
            if (existing is null)
            {
                var p = CreatePending(o, now);
                state[key] = p;
                newUpdates.Add(p);
                added++;
                continue;
            }

            if (existing.State == UpdateState.Installing) { existing.LastSeenUtc = now; continue; }

            if (existing.State == UpdateState.Installed)
            {
                var installedRecently = existing.InstalledAtUtc is { } t && now - t < PostInstallGrace;
                var sameTarget = string.Equals(existing.AvailableVersion, r.AvailableVersion, StringComparison.OrdinalIgnoreCase);
                // The grace period covers "installed, but the scan still shows the old version" (pending reboot, stale
                // cache). It does not apply when the install itself already reported a version below the target: that
                // install did not take, so the update is simply still pending.
                var installKnownIncomplete = !VersionComparer.IsUnknown(existing.InstalledVersion) && !string.IsNullOrWhiteSpace(existing.AvailableVersion)
                                             && VersionComparer.Compare(existing.InstalledVersion, existing.AvailableVersion) < 0;
                if (installedRecently && sameTarget && !installKnownIncomplete) { existing.LastSeenUtc = now; continue; }
                // install did not stick, or a newer version appeared: start a new cycle but remember failures
                existing.State = UpdateState.Available;
                existing.InstalledAtUtc = null;
                existing.FirstDetectedUtc = now;
                existing.DeferralCount = 0;
                existing.DeferredUntilUtc = null;
                existing.ForceCloseAtUtc = null;
                existing.LastNotifiedUtc = null;
                existing.Announced = false;
                existing.Dismissed = false;
                if (sameTarget) existing.FailureCount++;
            }

            var versionChanged = !string.Equals(existing.AvailableVersion, r.AvailableVersion, StringComparison.OrdinalIgnoreCase);
            ApplyPolicy(existing, o.Policy, r, now);
            existing.LastSeenUtc = now;
            if (versionChanged)
            {
                // A newer version is a new thing to tell the user about, even in Quiet mode.
                existing.Announced = false;
                // A newer version superseded the one that failed: allow automatic retries again.
                if (existing.State == UpdateState.Failed) { existing.State = UpdateState.Available; existing.FailureCount = 0; existing.LastError = null; }
            }
            else if (existing.State == UpdateState.Failed && existing.FailureCount < MaxAutomaticRetries)
            {
                existing.State = UpdateState.Available; // retry on the next opportunity
            }
            updated++;
        }

        // Tracked updates whose combination was checked but no longer reported at all -> resolved (e.g. policy removed the app)
        foreach (var key in state.Keys.ToList())
        {
            if (seen.Contains(key) || !checkedKeys.Contains(key)) continue;
            var u = state[key];
            if (u.State == UpdateState.Installing) continue;
            state.Remove(key);
            removed++;
        }

        // Purge old "Installed" entries
        foreach (var key in state.Keys.ToList())
        {
            var u = state[key];
            if (u.State == UpdateState.Installed && u.InstalledAtUtc is { } t && now - t > InstalledRetention) state.Remove(key);
        }

        return new MergeSummary(added, updated, resolved, removed, newUpdates);
    }

    private static PendingUpdate CreatePending(ScanOutcome o, DateTimeOffset now)
    {
        var p = new PendingUpdate
        {
            AppId = o.Policy.AppId,
            Context = o.Context,
            UserSid = o.Context == InstallContext.User ? o.UserSid : null,
            Source = o.Result.Source,
            State = UpdateState.Available,
            FirstDetectedUtc = now,
            LastSeenUtc = now,
        };
        ApplyPolicy(p, o.Policy, o.Result, now);
        return p;
    }

    /// <summary>Copies policy-derived and result-derived fields; safe to re-apply when the policy changes.</summary>
    public static void ApplyPolicy(PendingUpdate p, AppPolicy policy, UpdateCheckResult r, DateTimeOffset now)
    {
        p.DisplayName = string.IsNullOrWhiteSpace(policy.DisplayName) ? policy.AppId : policy.DisplayName;
        p.InstalledVersion = r.InstalledVersion ?? p.InstalledVersion;
        p.AvailableVersion = r.AvailableVersion ?? p.AvailableVersion;
        p.Source = r.Source;
        p.Mandatory = policy.Mandatory;
        p.MaxDeferrals = policy.MaxDeferrals;
        p.AutoInstall = policy.AutoInstall;
        p.ForceCloseAtDeadline = policy.ForceCloseAtDeadline;
        p.CloseGracePeriodMinutes = policy.CloseGracePeriodMinutes;
        p.DeferralOptionsMinutes = [.. policy.DeferralOptionsMinutes];
        p.ProcessNames = [.. policy.ProcessNames];
        p.DeadlineUtc = policy.Mandatory && policy.DeadlineHours > 0 ? p.FirstDetectedUtc.AddHours(policy.DeadlineHours) : null;
        p.WingetId = r.WingetId ?? policy.WingetId;
        p.WingetIdAlternatives = policy.WingetId;
        p.WingetSourceName = policy.WingetSourceName;
        p.WingetExtraArgs = policy.WingetExtraArgs;
        p.DownloadUrl = r.DownloadUrl;
        p.InstallerArgs = r.InstallerArgs;
        p.InstallerType = r.InstallerType;
        p.Sha256 = r.Sha256;
        if (p.DeferredUntilUtc is { } d && p.DeadlineUtc is { } dl && d > dl) p.DeferredUntilUtc = dl;
    }

    /// <summary>
    /// Decides what to do for one tracked update on this tick. <paramref name="mode"/> only changes how often the user is
    /// interrupted, never whether an update is installed or enforced; it defaults to <see cref="NotificationMode.Reminders"/>
    /// so that callers which do not care about notification volume keep the original cadence.
    /// </summary>
    public static PolicyAction Decide(PendingUpdate u, DateTimeOffset now, TimeSpan notificationInterval, bool blockingProcessesRunning,
        NotificationMode mode = NotificationMode.Reminders)
    {
        switch (u.State)
        {
            case UpdateState.Installing:
            case UpdateState.Installed:
                return PolicyAction.None;
            case UpdateState.Scheduled:
                return new PolicyAction(PolicyActionKind.Install);
        }

        var quiet = mode == NotificationMode.Quiet;
        var notificationDue = u.LastNotifiedUtc is null || now - u.LastNotifiedUtc.Value >= notificationInterval;

        // A failed install is not retried automatically until the next scan re-evaluates it (see Merge); the user can
        // still choose "Install now". Reminders repeat the failure at the notification cadence; Quiet reports each
        // failure once (MarkFailed clears LastNotifiedUtc, so a new failure is announced again).
        if (u.State == UpdateState.Failed)
        {
            var failureDue = quiet ? u.LastNotifiedUtc is null : notificationDue;
            return failureDue ? new PolicyAction(PolicyActionKind.Notify, NotificationKind.Failed) : PolicyAction.None;
        }

        if (u.IsPastDeadline(now))
        {
            if (!blockingProcessesRunning) return new PolicyAction(PolicyActionKind.Install);

            // First time we see blocking processes after the deadline: prompt and start the grace-period countdown.
            if (u.ForceCloseAtUtc is null && u.ForceCloseAtDeadline) return new PolicyAction(PolicyActionKind.PromptClose, NotificationKind.CloseApplications);
            if (u.ForceCloseAtDeadline && u.ForceCloseAtUtc is { } f && now >= f) return new PolicyAction(PolicyActionKind.ForceClose);
            // Grace period running, or forced close disabled: keep reminding at the notification cadence
            return notificationDue ? new PolicyAction(PolicyActionKind.PromptClose, NotificationKind.CloseApplications) : PolicyAction.None;
        }

        if (u.IsDeferred(now)) return PolicyAction.None;

        if ((u.AutoInstall || u.InstallRequested) && !blockingProcessesRunning) return new PolicyAction(PolicyActionKind.Install);

        // Both of these need the user to act, so Quiet mode keeps them at the normal cadence.
        if (blockingProcessesRunning && (u.AutoInstall || u.Mandatory || u.InstallRequested || u.State == UpdateState.WaitingForClose))
            return notificationDue ? new PolicyAction(PolicyActionKind.PromptClose, NotificationKind.CloseApplications) : PolicyAction.None;

        if (u.Mandatory && u.DeadlineUtc is { } dl && dl - now <= DeadlineWarningWindow)
            return notificationDue ? new PolicyAction(PolicyActionKind.Notify, NotificationKind.DeadlineApproaching) : PolicyAction.None;

        // Quiet mode announces an update once. Everything that moved LastNotifiedUtc afterwards (a deferral running out,
        // a dismissal) must not produce a second toast; only the cases above may interrupt again.
        if (quiet && u.Announced) return PolicyAction.None;

        return notificationDue ? new PolicyAction(PolicyActionKind.Notify, NotificationKind.UpdateAvailable) : PolicyAction.None;
    }

    /// <summary>Applies a user deferral. Returns false (and leaves the update untouched) when the request is not allowed.</summary>
    public static bool TryDefer(PendingUpdate u, int minutes, DateTimeOffset now, out string? reason)
    {
        reason = null;
        if (!u.CanDefer(now)) { reason = u.IsPastDeadline(now) ? "The deadline has passed." : "No more deferrals are allowed."; return false; }
        if (minutes <= 0 || (u.DeferralOptionsMinutes.Count > 0 && !u.DeferralOptionsMinutes.Contains(minutes)))
        { reason = "That deferral length is not allowed."; return false; }

        var until = now.AddMinutes(minutes);
        if (u.DeadlineUtc is { } dl && until > dl) until = dl;
        u.DeferredUntilUtc = until;
        u.DeferralCount++;
        u.State = UpdateState.Deferred;
        u.ForceCloseAtUtc = null;
        u.LastNotifiedUtc = now;
        u.Dismissed = false;
        u.InstallRequested = false;
        return true;
    }

    public static void Dismiss(PendingUpdate u, DateTimeOffset now)
    {
        if (u.State is UpdateState.Installing or UpdateState.Installed or UpdateState.Scheduled) return;
        u.Dismissed = true;
        u.InstallRequested = false;
        u.LastNotifiedUtc = now;
        if (u.State is UpdateState.WaitingForClose or UpdateState.Deferred) u.State = UpdateState.Available;
        if (!u.IsPastDeadline(now)) u.ForceCloseAtUtc = null;
    }

    public static void RequestInstall(PendingUpdate u)
    {
        if (u.State is UpdateState.Installing or UpdateState.Installed) return;
        u.State = UpdateState.Scheduled;
        u.InstallRequested = true;
        u.DeferredUntilUtc = null;
        u.Dismissed = false;
    }

    public static void MarkInstalling(PendingUpdate u)
    {
        u.State = UpdateState.Installing;
        u.LastError = null;
    }

    public static void MarkInstalled(PendingUpdate u, InstallResult result, DateTimeOffset now)
    {
        u.State = UpdateState.Installed;
        u.InstalledAtUtc = now;
        u.LastError = null;
        u.ForceCloseAtUtc = null;
        u.InstallRequested = false;
        u.BlockingProcesses = [];
        if (!string.IsNullOrWhiteSpace(result.InstalledVersion)) u.InstalledVersion = result.InstalledVersion;
        else if (u.AvailableVersion is not null) u.InstalledVersion = u.AvailableVersion;
    }

    public static void MarkFailed(PendingUpdate u, string error, DateTimeOffset now)
    {
        u.State = UpdateState.Failed;
        u.FailureCount++;
        u.LastError = error;
        u.LastNotifiedUtc = null; // notify about the failure promptly
        u.ForceCloseAtUtc = null;
        u.InstallRequested = false;
    }

    public static void MarkWaitingForClose(PendingUpdate u, IReadOnlyList<string> blocking, DateTimeOffset now, bool scheduleForcedClose)
    {
        u.State = UpdateState.WaitingForClose;
        u.BlockingProcesses = [.. blocking];
        if (scheduleForcedClose && u.ForceCloseAtDeadline && u.IsPastDeadline(now) && u.ForceCloseAtUtc is null)
            u.ForceCloseAtUtc = now.AddMinutes(Math.Max(0, u.CloseGracePeriodMinutes));
    }

    /// <summary>True when the installed version reported is already at or above the available version.</summary>
    public static bool IsSatisfied(PendingUpdate u) =>
        !VersionComparer.IsUnknown(u.InstalledVersion) && VersionComparer.Compare(u.InstalledVersion, u.AvailableVersion) >= 0;
}
