using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Prerequisites;

/// <summary>
/// Detects and, when allowed, installs or repairs the agent's runtime prerequisite: the Windows Package Manager
/// (App Installer package <c>Microsoft.DesktopAppInstaller</c>). Runs entirely in the service's LocalSystem context so
/// end users never need administrative rights. The agent binaries themselves are self-contained and need no runtime.
/// <para>
/// Repair strategy: (1) <c>Repair-WinGetPackageManager -AllUsers -Latest</c> from the Microsoft.WinGet.Client module,
/// (2) fallback: download the latest App Installer bundle + licence from the microsoft/winget-cli release and its
/// VCLibs / Microsoft.UI.Xaml dependencies and provision them with <c>Add-AppxProvisionedPackage</c>.
/// Provisioned packages are registered for users at their next logon; the tray agent registers them immediately with
/// <see cref="RegisterForCurrentUserAsync"/>.
/// </para>
/// </summary>
public sealed partial class PrerequisiteManager
{
    public const string AppInstallerPackageName = "Microsoft.DesktopAppInstaller";
    public const string AppInstallerFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PrerequisiteManager(ILogger<PrerequisiteManager> logger) => _logger = logger;

    [GeneratedRegex(@"v?(\d+(?:\.\d+)+)")]
    private static partial Regex VersionRegex();

