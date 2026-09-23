using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Install;

/// <summary>
/// Runs a downloaded installer file silently (MSI via msiexec, EXE directly, MSIX via Add-AppxPackage /
/// Add-AppxProvisionedPackage) and maps the exit code to an <see cref="InstallResult"/>.
/// No windows, no prompts, no elevation attempts: the caller must already be in the right context
/// (LocalSystem for machine-wide installs, the user's session for per-user installs).
/// </summary>
public sealed class InstallerRunner
{
    /// <summary>MSI/Windows Installer: success, but a reboot is required to finish.</summary>
    public const int ExitRebootRequired = 3010;
    /// <summary>MSI/Windows Installer: success, a reboot has been initiated.</summary>
    public const int ExitRebootInitiated = 1641;
    /// <summary>MSI: fatal error during installation.</summary>
    public const int ExitFatalError = 1603;
    /// <summary>MSI: user cancelled the installation.</summary>
    public const int ExitUserCancelled = 1602;
    /// <summary>Windows: the operation was cancelled by the user (also returned by many EXE installers).</summary>
    public const int ExitCancelledByUser = 1223;
    /// <summary>MSI: another installation is already in progress.</summary>
    public const int ExitAnotherInstallInProgress = 1618;
    /// <summary>MSI: this product is already installed.</summary>
    public const int ExitProductAlreadyInstalled = 1638;
    /// <summary>MSI: the installer requires elevated privileges.</summary>
    public const int ExitElevationRequired = 1925;

    private readonly ILogger _logger;

    /// <summary>Accepts any logger (typically the calling provider's, so installer lines land in that category).</summary>
    public InstallerRunner(ILogger logger) => _logger = logger;

    /// <summary>
    /// Runs <paramref name="installerPath"/> silently and returns the interpreted result.
    /// </summary>
    /// <param name="installerPath">Full path to the downloaded installer.</param>
    /// <param name="type">Installer flavour; decides how the file is invoked.</param>
    /// <param name="arguments">Extra silent-install arguments from the app policy (may be null).</param>
    /// <param name="context">Execution context; MSIX provisioning differs between SYSTEM and user.</param>
    /// <param name="timeout">Maximum run time; the process tree is killed when it elapses.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<InstallResult> RunAsync(
        string installerPath,
        InstallerType type,
        string? arguments,
        ExecutionContextInfo context,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return InstallResult.Fail($"Installer file not found: '{installerPath}'.");

        var (exe, args) = BuildCommand(installerPath, type, arguments, context);
        _logger.LogInformation("Running {Type} installer: \"{Exe}\" {Args}", type, exe, args);
        progress?.Report($"Running {Path.GetFileName(installerPath)}...");

        // Run from a neutral directory: under LocalSystem the current directory must not be a user profile path.
        var workingDirectory = Path.GetDirectoryName(installerPath);

        var run = await ProcessRunner.RunAsync(
            _logger, exe, args, timeout,
            onOutputLine: line => { if (!string.IsNullOrWhiteSpace(line)) progress?.Report(line.Trim()); },
            workingDirectory: workingDirectory,
            // User context: RunAsInvoker, so an installer that asks for administrator rights cannot raise a UAC prompt.
            environment: ProcessRunner.ChildEnvironment(context),
            ct: ct).ConfigureAwait(false);

        if (!run.Started)
            return InstallResult.Fail(run.StartFailure!);

        if (run.TimedOut)
            return InstallResult.Fail($"Installer timed out after {timeout.TotalMinutes:0} minutes and was terminated.");

        var result = InterpretExitCode(run.ExitCode, type);
        var detail = run.LastLines();
        if (!result.Success && !string.IsNullOrWhiteSpace(detail))
            result = result with { Message = $"{result.Message} Output: {detail}" };

        if (result.Success)
            _logger.LogInformation("Installer finished successfully (exit {ExitCode}{Reboot}).", run.ExitCode, result.RebootRequired ? ", reboot required" : string.Empty);
        else
            _logger.LogError("Installer failed: {Message}", result.Message);

