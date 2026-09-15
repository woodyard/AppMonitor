using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Configuration;

public enum SettingKind
{
    Bool,
    Int,
    String,
    /// <summary>A string that may contain environment variables (stored as REG_EXPAND_SZ).</summary>
    Path,
    /// <summary>Comma-separated integers, e.g. deferral options in minutes.</summary>
    IntList,
    /// <summary>List of strings (stored as REG_MULTI_SZ).</summary>
    StringList,
    /// <summary>String restricted to <see cref="SettingDefinition.Choices"/>.</summary>
    Choice,
}

/// <summary>Describes one registry value the agent understands. Single source of truth for the admin UI and the exporters.</summary>
public sealed record SettingDefinition(
    string Name,
    SettingKind Kind,
    string Category,
    string Title,
    string Description,
    object? Default = null,
    int Min = int.MinValue,
    int Max = int.MaxValue,
    IReadOnlyList<string>? Choices = null,
    bool Advanced = false)
{
    public RegistryValueKind RegistryKind => Kind switch
    {
        SettingKind.Bool or SettingKind.Int => RegistryValueKind.DWord,
        SettingKind.Path => RegistryValueKind.ExpandString,
        SettingKind.StringList => RegistryValueKind.MultiString,
        _ => RegistryValueKind.String,
    };

    public bool IsNumeric => Kind is SettingKind.Int or SettingKind.Bool;
}

/// <summary>
/// The registry value catalogue read by <see cref="RegistryConfigurationReader"/>: global values under
/// SOFTWARE\Arkimentum\AppMonitor and per-app values under Apps\&lt;AppId&gt;. Keep in sync with the reader.
/// </summary>
public static class SettingsSchema
{
    public const string CategoryScanning = "Scanning";
    public const string CategoryNotifications = "Notifications";
    public const string CategoryBehaviour = "Default update behaviour";
    public const string CategorySources = "Sources";
    public const string CategoryCloud = "Organization and updates";
    public const string CategoryLogging = "Logging and storage";
    public const string CategoryAdvanced = "Advanced";

    public static readonly IReadOnlyList<string> LogLevels = ["Trace", "Debug", "Information", "Warning", "Error"];
    public static readonly IReadOnlyList<string> SourceChoices = ["winget", "web"];
    public static readonly IReadOnlyList<string> ContextChoices = ["auto", "system", "user"];
    public static readonly IReadOnlyList<string> InstallerTypeChoices = ["exe", "msi", "msix"];
    public static readonly IReadOnlyList<string> NotificationModeChoices = ["Quiet", "Reminders"];

