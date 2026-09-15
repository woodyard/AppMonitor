#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Writes a realistic example configuration for the Arkimentum AppMonitor agent.

.DESCRIPTION
    Creates global settings and five example applications that between them show every supported
    behaviour. Use it on a pilot machine, then adapt it - or copy the pieces you need into your own
    tooling. Every value name, type and range below matches
    src\Arkimentum.AppMonitor.Core\Configuration\RegistryConfigurationReader.cs; see docs\Registry.md.

    Examples written:
      chrome       mandatory winget app, 72 h deadline, 3 deferrals, closes chrome.exe
      7zip         optional app updated from the vendor web site (7-zip.org)
      vscode       per-user install, handled by the tray agent in the user's session
      powershell   silent auto-install of an MSI downloaded from the web
      firefox      the flat AppList format (one REG_SZ value, no subkey)

    The organization connection (CloudServerUrl, CloudOrganizationId, CloudEnrollmentKey) and the
    agent self-update values are included as commented-out examples rather than being written: they
    are organization specific, and provisioning them is normally the installer's or Intune's job.
    See docs\Cloud.md.

    The agent re-reads the registry on its own schedule; restart the ArkimentumAppMonitor service to
    apply the configuration immediately.

.PARAMETER Scope
    Preference (default) writes to HKLM\SOFTWARE\Arkimentum\AppMonitor.
    Policy writes to HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor instead. Only use Policy on a test
    machine that is not receiving these settings from Group Policy or Intune - a policy refresh
    overwrites anything written there by hand.

.PARAMETER RemoveExistingApps
    Delete the existing Apps subkey and AppList key before writing, so the result is exactly the
    example set. Without this, existing apps are left alone and the examples are added or updated.

.PARAMETER RestartService
    Restart the ArkimentumAppMonitor service afterwards so the new configuration is picked up at once.

.EXAMPLE
    .\Set-SampleConfiguration.ps1

.EXAMPLE
    .\Set-SampleConfiguration.ps1 -RemoveExistingApps -RestartService

.EXAMPLE
    .\Set-SampleConfiguration.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Preference', 'Policy')]
    [string]$Scope = 'Preference',

    [switch]$RemoveExistingApps,

    [switch]$RestartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit PowerShell; a 32-bit host writes to the WOW6432Node redirected view, where the agent does not look.'
}

$root = if ($Scope -eq 'Policy') { 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor' } else { 'HKLM:\SOFTWARE\Arkimentum\AppMonitor' }
if ($Scope -eq 'Policy') {
    Write-Warning 'Writing to the Policies key. Group Policy / Intune owns that key and will overwrite these values on the next refresh.'
}

$appsRoot = Join-Path $root 'Apps'
$appListKey = Join-Path $root 'AppList'

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ''
    Write-Host $Message -ForegroundColor Cyan
}

$script:AnnouncedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

function Initialize-RegistryKey {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { return }
    if (-not $script:AnnouncedKeys.Add($Path)) { return }   # already reported for this run
    if ($PSCmdlet.ShouldProcess($Path, 'Create key')) { New-Item -Path $Path -Force | Out-Null }
}

function Set-Value {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()]$Value,
        [Parameter(Mandatory)][ValidateSet('DWord', 'String', 'MultiString')][string]$Type
    )
    Initialize-RegistryKey -Path $Path
    if ($PSCmdlet.ShouldProcess("$Path\$Name", "Set $Type")) {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
        Write-Host ("    {0,-32} {1}" -f $Name, ($Value -join ' | '))
    }
}

function Set-App {
    param(
        [Parameter(Mandatory)][string]$AppId,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Values
    )
    $key = Join-Path $appsRoot $AppId
    Write-Host ''
    Write-Host "  Apps\$AppId" -ForegroundColor Yellow
    foreach ($entry in $Values.GetEnumerator()) {
        Set-Value -Path $key -Name $entry.Key -Value $entry.Value.Value -Type $entry.Value.Type
    }
}

Write-Host 'Arkimentum AppMonitor - example configuration' -ForegroundColor Green
Write-Host "  Target key : $root"

# ---------------------------------------------------------------------------------------------
# Global settings. Everything here is optional - the agent has sensible built-in defaults - but a
# managed fleet normally pins at least the intervals, the log level and the default behaviour.
# ---------------------------------------------------------------------------------------------
Write-Step 'Global settings'

