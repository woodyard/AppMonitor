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
    /// <summary>
    /// Blocking processes are running: ask the user to close them (with countdown when forced close is scheduled) -
    /// but only when no install is ahead of this one, see <see cref="PolicyEngine.HoldPromptForTurn"/>.
    /// </summary>
    PromptClose,
    /// <summary>Grace period elapsed: queue the install; the blocking processes are closed when its turn comes.</summary>
    ForceClose,
}

/// <summary>What an update does once it holds the install lock; see <see cref="PolicyEngine.DecideAtTurn"/>.</summary>
public enum TurnAction
{
    /// <summary>Nothing is in the way: start the installer.</summary>
    Install,
    /// <summary>The user chose "Close apps and update", or deadline enforcement's grace period is over: close, then install.</summary>
    CloseAndInstall,
    /// <summary>Ask the user to close the blocking applications and give up the turn until they have.</summary>
    PromptClose,
    /// <summary>Blocked, but another queued install can start now: let it go first and stay queued.</summary>
    Yield,
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
    /// Whether the "Installing ..." toast is shown when this app's install starts: the app's own choice when it makes
    /// one, otherwise the global default. Auto keeps the historical behaviour (only Reminders shows the toast).
    /// </summary>
    public static bool NotifyInstallingFor(AppPolicy? policy, AgentSettings settings) =>
        (policy?.NotifyInstalling ?? settings.DefaultNotifyInstalling) switch
        {
            NotifyInstallingMode.Always => true,
            NotifyInstallingMode.Never => false,
            _ => NotificationModeFor(policy, settings) == NotificationMode.Reminders,
        };

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
                // Nor does it apply when the install read the new version back from the very source the scan uses and
                // no reboot is pending: a lower version now is a real change on the device (someone reinstalled an
                // older build), so it is a fresh update, not a failure of ours.
                var realChange = existing.InstallVerified && !existing.RebootPending;
                // But a scan that began before the install finished (now is the scan's start) may have read the version
                // before the installer moved it: that reading says nothing about the install and must not undo it. Seen on
                // H-PARALLELSVM: VS Code installed, verified, and installed a second time 51 seconds later.
                var readBeforeInstall = existing.InstalledAtUtc is { } done && done > now;
                if (readBeforeInstall || (installedRecently && sameTarget && !installKnownIncomplete && !realChange)) { existing.LastSeenUtc = now; continue; }
                // install did not stick, or a newer version appeared: start a new cycle but remember failures
                existing.State = UpdateState.Available;
                existing.InstalledAtUtc = null;
                existing.FirstDetectedUtc = now;
                existing.DeferralCount = 0;
                existing.DeferredUntilUtc = null;
                existing.ForceCloseAtUtc = null;
                existing.ForceCloseRequestedUtc = null;
                existing.LastNotifiedUtc = null;
                existing.Announced = false;
                existing.Dismissed = false;
                if (sameTarget && !realChange) existing.FailureCount++;
                existing.InstallVerified = false;
                existing.RebootPending = false;
            }

            var versionChanged = !string.Equals(existing.AvailableVersion, r.AvailableVersion, StringComparison.OrdinalIgnoreCase);
            ApplyPolicy(existing, o.Policy, r, now);
            existing.LastSeenUtc = now;
            if (versionChanged)
            {
                // A newer version is a new thing to tell the user about, even in Quiet mode.
                existing.Announced = false;
                // And a new install: a countdown announced, or a "Close apps and update" given, for the version it
                // replaces is no licence to close anything for this one. The deadline clock keeps running; the user is
                // simply warned (or asked) again when this version's turn comes.
                existing.ForceCloseAtUtc = null;
                existing.ForceCloseRequestedUtc = null;
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

    /// <summary>
    /// Drops the tracked updates of applications that are no longer among the enabled ones: removed from the
    /// configuration, disabled, or rejected by the configuration reader (a "web" application without its URLs).
    /// <see cref="Merge"/> cannot do this: such an application is never checked, so it is neither reported nor among
    /// the checked keys, and its update would survive every scan. One that was already scheduled then sits at
    /// "Preparing updates" for good, because the install flow finds no policy and has nothing to run. An install in
    /// flight is left alone. Returns how many entries were dropped.
    /// </summary>
    public static int PruneUnconfigured(IDictionary<string, PendingUpdate> state, IEnumerable<AppPolicy> enabledApps)
    {
        var configured = enabledApps.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var key in state.Keys.ToList())
        {
            var u = state[key];
            if (u.State == UpdateState.Installing || configured.Contains(u.AppId)) continue;
            state.Remove(key);
            removed++;
        }
        return removed;
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
        p.WingetReplaceOnMismatch = policy.WingetReplaceOnMismatch;
        p.DownloadUrl = r.DownloadUrl;
        p.InstallerArgs = r.InstallerArgs;
        p.InstallerType = r.InstallerType;
        p.Sha256 = r.Sha256;
        // A scan that found no icon (the entry briefly missing mid-upgrade, an older tray) keeps the one already known.
        p.IconPath = r.IconPath ?? p.IconPath;
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

    /// <summary>
    /// How long a user's "Close apps and update" stays a licence for the service to kill. The install normally starts
    /// within seconds; the window only guards against a request that was persisted and then sat around (no tray agent
    /// for a user-context install, a service restart), after which the user is asked again rather than surprised.
    /// </summary>
    public static readonly TimeSpan ForceCloseRequestWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Decides whether the service may terminate the processes that block an update itself instead of prompting the
    /// user again. Two things earn that, and nothing else: the user pressed "Close apps and update" recently - the
    /// dialog warns that unsaved work may be lost, and the tray has already closed everything it could reach in its
    /// own session - or deadline enforcement is on and its grace period has run out. Without this the service keeps
    /// re-prompting for processes no tray agent can ever close (elevated, or in another session) and the dialog
    /// reappears forever.
    /// </summary>
    public static bool MayServiceForceClose(PendingUpdate u, DateTimeOffset now)
    {
        if (u.State is UpdateState.Installing or UpdateState.Installed) return false;
        if (HasForceCloseRequest(u, now)) return true;
        // Both licences must belong to this update cycle: a timestamp from before it was detected (a state file written
        // by an older agent that carried it over to a newer version) was given for something else.
        return u.ForceCloseAtDeadline && u.IsPastDeadline(now) && u.ForceCloseAtUtc is { } at && at >= u.FirstDetectedUtc && now >= at;
    }

    /// <summary>True while a "Close apps and update" from this update cycle is less than <see cref="ForceCloseRequestWindow"/> old.</summary>
    public static bool HasForceCloseRequest(PendingUpdate u, DateTimeOffset now) =>
        u.ForceCloseRequestedUtc is { } requested && requested >= u.FirstDetectedUtc && now - requested <= ForceCloseRequestWindow;

    /// <summary>
    /// Forgets a forced-close time or a "Close apps and update" that predates the current update cycle, the way
    /// <see cref="Merge"/> clears both when a cycle starts. Older agents did not clear them when a newer version
    /// superseded a pending one, so a state file can still carry them. Returns true when something was cleared.
    /// </summary>
    public static bool ClearStaleForceClose(PendingUpdate u)
    {
        var cleared = false;
        if (u.ForceCloseAtUtc is { } at && at < u.FirstDetectedUtc) { u.ForceCloseAtUtc = null; cleared = true; }
        if (u.ForceCloseRequestedUtc is { } requested && requested < u.FirstDetectedUtc) { u.ForceCloseRequestedUtc = null; cleared = true; }
        return cleared;
    }

    /// <summary>
    /// Whether an update would really start installing at its turn: it is installing already, nothing of it is
    /// running, or the service may close what is (see <see cref="MayServiceForceClose"/>). An update that would only
    /// ask the user to close something is not ready, and does not hold anyone else up.
    /// </summary>
    public static bool IsReadyToInstall(PendingUpdate u, DateTimeOffset now, bool blockingProcessesRunning) =>
        u.State == UpdateState.Installing || !blockingProcessesRunning || MayServiceForceClose(u, now);

    /// <summary>
    /// What an update does when its turn comes: it holds the install lock, so nothing else is installing, and this
    /// is the moment right before its installer would start. That is the only place the user is asked to close an
    /// application and the only place anything is closed for them, so a closed application is never left waiting
    /// behind other installs. <paramref name="anotherInstallReady"/> is whether another queued install would start
    /// as soon as this one lets go; a blocked update then gives up its turn instead of asking - it stays queued, and
    /// the prompt comes once nothing is left ahead of it.
    /// </summary>
    public static TurnAction DecideAtTurn(PendingUpdate u, DateTimeOffset now, bool blockingProcessesRunning, bool anotherInstallReady)
    {
        if (!blockingProcessesRunning) return TurnAction.Install;
        if (MayServiceForceClose(u, now)) return TurnAction.CloseAndInstall;
        return anotherInstallReady ? TurnAction.Yield : TurnAction.PromptClose;
    }

    /// <summary>
    /// The policy tick's close prompt, held back while an install is running or ready to start
    /// (<paramref name="installAhead"/>): the user would close the application and then wait for the others, and
    /// might well reopen it. The prompt comes on a later tick, once nothing is ahead of this update any more.
    /// </summary>
    public static PolicyAction HoldPromptForTurn(PolicyAction action, bool installAhead) =>
        action.Kind == PolicyActionKind.PromptClose && installAhead ? PolicyAction.None : action;

    /// <summary>Records that a user pressed "Close apps and update" for this update.</summary>
    public static void RequestForcedClose(PendingUpdate u, DateTimeOffset now)
    {
        if (u.State is UpdateState.Installed) return;
        u.ForceCloseRequestedUtc = now;
    }

    /// <summary>Applies a user deferral. Returns false (and leaves the update untouched) when the request is not allowed.</summary>
    public static bool TryDefer(PendingUpdate u, int minutes, DateTimeOffset now, out string? reason)
    {
        reason = null;
        if (!u.CanDefer(now))
        {
            reason = u.IsPastDeadline(now) ? "The deadline has passed."
                : u.IsDeferred(now) ? $"Already deferred until {u.DeferredUntilUtc!.Value.ToLocalTime():t}."
                : "No more deferrals are allowed.";
            return false;
        }
        if (minutes <= 0 || (u.DeferralOptionsMinutes.Count > 0 && !u.DeferralOptionsMinutes.Contains(minutes)))
        { reason = "That deferral length is not allowed."; return false; }

        var until = now.AddMinutes(minutes);
        if (u.DeadlineUtc is { } dl && until > dl) until = dl;
        u.DeferredUntilUtc = until;
        u.DeferralCount++;
        u.State = UpdateState.Deferred;
        u.ForceCloseAtUtc = null;
        u.ForceCloseRequestedUtc = null; // the user changed their mind; withdraw the licence to kill
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
        u.ForceCloseRequestedUtc = null; // the user changed their mind; withdraw the licence to kill
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
        u.ForceCloseRequestedUtc = null;
        u.InstallRequested = false;
        u.BlockingProcesses = [];
        u.BlockingDetails = [];
        if (!string.IsNullOrWhiteSpace(result.InstalledVersion)) u.InstalledVersion = result.InstalledVersion;
        else if (u.AvailableVersion is not null) u.InstalledVersion = u.AvailableVersion;
        // Verified = the provider read the target version back after installing; an assumed version is not a verification.
        u.InstallVerified = !string.IsNullOrWhiteSpace(result.InstalledVersion) && !VersionComparer.IsUnknown(result.InstalledVersion)
                            && !string.IsNullOrWhiteSpace(u.AvailableVersion)
                            && VersionComparer.Compare(result.InstalledVersion, u.AvailableVersion) >= 0;
        u.RebootPending = result.RebootRequired;
    }

    public static void MarkFailed(PendingUpdate u, string error, DateTimeOffset now)
    {
        u.State = UpdateState.Failed;
        u.FailureCount++;
        u.LastError = error;
        u.LastNotifiedUtc = null; // notify about the failure promptly
        u.ForceCloseAtUtc = null;
        u.ForceCloseRequestedUtc = null; // a new attempt must be asked for again
        u.InstallRequested = false;
    }

    /// <summary>
    /// Parks the update until the blocking processes are gone. <paramref name="details"/> is what the service (SYSTEM)
    /// could read about each instance, so the tray dialog can say which of them it is not able to close itself.
    /// </summary>
    public static void MarkWaitingForClose(PendingUpdate u, IReadOnlyList<string> blocking, DateTimeOffset now, bool scheduleForcedClose,
        IReadOnlyList<BlockingProcessInfo>? details = null)
    {
        u.State = UpdateState.WaitingForClose;
        u.BlockingProcesses = [.. blocking];
        if (details is not null) u.BlockingDetails = [.. details];
        if (scheduleForcedClose && u.ForceCloseAtDeadline && u.IsPastDeadline(now) && u.ForceCloseAtUtc is null)
            u.ForceCloseAtUtc = now.AddMinutes(Math.Max(0, u.CloseGracePeriodMinutes));
    }

    /// <summary>True when the installed version reported is already at or above the available version.</summary>
    public static bool IsSatisfied(PendingUpdate u) =>
        !VersionComparer.IsUnknown(u.InstalledVersion) && VersionComparer.Compare(u.InstalledVersion, u.AvailableVersion) >= 0;
}
