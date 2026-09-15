#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Arkimentum AppMonitor agent from this machine.

.DESCRIPTION
    Stops the tray agents, the admin console and the ArkimentumAppMonitor service, deletes the
    service, removes the ArkimentumAppMonitorTray Run value, removes the all-users Start Menu
    shortcut of the admin console and deletes the install folder (Service\, Tray\ and Admin\).

    Configuration and data are kept unless you ask for them to be removed:
      -RemoveConfiguration  also deletes HKLM\SOFTWARE\Arkimentum\AppMonitor (the local preferences,
                            including the Cloud* connection values)
      -RemoveData           also deletes %ProgramData%\Arkimentum\AppMonitor. That folder holds the logs,
                            state.json and the Downloads cache, and everything the cloud side keeps on
                            the device: device.credential (the DPAPI-protected per-device key),
                            cloud-config.json (the cached organization configuration), cloud-status.json,
                            and the AgentUpdates\ folder used by the self-updater.

    HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor is never touched: that key is owned by Group Policy /
    Intune and must be removed by unassigning the policy, not by this script.

.PARAMETER InstallDir
    Install root to remove. Default: %ProgramFiles%\Arkimentum\AppMonitor.

.PARAMETER RemoveConfiguration
    Also delete HKLM\SOFTWARE\Arkimentum\AppMonitor (including Apps and AppList).

.PARAMETER RemoveData
    Also delete %ProgramData%\Arkimentum\AppMonitor: logs, state.json, cached downloads, and the cloud
    files device.credential, cloud-config.json, cloud-status.json and AgentUpdates\.

    Deleting device.credential is what un-enrols the device locally. A later install with a valid
    enrollment key enrols it again, and the cloud re-attaches it to the same record by MachineGuid.
    The record in the cloud is never touched by this script - remove it on the admin console's Devices
    page if the machine is gone for good. See docs\Cloud.md.

.PARAMETER RemoveEventLogSource
    Also unregister the 'Arkimentum AppMonitor' event log source. Existing event log entries remain.

.EXAMPLE
    .\Uninstall-ArkimentumAppMonitor.ps1

.EXAMPLE
    .\Uninstall-ArkimentumAppMonitor.ps1 -RemoveConfiguration -RemoveData
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Arkimentum\AppMonitor'),
    [switch]$RemoveConfiguration,
    [switch]$RemoveData,
    [switch]$RemoveEventLogSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ServiceName     = 'ArkimentumAppMonitor'
$EventLogSource  = 'Arkimentum AppMonitor'
$RunValueName    = 'ArkimentumAppMonitorTray'
$RunKey          = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$PreferenceKey   = 'HKLM:\SOFTWARE\Arkimentum\AppMonitor'
$PreferenceRoot  = 'HKLM:\SOFTWARE\Arkimentum'
$PolicyKey       = 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor'
$ProgramDataRoot = Join-Path $env:ProgramData 'Arkimentum\AppMonitor'
# All-users Start Menu entry created by the installer for the admin console.
$StartMenuDir    = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Arkimentum'
$AdminShortcut   = Join-Path $StartMenuDir 'Arkimentum AppMonitor Admin.lnk'

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

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit PowerShell, otherwise the registry cleanup hits the WOW6432Node redirected view.'
}

Write-Host 'Arkimentum AppMonitor - uninstall' -ForegroundColor Green
Write-Host "  Install dir : $InstallDir"

# --------------------------------------------------------------------------------- stop

Write-Step 'Stopping tray agents'
foreach ($p in @(Get-Process -Name 'Arkimentum.AppMonitor.Tray' -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess("pid $($p.Id)", 'Stop tray agent')) {
        try { $p | Stop-Process -Force -ErrorAction Stop; Write-Info "Stopped tray agent (pid $($p.Id))." }
        catch { Write-Warning "Could not stop tray agent (pid $($p.Id)): $($_.Exception.Message)" }
    }
}

Write-Step 'Stopping the admin console'
foreach ($p in @(Get-Process -Name 'Arkimentum.AppMonitor.Admin' -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess("pid $($p.Id)", 'Stop admin console')) {
        try { $p | Stop-Process -Force -ErrorAction Stop; Write-Info "Stopped admin console (pid $($p.Id))." }
        catch { Write-Warning "Could not stop the admin console (pid $($p.Id)): $($_.Exception.Message)" }
    }
}

