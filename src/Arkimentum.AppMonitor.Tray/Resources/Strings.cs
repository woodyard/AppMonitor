using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Tray.Resources;

/// <summary>
/// Every user-visible string of the tray agent. English only for now; keeping them here means no literal
/// ever lives in a view or a code-behind file.
/// </summary>
public static class Strings
{
    // ---------------------------------------------------------------- product
    public const string ProductName = AgentSettings.ProductName;
    public const string Copyright = "© 2026 Arkimentum";

    /// <summary>The house name of the wordmark; set in the Gelasio serif, olive.</summary>
    public const string WordmarkBrand = "Arkimentum";

    /// <summary>The product word of the wordmark; set as small letter-spaced terracotta caps.</summary>
    public const string WordmarkProduct = "APPMONITOR";

    // ---------------------------------------------------------------- tray
    public const string TrayMenuOpen = "Open Arkimentum AppMonitor";
    public const string TrayMenuCheckNow = "Check apps for updates";
    public const string TrayMenuCheckAgentUpdate = "Check for AppMonitor update";
    /// <summary>Title of the toast that answers the menu's AppMonitor update item; the body is the service's answer.</summary>
    public const string AgentUpdateToastTitle = "AppMonitor update";
    public const string AgentUpdateNoAnswer = "The service did not answer. Try again in a moment.";
    /// <summary>Replaces <see cref="TrayMenuCheckAgentUpdate"/> once the service knows a newer agent release exists.</summary>
    public static string TrayMenuUpdateAgent(string version) => $"Update AppMonitor to {version}";
    public const string TrayMenuOpenLogFolder = "Open log folder";
    public const string TrayMenuAbout = "About Arkimentum AppMonitor";
    public const string TrayMenuExit = "Exit (debug)";
    public const string TrayTooltipUpToDate = ProductName + " — up to date";
    public const string TrayTooltipDisconnected = ProductName + " — service not connected";

    public static string TrayTooltipUpdates(int count) =>
        count == 1
            ? ProductName + " — 1 update available"
            : $"{ProductName} — {count} updates available";

    /// <summary>Tooltip while an install is running, so the user sees progress without opening the window.</summary>
    public static string TrayTooltipInstalling(string app) => $"{ProductName} — installing {app}…";
    public const string TrayTooltipChecking = ProductName + " — checking for updates…";

    // ---------------------------------------------------------------- main window
    public const string MainWindowTitle = ProductName;
    public const string MainHeaderSubtitle = "Keeps your applications up to date";
    public const string CheckNow = "Check now";
    public const string UpdateAll = "Update all";
    /// <summary>"Update all (3)" - the count is the number of updates the button will queue.</summary>
    public static string UpdateAllCount(int count) => count > 0 ? $"{UpdateAll} ({count})" : UpdateAll;
    public const string Checking = "Checking…";

    // ---- progress banner: shown while the service works through queued updates
    /// <summary>Headline while one update installs, e.g. "Installing 7-Zip…".</summary>
    public static string ProgressInstalling(string app) => $"Installing {app}…";
    /// <summary>Headline while updates are queued but none has started yet.</summary>
    public const string ProgressPreparing = "Preparing updates…";
    /// <summary>Headline while a scan runs and nothing is installing, e.g. after "Check now".</summary>
    public const string ProgressChecking = "Checking for updates…";
    /// <summary>Headline while the user still has to close an application before the install can start.</summary>
    public const string ProgressWaitingForClose = "Waiting for applications to close…";

