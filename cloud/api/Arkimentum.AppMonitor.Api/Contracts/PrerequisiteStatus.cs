namespace Arkimentum.AppMonitor.Api.Contracts;

/// <summary>
/// Result of the last prerequisite check on a device. Ported from Core's Prerequisites/PrerequisiteStatus.cs -
/// including the two computed, get-only properties, because System.Text.Json serialises those and the wire shape
/// must stay byte-identical to what the agent produces.
/// </summary>
public sealed class PrerequisiteStatus
{
    public bool WingetAvailable { get; set; }
    public string? WingetPath { get; set; }
    public string? WingetVersion { get; set; }
    public string? MinimumVersion { get; set; }
    public bool WingetMeetsMinimum { get; set; }
    public bool? AppInstallerProvisioned { get; set; }
    public string? AppInstallerProvisionedVersion { get; set; }
    public bool AutoInstallEnabled { get; set; }
    public DateTimeOffset? CheckedUtc { get; set; }
    public string? LastAction { get; set; }
    public string? LastError { get; set; }
    public bool RepairInProgress { get; set; }

    public bool IsHealthy => WingetAvailable && WingetMeetsMinimum;

    public string Summary =>
        RepairInProgress ? "Repair in progress" :
        !WingetAvailable ? "winget is not available" :
        !WingetMeetsMinimum ? $"winget {WingetVersion} is older than the required {MinimumVersion}" :
        $"winget {WingetVersion} available";
}