Write-Step 'Stopping and deleting the service'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete service')) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }
        }
        $output = & "$env:SystemRoot\System32\sc.exe" delete $ServiceName 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning ("sc.exe delete {0} returned {1}: {2}" -f $ServiceName, $LASTEXITCODE, ($output -join ' '))
        } else {
            Write-Info 'Service deleted.'
        }
    }
} else {
    Write-Info 'Service not installed.'
}

# Any service host still running (for example started with --console) must go before the files.
foreach ($p in @(Get-Process -Name 'Arkimentum.AppMonitor.Service' -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess("pid $($p.Id)", 'Stop service process')) {
        try { $p | Stop-Process -Force -ErrorAction Stop; Write-Info "Stopped service process (pid $($p.Id))." } catch { }
    }
}

# --------------------------------------------------------------------------------- registry / files

Write-Step 'Removing the tray agent Run value'
if (Get-ItemProperty -LiteralPath $RunKey -Name $RunValueName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess("$RunKey\$RunValueName", 'Remove value')) {
        Remove-ItemProperty -LiteralPath $RunKey -Name $RunValueName -Force
        Write-Info "Removed $RunValueName."
    }
} else {
    Write-Info 'Run value not present.'
}

Write-Step 'Removing the toast notification registration of the current user'
# The tray agent registers a per-user AppUserModelId and a COM activator (HKCU) so Windows accepts its toasts.
# Other users' registrations are harmless leftovers that point at a no-longer-existing executable.
$aumidKey = 'HKCU:\SOFTWARE\Classes\AppUserModelId\Arkimentum.AppMonitor.Tray'
if (Test-Path -LiteralPath $aumidKey) {
    if ($PSCmdlet.ShouldProcess($aumidKey, 'Remove key')) { Remove-Item -LiteralPath $aumidKey -Recurse -Force; Write-Info 'Removed AppUserModelId registration.' }
} else {
    Write-Info 'AppUserModelId registration not present for this user.'
}
Get-ChildItem 'HKCU:\SOFTWARE\Classes\CLSID' -ErrorAction SilentlyContinue | ForEach-Object {
    # Set-StrictMode makes a missing '(default)' property fatal, and most CLSIDs have no default value.
    $props = Get-ItemProperty -LiteralPath "$($_.PSPath)\LocalServer32" -ErrorAction SilentlyContinue
    $server = if ($props -and $props.PSObject.Properties.Name -contains '(default)') { $props.'(default)' } else { $null }
    if ($server -and $server -like '*Arkimentum.AppMonitor.Tray.exe*') {
        if ($PSCmdlet.ShouldProcess($_.PSPath, 'Remove toast activator CLSID')) { Remove-Item -LiteralPath $_.PSPath -Recurse -Force; Write-Info "Removed toast activator $($_.PSChildName)." }
    }
}

Write-Step 'Removing the admin console Start Menu shortcut'
if (Test-Path -LiteralPath $AdminShortcut) {
    if ($PSCmdlet.ShouldProcess($AdminShortcut, 'Remove shortcut')) {
        Remove-Item -LiteralPath $AdminShortcut -Force
        Write-Info "Removed $AdminShortcut."
    }
} else {
    Write-Info 'Start Menu shortcut not present.'
}
# Remove the Programs\Arkimentum folder as well, but only when nothing else was put there.
if ((Test-Path -LiteralPath $StartMenuDir) -and -not (Get-ChildItem -LiteralPath $StartMenuDir -Force)) {
    if ($PSCmdlet.ShouldProcess($StartMenuDir, 'Remove empty Start Menu folder')) {
        Remove-Item -LiteralPath $StartMenuDir -Force
        Write-Info "Removed the empty $StartMenuDir."
    }
}

Write-Step 'Removing the install folder'
if (Test-Path -LiteralPath $InstallDir) {
    if ($PSCmdlet.ShouldProcess($InstallDir, 'Delete folder')) {
        try {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
            Write-Info "Deleted $InstallDir."
        } catch {
            Write-Warning ("Could not delete {0}: {1}. Reboot and delete it manually if files are still locked." -f $InstallDir, $_.Exception.Message)
        }
    }
} else {
    Write-Info "Install folder not present: $InstallDir"
}

