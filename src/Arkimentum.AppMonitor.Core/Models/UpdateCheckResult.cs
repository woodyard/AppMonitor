namespace Arkimentum.AppMonitor.Models;

/// <summary>Result of asking a provider whether an update exists for an app.</summary>
public sealed record UpdateCheckResult
{
    public required string AppId { get; init; }
    public required UpdateSource Source { get; init; }
    /// <summary>True when the app was found on the machine (in the relevant context).</summary>
    public bool IsInstalled { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InstallerArgs { get; init; }
    public InstallerType InstallerType { get; init; }
    public string? Sha256 { get; init; }
    public string? WingetId { get; init; }
    /// <summary>Where the tray finds the application's icon (see <see cref="Inventory.AppIconSource.ForInstalledApp"/>); display only.</summary>
    public string? IconPath { get; init; }
    public string? Error { get; init; }
    /// <summary>System or User; the context in which the install was detected.</summary>
    public InstallContext ResolvedContext { get; init; }

    /// <summary>
    /// winget does not correlate the installed product with any package (its listing shows it only under an
    /// <c>ARP\…</c> pseudo id, e.g. an MSI product code that several packages claim), so the installed version comes from
    /// the registry detection and the available version from <c>winget show</c> for <see cref="WingetId"/>. The install
    /// then runs <c>winget install</c> instead of <c>winget upgrade</c> and is verified against the registry.
    /// </summary>
    public bool WingetUncorrelated { get; init; }

    public static UpdateCheckResult NotInstalled(string appId, UpdateSource source) =>
        new() { AppId = appId, Source = source, IsInstalled = false };

    public static UpdateCheckResult Failed(string appId, UpdateSource source, string error) =>
        new() { AppId = appId, Source = source, Error = error };
}

public sealed record InstallResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? Message { get; init; }
    public bool RebootRequired { get; init; }
    public string? InstalledVersion { get; init; }
    /// <summary>
    /// A success without an install: the provider found the product already at the target version and ran nothing
    /// (winget answered "no applicable upgrade"). The scan that offered the update read the device differently, or the
    /// product was updated in the meantime; either way it is not an install to announce or to record. Added after
    /// 1.1.39; an IPC message without it means false.
    /// </summary>
    public bool NothingInstalled { get; init; }

    /// <summary>
    /// A success that has not taken effect yet: the installer staged the new version of a running MSIX package and
    /// Windows registers it the next time the application starts (winget: "Successfully installed. Restart the
    /// application to complete the upgrade.", exit 0). <see cref="InstalledVersion"/> is the version still registered,
    /// which is below the target; that is expected here and no failure. Added after 1.1.44; an IPC message without it
    /// means false.
    /// </summary>
    public bool AppRestartPending { get; init; }

    /// <summary>
    /// A failure that is no fault of the update: the installer was refused because another installation held Windows
    /// Installer (MSI 1618, ERROR_INSTALL_ALREADY_RUNNING; winget 0x8A150102). Nothing was installed, and the same
    /// install is likely to work once the other one is done, so the service puts the update back for a later attempt
    /// instead of counting a failure. Seen on H-SURFACELAP5 (2026-10-19): five MSI-based updates in a row refused within
    /// six minutes while something outside AppMonitor (Windows Update or Intune) installed. Added after 1.1.48; an IPC
    /// message without it means false.
    /// </summary>
    public bool InstallerBusy { get; init; }

    /// <summary>
    /// A success that did not reach the target: winget exited with success and the version read back afterwards
    /// (<see cref="InstalledVersion"/>) is above the one installed before but below the update's available version. winget
    /// installs the latest version that has an installer of the installed copy's type, so when a product changes its
    /// installer technology (PowerShell 7.7.0 ships only as MSIX, devices have the MSI) the upgrade quietly stops at the last
    /// release of the old type (7.6.10). That version is recorded as installed (history, event, toast), but the update for
    /// the target stays pending - no post-install grace hides it, and nothing counts as a failure. See
    /// <see cref="IsPartialUpgrade"/>. Added after 1.1.55; an IPC message without it means false.
    /// </summary>
    public bool PartialUpgrade { get; init; }

    /// <summary>
    /// A refusal that is no fault of the attempt: winget answered "the install technology is different from the current
    /// version installed" (0x8A15008E) and no take-over ran (<c>WingetReplaceOnMismatch</c> is off). Nothing was installed,
    /// and every further attempt would be refused the same way until a newer version, the take-over or the user changes
    /// something, so the service holds the update (<c>PolicyEngine.MarkTechnologyMismatch</c>) instead of counting a
    /// failure. Added after 1.1.55; an IPC message without it means false.
    /// </summary>
    public bool TechnologyMismatch { get; init; }

    /// <summary>
    /// The MSIX package family the service's take-over (<c>WingetReplaceOnMismatch</c>, as LocalSystem) just staged and
    /// provisioned for all users, e.g. <c>Microsoft.PowerShell_8wekyb3d8bbwe</c>. Windows registers a provisioned package
    /// for existing users only at their next sign-in, so the service asks the tray agents of the signed-in users to
    /// register it right away. Null for every other install; never set by a user-context install.
    /// </summary>
    public string? ProvisionedPackageFamilyName { get; init; }

    /// <summary>
    /// Whether <paramref name="reached"/> is a partial upgrade from <paramref name="before"/> towards
    /// <paramref name="target"/> (see <see cref="PartialUpgrade"/>): all three versions known, and
    /// before &lt; reached &lt; target. Pure, so the rule is testable.
    /// </summary>
    public static bool IsPartialUpgrade(string? before, string? reached, string? target) =>
        Known(before) && Known(reached) && Known(target)
        && Versioning.VersionComparer.Compare(reached, before) > 0
        && Versioning.VersionComparer.Compare(reached, target) < 0;

    private static bool Known(string? version) => !string.IsNullOrWhiteSpace(version) && !Versioning.VersionComparer.IsUnknown(version);

    public static InstallResult Ok(string? message = null, int exitCode = 0, bool reboot = false) =>
        new() { Success = true, ExitCode = exitCode, Message = message, RebootRequired = reboot };

    public static InstallResult Fail(string message, int exitCode = -1) =>
        new() { Success = false, ExitCode = exitCode, Message = message };

    /// <summary>A refusal because another installation is in progress (see <see cref="InstallerBusy"/>).</summary>
    public static InstallResult Busy(string message, int exitCode) =>
        new() { Success = false, ExitCode = exitCode, Message = message, InstallerBusy = true };
}

/// <summary>Describes the security context the provider is running in.</summary>
public sealed record ExecutionContextInfo
{
    /// <summary>True when running as LocalSystem (service); false in the user's session (tray agent).</summary>
    public required bool IsSystem { get; init; }
    public string? UserSid { get; init; }
    public int SessionId { get; init; }
    public InstallContext Context => IsSystem ? InstallContext.System : InstallContext.User;

    public static ExecutionContextInfo System { get; } = new() { IsSystem = true, SessionId = 0 };
}