# Scan for updates every 4 hours (5-10080 minutes).
Set-Value -Path $root -Name 'ScanIntervalMinutes'         -Value 240 -Type DWord
# Notification style: Quiet announces an update once and afterwards only interrupts when the user
# must act (deadline, applications to close, failure). Reminders repeats every interval below.
Set-Value -Path $root -Name 'NotificationMode'            -Value 'Quiet' -Type String
# Reminders style only: repeat a notification for the same pending update at most every 4 hours (1-10080).
Set-Value -Path $root -Name 'NotificationIntervalMinutes' -Value 240 -Type DWord
# Wait 2 minutes after service start before the first scan, so logon is not slowed down (0-3600 s).
Set-Value -Path $root -Name 'StartupDelaySeconds'         -Value 120 -Type DWord
# Scan once at service start (in addition to the interval).
Set-Value -Path $root -Name 'ScanOnStartup'               -Value 1   -Type DWord
# Sources: winget and vendor web sites are both enabled.
Set-Value -Path $root -Name 'WingetEnabled'               -Value 1   -Type DWord
Set-Value -Path $root -Name 'WebSourcesEnabled'           -Value 1   -Type DWord
# Prerequisites: winget (Microsoft.DesktopAppInstaller) is the agent's only runtime prerequisite.
# The service installs or repairs it for the SYSTEM account when it is missing or too old, at startup
# and then every 24 hours (1-720). It needs outbound HTTPS to github.com, aka.ms, nuget.org and the
# PowerShell Gallery - set AutoInstallPrerequisites to 0 on isolated machines and provision App
# Installer in the image instead.
Set-Value -Path $root -Name 'AutoInstallPrerequisites'       -Value 1  -Type DWord
Set-Value -Path $root -Name 'PrerequisiteCheckIntervalHours' -Value 24 -Type DWord
Set-Value -Path $root -Name 'WingetMinimumVersion'           -Value '1.6.0' -Type String
# The service starts the tray agent in every interactive session.
Set-Value -Path $root -Name 'LaunchTrayAgent'             -Value 1   -Type DWord
# Toast notifications on; no confirmation toast after a successful install (set to 1 to get one).
Set-Value -Path $root -Name 'NotificationsEnabled'        -Value 1   -Type DWord
Set-Value -Path $root -Name 'ShowInstalledNotifications'  -Value 0   -Type DWord
# Logging: Trace, Debug, Information, Warning or Error.
Set-Value -Path $root -Name 'LogLevel'                    -Value 'Information' -Type String
Set-Value -Path $root -Name 'LogRetentionDays'            -Value 30  -Type DWord
Set-Value -Path $root -Name 'MaxLogFileSizeMB'            -Value 10  -Type DWord
# Abort a single installer run after 30 minutes (1-600).
Set-Value -Path $root -Name 'InstallTimeoutMinutes'       -Value 30  -Type DWord
# Abort a single update check (one winget call or one web request) after 3 minutes (1-60).
Set-Value -Path $root -Name 'CheckTimeoutMinutes'         -Value 3   -Type DWord
# Fill missing per-app values from the built-in catalog (catalog.json next to the service exe).
Set-Value -Path $root -Name 'UseCatalog'                  -Value 1   -Type DWord
# Ignore winget results with an unknown installed version (they cause false positives).
Set-Value -Path $root -Name 'WingetIncludeUnknown'        -Value 0   -Type DWord
# Deadline / deferral / close evaluation runs every 60 seconds (10-600).
Set-Value -Path $root -Name 'PolicyTickSeconds'           -Value 60  -Type DWord

# Defaults for apps that do not set the value themselves.
Set-Value -Path $root -Name 'DefaultMandatory'                -Value 0   -Type DWord
Set-Value -Path $root -Name 'DefaultDeadlineHours'            -Value 72  -Type DWord
Set-Value -Path $root -Name 'DefaultMaxDeferrals'             -Value 3   -Type DWord
# Deferral choices offered in the tray agent, in minutes: 1 h, 4 h, 24 h.
Set-Value -Path $root -Name 'DefaultDeferralOptions'          -Value '60,240,1440' -Type String
Set-Value -Path $root -Name 'DefaultAutoInstall'              -Value 0   -Type DWord
# Users get 15 minutes to save their work once a forced close is announced.
Set-Value -Path $root -Name 'DefaultCloseGracePeriodMinutes'  -Value 15  -Type DWord
Set-Value -Path $root -Name 'DefaultForceCloseAtDeadline'     -Value 1   -Type DWord

