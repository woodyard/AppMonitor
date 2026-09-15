namespace Arkimentum.AppMonitor.Prerequisites;

/// <summary>Result of the last prerequisite check. Sent to the admin console inside the settings summary.</summary>
public sealed class PrerequisiteStatus
{
    /// <summary>winget.exe was found in the current context (WindowsApps package folder for SYSTEM, alias for users).</summary>
    public bool WingetAvailable { get; set; }
    public string? WingetPath { get; set; }
    /// <summary>Version reported by <c>winget --version</c> (without the leading "v").</summary>
    public string? WingetVersion { get; set; }
    public string? MinimumVersion { get; set; }
    public bool WingetMeetsMinimum { get; set; }
    /// <summary>The App Installer package is provisioned for all users (null = could not determine, e.g. not elevated).</summary>
    public bool? AppInstallerProvisioned { get; set; }
    public string? AppInstallerProvisionedVersion { get; set; }
    public bool AutoInstallEnabled { get; set; }
    public DateTimeOffset? CheckedUtc { get; set; }
    /// <summary>Human-readable description of the last repair attempt (method used, or why it was skipped).</summary>
    public string? LastAction { get; set; }
    public string? LastError { get; set; }
    public bool RepairInProgress { get; set; }

    public bool IsHealthy => WingetAvailable && WingetMeetsMinimum;

    public string Summary =>
        RepairInProgress ? "Repair in progress" :
        !WingetAvailable ? "winget is not available" :
        !WingetMeetsMinimum ? $"winget {WingetVersion} is older than the required {MinimumVersion}" :
        $"winget {WingetVersion} available";

    public PrerequisiteStatus Clone() => (PrerequisiteStatus)MemberwiseClone();
}