    /// <summary>Checks winget availability/version and provisioning state. Never throws.</summary>
    public async Task<PrerequisiteStatus> CheckAsync(string minimumVersion, string? wingetPathOverride, bool isSystem, bool autoInstallEnabled, CancellationToken ct)
    {
        var status = new PrerequisiteStatus { MinimumVersion = minimumVersion, AutoInstallEnabled = autoInstallEnabled, CheckedUtc = DateTimeOffset.UtcNow };
        try
        {
            WingetLocator.ResetCache();
            var path = WingetLocator.Find(_logger, isSystem, string.IsNullOrWhiteSpace(wingetPathOverride) ? null : wingetPathOverride);
            status.WingetPath = path;
            status.WingetAvailable = path is not null;
            if (path is not null)
            {
                var run = await ProcessRunner.RunAsync(_logger, path, "--version --disable-interactivity", TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
                var m = VersionRegex().Match(run.StandardOutput + " " + run.StandardError);
                if (m.Success) status.WingetVersion = m.Groups[1].Value;
                else status.LastError = $"winget --version returned exit {run.ExitCode}: {run.LastLines(2)}";
                status.WingetMeetsMinimum = status.WingetVersion is not null && VersionComparer.Compare(status.WingetVersion, minimumVersion) >= 0;
            }

            if (isSystem || IsElevated())
            {
                var prov = await ProcessRunner.RunAsync(_logger, PowerShellPath,
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='SilentlyContinue'; $p = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{AppInstallerPackageName}' | Select-Object -First 1; if ($p) {{ 'PROVISIONED=' + $p.Version }} else {{ 'PROVISIONED=none' }}\"",
                    TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
                var line = prov.StandardOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PROVISIONED=", StringComparison.Ordinal));
                if (line is not null)
                {
                    var v = line["PROVISIONED=".Length..];
                    status.AppInstallerProvisioned = v != "none";
                    status.AppInstallerProvisionedVersion = v == "none" ? null : v;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            status.LastError = ex.Message;
            _logger.LogWarning(ex, "Prerequisite check failed");
        }

        _logger.LogInformation("Prerequisites: {Summary}{Path}{Prov}", status.Summary,
            status.WingetPath is null ? "" : $" ({status.WingetPath})",
            status.AppInstallerProvisioned is null ? "" : status.AppInstallerProvisioned == true ? $"; App Installer provisioned {status.AppInstallerProvisionedVersion}" : "; App Installer not provisioned");
        return status;
    }

    /// <summary>Checks and, when unhealthy and permitted, repairs. Returns the final status.</summary>
    public async Task<PrerequisiteStatus> EnsureAsync(AgentSettings settings, bool isSystem, CancellationToken ct, bool force = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await CheckAsync(settings.WingetMinimumVersion, settings.WingetPath, isSystem, settings.AutoInstallPrerequisites, ct).ConfigureAwait(false);
            if (status.IsHealthy && !force) return status;
            if (!settings.WingetEnabled && !force)
            {
                status.LastAction = "winget source disabled; prerequisite not installed";
                return status;
            }
            if (!settings.AutoInstallPrerequisites && !force)
            {
                status.LastAction = "Automatic prerequisite installation is disabled (AutoInstallPrerequisites=0)";
                _logger.LogWarning("winget is missing or outdated and AutoInstallPrerequisites is disabled; winget-based updates will not work");
                return status;
            }
            if (!isSystem && !IsElevated())
            {
                status.LastAction = status.IsHealthy ? "Nothing to repair" : "Cannot install prerequisites without administrative rights";
                return status;
            }

            _logger.LogWarning("Installing/repairing the Windows Package Manager (current: {Current}, required: {Min})", status.WingetVersion ?? "missing", settings.WingetMinimumVersion);
            var action = await RepairAsync(settings, ct).ConfigureAwait(false);
            var after = await CheckAsync(settings.WingetMinimumVersion, settings.WingetPath, isSystem, settings.AutoInstallPrerequisites, ct).ConfigureAwait(false);
            after.LastAction = action.Description;
            after.LastError = action.Error ?? after.LastError;
            if (after.IsHealthy) _logger.LogInformation("Prerequisite repair succeeded: {Action}", action.Description);
            else _logger.LogError("Prerequisite repair did not produce a working winget: {Action} {Error}", action.Description, action.Error);
            return after;
        }
        finally { _gate.Release(); }
    }

    private async Task<(string Description, string? Error)> RepairAsync(AgentSettings settings, CancellationToken ct)
    {
        var scriptDir = Path.Combine(settings.StateDirectory, "Prerequisites");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "Repair-Winget.ps1");
        await File.WriteAllTextAsync(scriptPath, RepairScript, ct).ConfigureAwait(false);

        var proxy = string.IsNullOrWhiteSpace(settings.ProxyUrl) ? "" : $" -ProxyUrl '{settings.ProxyUrl}'";
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -WorkDir \"{scriptDir}\"{proxy}";
        var run = await ProcessRunner.RunAsync(_logger, PowerShellPath, args, TimeSpan.FromMinutes(20),
            onOutputLine: line => _logger.LogInformation("[prerequisites] {Line}", line), ct: ct).ConfigureAwait(false);

        var method = run.StandardOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("METHOD=", StringComparison.Ordinal))?["METHOD=".Length..];
        var failure = run.StandardOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("FAILED=", StringComparison.Ordinal))?["FAILED=".Length..];
        if (run.TimedOut) return ("Repair timed out after 20 minutes", "timeout");
        if (run.ExitCode == 0 && method is not null)
            return (method switch
            {
                "module" => "Repaired with Repair-WinGetPackageManager (Microsoft.WinGet.Client)",
                "provision" => "Provisioned the App Installer bundle and dependencies with Add-AppxProvisionedPackage",
                _ => $"Repaired ({method})",
            }, null);
        return ("Repair failed", failure ?? run.LastLines(3));
    }

    /// <summary>
    /// Registers the provisioned App Installer package for the current (non-admin) user so the winget alias works in
    /// their session. Safe to call repeatedly; returns true when winget is available afterwards.
    /// </summary>
    public static async Task<bool> RegisterForCurrentUserAsync(ILogger logger, CancellationToken ct)
    {
        if (WingetLocator.Find(logger, isSystem: false) is not null) return true;
        logger.LogInformation("winget alias missing for this user; registering the provisioned App Installer package");
        var run = await ProcessRunner.RunAsync(logger, PowerShellPath,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; Add-AppxPackage -RegisterByFamilyName -MainPackage {AppInstallerFamilyName}\"",
            TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0) logger.LogWarning("Add-AppxPackage -RegisterByFamilyName failed ({Code}): {Output}", run.ExitCode, run.LastLines(3));
        WingetLocator.ResetCache();
        return WingetLocator.Find(logger, isSystem: false) is not null;
    }

