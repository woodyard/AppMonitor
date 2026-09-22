#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades the Arkimentum AppMonitor agent (Windows service + tray agent).

.DESCRIPTION
    Run this elevated on the target machine, from an expanded release package that contains the
    Service\, Tray\ and Admin\ folders produced by deploy\Build-Release.ps1.

    The script is idempotent: running it again upgrades the binaries in place, keeps the existing
    configuration, and only re-creates what is missing.

    What it does:
      1. Stops the ArkimentumAppMonitor service and any running tray/admin/service processes.
      2. Mirrors Service\, Tray\ and Admin\ into <InstallDir> with robocopy /MIR, and creates the
         all-users Start Menu shortcut for the admin console (unless -NoAdminConsole).
      3. Creates %ProgramData%\Arkimentum\AppMonitor\Logs (the service writes there as LocalSystem;
         the tray agent logs to each user's %LOCALAPPDATA% and needs no shared folder).
      4. Creates or reconfigures the ArkimentumAppMonitor service (LocalSystem, delayed auto-start)
         including description and failure/restart actions.
      5. Registers the "Arkimentum AppMonitor" event log source in the Application log.
      6. Writes default preferences under HKLM\SOFTWARE\Arkimentum\AppMonitor - only values that do not
         exist yet (or all of them with -Force), plus optional sample app policies.
      7. Adds the HKLM Run value ArkimentumAppMonitorTray as a logon fallback for the tray agent.
      8. Starts the service and launches the tray agent for the current interactive user.
      9. Runs the prerequisite check once (Arkimentum.AppMonitor.Service.exe --prerequisites), which
         installs or repairs winget for the SYSTEM account when needed. -SkipPrerequisites leaves it
         out; a failure is reported but never fails the installation.

    Group Policy (HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor) always wins over the preference values
    written here. This script never touches the Policies key.

    Cloud service: pass -CloudServerUrl, -CloudOrganizationId and -CloudEnrollmentKey to enrol the
    device in an organization. Those three values are the only ones that have to be provisioned per
    device; everything else can then come from the organization configuration in the cloud. See
    docs\Cloud.md.

    A cloud parameter you do not pass leaves the existing registry value untouched, so re-running the
    installer never un-configures a device by omission; passing one always overwrites (which is how an
    enrollment key is rotated). Passing an empty string ('') writes an empty value, which the agent
    reads as "not configured" - that is how you disconnect a device from the cloud.

    Unattended use: the script asks no questions and reads nothing from the console, so it can be run
    by Intune, by an RMM or by the agent's own self-updater (which expands a release package to a temp
    folder and calls this script with -SourceRoot <temp folder> as SYSTEM). Nothing but the default of
    -SourceRoot depends on where the script itself lives. Use -LogFile to capture a transcript when
    there is no console to read.

.PARAMETER SourceRoot
    Folder containing the Service\, Tray\ and Admin\ subfolders. Default: the folder this script is in.
    The self-updater passes a temporary folder here; the script never assumes it is the install folder
    or that it survives the run.

.PARAMETER InstallDir
    Install root. Default: %ProgramFiles%\Arkimentum\AppMonitor.
    Binaries land in <InstallDir>\Service, <InstallDir>\Tray and <InstallDir>\Admin.

.PARAMETER ScanIntervalMinutes
    Preference value ScanIntervalMinutes (5-10080). Default when not yet configured: 240.

.PARAMETER NotificationIntervalMinutes
    Preference value NotificationIntervalMinutes (1-10080). Default when not yet configured: 240.

.PARAMETER LogLevel
    Preference value LogLevel. Default when not yet configured: Information.

.PARAMETER CloudServerUrl
    Preference value CloudServerUrl (REG_SZ): base URL of the AppMonitor cloud API, for example
    https://appmonitor-contoso.azurewebsites.net. Must be https. Only written when you pass it; an
    existing value is left alone otherwise. Empty = the agent works stand-alone.

.PARAMETER CloudOrganizationId
    Preference value CloudOrganizationId (REG_SZ): the organization GUID from the cloud service's
    Enrollment page. Only written when you pass it.

.PARAMETER CloudEnrollmentKey
    Preference value CloudEnrollmentKey (REG_SZ): the organization enrollment key. The service uses it
    once, exchanges it for a per-device key stored DPAPI-protected in
    %ProgramData%\Arkimentum\AppMonitor\device.credential, and never sends it again. Only written when
    you pass it. It is masked in the summary this script prints.