# Optional values, shown commented out because they are environment specific:
#   ProxyUrl          REG_SZ  'http://proxy.contoso.com:8080'   proxy for web sources
#   WingetGlobalArgs  REG_SZ  '--disable-interactivity'         appended to every winget call
#   LogDirectory      REG_SZ  'D:\Logs\Arkimentum'               overrides %ProgramData%\Arkimentum\AppMonitor\Logs
#   StateDirectory    REG_SZ  'D:\State\Arkimentum'              overrides %ProgramData%\Arkimentum\AppMonitor
#   CatalogPath       REG_SZ  'C:\ProgramData\Arkimentum\catalog.json'  custom catalog file
#   TrayPath          REG_SZ  'C:\Apps\Tray\Arkimentum.AppMonitor.Tray.exe'  non-standard tray location
#   WingetPath        REG_SZ  '...\WindowsApps\Microsoft.DesktopAppInstaller_..._x64__8wekyb3d8bbwe\winget.exe'
#                             explicit winget.exe; empty = auto-detect (troubleshooting only)
#   EnableAllCatalogApps DWORD 1   monitor every app in the catalog without listing them here

# ---------------------------------------------------------------------------------------------
# Organization connection (the cloud service) - commented out on purpose.
#
# These three values are the entire per-device provisioning surface when the cloud is in use. With
# them set, the service enrols once, exchanges the enrollment key for a per-device key kept
# DPAPI-protected in %ProgramData%\Arkimentum\AppMonitor\device.credential, and from then on takes
# its configuration from the organization. Everything else below can then be published centrally
# instead of written to each machine.
#
# Precedence with the cloud in play:
#   Policies key  >  organization configuration (cloud)  >  these preferences  >  catalog  >  default
# The cloud can never override the three connection values themselves.
#
# Uncomment and fill in your own organization's values; the enrollment key comes from the Enrollment
# page of the admin console's organization mode. See docs\Cloud.md.
#
# Set-Value -Path $root -Name 'CloudServerUrl'           -Value 'https://appmonitor-contoso.azurewebsites.net' -Type String
# Set-Value -Path $root -Name 'CloudOrganizationId'      -Value '3f2504e0-4f89-11d3-9a0c-0305e82c3301'         -Type String
# Set-Value -Path $root -Name 'CloudEnrollmentKey'       -Value 'ek_live_replace_me'                            -Type String
# # Poll for configuration, hand in the report and pick up commands every 15 minutes (1-1440).
# Set-Value -Path $root -Name 'CloudSyncIntervalMinutes' -Value 15 -Type DWord
# # 0 = enrol and report, but keep configuring the device locally (useful for a pilot machine).
# Set-Value -Path $root -Name 'CloudConfigEnabled'       -Value 1  -Type DWord
# # 0 = fetch configuration only; nothing about the device is sent.
# Set-Value -Path $root -Name 'CloudReportingEnabled'    -Value 1  -Type DWord

# ---------------------------------------------------------------------------------------------
# Agent self-update - commented out on purpose.
#
# The service reads a release manifest on AgentUpdateCheckIntervalHours, verifies the package
# SHA-256 against it and installs the release as SYSTEM. Set AgentAutoUpdate to 0 when Intune,
# Configuration Manager or an RMM owns the binaries. See docs\SelfUpdate.md.
#
# Set-Value -Path $root -Name 'AgentAutoUpdate'               -Value 1 -Type DWord
# # A GitHub latest-release API URL, or a direct manifest.json URL. Empty = the cloud API's mirror.
# Set-Value -Path $root -Name 'AgentUpdateFeedUrl'            -Value 'https://api.github.com/repos/arkimentum/appmonitor/releases/latest' -Type String
# Set-Value -Path $root -Name 'AgentUpdateChannel'            -Value 'stable' -Type String
# Set-Value -Path $root -Name 'AgentUpdateCheckIntervalHours' -Value 12 -Type DWord
# # Pin the fleet to one validated build; empty = always the newest release in the channel.
# Set-Value -Path $root -Name 'AgentTargetVersion'            -Value '1.4.2' -Type String

