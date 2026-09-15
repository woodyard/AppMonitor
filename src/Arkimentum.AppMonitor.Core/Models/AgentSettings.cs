namespace Arkimentum.AppMonitor.Models;

/// <summary>Global configuration. Read from the registry; see docs/Registry.md.</summary>
public sealed record AgentSettings
{
    public const string ProductName = "Arkimentum AppMonitor";
    public const string ServiceName = "ArkimentumAppMonitor";
    public const string ServiceDisplayName = "Arkimentum AppMonitor Agent";
    public const string PipeName = "Arkimentum.AppMonitor.Agent";
    public const string EventLogSource = "Arkimentum AppMonitor";
    public const string TrayExecutableName = "Arkimentum.AppMonitor.Tray.exe";
    public const string TrayMutexName = "Local\\Arkimentum.AppMonitor.Tray";
    public const string ToastAppUserModelId = "Arkimentum.AppMonitor.Tray";
    public const string RegistryRoot = @"SOFTWARE\Arkimentum\AppMonitor";
    public const string PolicyRegistryRoot = @"SOFTWARE\Policies\Arkimentum\AppMonitor";

    /// <summary>Minutes between update scans.</summary>
    public int ScanIntervalMinutes { get; init; } = 240;
    /// <summary>Minutes between repeated notifications for the same pending update (only used in <see cref="Models.NotificationMode.Reminders"/> mode).</summary>
    public int NotificationIntervalMinutes { get; init; } = 240;
    /// <summary>How insistent notifications are; Quiet announces an update once, Reminders repeats every <see cref="NotificationIntervalMinutes"/>.</summary>
    public NotificationMode NotificationMode { get; init; } = NotificationMode.Quiet;
    /// <summary>Seconds to wait after service start before the first scan.</summary>
    public int StartupDelaySeconds { get; init; } = 120;
    public bool ScanOnStartup { get; init; } = true;
    public bool WingetEnabled { get; init; } = true;
    public bool WebSourcesEnabled { get; init; } = true;
    /// <summary>Service launches the tray agent into interactive sessions when it is not running.</summary>
    public bool LaunchTrayAgent { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    /// <summary>Confirmation toast after a successful install. Off by default: a silent update needs no applause.</summary>
    public bool ShowInstalledNotifications { get; init; }
    /// <summary>Trace, Debug, Information, Warning, Error.</summary>
    public string LogLevel { get; init; } = "Information";
    public string LogDirectory { get; init; } = DefaultLogDirectory;
    public int LogRetentionDays { get; init; } = 30;
    public int MaxLogFileSizeMb { get; init; } = 10;
    public string StateDirectory { get; init; } = DefaultStateDirectory;
    /// <summary>Path to a catalog JSON file with app definitions. Empty = built-in catalog next to the executable.</summary>
    public string CatalogPath { get; init; } = string.Empty;
    /// <summary>Whether apps from the catalog are used to fill in missing per-app values.</summary>
    public bool UseCatalog { get; init; } = true;
    /// <summary>Timeout for a single installer run.</summary>
    public int InstallTimeoutMinutes { get; init; } = 30;
    /// <summary>Timeout for a single update check (one winget call or one web request).</summary>
    public int CheckTimeoutMinutes { get; init; } = 3;
    /// <summary>Explicit path to winget.exe; empty = auto-detect (WindowsApps package folder when running as SYSTEM).</summary>
    public string WingetPath { get; init; } = string.Empty;
    /// <summary>Install or repair winget (App Installer) automatically as SYSTEM when it is missing or too old.</summary>
    public bool AutoInstallPrerequisites { get; init; } = true;
    /// <summary>Minimum acceptable winget version; older installations are repaired when auto-install is on.</summary>
    public string WingetMinimumVersion { get; init; } = "1.6.0";
    /// <summary>Hours between prerequisite checks.</summary>
    public int PrerequisiteCheckIntervalHours { get; init; } = 24;

    // ---- cloud (organization) connection: the only values that must be provisioned when the cloud service is used ----
    /// <summary>Base URL of the AppMonitor cloud API, e.g. https://appmonitor-api.azurewebsites.net. Empty = cloud disabled.</summary>
    public string CloudServerUrl { get; init; } = string.Empty;
    /// <summary>Organization id (GUID) issued by the cloud service.</summary>
    public string CloudOrganizationId { get; init; } = string.Empty;
    /// <summary>Enrollment key of the organization; used once to obtain a per-device key.</summary>
    public string CloudEnrollmentKey { get; init; } = string.Empty;
    /// <summary>Minutes between configuration polls / reports when the cloud connection is configured.</summary>
    public int CloudSyncIntervalMinutes { get; init; } = 15;
    /// <summary>Whether the organization configuration from the cloud is applied (layered above local preferences).</summary>
    public bool CloudConfigEnabled { get; init; } = true;
    /// <summary>Whether device inventory and update state are reported to the cloud.</summary>
    public bool CloudReportingEnabled { get; init; } = true;

    // ---- agent self-update ----
    public bool AgentAutoUpdate { get; init; } = true;
    /// <summary>URL of the release manifest: a GitHub "latest release" API URL or a direct manifest.json URL. The keyword "cloud" uses the cloud API's mirror. Default: the official AppMonitor releases.</summary>
    public const string DefaultUpdateFeedUrl = "https://api.github.com/repos/woodyard/AppMonitor/releases/latest";
    public string AgentUpdateFeedUrl { get; init; } = DefaultUpdateFeedUrl;
    public string AgentUpdateChannel { get; init; } = "stable";
    public int AgentUpdateCheckIntervalHours { get; init; } = 12;
    /// <summary>Pin the agent to this version (no updates beyond it). Empty = always latest.</summary>
    public string AgentTargetVersion { get; init; } = string.Empty;

    public bool CloudConfigured => !string.IsNullOrWhiteSpace(CloudServerUrl) && Guid.TryParse(CloudOrganizationId, out _);
    /// <summary>Optional proxy URL for web sources; empty = system default.</summary>
    public string ProxyUrl { get; init; } = string.Empty;
    /// <summary>Additional winget arguments applied to every winget invocation.</summary>
    public string WingetGlobalArgs { get; init; } = string.Empty;
    /// <summary>Include winget results whose installed version is unknown.</summary>
    public bool WingetIncludeUnknown { get; init; }
    /// <summary>Minutes between checks for deadlines, deferrals and blocking processes.</summary>
    public int PolicyTickSeconds { get; init; } = 60;

    // ---- defaults applied to apps which do not specify a value ----
    public bool DefaultMandatory { get; init; }
    public int DefaultDeadlineHours { get; init; }
    public int DefaultMaxDeferrals { get; init; } = 3;
    public IReadOnlyList<int> DefaultDeferralOptionsMinutes { get; init; } = [60, 240, 1440];
    public bool DefaultAutoInstall { get; init; }
    public int DefaultCloseGracePeriodMinutes { get; init; } = 15;
    public bool DefaultForceCloseAtDeadline { get; init; } = true;

    public IReadOnlyList<AppPolicy> Apps { get; init; } = [];

    /// <summary>Diagnostic: where each value came from (Policy, Preference, Catalog, Default).</summary>
    public IReadOnlyDictionary<string, string> ValueSources { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset LoadedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static string DefaultLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Arkimentum", "AppMonitor", "Logs");

    public static string DefaultStateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Arkimentum", "AppMonitor");

    public TimeSpan ScanInterval => TimeSpan.FromMinutes(Math.Max(5, ScanIntervalMinutes));
    public TimeSpan NotificationInterval => TimeSpan.FromMinutes(Math.Max(1, NotificationIntervalMinutes));
    public TimeSpan PolicyTick => TimeSpan.FromSeconds(Math.Clamp(PolicyTickSeconds, 10, 600));
}