.PARAMETER AgentUpdateFeedUrl
    Preference value AgentUpdateFeedUrl (REG_SZ): the release feed the self-updater reads - a GitHub
    latest-release API URL (https://api.github.com/repos/OWNER/REPO/releases/latest) or a direct
    manifest.json URL. Empty = the cloud API's mirror, or no self-update at all when no cloud is
    configured. Only written when you pass it.

.PARAMETER NoAutoUpdate
    Write AgentAutoUpdate = 0, so the agent never updates itself. Use this when the agent is packaged
    and deployed by Intune, Configuration Manager or an RMM. Without the switch the value is left
    alone (built-in default: 1 = the agent updates itself).

.PARAMETER LogFile
    Append a transcript of this run to a file. Useful when the script is run with no console attached
    (self-update, Intune Win32 app, scheduled task). The folder is created if necessary; a transcript
    that cannot be started is reported as a warning and never fails the installation.

.PARAMETER NoStart
    Install and configure, but do not start the service or the tray agent.

.PARAMETER NoSampleApps
    Do not create the sample Apps\<AppId> subkeys.

.PARAMETER SkipPrerequisites
    Do not run the prerequisite check after the service has been started. By default the installer runs
    <InstallDir>\Service\Arkimentum.AppMonitor.Service.exe --prerequisites once, which installs or
    repairs winget (the Microsoft.DesktopAppInstaller package) for the SYSTEM account when it is
    missing or too old. That needs outbound HTTPS to github.com, aka.ms, nuget.org and the PowerShell
    Gallery; use this switch on isolated machines where winget is provisioned in the image. A failing
    check never fails the installation - it is reported as a warning.

.PARAMETER NoAdminConsole
    Do not create the all-users Start Menu shortcut "Arkimentum AppMonitor Admin" (and remove it if a
    previous install created one). The Admin\ binaries are still copied, so the console can be started
    from <InstallDir>\Admin\Arkimentum.AppMonitor.Admin.exe. It opens on the organization pages; the
    per-machine pages (--local) and the headless --export / --import are deprecated.

.PARAMETER Force
    Overwrite existing preference values (including sample apps) instead of leaving them alone, and
    re-create the service definition even if it already exists.

.EXAMPLE
    .\Install-ArkimentumAppMonitor.ps1

.EXAMPLE
    .\Install-ArkimentumAppMonitor.ps1 -ScanIntervalMinutes 120 -LogLevel Debug -NoSampleApps

.EXAMPLE
    # Managed by the cloud: provision the three connection values and let the organization
    # configuration supply everything else.
    .\Install-ArkimentumAppMonitor.ps1 -NoSampleApps `
        -CloudServerUrl 'https://appmonitor-contoso.azurewebsites.net' `
        -CloudOrganizationId '3f2504e0-4f89-11d3-9a0c-0305e82c3301' `
        -CloudEnrollmentKey 'ek_live_9c1f...' `
        -LogFile "$env:ProgramData\Arkimentum\AppMonitor\Logs\install.log"

.NOTES
    Uninstall with Uninstall-ArkimentumAppMonitor.ps1.
    Configuration reference: docs\Registry.md. Cloud service: docs\Cloud.md.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourceRoot = $PSScriptRoot,

    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Arkimentum\AppMonitor'),

    [ValidateRange(5, 10080)]
    [int]$ScanIntervalMinutes = 240,

    [ValidateRange(1, 10080)]
    [int]$NotificationIntervalMinutes = 240,

    [ValidateSet('Trace', 'Debug', 'Information', 'Warning', 'Error')]
    [string]$LogLevel = 'Information',

    # ---- cloud service (docs\Cloud.md). Only written when passed; existing values stay as they are.
    [AllowEmptyString()]
    [string]$CloudServerUrl,

    [AllowEmptyString()]
    [string]$CloudOrganizationId,

    [AllowEmptyString()]
    [string]$CloudEnrollmentKey,

    [AllowEmptyString()]
    [string]$AgentUpdateFeedUrl,

    [switch]$NoAutoUpdate,

    [string]$LogFile,

    [switch]$NoStart,
    [switch]$NoSampleApps,
    [switch]$NoAdminConsole,
    [switch]$SkipPrerequisites,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Never wait for a console answer: this script also runs unattended (Intune, RMM, the agent's own
# self-updater as SYSTEM with no console at all). ShouldProcess prompts only at ConfirmPreference,
# which is left at High; no cmdlet below is High-impact, so nothing prompts.
$ConfirmPreference = 'High'

# --------------------------------------------------------------------------------- 64-bit host

# The Intune Management Extension, many RMM agents and some scheduled tasks start "powershell.exe" as a 32-bit
# process. There HKLM\SOFTWARE is redirected to WOW6432Node, where the agent never looks, and the service binary
# is x64. Re-launch this script in the native 64-bit host with the same parameters and hand its exit code back,
# so a plain "powershell.exe -File Install-ArkimentumAppMonitor.ps1 ..." install command works from anywhere.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    $nativeHost = Join-Path $env:WINDIR 'SysNative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $nativeHost) {
        # -File passes arguments literally (no quoting syntax is interpreted), so values go through as they are and
        # switches are forwarded only when they were given.
        $forwarded = @()
        foreach ($entry in $PSBoundParameters.GetEnumerator()) {
            if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
                if ($entry.Value.IsPresent) { $forwarded += ('-{0}' -f $entry.Key) }
            }
            else {
                $forwarded += ('-{0}' -f $entry.Key)
                $forwarded += [string]$entry.Value
            }
        }
        & $nativeHost -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @forwarded
        exit $LASTEXITCODE
    }
}

# --------------------------------------------------------------------------------- transcript

$script:TranscriptStarted = $false
if ($LogFile) {
    try {
        $logFolder = Split-Path -Parent $LogFile
        if ($logFolder -and -not (Test-Path -LiteralPath $logFolder)) {
            New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
        }
        Start-Transcript -LiteralPath $LogFile -Append -ErrorAction Stop | Out-Null
        $script:TranscriptStarted = $true
    } catch {
        Write-Warning ("Could not start the transcript at {0}: {1}" -f $LogFile, $_.Exception.Message)
    }
}

# --------------------------------------------------------------------------------- constants

