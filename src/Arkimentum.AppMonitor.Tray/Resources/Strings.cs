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
    public const string TrayMenuCheckNow = "Check for updates now";
    public const string TrayMenuOpenLogFolder = "Open log folder";
    public const string TrayMenuAbout = "About Arkimentum AppMonitor";
    public const string TrayMenuExit = "Exit (debug)";
    public const string TrayTooltipUpToDate = ProductName + " — up to date";
    public const string TrayTooltipDisconnected = ProductName + " — service not connected";

    public static string TrayTooltipUpdates(int count) =>
        count == 1
            ? ProductName + " — 1 update available"
            : $"{ProductName} — {count} updates available";

    // ---------------------------------------------------------------- main window
    public const string MainWindowTitle = ProductName;
    public const string MainHeaderSubtitle = "Keeps your applications up to date";
    public const string CheckNow = "Check now";
    public const string UpdateAll = "Update all";
    /// <summary>"Update all (3)" - the count is the number of updates the button will queue.</summary>
    public static string UpdateAllCount(int count) => count > 0 ? $"{UpdateAll} ({count})" : UpdateAll;
    public const string Checking = "Checking…";
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
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph "CheckMark".</summary>
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
    public static string StateInstalled(string when) => $"Installed {when}";
    public static string StateFailed(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "Failed" : $"Failed: {error}";
    public static string RequiredBy(string when) => $"Required by {when} ({StateMandatory})";
    public static string DeferralsUsed(int used, int max) => $"{used} of {max} deferrals used";

    // ---------------------------------------------------------------- details expander
    public const string DetailsHeader = "Details";
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

    // ---------------------------------------------------------------- close-apps dialog
    public const string CloseAppsIntro = "These applications must be closed before the update can be installed:";
    public const string CloseAppsIntroSingle = "This application must be closed before the update can be installed:";
    public const string CloseAppsSaveHint = "Save your work first — unsaved changes may be lost.";
    public const string CloseAppsAndUpdate = "Close apps and update";
    public const string CloseAppsNotNow = "Not now";
    public const string CloseAppsClosing = "Closing applications…";
    public const string CloseAppsCountdownElapsed = "Your apps are being closed now.";
    public const string CloseAppsAllClosed = "All applications closed. Starting the update…";

    public static string CloseAppsTitle(string displayName) => $"Close apps to update {displayName}";
    public static string CloseAppsCountdown(string remaining) => $"Your apps will be closed automatically in {remaining}";
    public static string CloseAppsStillRunning(string processes) => $"Still running: {processes}. Close them and try again.";

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
    public const string ToastButtonCloseAndUpdate = "Close apps and update";

    public static string ToastButtonDefer(string duration) => $"Defer {duration}";

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