    public static readonly IReadOnlyList<SettingDefinition> Global =
    [
        new("ScanIntervalMinutes", SettingKind.Int, CategoryScanning, "Scan interval (minutes)", "How often the service checks the configured applications for updates.", 240, 5, 10080),
        new("ScanOnStartup", SettingKind.Bool, CategoryScanning, "Scan on service start", "Run a scan shortly after the service starts (after the startup delay).", true),
        new("StartupDelaySeconds", SettingKind.Int, CategoryScanning, "Startup delay (seconds)", "Wait this long after the service starts before the first scan.", 120, 0, 3600),
        new("PolicyTickSeconds", SettingKind.Int, CategoryScanning, "Policy evaluation interval (seconds)", "How often deadlines, deferrals and blocking processes are re-evaluated.", 60, 10, 600, Advanced: true),
        new("CheckTimeoutMinutes", SettingKind.Int, CategoryScanning, "Check timeout (minutes)", "Timeout for one update check (one winget call or one web request).", 3, 1, 60, Advanced: true),
        new("InstallTimeoutMinutes", SettingKind.Int, CategoryScanning, "Install timeout (minutes)", "Maximum time an installer may run before it is terminated.", 30, 1, 600, Advanced: true),

        new("NotificationsEnabled", SettingKind.Bool, CategoryNotifications, "Show notifications", "Show toast notifications to users about available updates.", true),
        new("NotificationIntervalMinutes", SettingKind.Int, CategoryNotifications, "Notification interval (minutes)", "Minimum time between repeated notifications for the same pending update.", 240, 1, 10080),
        new("NotificationMode", SettingKind.Choice, CategoryNotifications, "Notification style", "Quiet announces an update once and then only interrupts when the user must act (deadline, close applications, failure). Reminders repeats every notification interval.", "Quiet", Choices: NotificationModeChoices),
        new("ShowInstalledNotifications", SettingKind.Bool, CategoryNotifications, "Notify when an update was installed", "Show a confirmation toast after a successful install.", false),
        new("LaunchTrayAgent", SettingKind.Bool, CategoryNotifications, "Start the tray agent automatically", "The service starts the tray agent in every interactive session where it is not running.", true),

        new("DefaultMandatory", SettingKind.Bool, CategoryBehaviour, "Mandatory by default", "Treat updates as mandatory unless an application says otherwise.", false),
        new("DefaultDeadlineHours", SettingKind.Int, CategoryBehaviour, "Default deadline (hours)", "Hours after detection when a mandatory update is enforced. 0 = no deadline.", 0, 0, 8760),
        new("DefaultMaxDeferrals", SettingKind.Int, CategoryBehaviour, "Default maximum deferrals", "How many times a user may defer an update. 0 = unlimited.", 3, 0, 1000),
        new("DefaultDeferralOptions", SettingKind.IntList, CategoryBehaviour, "Default deferral choices (minutes)", "Deferral lengths offered to the user, in minutes, e.g. 60,240,1440.", "60,240,1440"),
        new("DefaultAutoInstall", SettingKind.Bool, CategoryBehaviour, "Install silently by default", "Install without asking when none of the application's processes are running.", false),
        new("DefaultCloseGracePeriodMinutes", SettingKind.Int, CategoryBehaviour, "Default grace period before forced close (minutes)", "Time the user gets to save work after a forced close is announced.", 15, 0, 1440),
        new("DefaultForceCloseAtDeadline", SettingKind.Bool, CategoryBehaviour, "Force-close applications at the deadline", "Terminate blocking processes when the deadline has passed and the grace period elapsed.", true),

        new("WingetEnabled", SettingKind.Bool, CategorySources, "Use winget", "Allow updates from the Windows Package Manager.", true),
        new("WebSourcesEnabled", SettingKind.Bool, CategorySources, "Use vendor web sites", "Allow updates downloaded directly from vendor web sites.", true),
        new("UseCatalog", SettingKind.Bool, CategorySources, "Use the application catalog", "Fill in application details (winget ids, URLs, process names) from catalog.json.", true),
        new("EnableAllCatalogApps", SettingKind.Bool, CategorySources, "Monitor every catalog application", "Monitor all catalog entries even when they have no registry entry.", false),
        new("CatalogPath", SettingKind.Path, CategorySources, "Catalog file", "Path to a custom catalog.json. Empty = the file shipped with the service.", "", Advanced: true),
        new("ProxyUrl", SettingKind.String, CategorySources, "Proxy URL", "HTTP proxy for web sources. Empty = system default.", "", Advanced: true),
        new("WingetGlobalArgs", SettingKind.String, CategorySources, "Extra winget arguments", "Appended to every winget invocation.", "", Advanced: true),
        new("WingetIncludeUnknown", SettingKind.Bool, CategorySources, "Include packages with unknown version", "Treat winget packages whose installed version is unknown as updatable.", false, Advanced: true),
        new("WingetPath", SettingKind.Path, CategorySources, "winget.exe path", "Explicit path to winget.exe. Empty = auto-detect.", "", Advanced: true),
        new("AutoInstallPrerequisites", SettingKind.Bool, CategorySources, "Install prerequisites automatically", "Install or repair the Windows Package Manager (App Installer) as SYSTEM when it is missing or older than the minimum version. Users need no rights.", true),
        new("WingetMinimumVersion", SettingKind.String, CategorySources, "Minimum winget version", "Older winget installations are repaired when automatic prerequisite installation is on.", "1.6.0", Advanced: true),
        new("PrerequisiteCheckIntervalHours", SettingKind.Int, CategorySources, "Prerequisite check interval (hours)", "How often the service re-checks that winget is available and current.", 24, 1, 720, Advanced: true),

        new("CloudServerUrl", SettingKind.String, CategoryCloud, "Cloud server URL", "Base URL of the AppMonitor cloud API. Empty = the agent works stand-alone.", ""),
        new("CloudOrganizationId", SettingKind.String, CategoryCloud, "Organization id", "Organization identifier (GUID) from the cloud service.", ""),
        new("CloudEnrollmentKey", SettingKind.String, CategoryCloud, "Enrollment key", "Organization enrollment key; used once per device to obtain a device key.", ""),
        new("CloudSyncIntervalMinutes", SettingKind.Int, CategoryCloud, "Sync interval (minutes)", "How often the agent polls the organization configuration and reports.", 15, 1, 1440, Advanced: true),
        new("CloudConfigEnabled", SettingKind.Bool, CategoryCloud, "Apply organization configuration", "Layer the organization configuration from the cloud above the local preferences.", true, Advanced: true),
        new("CloudReportingEnabled", SettingKind.Bool, CategoryCloud, "Report inventory to the cloud", "Send installed applications and update state to the organization's dashboard.", true, Advanced: true),
        new("AgentAutoUpdate", SettingKind.Bool, CategoryCloud, "Update the agent automatically", "Download and install new AppMonitor releases as SYSTEM.", true),
        new("AgentUpdateFeedUrl", SettingKind.String, CategoryCloud, "Agent update feed URL", "GitHub latest-release API URL or a manifest.json URL. Enter 'cloud' to use the cloud API's release mirror instead. Default: the official AppMonitor releases on GitHub.", Models.AgentSettings.DefaultUpdateFeedUrl, Advanced: true),
        new("AgentUpdateChannel", SettingKind.String, CategoryCloud, "Agent update channel", "stable or preview.", "stable", Advanced: true),
        new("AgentUpdateCheckIntervalHours", SettingKind.Int, CategoryCloud, "Agent update check interval (hours)", "", 12, 1, 720, Advanced: true),
        new("AgentTargetVersion", SettingKind.String, CategoryCloud, "Agent version ceiling", "Never update the agent beyond this version (it is not downgraded to it). Empty = always latest.", "", Advanced: true),

        new("LogLevel", SettingKind.Choice, CategoryLogging, "Log level", "Minimum level written to the log files.", "Information", Choices: LogLevels),
        new("LogDirectory", SettingKind.Path, CategoryLogging, "Log folder", "Folder for the service log files.", @"%ProgramData%\Arkimentum\AppMonitor\Logs"),
        new("LogRetentionDays", SettingKind.Int, CategoryLogging, "Log retention (days)", "Delete log files older than this.", 30, 1, 3650),
        new("MaxLogFileSizeMB", SettingKind.Int, CategoryLogging, "Maximum log file size (MB)", "Roll to a new file when the current one exceeds this size.", 10, 1, 1024),
        new("StateDirectory", SettingKind.Path, CategoryLogging, "State folder", "Folder for state.json and downloaded installers.", @"%ProgramData%\Arkimentum\AppMonitor", Advanced: true),
        new("TrayPath", SettingKind.Path, CategoryAdvanced, "Tray agent path", "Explicit path to the tray agent executable. Empty = auto-detect next to the service.", "", Advanced: true),
    ];