if ($RemoveConfiguration) {
    Write-Step 'Removing local configuration'
    if (Test-Path -LiteralPath $PreferenceKey) {
        if ($PSCmdlet.ShouldProcess($PreferenceKey, 'Delete registry key')) {
            Remove-Item -LiteralPath $PreferenceKey -Recurse -Force
            Write-Info 'Deleted HKLM\SOFTWARE\Arkimentum\AppMonitor.'
        }
    } else {
        Write-Info 'HKLM\SOFTWARE\Arkimentum\AppMonitor not present.'
    }
    # Remove the now-empty parent key, but only when nothing else lives under it.
    if (Test-Path -LiteralPath $PreferenceRoot) {
        $key = Get-Item -LiteralPath $PreferenceRoot
        if ($key.SubKeyCount -eq 0 -and $key.ValueCount -eq 0 -and $PSCmdlet.ShouldProcess($PreferenceRoot, 'Delete empty registry key')) {
            Remove-Item -LiteralPath $PreferenceRoot -Force
            Write-Info 'Deleted the empty HKLM\SOFTWARE\Arkimentum key.'
        }
    }
} else {
    Write-Step 'Local configuration kept (use -RemoveConfiguration to delete it)'
    Write-Info 'HKLM\SOFTWARE\Arkimentum\AppMonitor left in place.'
}

if ($RemoveData) {
    Write-Step 'Removing logs and state'
    if (Test-Path -LiteralPath $ProgramDataRoot) {
        if ($PSCmdlet.ShouldProcess($ProgramDataRoot, 'Delete folder')) {
            try {
                Remove-Item -LiteralPath $ProgramDataRoot -Recurse -Force
                Write-Info "Deleted $ProgramDataRoot."
            } catch {
                Write-Warning ("Could not delete {0}: {1}" -f $ProgramDataRoot, $_.Exception.Message)
            }
        }
    } else {
        Write-Info "Data folder not present: $ProgramDataRoot"
    }
    Write-Info 'The cloud files that live in that folder went with it: device.credential, cloud-config.json,'
    Write-Info 'cloud-status.json and AgentUpdates\. The device record in the cloud is untouched - remove it on'
    Write-Info 'the Devices page of the admin console if this machine is gone for good.'
    Write-Info 'Per-user tray logs in %LOCALAPPDATA%\Arkimentum\AppMonitor\Logs are not removed (they live in each user profile).'
} else {
    Write-Step 'Logs and state kept (use -RemoveData to delete them)'
    Write-Info "$ProgramDataRoot left in place."
    Write-Info 'That includes device.credential, cloud-config.json, cloud-status.json and AgentUpdates\, so a'
    Write-Info 'later re-install rejoins the same organization without needing the enrollment key again.'
}

if ($RemoveEventLogSource) {
    Write-Step 'Unregistering the event log source'
    $sourceExists = $false
    try {
        $sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventLogSource)
    } catch {
        Write-Warning ("Could not query the event log sources ({0})." -f $_.Exception.Message)
    }
    if ($sourceExists) {
        if ($PSCmdlet.ShouldProcess($EventLogSource, 'Remove event log source')) {
            try {
                Remove-EventLog -Source $EventLogSource -ErrorAction Stop
                Write-Info "Removed event log source '$EventLogSource'."
            } catch {
                Write-Warning ("Could not remove the event log source: {0}" -f $_.Exception.Message)
            }
        }
    } else {
        Write-Info 'Event log source not registered.'
    }
}

# --------------------------------------------------------------------------------- summary

Write-Host ''
Write-Host 'Uninstall complete.' -ForegroundColor Green
if (Test-Path -LiteralPath $PolicyKey) {
    Write-Host '  NOTE: HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor still exists and was deliberately NOT touched.'
    Write-Host '        That key is owned by Group Policy / Intune - remove the assignment there, or the settings'
    Write-Host '        will simply come back the next time the agent is installed.'
} else {
    Write-Host '  HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor is not present. This script never modifies that key;'
    Write-Host '  it is owned by Group Policy / Intune.'
}
if (-not $RemoveConfiguration) { Write-Host '  Local preferences kept: HKLM\SOFTWARE\Arkimentum\AppMonitor' }
if (-not $RemoveData) { Write-Host "  Logs and state kept: $ProgramDataRoot" }
