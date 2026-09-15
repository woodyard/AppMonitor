<#
.SYNOPSIS
    Intune Win32 app detection script for Arkimentum AppMonitor.

.DESCRIPTION
    Detected (exit code 0 and text on standard output) when the service executable is installed, its file version
    is at least $MinimumVersion, and the Windows service is registered. Otherwise: no output and exit code 1, which
    Intune reads as "not installed" and installs the app.

    The version check is "at least", never "equals": the agent updates itself from its release feed, so a device
    is frequently newer than the package Intune holds. An "equals" rule would flip such a device to "not detected"
    and make Intune reinstall the older package over it.

    Set $MinimumVersion to the version of the package you upload. Intune passes no parameters to detection scripts,
    so it is a variable rather than a parameter.

    Intune settings for the detection rule: Rules format "Use a custom detection script", this file,
    "Run script as 32-bit process on 64-bit clients" = No (the script copes either way, but keep it No),
    "Enforce script signature check" = No unless you sign the shipped scripts.

    Windows PowerShell 5.1 compatible (Intune runs detection scripts in Windows PowerShell).
#>

$MinimumVersion = '1.1.1'

$ErrorActionPreference = 'Stop'

# Intune may run this in a 32-bit host, where $env:ProgramFiles points at "Program Files (x86)".
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$serviceExe   = Join-Path $programFiles 'Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe'
$serviceName  = 'ArkimentumAppMonitor'

try {
    if (-not (Test-Path -LiteralPath $serviceExe)) { exit 1 }

    $fileVersion = (Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) { exit 1 }

    # FileVersion is "1.1.1.0"; tolerate a trailing "+commit" or other suffix by keeping the leading digits and dots.
    $installed = [version]([regex]::Match($fileVersion, '^\d+(\.\d+){1,3}').Value)
    $minimum   = [version]$MinimumVersion
    if ($installed -lt $minimum) { exit 1 }

    if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { exit 1 }

    Write-Output ("Arkimentum AppMonitor {0} is installed (service '{1}' registered)." -f $installed, $serviceName)
    exit 0
}
catch {
    exit 1
}