    /// <summary>
    /// The tail of the progress line, e.g. "(3 of 6 finished, 1 failed, 2 queued)". Finished means installed or
    /// failed, so the failures are a part of it; each of the last two is left out while it is zero. Only added when
    /// the round holds more than one update; a single install needs no scoreboard.
    /// </summary>
    public static string ProgressCounts(int finished, int total, int failed, int queued)
    {
        var parts = new List<string>(3) { $"{finished} of {total} finished" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (queued > 0) parts.Add($"{queued} queued");
        return $"({string.Join(", ", parts)})";
    }
    public const string StatusDisconnected = "Service not connected";
    public const string DisconnectedBanner = "Waiting for the Arkimentum AppMonitor service…";
    public const string DisconnectedBannerDetail =
        "The background service is not running yet, or it has not accepted the connection. Updates cannot be installed until it does.";
    public const string NeverChecked = "Not checked yet";
    public const string StatusSeparator = " · ";

    public static string LastChecked(string when) => $"Last checked {when}";
    public static string NextCheck(string when) => $"Next check {when}";

    public const string EmptyTitle = "You’re up to date";
    public const string EmptySubtitleNoScan = "No update check has run yet.";
    public const string EmptyGlyph = "\uE73E";

    public static string EmptySubtitle(string when) => $"All monitored applications were up to date at {when}.";

    // ---------------------------------------------------------------- update card
    public const string InstallNow = "Install now";
    public const string Defer = "Defer";
    public const string RemindMeLater = "Remind me later";
    public const string NoMoreDeferrals = "No more deferrals left";
    public const string BadgeWinget = "winget";
    public const string BadgeWeb = "web";
    public const string BadgeSystem = "System";
    public const string BadgeUser = "User";
    public const string VersionArrow = " → ";
    public const string UnknownVersion = "unknown";

    public const string StateAvailable = "Available";
    public const string StateScheduled = "Queued for installation";
    public const string StateInstalling = "Installing…";
    public const string StateMandatory = "mandatory";

    public static string StateDeferred(string until) => $"Deferred until {until}";
    public static string StateWaitingForClose(string processes) => $"Waiting for you to close: {processes}";

    /// <summary>
    /// The same status with the way out: the close-apps dialog can be closed (or dismissed with its X) and the card
    /// is then the only thing left saying what is wrong. Appended whenever the card offers to reopen the dialog.
    /// </summary>
    public const string StateWaitingForCloseReopenHint = "Choose “Close apps and update” to open the dialog again.";

    public static string StateWaitingForCloseWithHint(string processes) =>
        $"{StateWaitingForClose(processes)} {StateWaitingForCloseReopenHint}";
    public static string StateInstalled(string when) => $"Installed {when}";
    public static string StateFailed(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "Failed" : $"Failed: {error}";
    public static string RequiredBy(string when) => $"Required by {when} ({StateMandatory})";
    public static string DeferralsUsed(int used, int max) => $"{used} of {max} deferrals used";

    // ---------------------------------------------------------------- tabs under the header
    /// <summary>The Updates tab while there are none; <see cref="TabUpdatesCount"/> otherwise.</summary>
    public const string TabUpdates = "Updates";
    public static string TabUpdatesCount(int count) => $"Updates · {count}";
    public const string TabRecent = "Recent";
    public const string TabDetails = "Details";

    // ---------------------------------------------------------------- recent updates tab
    public const string RecentInstallsEmpty = "No updates installed yet";
    /// <summary>Tooltip of a successful row, e.g. "Updated from 155.0.4 to 156.0.1".</summary>
    public static string RecentInstallUpdatedFrom(string from, string to) => $"Updated from {from} to {to}";

    // ---------------------------------------------------------------- details tab
    public const string DetailsOrganization = "Organization";
    public const string OrganizationStandalone = "None — this device is managed locally";
    public const string OrganizationEnrolling = "Connecting to the organization…";
    public const string DetailsScanInterval = "Scan interval";
    public const string DetailsNotificationInterval = "Notification interval";
    public const string DetailsMonitoredApps = "Monitored applications";
    public const string DetailsLogFolder = "Log folder";
    public const string DetailsOpen = "Open";
    public const string DetailsServiceVersion = "Service version";
    public const string DetailsAgentVersion = "Agent version";
    public const string DetailsSources = "Update sources";
    public const string DetailsNotifications = "Notifications";
    public const string DetailsNone = "—";
    public const string DetailsEnabled = "Enabled";
    public const string DetailsDisabled = "Disabled";

    public static string EveryDuration(string duration) => $"Every {duration}";
    public static string AndMore(int count) => $"and {count} more";

    /// <summary>
    /// The second line under "Monitored applications": the configuration is fleet-wide, so the panel says how much of
    /// it is about this device. Only shown when the two numbers differ.
    /// </summary>
    public static string MonitoredAppsScope(int applicable, int configured) =>
        $"{applicable} of {configured} monitored applications apply to this device";

    /// <summary>Shown instead of the list when the configuration covers nothing that is installed here.</summary>
    public static string MonitoredAppsNoneApply(int configured) =>
        configured == 1
            ? "The 1 monitored application is not installed on this device"
            : $"None of the {configured} monitored applications are installed on this device";

    // ---------------------------------------------------------------- the agent's own update
    /// <summary>The two buttons next to the agent version, in the details footer and in the About dialog.</summary>
    public const string AgentCheckForUpdates = "Check for updates";
    public const string AgentUpdateNow = "Update now";

    /// <summary>Joins the version and its status: "1.1.3 · up to date (checked 16:22)".</summary>
    public static string AgentVersionWithStatus(string version, string status) => $"{version}{StatusSeparator}{status}";

    public static string AgentUpToDate(string when) => $"up to date (checked {when})";
    public const string AgentNotCheckedYet = "not checked yet";
    public static string AgentUpdateAvailable(string version) => $"update {version} available";
    public const string AgentUpdateDisabled = "updates disabled by policy";
    public const string AgentUpdateChecking = "checking…";
    public const string AgentUpdateCheckFailed = "the last check failed";

    /// <summary>Progress banner while the service replaces the agent; it stops and starts the tray itself.</summary>
    public static string AgentUpdateProgress(string version) =>
        $"Updating {ProductName} to {version}… the agent restarts by itself";
    /// <summary>The same banner before a version is known (the check is still running).</summary>
    public const string AgentUpdateProgressUnknown = "Updating " + ProductName + "… the agent restarts by itself";

    // ---------------------------------------------------------------- close-apps dialog
    public const string CloseAppsIntro = "These applications must be closed before the update can be installed:";
    public const string CloseAppsIntroSingle = "This application must be closed before the update can be installed:";
    public const string CloseAppsSaveHint = "Save your work first — unsaved changes may be lost.";
    public const string CloseAppsAndUpdate = "Close apps and update";
    public const string CloseAppsNotNow = "Not now";
    public const string CloseAppsClosing = "Closing applications…";
    public const string CloseAppsForcing = "Closing what did not respond…";
    public const string CloseAppsCountdownElapsed = "Your apps are being closed now.";
    public const string CloseAppsAllClosed = "All applications closed. Starting the update…";
    /// <summary>Shown when something is left that only the service can end; the dialog then closes instead of re-asking.</summary>
    public const string CloseAppsHandedToService = "The update service is closing the rest and then installs the update…";
    /// <summary>Explains the "elevated" / "another session" markers below the list of blocking applications.</summary>
    public const string CloseAppsServiceCloses =
        "Some of these run as an administrator or in another user's session, so this app cannot close them — " +
        "the update service closes those for you when you choose \"Close apps and update\".";
    /// <summary>Marker after a process that runs elevated: this agent cannot touch it.</summary>
    public const string CloseAppsElevated = "elevated";
    /// <summary>Marker for a process in a session other than the one this agent runs in, when the user is not readable.</summary>
    public const string CloseAppsOtherSession = "another session";

    public static string CloseAppsTitle(string displayName) => $"Close apps to update {displayName}";
    public static string CloseAppsCountdown(string remaining) => $"Your apps will be closed automatically in {remaining}";
    public static string CloseAppsStillRunning(string processes) => $"Still running: {processes}. Close them and try again.";
    /// <summary>"another session (H-SURFACELAP5\bob)" — who else has the application open.</summary>
    public static string CloseAppsOtherSessionAs(string userName) => $"{CloseAppsOtherSession} ({userName})";
    /// <summary>Wraps the markers so they read as an aside: "pwsh — elevated, another session (CONTOSO\bob)".</summary>
    public static string CloseAppsQualifier(string markers) => $"— {markers}";

    // ---------------------------------------------------------------- about dialog
    public const string AboutTitle = "About " + ProductName;
    public const string AboutDescription =
        "Arkimentum AppMonitor keeps the applications on this device up to date. A background service finds the updates; " +
        "this agent shows them to you and installs the ones that belong to your user account.";
    public const string AboutOpenLogFolder = "Open log folder";
    public const string AboutClose = "Close";

    public static string AboutVersion(string version) => $"Version {version}";

    // ---------------------------------------------------------------- toasts
    public const string ToastButtonInstallNow = "Install now";
    public const string ToastButtonDetails = "Details";
    /// <summary>The only button on the "Installing" toast: hides it; the install carries on.</summary>
    public const string ToastButtonHide = "Hide";
    /// <summary>Status line under the progress bar of the "Installing" toast.</summary>
    public const string ToastInstallingStatus = "In progress";
    public const string ToastButtonCloseAndUpdate = "Close apps and update";

    public static string ToastButtonDefer(string duration) => $"Defer {duration}";

    /// <summary>Title of the toast that stands in for several "update available" toasts from the same scan.</summary>
    public static string ToastSummaryTitle(int count) => $"{count} updates available";

    /// <summary>Body of the summary toast: the application names, with an overflow tail when there are many.</summary>
    public static string ToastSummaryBody(IReadOnlyList<string> names, int shown)
    {
        var listed = string.Join(", ", names.Take(shown));
        var rest = names.Count - shown;
        return rest > 0 ? $"{listed} and {rest} more are ready to install." : $"{listed} are ready to install.";
    }

    // ---------------------------------------------------------------- user-context execution
    public const string UserInstallPreparing = "Preparing…";
    public const string UserInstallClosingApps = "Closing applications…";
    public const string UserInstallQueued = "Queued…";

    public static string UserInstallProcessesStillRunning(string processes) => $"processes still running: {processes}";

    // ---------------------------------------------------------------- durations
    public const string DurationNow = "now";

    public static string InDuration(string duration) => $"in {duration}";
    public static string DurationAgo(string duration) => $"{duration} ago";
}