# ---------------------------------------------------------------------------------------------
# Applications
#
# The AppIds below are the ids used by the shipped catalog (chrome, 7zip, vscode, powershell,
# firefox), so anything not written here - detection regexes, vendor URLs, process names - is
# filled in from catalog.json. Registry values always win over the catalog, and the catalog never
# decides behaviour: Mandatory, DeadlineHours, MaxDeferrals, DeferralOptions, AutoInstall,
# CloseGracePeriodMinutes and ForceCloseAtDeadline come from these keys or the Default* values.
# ---------------------------------------------------------------------------------------------
if ($RemoveExistingApps) {
    Write-Step 'Removing existing app configuration'
    foreach ($key in @($appsRoot, $appListKey)) {
        if ((Test-Path -LiteralPath $key) -and $PSCmdlet.ShouldProcess($key, 'Delete key')) {
            Remove-Item -LiteralPath $key -Recurse -Force
            Write-Host "    Removed $key"
        }
    }
}

Write-Step 'Applications (Apps\<AppId> subkeys)'

# 1) Mandatory winget app with a hard deadline ------------------------------------------------
#    Chrome must be current: after 72 hours the update is enforced. The user may defer three
#    times (1 h / 4 h / 24 h). When the deadline passes and chrome.exe is still running, the tray
#    agent asks the user to close it, waits 15 minutes, then the browser is terminated and the
#    update installed.
Set-App -AppId 'chrome' -Values ([ordered]@{
    DisplayName             = @{ Type = 'String';      Value = 'Google Chrome' }
    Enabled                 = @{ Type = 'DWord';       Value = 1 }
    Source                  = @{ Type = 'String';      Value = 'winget' }
    WingetId                = @{ Type = 'String';      Value = 'Google.Chrome' }
    Context                 = @{ Type = 'String';      Value = 'system' }
    Mandatory               = @{ Type = 'DWord';       Value = 1 }
    DeadlineHours           = @{ Type = 'DWord';       Value = 72 }
    MaxDeferrals            = @{ Type = 'DWord';       Value = 3 }
    DeferralOptions         = @{ Type = 'String';      Value = '60,240,1440' }
    ProcessNames            = @{ Type = 'MultiString'; Value = @('chrome') }
    CloseGracePeriodMinutes = @{ Type = 'DWord';       Value = 15 }
    ForceCloseAtDeadline    = @{ Type = 'DWord';       Value = 1 }
})

# 2) Optional app from the vendor web site -----------------------------------------------------
#    No winget involved: the agent fetches VersionUrl, extracts the version with VersionRegex,
#    compares it with the installed version found through DetectDisplayNameRegex, and downloads
#    DownloadUrl. {version_nodots} turns 26.03 into 2603, which is what 7-zip.org uses in the file
#    name. Not mandatory: the user can postpone it indefinitely.
Set-App -AppId '7zip' -Values ([ordered]@{
    DisplayName            = @{ Type = 'String';      Value = '7-Zip' }
    Enabled                = @{ Type = 'DWord';       Value = 1 }
    Source                 = @{ Type = 'String';      Value = 'web' }
    Context                = @{ Type = 'String';      Value = 'system' }
    VersionUrl             = @{ Type = 'String';      Value = 'https://www.7-zip.org/download.html' }
    VersionRegex           = @{ Type = 'String';      Value = 'Download 7-Zip (\d+\.\d+)' }
    DownloadUrl            = @{ Type = 'String';      Value = 'https://www.7-zip.org/a/7z{version_nodots}-x64.exe' }
    InstallerType          = @{ Type = 'String';      Value = 'exe' }
    InstallerArgs          = @{ Type = 'String';      Value = '/S' }
    DetectDisplayNameRegex = @{ Type = 'String';      Value = '^7-Zip' }
    DetectPublisherRegex   = @{ Type = 'String';      Value = 'Igor Pavlov' }
    Mandatory              = @{ Type = 'DWord';       Value = 0 }
    # ProcessNames also accepts a comma-separated REG_SZ - both forms are read identically.
    ProcessNames           = @{ Type = 'MultiString'; Value = @('7zFM', '7zG') }
})