$ServiceName        = 'ArkimentumAppMonitor'
$ServiceDisplayName = 'Arkimentum AppMonitor Agent'
$ServiceDescription = 'Monitors configured applications for updates from winget and vendor web sites, notifies users and installs updates according to policy.'
$ServiceExeName     = 'Arkimentum.AppMonitor.Service.exe'
$TrayExeName        = 'Arkimentum.AppMonitor.Tray.exe'
$AdminExeName       = 'Arkimentum.AppMonitor.Admin.exe'
$AdminShortcutName  = 'Arkimentum AppMonitor Admin'
$StartMenuFolder    = 'Arkimentum'
$EventLogSource     = 'Arkimentum AppMonitor'
$RunValueName       = 'ArkimentumAppMonitorTray'
$RunKey             = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$PreferenceKey      = 'HKLM:\SOFTWARE\Arkimentum\AppMonitor'
$PolicyKey          = 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor'
$ProgramDataRoot    = Join-Path $env:ProgramData 'Arkimentum\AppMonitor'
$LogDirectory       = Join-Path $ProgramDataRoot 'Logs'

$script:StepNumber = 0

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    $script:StepNumber++
    Write-Host ''
    Write-Host ("[{0}] {1}" -f $script:StepNumber, $Message) -ForegroundColor Cyan
}

function Write-Info {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "    $Message"
}

# --------------------------------------------------------------------------------- helpers

function Assert-Native64Bit {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        throw 'Run this script in 64-bit PowerShell. A 32-bit host would write the configuration to the WOW6432Node redirected registry view, where the agent does not look.'
    }
}

function Get-ServiceOrNull {
    param([Parameter(Mandatory)][string]$Name)
    Get-Service -Name $Name -ErrorAction SilentlyContinue
}

function Invoke-Sc {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$IgnoreFailure
    )
    $output = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $IgnoreFailure) {
        throw ("sc.exe {0} failed with exit code {1}: {2}" -f ($Arguments -join ' '), $LASTEXITCODE, ($output -join ' '))
    }
    return $output
}

function Stop-AgentProcesses {
    foreach ($name in @('Arkimentum.AppMonitor.Tray', 'Arkimentum.AppMonitor.Admin', 'Arkimentum.AppMonitor.Service')) {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            try {
                $p | Stop-Process -Force -ErrorAction Stop
                Write-Info ("Stopped process {0} (pid {1})" -f $name, $p.Id)
            } catch {
                Write-Warning ("Could not stop {0} (pid {1}): {2}" -f $name, $p.Id, $_.Exception.Message)
            }
        }
    }
}

function Copy-Payload {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $roboArgs = @($Source, $Destination, '/MIR', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    $output = & "$env:SystemRoot\System32\robocopy.exe" @roboArgs
    # robocopy: 0-7 = success (files copied / nothing to do), 8+ = at least one failure
    if ($LASTEXITCODE -ge 8) {
        throw ("robocopy '{0}' -> '{1}' failed with exit code {2}: {3}" -f $Source, $Destination, $LASTEXITCODE, ($output -join ' '))
    }
    $global:LASTEXITCODE = 0
    Write-Info ("Copied {0} -> {1}" -f $Source, $Destination)
}

function Set-PreferenceValue {
    <#
        Writes a value under HKLM\SOFTWARE\Arkimentum\AppMonitor.
        Existing values are kept unless -Force was passed to the script or -Always is used.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()]$Value,
        [Parameter(Mandatory)][ValidateSet('DWord', 'String', 'MultiString')][string]$Type,
        [switch]$Always
    )
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    $existing = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $existing -and -not $Force -and -not $Always) {
        Write-Info ("Kept existing {0}\{1}" -f (Split-Path -Leaf $Path), $Name)
        return
    }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    Write-Info ("Set {0} = {1} ({2})" -f $Name, ($Value -join ','), $Type)
}