    private static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Windows PowerShell 5.1 compatible repair script (runs as SYSTEM).</summary>
    public const string RepairScript = """
        param([string]$WorkDir, [string]$ProxyUrl)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'User-Agent' = 'Arkimentum-AppMonitor/1.0' }
        $webArgs = @{ UseBasicParsing = $true; Headers = $headers }
        if ($ProxyUrl) { $webArgs.Proxy = $ProxyUrl }

        function Try-ModuleRepair {
            try {
                Write-Output 'Trying Microsoft.WinGet.Client / Repair-WinGetPackageManager'
                if (-not (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) {
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
                }
                if (-not (Get-Module -ListAvailable -Name Microsoft.WinGet.Client)) {
                    if ((Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue).InstallationPolicy -ne 'Trusted') {
                        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
                    }
                    Install-Module -Name Microsoft.WinGet.Client -Force -Scope AllUsers -AllowClobber -Repository PSGallery
                }
                Import-Module Microsoft.WinGet.Client -ErrorAction Stop
                Repair-WinGetPackageManager -AllUsers -Latest -Force -ErrorAction Stop
                Write-Output 'METHOD=module'
                return $true
            } catch {
                Write-Output ("Module repair failed: " + $_.Exception.Message)
                return $false
            }
        }

        function Try-ManualProvision {
            Write-Output 'Trying manual provisioning from the microsoft/winget-cli release'
            New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
            $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
            $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' @webArgs
            $bundle  = $release.assets | Where-Object { $_.name -like '*.msixbundle' } | Select-Object -First 1
            $license = $release.assets | Where-Object { $_.name -like '*_License1.xml' } | Select-Object -First 1
            if (-not $bundle) { throw 'No msixbundle asset in the latest winget-cli release.' }
            $bundlePath = Join-Path $WorkDir $bundle.name
            Invoke-WebRequest -Uri $bundle.browser_download_url -OutFile $bundlePath @webArgs
            $licensePath = $null
            if ($license) { $licensePath = Join-Path $WorkDir $license.name; Invoke-WebRequest -Uri $license.browser_download_url -OutFile $licensePath @webArgs }

            $vclibs = Join-Path $WorkDir "Microsoft.VCLibs.$arch.14.00.Desktop.appx"
            Invoke-WebRequest -Uri "https://aka.ms/Microsoft.VCLibs.$arch.14.00.Desktop.appx" -OutFile $vclibs @webArgs

            $index = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.ui.xaml/index.json' @webArgs
            $xamlVersion = $index.versions | Where-Object { $_ -like '2.8.*' -and $_ -notmatch '-' } | Select-Object -Last 1
            if (-not $xamlVersion) { throw 'No Microsoft.UI.Xaml 2.8.x package found on nuget.org.' }
            $xamlZip = Join-Path $WorkDir "microsoft.ui.xaml.$xamlVersion.zip"
            Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.ui.xaml/$xamlVersion/microsoft.ui.xaml.$xamlVersion.nupkg" -OutFile $xamlZip @webArgs
            $xamlDir = Join-Path $WorkDir 'xaml'
            if (Test-Path $xamlDir) { Remove-Item $xamlDir -Recurse -Force }
            Expand-Archive -Path $xamlZip -DestinationPath $xamlDir -Force
            $xaml = Get-ChildItem -Path (Join-Path $xamlDir "tools\AppX\$arch\Release") -Filter '*.appx' | Select-Object -First 1
            if (-not $xaml) { throw 'Microsoft.UI.Xaml appx not found inside the NuGet package.' }

            $params = @{ Online = $true; PackagePath = $bundlePath; DependencyPackagePath = @($vclibs, $xaml.FullName) }
            if ($licensePath) { $params.LicensePath = $licensePath } else { $params.SkipLicense = $true }
            Add-AppxProvisionedPackage @params | Out-Null
            Write-Output ("Provisioned " + $bundle.name + " with VCLibs 14 and Microsoft.UI.Xaml " + $xamlVersion)
            Write-Output 'METHOD=provision'
        }

        try {
            if (Try-ModuleRepair) { exit 0 }
            Try-ManualProvision
            exit 0
        } catch {
            Write-Output ("FAILED=" + $_.Exception.Message)
            exit 1
        }
        """;
}