    public const string AppCategoryIdentity = "Identity";
    public const string AppCategorySource = "Source";
    public const string AppCategoryDetection = "Detection";
    public const string AppCategoryBehaviour = "Behaviour";

    public static readonly IReadOnlyList<SettingDefinition> App =
    [
        new("Enabled", SettingKind.Bool, AppCategoryIdentity, "Enabled", "Monitor this application.", true),
        new("DisplayName", SettingKind.String, AppCategoryIdentity, "Display name", "Name shown to users. Empty = from the catalog or the AppId."),
        new("Source", SettingKind.Choice, AppCategorySource, "Update source", "winget or the vendor web site.", "winget", Choices: SourceChoices),
        new("Context", SettingKind.Choice, AppCategorySource, "Install context", "auto = detect from where the app is installed; system = machine-wide; user = per user.", "auto", Choices: ContextChoices),
        new("WingetId", SettingKind.String, AppCategorySource, "winget package id", "Exact package identifier, e.g. 7zip.7zip. Separate alternatives with ; (e.g. Mozilla.Firefox;Mozilla.Firefox.MSIX); the first one that is installed is used."),
        new("WingetSource", SettingKind.String, AppCategorySource, "winget source", "winget source name.", "winget", Advanced: true),
        new("WingetExtraArgs", SettingKind.String, AppCategorySource, "Extra winget upgrade arguments", "Appended to winget upgrade for this app.", Advanced: true),
        new("VersionUrl", SettingKind.String, AppCategorySource, "Version URL", "Web page or API whose response contains the latest version."),
        new("VersionRegex", SettingKind.String, AppCategorySource, "Version regex", "Regular expression with one capture group (or a group named 'version')."),
        new("DownloadUrl", SettingKind.String, AppCategorySource, "Download URL", "Installer URL; supports {version}, {version_nodots}, {version_underscore}, {version_major}, {version_major_minor}."),
        new("InstallerType", SettingKind.Choice, AppCategorySource, "Installer type", "exe, msi or msix.", "exe", Choices: InstallerTypeChoices),
        new("InstallerArgs", SettingKind.String, AppCategorySource, "Silent install arguments", "Arguments for a silent install, e.g. /S or /qn."),
        new("UserDownloadUrl", SettingKind.String, AppCategorySource, "Download URL (user context)", "Override used when installing per user.", Advanced: true),
        new("UserInstallerArgs", SettingKind.String, AppCategorySource, "Install arguments (user context)", "Override used when installing per user.", Advanced: true),
        new("Sha256Url", SettingKind.String, AppCategorySource, "SHA-256 file URL", "Text file containing the SHA-256 of the download.", Advanced: true),
        new("Sha256", SettingKind.String, AppCategorySource, "SHA-256", "Literal SHA-256 of the download.", Advanced: true),
        new("DetectDisplayNameRegex", SettingKind.String, AppCategoryDetection, "Display name regex", "Matched against DisplayName in the Uninstall registry keys."),
        new("DetectPublisherRegex", SettingKind.String, AppCategoryDetection, "Publisher regex", "Matched against Publisher in the Uninstall registry keys.", Advanced: true),
        new("DetectFilePath", SettingKind.Path, AppCategoryDetection, "Version file", "File whose version is used as the installed version.", Advanced: true),
        new("MinimumVersion", SettingKind.String, AppCategoryDetection, "Minimum version", "Only flag an update when the installed version is below this.", Advanced: true),
        new("ProcessNames", SettingKind.StringList, AppCategoryBehaviour, "Processes to close", "Process names (without .exe) that must not run during the install."),
        new("Mandatory", SettingKind.Bool, AppCategoryBehaviour, "Mandatory", "The update must be installed by the deadline."),
        new("DeadlineHours", SettingKind.Int, AppCategoryBehaviour, "Deadline (hours after detection)", "0 = no deadline.", Min: 0, Max: 8760),
        new("MaxDeferrals", SettingKind.Int, AppCategoryBehaviour, "Maximum deferrals", "0 = unlimited.", Min: 0, Max: 1000),
        new("DeferralOptions", SettingKind.IntList, AppCategoryBehaviour, "Deferral choices (minutes)", "e.g. 60,240,1440."),
        new("AutoInstall", SettingKind.Bool, AppCategoryBehaviour, "Install silently", "Install without asking when no listed process is running."),
        new("CloseGracePeriodMinutes", SettingKind.Int, AppCategoryBehaviour, "Grace period before forced close (minutes)", "", Min: 0, Max: 1440),
        new("ForceCloseAtDeadline", SettingKind.Bool, AppCategoryBehaviour, "Force-close at the deadline", ""),
        new("NotificationIntervalMinutes", SettingKind.Int, AppCategoryBehaviour, "Notification interval (minutes)", "Overrides the global interval for this app.", Min: 1, Max: 10080, Advanced: true),
        new("NotificationMode", SettingKind.Choice, AppCategoryBehaviour, "Notification style", "Overrides the global notification style for this app.", Choices: NotificationModeChoices, Advanced: true),
    ];

    public static SettingDefinition? FindGlobal(string name) => Global.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static SettingDefinition? FindApp(string name) => App.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