function Get-MaskedSecret {
    <# Never print an enrollment key: show only its length and the first few characters. #>
    param([AllowEmptyString()][string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '(not set)' }
    if ($Value.Length -le 6) { return ('{0} ({1} characters)' -f ('*' * $Value.Length), $Value.Length) }
    return ('{0}{1} ({2} characters)' -f $Value.Substring(0, 4), ('*' * 8), $Value.Length)
}

function Get-CurrentPreference {
    <# Current value of a preference, or $null. Used for the summary, never for a decision. #>
    param([Parameter(Mandatory)][string]$Name)
    $item = Get-ItemProperty -LiteralPath $PreferenceKey -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$Name
}

# --------------------------------------------------------------------------------- validation

Assert-Native64Bit

# ---- cloud parameters: validate before anything is changed on the machine.
$cloudParams = @('CloudServerUrl', 'CloudOrganizationId', 'CloudEnrollmentKey')
$cloudGiven = @($cloudParams | Where-Object { $PSBoundParameters.ContainsKey($_) })

if ($PSBoundParameters.ContainsKey('CloudServerUrl') -and $CloudServerUrl) {
    $uri = $null
    if (-not [Uri]::TryCreate($CloudServerUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
        throw "-CloudServerUrl must be an absolute https URL, for example https://appmonitor-contoso.azurewebsites.net (got '$CloudServerUrl')."
    }
    # Trailing slashes are harmless but make the logged URLs inconsistent between devices.
    $CloudServerUrl = $CloudServerUrl.TrimEnd('/')
}

if ($PSBoundParameters.ContainsKey('CloudOrganizationId') -and $CloudOrganizationId) {
    $guid = [Guid]::Empty
    if (-not [Guid]::TryParse($CloudOrganizationId, [ref]$guid)) {
        throw "-CloudOrganizationId must be a GUID, for example 3f2504e0-4f89-11d3-9a0c-0305e82c3301 (got '$CloudOrganizationId')."
    }
    $CloudOrganizationId = $guid.ToString('D')
}

if ($PSBoundParameters.ContainsKey('AgentUpdateFeedUrl') -and $AgentUpdateFeedUrl) {
    $feedUri = $null
    if (-not [Uri]::TryCreate($AgentUpdateFeedUrl, [UriKind]::Absolute, [ref]$feedUri) -or $feedUri.Scheme -ne 'https') {
        throw "-AgentUpdateFeedUrl must be an absolute https URL - a GitHub latest-release API URL or a manifest.json URL (got '$AgentUpdateFeedUrl')."
    }
}

# Enrolment needs all three. Warn rather than fail: an administrator may well be setting them in two
# steps, or supplying the other two through Group Policy.
if ($cloudGiven.Count -gt 0 -and $cloudGiven.Count -lt 3) {
    # Note the parentheses around the concatenation: -f would otherwise bind to the last string only.
    Write-Warning (("Cloud enrolment needs CloudServerUrl, CloudOrganizationId and CloudEnrollmentKey together; " +
                    "only {0} given. The agent stays stand-alone until all three are present (from here, from " +
                    "Group Policy or from the admin console).") -f ($cloudGiven -join ', '))
}

# -SourceRoot defaults to the script's own folder, but the default cannot be trusted: under Windows PowerShell 5.1,
# when a script with comment-based help or a [CmdletBinding()] attribute is started with "powershell.exe -File",
# $PSScriptRoot is still empty while the parameter defaults are evaluated (it is populated once the body runs).
# That is exactly how Intune and most RMM agents start this script, so resolve the folder here instead.
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($SourceRoot) -and $PSCommandPath) { $SourceRoot = Split-Path -Parent $PSCommandPath }
if ([string]::IsNullOrWhiteSpace($SourceRoot) -and $MyInvocation.MyCommand.Path) { $SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    throw 'Could not determine where the release package is. Pass -SourceRoot <folder that contains Service\, Tray\ and Admin\>.'
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$serviceSource = Join-Path $SourceRoot 'Service'
$traySource    = Join-Path $SourceRoot 'Tray'
$adminSource   = Join-Path $SourceRoot 'Admin'
$serviceSourceExe = Join-Path $serviceSource $ServiceExeName
$traySourceExe    = Join-Path $traySource $TrayExeName
$adminSourceExe   = Join-Path $adminSource $AdminExeName

foreach ($required in @($serviceSourceExe, $traySourceExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Payload not found: $required. -SourceRoot must point at a folder that contains the Service\ and Tray\ folders from the release package."
    }
}

# The admin console is optional in the payload: an older package may not contain it.
$hasAdminPayload = Test-Path -LiteralPath $adminSourceExe -PathType Leaf
if (-not $hasAdminPayload) {
    Write-Warning "Admin console not found in the payload ($adminSourceExe); it will not be installed. Build the package with deploy\Build-Release.ps1 to include it."
}

$serviceInstallDir = Join-Path $InstallDir 'Service'
$trayInstallDir    = Join-Path $InstallDir 'Tray'
$adminInstallDir   = Join-Path $InstallDir 'Admin'
$serviceExe        = Join-Path $serviceInstallDir $ServiceExeName
$trayExe           = Join-Path $trayInstallDir $TrayExeName
$adminExe          = Join-Path $adminInstallDir $AdminExeName

# All-users Start Menu: %ProgramData%\Microsoft\Windows\Start Menu\Programs\Arkimentum
$startMenuDir      = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $StartMenuFolder
$adminShortcut     = Join-Path $startMenuDir ("{0}.lnk" -f $AdminShortcutName)

Write-Host 'Arkimentum AppMonitor - install' -ForegroundColor Green
Write-Host "  Source      : $SourceRoot"
Write-Host "  Install dir : $InstallDir"
Write-Host "  Log dir     : $LogDirectory"

# --------------------------------------------------------------------------------- stop

Write-Step 'Stopping the service and any running agents'
$existingService = Get-ServiceOrNull -Name $ServiceName
if ($existingService) {
    if ($existingService.Status -ne 'Stopped') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-ServiceOrNull -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }
            Write-Info ("Service status: {0}" -f (Get-ServiceOrNull -Name $ServiceName).Status)
        }
    } else {
        Write-Info 'Service already stopped.'
    }
} else {
    Write-Info 'Service not installed yet.'
}
Stop-AgentProcesses

# --------------------------------------------------------------------------------- copy

Write-Step 'Copying binaries'
if ($PSCmdlet.ShouldProcess($InstallDir, 'Copy payload')) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Payload -Source $serviceSource -Destination $serviceInstallDir
    Copy-Payload -Source $traySource -Destination $trayInstallDir
    if ($hasAdminPayload) { Copy-Payload -Source $adminSource -Destination $adminInstallDir }
}

