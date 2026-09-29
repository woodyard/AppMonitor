namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// What the user context asks the AppMonitor service to install machine-wide (see
/// <see cref="ProviderOptions.SystemInstallHandOver"/>). The package is installed in the user's session as an MSIX
/// package, and winget's manifest only offers an installer that needs elevation (Microsoft.WindowsAppRuntime.1.6: an
/// exe that provisions the runtime, no Scope declared, so neither <c>--scope user</c> nor <c>--installer-type msix</c>
/// selects it). The service runs as SYSTEM in session 0, where no UAC prompt can appear, and installs it for all users.
/// </summary>
/// <param name="UpdateKey">The pending update the user-context install belongs to; the service only accepts a hand-over for the install it is waiting for.</param>
/// <param name="WingetId">The winget id winget refused in the user context; the service only installs it when it is one of the update's own ids.</param>
/// <param name="PackageFamilyName">The MSIX package family of the user's installed copy, for the service's before/after evidence.</param>
/// <param name="UserPackageVersion">The highest MSIX package version registered for the user before the hand-over (e.g. 6000.519.297.0), or null.</param>
public sealed record SystemInstallHandOverRequest(string UpdateKey, string WingetId, string PackageFamilyName, string? UserPackageVersion);

/// <summary>The service's answer to a <see cref="SystemInstallHandOverRequest"/>.</summary>
/// <param name="Ok">True when the service's machine-wide install succeeded (or winget found nothing newer to install).</param>
/// <param name="Message">The service's summary: winget's exit code and the package registrations before and after; the reason when it refused.</param>
/// <param name="ExitCode">winget's exit code, or the refusal's code when nothing ran.</param>
/// <param name="MachinePackageVersion">The highest package version of the family on the machine after the install (any user, or provisioned), or null when it could not be read.</param>
public sealed record SystemInstallHandOverReply(bool Ok, string? Message, int ExitCode, string? MachinePackageVersion);

/// <summary>How the user context judges a hand-over to the service (see <c>WingetProvider.JudgeHandOver</c>).</summary>
public enum HandOverVerdict
{
    /// <summary>The service did not answer, refused, or its install failed.</summary>
    Failed,
    /// <summary>The user's own copy now has the expected version.</summary>
    Installed,
    /// <summary>A newer package of the family is on the machine, but the user's registration still has the old one; it moves at the next sign-in.</summary>
    PendingSignIn,
    /// <summary>The service reported success, but no newer package of the family is on the device.</summary>
    NoNewerPackage,
}
