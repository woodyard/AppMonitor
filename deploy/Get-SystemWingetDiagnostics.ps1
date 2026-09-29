#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Shows what winget sees when the AppMonitor service runs it: as LocalSystem, without a console.

.DESCRIPTION
    The service runs winget as SYSTEM with its output redirected, and SYSTEM's winget has its own settings, its
    own source cache and its own view of what is installed. When the inventory or a scan disagrees with what you
    see in your own terminal ("No winget package" for an application winget clearly knows), this is the listing to
    look at.

    The script registers a one-shot scheduled task that runs as SYSTEM, starts winget through System.Diagnostics.Process
    with the output redirected and no console (exactly like the service), and writes to one text file:

      winget --info
      winget source list
      winget list --scope machine
      winget list (every scope)

    The task is removed again afterwards. Nothing else changes on the machine.

.PARAMETER OutputPath
    Where the listing is written. Default: %ProgramData%\Arkimentum\AppMonitor\Logs\system-winget.txt

.PARAMETER TimeoutSeconds
    How long to wait for SYSTEM's winget to finish. Default 300.

.PARAMETER Extra
    Additional winget argument lines to run as SYSTEM after the standard four, one string each, e.g.
    'search --id OpenJS.NodeJS.LTS --exact --versions --disable-interactivity' or 'source update --disable-interactivity'.

.EXAMPLE
    .\Get-SystemWingetDiagnostics.ps1
    notepad "$env:ProgramData\Arkimentum\AppMonitor\Logs\system-winget.txt"

.EXAMPLE
    .\Get-SystemWingetDiagnostics.ps1 -Extra 'source update --disable-interactivity', 'list --scope machine --disable-interactivity'
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $env:ProgramData 'Arkimentum\AppMonitor\Logs\system-winget.txt'),
    [int] $TimeoutSeconds = 300,
    [string[]] $Extra = @()
)

$ErrorActionPreference = 'Stop'
$taskName = 'Arkimentum AppMonitor winget diagnostics'

# winget for SYSTEM: the App Installer package payload, not the per-user execution alias (which SYSTEM does not have).
$packages = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'WindowsApps') -Directory -Filter 'Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe' |
    Where-Object { $_.Name -match '_(x64|arm64)__' -and (Test-Path (Join-Path $_.FullName 'winget.exe')) }
if (-not $packages) { throw 'No App Installer package with winget.exe was found under Program Files\WindowsApps.' }
$winget = ($packages | Sort-Object { [version]($_.Name -replace '^Microsoft\.DesktopAppInstaller_([0-9.]+)_.*$', '$1') } -Descending | Select-Object -First 1)
$wingetPath = Join-Path $winget.FullName 'winget.exe'
Write-Host ("winget for SYSTEM: {0}" -f $wingetPath)

$outputDir = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }

# The worker script the task runs. It launches winget the way the service does: redirected output, no window.
$workerPath = Join-Path $outputDir 'system-winget-worker.ps1'
$worker = @'
param([string] $Winget, [string] $Out, [string] $ExtraFile)
$commands = @(
    '--info',
    'source list --disable-interactivity',
    'list --scope machine --accept-source-agreements --disable-interactivity',
    'list --accept-source-agreements --disable-interactivity'
)
if ($ExtraFile -and (Test-Path $ExtraFile)) {
    $commands += @(Get-Content $ExtraFile | Where-Object { $_.Trim().Length -gt 0 })
}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("Identity: " + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
[void]$sb.AppendLine("winget:   " + $Winget)
foreach ($argumentLine in $commands) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("=== winget " + $argumentLine)
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Winget
        $psi.Arguments = $argumentLine
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $p = [System.Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(240000)) { try { $p.Kill() } catch {} ; [void]$sb.AppendLine("(timed out)") }
        [void]$sb.AppendLine(("exit 0x{0:X8}" -f $p.ExitCode))
        [void]$sb.Append($stdout.Result)
        if ($stderr.Result) { [void]$sb.AppendLine("--- stderr"); [void]$sb.Append($stderr.Result) }
    }
    catch { [void]$sb.AppendLine("FAILED: " + $_.Exception.Message) }
}
[void]$sb.AppendLine()
# SYSTEM's own winget logs (its %TEMP%\WinGet\defaultState) are unreadable for a normal user; copy the ones this run
# produced next to the listing, where an administrator or a support session can read them.
try {
    $logDir = Join-Path $env:TEMP 'WinGet\defaultState'
    $target = Join-Path (Split-Path -Parent $Out) 'system-winget-logs'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem $target -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $copied = 0
    foreach ($log in Get-ChildItem $logDir -Filter 'WinGet-*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) }) {
        Copy-Item $log.FullName -Destination $target -Force
        $copied++
    }
    [void]$sb.AppendLine(("=== winget logs: {0} file(s) from {1} copied to {2}" -f $copied, $logDir, $target))
}
catch { [void]$sb.AppendLine("=== winget logs: not copied: " + $_.Exception.Message) }
[void]$sb.AppendLine("=== done")
[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
'@
Set-Content -Path $workerPath -Value $worker -Encoding UTF8

# Extra argument lines travel in a file: quoting them through the task's command line is not worth the trouble.
$extraPath = Join-Path $outputDir 'system-winget-extra.txt'
Set-Content -Path $extraPath -Value ($Extra -join "`r`n") -Encoding UTF8

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Winget "{1}" -Out "{2}" -ExtraFile "{3}"' -f $workerPath, $wingetPath, $OutputPath, $extraPath)
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    Write-Host 'Running winget as SYSTEM ...'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $done = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if ((Test-Path $OutputPath) -and (Select-String -Path $OutputPath -Pattern '^=== done' -Quiet)) { $done = $true; break }
    }
    if (-not $done) { Write-Warning ("SYSTEM's winget did not finish within {0} seconds; whatever it wrote is in {1}." -f $TimeoutSeconds, $OutputPath) }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $workerPath, $extraPath -Force -ErrorAction SilentlyContinue
}

if (Test-Path $OutputPath) {
    Write-Host ("Written: {0}" -f $OutputPath)
    Get-Content $OutputPath -TotalCount 12
}