Write-Step 'Admin console Start Menu shortcut (all users)'
if (-not $hasAdminPayload) {
    Write-Info 'Skipped: the payload contains no Admin\ folder.'
} elseif ($NoAdminConsole) {
    if ((Test-Path -LiteralPath $adminShortcut) -and $PSCmdlet.ShouldProcess($adminShortcut, 'Remove shortcut (-NoAdminConsole)')) {
        Remove-Item -LiteralPath $adminShortcut -Force
        Write-Info 'Removed the shortcut left by an earlier install.'
    } else {
        Write-Info 'Skipped (-NoAdminConsole).'
    }
    Write-Info ("The console can still be started from {0}" -f $adminExe)
} elseif ($PSCmdlet.ShouldProcess($adminShortcut, 'Create Start Menu shortcut')) {
    try {
        New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        try {
            $lnk = $shell.CreateShortcut($adminShortcut)
            $lnk.TargetPath       = $adminExe
            $lnk.IconLocation     = "$adminExe,0"
            $lnk.WorkingDirectory = $adminInstallDir
            $lnk.Description      = 'Manage Arkimentum AppMonitor centrally: sign in and configure your organization.'
            $lnk.Save()
        } finally {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
        Write-Info ("Shortcut: {0} -> {1}" -f $adminShortcut, $adminExe)
        Write-Info 'The console opens on the organization pages and needs no elevation; only the deprecated per-machine pages (--local) do.'
    } catch {
        Write-Warning ("Could not create the Start Menu shortcut: {0}. Start the console from {1} instead." -f $_.Exception.Message, $adminExe)
    }
}

Write-Step 'Creating data folders'
if ($PSCmdlet.ShouldProcess($LogDirectory, 'Create log folder')) {
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    Write-Info "Service log folder: $LogDirectory"
    Write-Info 'Default ACLs are correct: only the service (LocalSystem) writes here.'
    Write-Info 'Tray agents log to %LOCALAPPDATA%\Arkimentum\AppMonitor\Logs in each user profile.'
}

# --------------------------------------------------------------------------------- service

Write-Step 'Creating / updating the Windows service'
$existingService = Get-ServiceOrNull -Name $ServiceName
if ($existingService -and $Force) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete service (-Force)')) {
        Invoke-Sc -Arguments @('delete', $ServiceName) -IgnoreFailure | Out-Null
        Start-Sleep -Seconds 2
        $existingService = Get-ServiceOrNull -Name $ServiceName
    }
}

$binPath = '"{0}"' -f $serviceExe
if ($existingService) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Reconfigure service')) {
        Invoke-Sc -Arguments @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto',
                               'obj=', 'LocalSystem', 'DisplayName=', $ServiceDisplayName) | Out-Null
        Write-Info 'Existing service reconfigured.'
    }
} else {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Create service')) {
        Invoke-Sc -Arguments @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto',
                               'obj=', 'LocalSystem', 'DisplayName=', $ServiceDisplayName) | Out-Null
        Write-Info 'Service created.'
    }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Set description and recovery actions')) {
    Invoke-Sc -Arguments @('description', $ServiceName, $ServiceDescription) | Out-Null
    # Restart after 60 s on the first three failures; the failure counter resets after 24 h.
    Invoke-Sc -Arguments @('failure', $ServiceName, 'reset=', '86400',
                           'actions=', 'restart/60000/restart/60000/restart/60000') | Out-Null
    # Also apply the recovery actions when the service exits with a non-zero code (not just on a crash).
    Invoke-Sc -Arguments @('failureflag', $ServiceName, '1') | Out-Null
    Write-Info 'Description and recovery actions set (restart 3x after 60 s, reset after 24 h).'
}

# --------------------------------------------------------------------------------- event log

Write-Step 'Registering the event log source'
# SourceExists enumerates every event log and throws when one of them (for example Security) cannot
# be read; treat that as "unknown" and let the creation attempt decide.
$sourceExists = $false
try {
    $sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventLogSource)
} catch {
    Write-Warning ("Could not query the event log sources ({0}); attempting to create the source anyway." -f $_.Exception.Message)
}
if ($sourceExists) {
    Write-Info "Event log source '$EventLogSource' already exists."
} elseif ($PSCmdlet.ShouldProcess($EventLogSource, 'Create event log source')) {
    try {
        New-EventLog -LogName 'Application' -Source $EventLogSource -ErrorAction Stop
        Write-Info "Created event log source '$EventLogSource' in the Application log."
    } catch {
        Write-Warning ("Could not create the event log source: {0}. The service falls back to file logging only." -f $_.Exception.Message)
    }
}

# --------------------------------------------------------------------------------- registry

