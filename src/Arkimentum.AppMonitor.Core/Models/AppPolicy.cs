namespace Arkimentum.AppMonitor.Models;

/// <summary>
/// Per-application configuration. Read from the registry (HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\&lt;AppId&gt;,
/// overridable by HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor\Apps\&lt;AppId&gt;) and optionally seeded from the built-in catalog.
/// </summary>
public sealed record AppPolicy
{
    public required string AppId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public UpdateSource Source { get; init; } = UpdateSource.Winget;
    public InstallContext Context { get; init; } = InstallContext.Auto;

    // ---- winget ----
    public string? WingetId { get; init; }
    public string WingetSourceName { get; init; } = "winget";
    /// <summary>Extra arguments appended to winget upgrade.</summary>
    public string? WingetExtraArgs { get; init; }
    /// <summary>
    /// Opt-in: when winget refuses the upgrade because the installed package's technology differs from the
    /// manifest's installer (exit 0x8A15008E), uninstall the current package with winget and install the new one.
    /// Behaviour, so it never comes from the catalog.
    /// </summary>
    public bool WingetReplaceOnMismatch { get; init; }

    // ---- web (official site) ----
    /// <summary>URL whose response body contains the latest version (matched with <see cref="VersionRegex"/>).</summary>
    public string? VersionUrl { get; init; }
    /// <summary>Regex with one capture group (or a group named "version") yielding the latest version.</summary>
    public string? VersionRegex { get; init; }
    /// <summary>Download URL. Supports {version}, {version_nodots}, {version_major}, {version_underscore} placeholders.</summary>
    public string? DownloadUrl { get; init; }
    public string? InstallerArgs { get; init; }
    public InstallerType InstallerType { get; init; } = InstallerType.Exe;
    /// <summary>Optional URL to a text file with the SHA-256 hash of the download.</summary>
    public string? Sha256Url { get; init; }
    /// <summary>Optional literal SHA-256 of the download.</summary>
    public string? Sha256 { get; init; }
    /// <summary>Optional download URL override used for user-context installs (e.g. VS Code user setup).</summary>
    public string? UserDownloadUrl { get; init; }
    /// <summary>Optional installer arguments override used for user-context installs.</summary>
    public string? UserInstallerArgs { get; init; }

    // ---- detection (used for Web source and for Auto context) ----
    /// <summary>Regex matched against the DisplayName in the Uninstall registry keys.</summary>
    public string? DetectDisplayNameRegex { get; init; }
    /// <summary>Optional regex matched against the Publisher in the Uninstall registry keys.</summary>
    public string? DetectPublisherRegex { get; init; }
    /// <summary>Optional file path (env vars expanded) whose file version is used as the installed version.</summary>
    public string? DetectFilePath { get; init; }

    // ---- behaviour ----
    /// <summary>When true the update must be installed by the deadline; deferrals are bounded.</summary>
    public bool Mandatory { get; init; }
    /// <summary>Hours after first detection at which a mandatory update is enforced. 0 = no deadline.</summary>
    public int DeadlineHours { get; init; }
    /// <summary>Maximum number of deferrals the user may request. 0 = unlimited.</summary>
    public int MaxDeferrals { get; init; }
    /// <summary>Deferral choices offered to the user, in minutes.</summary>
    public IReadOnlyList<int> DeferralOptionsMinutes { get; init; } = [60, 240, 1440];
    /// <summary>Process names (without .exe) that must not be running while the update installs.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    /// <summary>Install without asking when no blocking process is running.</summary>
    public bool AutoInstall { get; init; }
    /// <summary>Minutes the user gets to save work after a forced close is announced.</summary>
    public int CloseGracePeriodMinutes { get; init; } = 15;
    /// <summary>Terminate blocking processes when the deadline has passed and the grace period elapsed.</summary>
    public bool ForceCloseAtDeadline { get; init; } = true;
    /// <summary>Only flag an update when the installed version is lower than this (optional).</summary>
    public string? MinimumVersion { get; init; }
    /// <summary>Override of the global notification interval for this app (minutes). null = global.</summary>
    public int? NotificationIntervalMinutes { get; init; }
    /// <summary>Override of the global notification mode for this app. null = global.</summary>
    public NotificationMode? NotificationMode { get; init; }

    public bool IsWeb => Source == UpdateSource.Web;
    public bool IsWinget => Source == UpdateSource.Winget;

    /// <summary>Returns a copy where empty/unset values are filled from <paramref name="fallback"/> (typically a catalog entry).</summary>
    public AppPolicy MergeDefaultsFrom(AppPolicy fallback)
    {
        return this with
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? fallback.DisplayName : DisplayName,
            WingetId = WingetId ?? fallback.WingetId,
            WingetSourceName = string.IsNullOrWhiteSpace(WingetSourceName) ? fallback.WingetSourceName : WingetSourceName,
            WingetExtraArgs = WingetExtraArgs ?? fallback.WingetExtraArgs,
            VersionUrl = VersionUrl ?? fallback.VersionUrl,
            VersionRegex = VersionRegex ?? fallback.VersionRegex,
            DownloadUrl = DownloadUrl ?? fallback.DownloadUrl,
            InstallerArgs = InstallerArgs ?? fallback.InstallerArgs,
            InstallerType = InstallerType == default && fallback.InstallerType != default ? fallback.InstallerType : InstallerType,
            Sha256Url = Sha256Url ?? fallback.Sha256Url,
            Sha256 = Sha256 ?? fallback.Sha256,
            UserDownloadUrl = UserDownloadUrl ?? fallback.UserDownloadUrl,
            UserInstallerArgs = UserInstallerArgs ?? fallback.UserInstallerArgs,
            DetectDisplayNameRegex = DetectDisplayNameRegex ?? fallback.DetectDisplayNameRegex,
            DetectPublisherRegex = DetectPublisherRegex ?? fallback.DetectPublisherRegex,
            DetectFilePath = DetectFilePath ?? fallback.DetectFilePath,
            ProcessNames = ProcessNames.Count > 0 ? ProcessNames : fallback.ProcessNames,
        };
    }
}
