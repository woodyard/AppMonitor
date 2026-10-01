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

    public static InstallResult Ok(string? message = null, int exitCode = 0, bool reboot = false) =>
        new() { Success = true, ExitCode = exitCode, Message = message, RebootRequired = reboot };

    public static InstallResult Fail(string message, int exitCode = -1) =>
        new() { Success = false, ExitCode = exitCode, Message = message };
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
