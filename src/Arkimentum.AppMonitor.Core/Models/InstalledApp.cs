namespace Arkimentum.AppMonitor.Models;

/// <summary>An application found in the Windows Uninstall registry keys.</summary>
public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>
    /// The raw <c>DisplayIcon</c> value (<c>path</c>, <c>path,index</c> or <c>"path",index</c>) with environment variables
    /// left unexpanded, so the tray expands them in its own session. Only used for the application's icon in the tray
    /// window; see <see cref="Inventory.AppIconSource"/>.
    /// </summary>
    public string? DisplayIcon { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ProductCode { get; init; }
    public bool IsMsi => UninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true;
    /// <summary>System (HKLM) or User (HKU\SID).</summary>
    public InstallContext Context { get; init; }
    /// <summary>SID of the owning user for per-user installs.</summary>
    public string? UserSid { get; init; }
    public string RegistryKeyPath { get; init; } = string.Empty;
    public bool Is64Bit { get; init; }
}