        return result;
    }

    /// <summary>
    /// Builds the command line used to run an installer silently. Exposed for tests.
    /// </summary>
    public static (string FileName, string Arguments) BuildCommand(
        string installerPath, InstallerType type, string? arguments, ExecutionContextInfo context)
    {
        var extra = (arguments ?? string.Empty).Trim();

        switch (type)
        {
            case InstallerType.Msi:
            {
                var parts = new List<string>();
                // Do not duplicate switches the policy already supplies.
                if (!ContainsSwitch(extra, "i") && !ContainsSwitch(extra, "package") && !ContainsSwitch(extra, "a"))
                    parts.Add($"/i \"{installerPath}\"");
                else
                    parts.Add($"\"{installerPath}\"");

                if (!ContainsSwitch(extra, "qn") && !ContainsSwitch(extra, "quiet") && !ContainsSwitch(extra, "passive") &&
                    !ContainsSwitch(extra, "qb") && !ContainsSwitch(extra, "q"))
                    parts.Add("/qn");

                if (!ContainsSwitch(extra, "norestart") && !ContainsSwitch(extra, "forcerestart") && !ContainsSwitch(extra, "promptrestart"))
                    parts.Add("/norestart");

                if (extra.Length > 0) parts.Add(extra);
                return (MsiExecPath(), string.Join(' ', parts));
            }

            case InstallerType.Msix:
            {
                var literal = installerPath.Replace("'", "''");
                var command = context.IsSystem
                    // LocalSystem: provision for all users, then try the per-machine install as well.
                    ? $"$ErrorActionPreference='Stop'; " +
                      $"Add-AppxProvisionedPackage -Online -PackagePath '{literal}' -SkipLicense | Out-Null; " +
                      $"try {{ Add-AppxPackage -Path '{literal}' -ForceUpdateFromAnyVersion -ErrorAction Stop }} catch {{ Write-Output \"Add-AppxPackage: $($_.Exception.Message)\" }}"
                    : $"$ErrorActionPreference='Stop'; Add-AppxPackage -Path '{literal}' -ForceUpdateFromAnyVersion";
                if (extra.Length > 0) command += "; " + extra;
                return (PowerShellPath(), $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"");
            }

            default:
                // ProcessStartInfo.FileName must not be quoted; the runner passes it verbatim.
                return (installerPath, extra);
        }
    }

    /// <summary>
    /// Maps an installer exit code to an <see cref="InstallResult"/> using the standard Windows Installer /
    /// Inno Setup / NSIS conventions. Exposed for tests.
    /// </summary>
    public static InstallResult InterpretExitCode(int exitCode, InstallerType type) => exitCode switch
    {
        0 => InstallResult.Ok("Installed successfully."),
        ExitRebootRequired => InstallResult.Ok("Installed successfully; a restart is required to complete the update.", ExitRebootRequired, reboot: true),
        ExitRebootInitiated => InstallResult.Ok("Installed successfully; a restart has been initiated.", ExitRebootInitiated, reboot: true),
        ExitProductAlreadyInstalled => InstallResult.Ok("This version is already installed.", ExitProductAlreadyInstalled),
        ExitUserCancelled => InstallResult.Fail("The installation was cancelled (exit 1602).", ExitUserCancelled),
        ExitCancelledByUser => InstallResult.Fail("The installation was cancelled (exit 1223).", ExitCancelledByUser),
        ExitAnotherInstallInProgress => InstallResult.Fail(
            "Another installation is already in progress (exit 1618, ERROR_INSTALL_ALREADY_RUNNING). " +
            "Wait for Windows Installer to finish and retry.", ExitAnotherInstallInProgress),
        ExitFatalError => InstallResult.Fail(
            "The installer reported a fatal error (exit 1603). Check the installer log; this is often caused by the " +
            "application still running, insufficient disk space, or a pending reboot.", ExitFatalError),
        ExitElevationRequired => InstallResult.Fail(
            "The installer requires elevated privileges (exit 1925). A machine-wide install must be run by the service (LocalSystem).",
            ExitElevationRequired),
        _ => InstallResult.Fail($"The {type} installer failed with exit code {exitCode} (0x{exitCode:X8}).", exitCode),
    };

    private static bool ContainsSwitch(string arguments, string name)
    {
        if (arguments.Length == 0) return false;
        foreach (var token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2 || (token[0] != '/' && token[0] != '-')) continue;
            var value = token[1..];
            var eq = value.IndexOfAny(['=', ':']);
            if (eq >= 0) value = value[..eq];
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string MsiExecPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    internal static string PowerShellPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