Write-Step 'Writing default preferences (HKLM\SOFTWARE\Arkimentum\AppMonitor)'
if ($PSCmdlet.ShouldProcess($PreferenceKey, 'Write default preferences')) {
    if (-not (Test-Path -LiteralPath $PreferenceKey)) { New-Item -Path $PreferenceKey -Force | Out-Null }

    # Values explicitly passed on the command line always win over an existing value.
    Set-PreferenceValue -Path $PreferenceKey -Name 'ScanIntervalMinutes' -Value $ScanIntervalMinutes -Type DWord `
        -Always:([bool]$PSBoundParameters.ContainsKey('ScanIntervalMinutes'))
    Set-PreferenceValue -Path $PreferenceKey -Name 'NotificationIntervalMinutes' -Value $NotificationIntervalMinutes -Type DWord `
        -Always:([bool]$PSBoundParameters.ContainsKey('NotificationIntervalMinutes'))
    Set-PreferenceValue -Path $PreferenceKey -Name 'LogLevel' -Value $LogLevel -Type String `
        -Always:([bool]$PSBoundParameters.ContainsKey('LogLevel'))
    # 1 = the service launches the tray agent into every interactive session (the normal setup).
    Set-PreferenceValue -Path $PreferenceKey -Name 'LaunchTrayAgent' -Value 1 -Type DWord
}

# --------------------------------------------------------------------------------- cloud

# The three Cloud* values are the whole per-device provisioning surface when the cloud service is in
# use: everything else can then be delivered as organization configuration (docs\Cloud.md). They are
# written only when passed, and a passed value always wins over what is already there - re-running the
# installer with a new enrollment key rotates it. Nothing is deleted: omitting a parameter never
# unconfigures a device. The organization configuration can never override these values, because they
# are what the agent uses to reach the cloud in the first place.
if ($cloudGiven.Count -gt 0 -or $PSBoundParameters.ContainsKey('AgentUpdateFeedUrl') -or $NoAutoUpdate) {
    Write-Step 'Writing cloud and self-update preferences'
    if ($PSCmdlet.ShouldProcess($PreferenceKey, 'Write cloud preferences')) {
        if ($PSBoundParameters.ContainsKey('CloudServerUrl')) {
            Set-PreferenceValue -Path $PreferenceKey -Name 'CloudServerUrl' -Value $CloudServerUrl -Type String -Always
        }
        if ($PSBoundParameters.ContainsKey('CloudOrganizationId')) {
            Set-PreferenceValue -Path $PreferenceKey -Name 'CloudOrganizationId' -Value $CloudOrganizationId -Type String -Always
        }
        if ($PSBoundParameters.ContainsKey('CloudEnrollmentKey')) {
            # Written through the same helper, but the value is never echoed.
            if (-not (Test-Path -LiteralPath $PreferenceKey)) { New-Item -Path $PreferenceKey -Force | Out-Null }
            New-ItemProperty -LiteralPath $PreferenceKey -Name 'CloudEnrollmentKey' -Value $CloudEnrollmentKey -PropertyType String -Force | Out-Null
            Write-Info ("Set CloudEnrollmentKey = {0} (String)" -f (Get-MaskedSecret $CloudEnrollmentKey))
        }
        if ($PSBoundParameters.ContainsKey('AgentUpdateFeedUrl')) {
            Set-PreferenceValue -Path $PreferenceKey -Name 'AgentUpdateFeedUrl' -Value $AgentUpdateFeedUrl -Type String -Always
        }
        if ($NoAutoUpdate) {
            Set-PreferenceValue -Path $PreferenceKey -Name 'AgentAutoUpdate' -Value 0 -Type DWord -Always
            Write-Info 'The agent will not update itself; deploy new releases with your own tooling.'
        }
        if ($cloudGiven.Count -eq 3) {
            Write-Info 'The service enrols on its first sync and stores the per-device key in'
            Write-Info ("  {0}\device.credential (DPAPI, SYSTEM + Administrators only)." -f $ProgramDataRoot)
            Write-Info 'Check it afterwards with: Arkimentum.AppMonitor.Service.exe --cloud-status'
        }
    }
}

if ($NoSampleApps) {
    Write-Step 'Sample app policies skipped (-NoSampleApps)'
} else {
    Write-Step 'Writing sample app policies (HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps)'
    # ---------------------------------------------------------------------------------------
    # Minimal starter set so a fresh install has something to show. Apart from 7-Zip everything
    # is Enabled=0, so nothing is installed on users' machines until an administrator turns it on.
    # The AppIds are the ones used by the shipped catalog, so detection regexes, vendor URLs and
    # anything else omitted here is filled in from catalog.json. Behaviour (Mandatory, deadlines,
    # deferrals) is never taken from the catalog - it comes from these values or the global
    # Default* values. Replace this set with your own: see docs\Registry.md and
    # Set-SampleConfiguration.ps1.
    # ---------------------------------------------------------------------------------------
    $sampleApps = @(
        [ordered]@{
            AppId = '7zip'
            Values = [ordered]@{
                DisplayName  = @{ Type = 'String';      Value = '7-Zip' }
                Enabled      = @{ Type = 'DWord';       Value = 1 }        # the harmless one: on by default
                Source       = @{ Type = 'String';      Value = 'winget' }
                WingetId     = @{ Type = 'String';      Value = '7zip.7zip' }
                Mandatory    = @{ Type = 'DWord';       Value = 0 }        # user may keep deferring
                ProcessNames = @{ Type = 'MultiString'; Value = @('7zFM', '7zG') }
            }
        },
        [ordered]@{
            AppId = 'chrome'
            Values = [ordered]@{
                DisplayName   = @{ Type = 'String';      Value = 'Google Chrome' }
                Enabled       = @{ Type = 'DWord';       Value = 0 }       # review, then set to 1
                Source        = @{ Type = 'String';      Value = 'winget' }
                WingetId      = @{ Type = 'String';      Value = 'Google.Chrome' }
                Mandatory     = @{ Type = 'DWord';       Value = 1 }
                DeadlineHours = @{ Type = 'DWord';       Value = 72 }
                MaxDeferrals  = @{ Type = 'DWord';       Value = 3 }
                ProcessNames  = @{ Type = 'MultiString'; Value = @('chrome') }
            }
        },
        [ordered]@{
            AppId = 'notepadplusplus'
            Values = [ordered]@{
                DisplayName  = @{ Type = 'String';      Value = 'Notepad++' }
                Enabled      = @{ Type = 'DWord';       Value = 0 }        # review, then set to 1
                Source       = @{ Type = 'String';      Value = 'winget' }
                WingetId     = @{ Type = 'String';      Value = 'Notepad++.Notepad++' }
                Mandatory    = @{ Type = 'DWord';       Value = 0 }
                ProcessNames = @{ Type = 'MultiString'; Value = @('notepad++') }
            }
        }
    )

    if ($PSCmdlet.ShouldProcess("$PreferenceKey\Apps", 'Write sample app policies')) {
        foreach ($app in $sampleApps) {
            $appKey = Join-Path "$PreferenceKey\Apps" $app.AppId
            if (-not (Test-Path -LiteralPath $appKey)) { New-Item -Path $appKey -Force | Out-Null }
            Write-Info ("App: {0}" -f $app.AppId)
            foreach ($entry in $app.Values.GetEnumerator()) {
                Set-PreferenceValue -Path $appKey -Name $entry.Key -Value $entry.Value.Value -Type $entry.Value.Type
            }
        }
    }
}

Write-Step 'Registering the tray agent logon fallback'
if ($PSCmdlet.ShouldProcess("$RunKey\$RunValueName", 'Write Run value')) {
    # The service normally starts the tray agent itself (LaunchTrayAgent=1). This Run value is the
    # fallback that also covers sessions created while the service is stopped.
    New-ItemProperty -LiteralPath $RunKey -Name $RunValueName -Value ('"{0}"' -f $trayExe) -PropertyType String -Force | Out-Null
    Write-Info ('{0} = "{1}"' -f $RunValueName, $trayExe)
}

# --------------------------------------------------------------------------------- start

if ($NoStart) {
    Write-Step 'Start skipped (-NoStart)'
} else {
    Write-Step 'Starting the service'
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
        Start-Service -Name $ServiceName
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-ServiceOrNull -Name $ServiceName).Status -ne 'Running' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        $status = (Get-ServiceOrNull -Name $ServiceName).Status
        Write-Info "Service status: $status"
        if ($status -ne 'Running') {
            Write-Warning "The service did not reach Running. Check $LogDirectory and the Application event log (source '$EventLogSource')."
        }
    }

    # Only for a real interactive administrator. When the installer runs as SYSTEM - the self-updater,
    # an Intune Win32 app, a scheduled task - there is no user session to put a tray icon in, and the
    # service launches the tray agent into each session itself (LaunchTrayAgent = 1).
    $runningAsSystem = $false
    try {
        $runningAsSystem = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
    } catch { }
    if ($runningAsSystem) {
        Write-Step 'Tray agent launch skipped (running as SYSTEM - no interactive session)'
        Write-Info 'The service starts the tray agent in every interactive session (LaunchTrayAgent = 1).'
    } elseif ([Environment]::UserInteractive) {
        Write-Step 'Launching the tray agent for the current session'
        if ($PSCmdlet.ShouldProcess($trayExe, 'Start tray agent')) {
            try {
                Start-Process -FilePath $trayExe -ErrorAction Stop | Out-Null
                Write-Info 'Tray agent started for the account running this script.'
            } catch {
                Write-Info ("Tray agent not started here ({0}). It starts at the next logon, or when the service launches it into the session." -f $_.Exception.Message)
            }
        }
    }
}

# --------------------------------------------------------------------------------- prerequisites

# winget (the Microsoft.DesktopAppInstaller package) is the agent's only runtime prerequisite - the
# binaries themselves are self-contained. The service can install or repair it for the SYSTEM account;
# running the check here means a fresh machine is ready before the first scan instead of up to one
# check interval later. This never fails the installation: a machine without internet access, or one
# where winget is provisioned in the image, is a legitimate state.
$script:PrerequisiteOutcome = 'not run'
if ($SkipPrerequisites) {
    Write-Step 'Prerequisite check skipped (-SkipPrerequisites)'
    Write-Info 'Run it later with: Arkimentum.AppMonitor.Service.exe --prerequisites'
    $script:PrerequisiteOutcome = 'skipped (-SkipPrerequisites)'
} elseif ($NoStart) {
    Write-Step 'Prerequisite check skipped (-NoStart)'
    $script:PrerequisiteOutcome = 'skipped (-NoStart)'
} else {
    Write-Step 'Checking prerequisites (winget for the SYSTEM account)'
    Write-Info 'Needs outbound HTTPS to github.com, aka.ms, nuget.org and the PowerShell Gallery.'
    if ($PSCmdlet.ShouldProcess($serviceExe, 'Run --prerequisites')) {
        try {
            $prereqOutput = & $serviceExe '--prerequisites' 2>&1
            $prereqExit = $LASTEXITCODE
            foreach ($line in @($prereqOutput)) { Write-Info ("  {0}" -f $line) }
            switch ($prereqExit) {
                0 {
                    Write-Info 'Prerequisites are in order (winget is available to the SYSTEM account).'
                    $script:PrerequisiteOutcome = 'OK'
                }
                2 {
                    Write-Warning ('winget is still not usable by the SYSTEM account. Applications with Source=winget will fail their update check until it is. ' +
                                   'See docs\Troubleshooting.md; on isolated machines provision App Installer in the image and set AutoInstallPrerequisites=0.')
                    $script:PrerequisiteOutcome = 'winget not available (exit 2)'
                }
                default {
                    Write-Warning ("The prerequisite check failed with exit code {0}. The installation is complete; re-run it later with: `"{1}`" --prerequisites" -f $prereqExit, $serviceExe)
                    $script:PrerequisiteOutcome = "failed (exit $prereqExit)"
                }
            }
        } catch {
            Write-Warning ("Could not run the prerequisite check: {0}. Re-run it later with: `"{1}`" --prerequisites" -f $_.Exception.Message, $serviceExe)
            $script:PrerequisiteOutcome = 'could not run'
        }
        $global:LASTEXITCODE = 0
    }
}

# --------------------------------------------------------------------------------- summary

$policyPresent = Test-Path -LiteralPath $PolicyKey

Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host "  Service          : $ServiceName ($ServiceDisplayName), LocalSystem, delayed auto-start"
Write-Host "  Service binary   : $serviceExe"
Write-Host "  Tray binary      : $trayExe"
if ($hasAdminPayload) {
    Write-Host "  Admin console    : $adminExe"
    if ($NoAdminConsole) {
        Write-Host '  Start Menu       : no shortcut (-NoAdminConsole) - start the console from the path above'
    } else {
        Write-Host "  Start Menu       : $adminShortcut"
    }
    Write-Host "  Admin log        : $LogDirectory\Arkimentum.AppMonitor.Admin_yyyyMMdd.log"
} else {
    Write-Host '  Admin console    : not in this payload'
}
Write-Host "  Preferences      : HKLM\SOFTWARE\Arkimentum\AppMonitor"

# Cloud summary: the effective preference values, whether they came from this run or an earlier one.
# A value passed on the command line is reported even under -WhatIf, where nothing was written yet.
# The enrollment key is masked; it is a shared organization secret.
$effectiveServerUrl = if ($PSBoundParameters.ContainsKey('CloudServerUrl'))      { $CloudServerUrl }      else { Get-CurrentPreference 'CloudServerUrl' }
$effectiveOrgId     = if ($PSBoundParameters.ContainsKey('CloudOrganizationId')) { $CloudOrganizationId } else { Get-CurrentPreference 'CloudOrganizationId' }
$effectiveKey       = if ($PSBoundParameters.ContainsKey('CloudEnrollmentKey'))  { $CloudEnrollmentKey }  else { Get-CurrentPreference 'CloudEnrollmentKey' }
$effectiveFeed      = if ($PSBoundParameters.ContainsKey('AgentUpdateFeedUrl'))  { $AgentUpdateFeedUrl }  else { Get-CurrentPreference 'AgentUpdateFeedUrl' }
$effectiveAutoUpd   = if ($NoAutoUpdate) { 0 } else { Get-CurrentPreference 'AgentAutoUpdate' }
if ($effectiveServerUrl) {
    Write-Host "  Cloud server     : $effectiveServerUrl"
    Write-Host ("  Organization id  : {0}" -f $(if ($effectiveOrgId) { $effectiveOrgId } else { '(not set - the agent stays stand-alone)' }))
    Write-Host ("  Enrollment key   : {0}" -f (Get-MaskedSecret ([string]$effectiveKey)))
    Write-Host ("  Device credential: {0}\device.credential (created at the first sync)" -f $ProgramDataRoot)
    Write-Host ("  Cloud status     : {0}\cloud-status.json, or `"{1}`" --cloud-status" -f $ProgramDataRoot, $serviceExe)
} else {
    Write-Host '  Cloud server     : (not configured - stand-alone agent, no data leaves the device)'
}
Write-Host ("  Agent self-update: {0}{1}" -f
    $(if ($null -ne $effectiveAutoUpd -and [int]$effectiveAutoUpd -eq 0) { 'off (AgentAutoUpdate = 0)' } else { 'on (default)' }),
    $(if ($effectiveFeed) { " - feed $effectiveFeed" } elseif ($effectiveServerUrl) { ' - feed from the cloud API mirror' } else { ' - no feed configured' }))
Write-Host ("  Group Policy key : HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor {0}" -f $(if ($policyPresent) { '(present - it overrides the preferences above)' } else { '(not present)' }))
Write-Host "  Service log      : $LogDirectory\Arkimentum.AppMonitor.Service_yyyyMMdd.log"
Write-Host '  Tray log         : %LOCALAPPDATA%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Tray_yyyyMMdd.log'
Write-Host "  State file       : $ProgramDataRoot\state.json"
Write-Host "  Event log        : Application, source '$EventLogSource'"
Write-Host "  Prerequisites    : $script:PrerequisiteOutcome (winget for the SYSTEM account)"
Write-Host ''
Write-Host 'Troubleshooting: run the service interactively with'
Write-Host ("  `"{0}`" --console" -f $serviceExe)
Write-Host 'Configuration is managed centrally: enrol the device, then publish the settings from the browser admin'
Write-Host 'console or from the Start Menu -> Arkimentum -> Arkimentum AppMonitor Admin. See docs\AdminConsole.md.'
Write-Host 'Stand-alone devices: docs\Registry.md or Set-SampleConfiguration.ps1.'
if ($effectiveServerUrl) {
    Write-Host 'Cloud service, provisioning and troubleshooting: docs\Cloud.md. Self-update: docs\SelfUpdate.md.'
}

if ($script:TranscriptStarted) {
    try { Stop-Transcript | Out-Null } catch { }
}