# 3) Per-user install --------------------------------------------------------------------------
#    VS Code User Setup lives in the user profile (HKCU), so the service cannot update it. With
#    Context=user the service delegates both the check and the install to the tray agent running
#    in that user's session. Context=auto would reach the same conclusion by finding the app only
#    in a user hive; it is spelled out here to make the intent explicit.
Set-App -AppId 'vscode' -Values ([ordered]@{
    DisplayName            = @{ Type = 'String';      Value = 'Visual Studio Code (User Setup)' }
    Enabled                = @{ Type = 'DWord';       Value = 1 }
    Source                 = @{ Type = 'String';      Value = 'winget' }
    WingetId               = @{ Type = 'String';      Value = 'Microsoft.VisualStudioCode' }
    Context                = @{ Type = 'String';      Value = 'user' }
    WingetExtraArgs        = @{ Type = 'String';      Value = '--scope user' }
    DetectDisplayNameRegex = @{ Type = 'String';      Value = '^Microsoft Visual Studio Code( \(User\))?$' }
    Mandatory              = @{ Type = 'DWord';       Value = 0 }
    MaxDeferrals           = @{ Type = 'DWord';       Value = 0 }   # 0 = unlimited deferrals
    ProcessNames           = @{ Type = 'MultiString'; Value = @('Code') }
})

# 4) Silent automatic install ------------------------------------------------------------------
#    PowerShell 7 is updated without asking: AutoInstall=1 means the service installs as soon as
#    no blocking process is running. The version comes from the GitHub releases API, the MSI is
#    downloaded and run with /qn. No ProcessNames, so nothing ever blocks the install.
Set-App -AppId 'powershell' -Values ([ordered]@{
    DisplayName            = @{ Type = 'String'; Value = 'PowerShell 7' }
    Enabled                = @{ Type = 'DWord';  Value = 1 }
    Source                 = @{ Type = 'String'; Value = 'web' }
    Context                = @{ Type = 'String'; Value = 'system' }
    VersionUrl             = @{ Type = 'String'; Value = 'https://api.github.com/repos/PowerShell/PowerShell/releases/latest' }
    VersionRegex           = @{ Type = 'String'; Value = '"tag_name"\s*:\s*"v(\d+\.\d+\.\d+)"' }
    DownloadUrl            = @{ Type = 'String'; Value = 'https://github.com/PowerShell/PowerShell/releases/download/v{version}/PowerShell-{version}-win-x64.msi' }
    InstallerType          = @{ Type = 'String'; Value = 'msi' }
    InstallerArgs          = @{ Type = 'String'; Value = '/qn /norestart' }
    DetectDisplayNameRegex = @{ Type = 'String'; Value = '^PowerShell 7' }
    AutoInstall            = @{ Type = 'DWord';  Value = 1 }
    Mandatory              = @{ Type = 'DWord';  Value = 1 }
    DeadlineHours          = @{ Type = 'DWord';  Value = 24 }
})

# 5) The flat AppList format -------------------------------------------------------------------
#    One REG_SZ value per app: the value name is the AppId, the data is Name=Value;Name=Value.
#    This is what the ADMX "Monitored applications" list element and Intune produce, because
#    neither can create nested registry keys. Inside an AppList entry, list values use '|' as the
#    separator (';' already separates the pairs) - the reader converts '|' to ',' for ProcessNames
#    and DeferralOptions.
Write-Step 'Applications (flat AppList format)'
Set-Value -Path $appListKey -Name 'firefox' -Type String -Value (
    'DisplayName=Mozilla Firefox;Enabled=1;Source=winget;WingetId=Mozilla.Firefox;Context=system;' +
    'Mandatory=1;DeadlineHours=48;MaxDeferrals=2;DeferralOptions=60|240|1440;' +
    'ProcessNames=firefox|crashreporter;CloseGracePeriodMinutes=10;ForceCloseAtDeadline=1'
)

# ---------------------------------------------------------------------------------------------
if ($RestartService) {
    Write-Step 'Restarting the service'
    $service = Get-Service -Name 'ArkimentumAppMonitor' -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Warning 'The ArkimentumAppMonitor service is not installed; nothing to restart.'
    } elseif ($PSCmdlet.ShouldProcess('ArkimentumAppMonitor', 'Restart service')) {
        Restart-Service -Name 'ArkimentumAppMonitor' -Force
        Write-Host '    Service restarted.'
    }
}

Write-Host ''
Write-Host 'Example configuration written.' -ForegroundColor Green
Write-Host "  Key        : $root"
Write-Host "  Apps       : chrome, 7zip, vscode, powershell"
Write-Host "  AppList    : firefox"
Write-Host '  Verify     : %ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Service_yyyyMMdd.log'
Write-Host '  Reference  : docs\Registry.md'
if (-not $RestartService) {
    Write-Host '  Restart the ArkimentumAppMonitor service to apply the configuration immediately.'
}
