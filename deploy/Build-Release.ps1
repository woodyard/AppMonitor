<#
.SYNOPSIS
    Builds, tests and packages a Arkimentum AppMonitor release.

.DESCRIPTION
    Publishes src\Arkimentum.AppMonitor.Service, src\Arkimentum.AppMonitor.Tray and
    src\Arkimentum.AppMonitor.Admin for win-x64 into <OutputRoot>\Service, <OutputRoot>\Tray and
    <OutputRoot>\Admin, runs the xunit test project, copies the deployment scripts, the ADMX policy
    folder and the documentation into <OutputRoot>, and finally creates
    artifacts\Arkimentum.AppMonitor-<Version>.zip.

    No administrative rights are required: this script only builds and packages. Installation is done
    on the target machine with deploy\Install-ArkimentumAppMonitor.ps1 (which does require admin).

.PARAMETER Configuration
    MSBuild configuration. Default: Release.

.PARAMETER SelfContained
    Publish self-contained, so target machines need no .NET runtime. Default: $true.
    Use -SelfContained:$false for a framework-dependent build (every target machine then needs the
    .NET 10 Desktop Runtime x64).

.PARAMETER Version
    Version stamped into the assemblies (/p:Version) and used in the zip file name. Default: 1.0.0.

.PARAMETER OutputRoot
    Publish/staging root. Default: <repo>\artifacts\publish.

.PARAMETER SkipTests
    Skip the dotnet test run.

.PARAMETER EmbedPdb
    Pass /p:DebugType=embedded so symbols live inside the assemblies and no .pdb files are shipped.
    Default: $true. Use -EmbedPdb:$false to keep separate .pdb files.

.PARAMETER SkipZip
    Stage the payload in OutputRoot but do not create the zip.

.EXAMPLE
    .\deploy\Build-Release.ps1 -Version 1.2.0

.EXAMPLE
    .\deploy\Build-Release.ps1 -SelfContained:$false -SkipTests

.NOTES
    Code signing (not implemented - a documented follow-up).
    Production releases should be Authenticode-signed, and it matters more now that the agent updates
    itself: a device downloads this package over the internet and runs Install-ArkimentumAppMonitor.ps1
    out of it as SYSTEM. The SHA-256 in the release manifest proves the package is the one the manifest
    describes; a signature proves who built it.

    This script is the one place to add it. A -SignCertificateThumbprint parameter would sign, after
    the publish step and before the zip is created (so the hash in manifest.json covers the signed
    files):
        <OutputRoot>\Service\Arkimentum.AppMonitor.Service.exe
        <OutputRoot>\Tray\Arkimentum.AppMonitor.Tray.exe
        <OutputRoot>\Admin\Arkimentum.AppMonitor.Admin.exe
        <OutputRoot>\Install-ArkimentumAppMonitor.ps1     (and the other shipped .ps1 files)
    with Set-AuthenticodeSignature (or signtool) plus an RFC 3161 timestamp, and then verify with
    Get-AuthenticodeSignature before packaging.

    Until it exists, sign in your own release pipeline between -SkipZip and packaging, and consider a
    WDAC/AppLocker publisher rule so an unsigned look-alike cannot take the agent's place. See
    docs\Cloud.md ("Code signing") and docs\SelfUpdate.md.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',

    [switch]$SelfContained = $true,

    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',

    [string]$OutputRoot,

    [switch]$SkipTests,

    [switch]$EmbedPdb = $true,

    [switch]$SkipZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Runtime = 'win-x64'
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

function Stop-WithError {
    param([Parameter(Mandatory)][string]$Message)
    throw $Message
}

# --------------------------------------------------------------------------------- .NET SDK

function Resolve-DotnetPath {
    # Preference order: dotnet on PATH, %LOCALAPPDATA%\Microsoft\dotnet, %ProgramFiles%\dotnet.
    # A candidate is only accepted when it really has an SDK - a runtime-only install cannot build.
    $candidates = [System.Collections.Generic.List[string]]::new()

    $onPath = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue |
              Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Source) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (-not $seen.Add($candidate)) { continue }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return (Resolve-Path -LiteralPath $candidate).Path }
        Write-Warning "Ignoring '$candidate': it reports no .NET SDK (runtime-only install)."
    }

    Stop-WithError ('No .NET SDK found. Looked for dotnet on PATH, in %LOCALAPPDATA%\Microsoft\dotnet ' +
                    'and in %ProgramFiles%\dotnet. Install the .NET 10 SDK (x64) and try again.')
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage
    )
    Write-Info ("dotnet {0}" -f ($Arguments -join ' '))
    & $script:Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError ("{0} (dotnet exit code {1})" -f $FailureMessage, $LASTEXITCODE)
    }
}

# --------------------------------------------------------------------------------- paths

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DeployDir = $PSScriptRoot
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
if (-not $PSBoundParameters.ContainsKey('OutputRoot') -or [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ArtifactsDir 'publish'
}
if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot $OutputRoot
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$ServiceProject = Join-Path $RepoRoot 'src\Arkimentum.AppMonitor.Service\Arkimentum.AppMonitor.Service.csproj'
$TrayProject    = Join-Path $RepoRoot 'src\Arkimentum.AppMonitor.Tray\Arkimentum.AppMonitor.Tray.csproj'
$AdminProject   = Join-Path $RepoRoot 'src\Arkimentum.AppMonitor.Admin\Arkimentum.AppMonitor.Admin.csproj'
$TestProject    = Join-Path $RepoRoot 'src\Arkimentum.AppMonitor.Tests\Arkimentum.AppMonitor.Tests.csproj'

foreach ($project in @($ServiceProject, $TrayProject, $AdminProject, $TestProject)) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        Stop-WithError "Project not found: $project. Run this script from the repository (deploy\Build-Release.ps1)."
    }
}

$ServiceOut = Join-Path $OutputRoot 'Service'
$TrayOut    = Join-Path $OutputRoot 'Tray'
$AdminOut   = Join-Path $OutputRoot 'Admin'

Write-Host 'Arkimentum AppMonitor - release build' -ForegroundColor Green
Write-Host "  Repository    : $RepoRoot"
Write-Host "  Configuration : $Configuration"
Write-Host "  Version       : $Version"
Write-Host ("  Runtime       : {0} (self-contained: {1})" -f $script:Runtime, [bool]$SelfContained)
Write-Host "  Output root   : $OutputRoot"

Write-Step 'Locating the .NET SDK'
$script:Dotnet = Resolve-DotnetPath
$dotnetRoot = Split-Path -Parent $script:Dotnet
$env:DOTNET_ROOT = $dotnetRoot
if (($env:PATH -split ';') -notcontains $dotnetRoot) { $env:PATH = "$dotnetRoot;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Info "dotnet: $script:Dotnet"
Write-Info ("SDK(s): {0}" -f ((& $script:Dotnet --list-sdks) -join '; '))

# --------------------------------------------------------------------------------- clean

Write-Step 'Preparing the output folder'
if (Test-Path -LiteralPath $OutputRoot) {
    if ($OutputRoot.TrimEnd('\').Length -le 3) { Stop-WithError "Refusing to clean '$OutputRoot'." }
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Write-Info "Clean: $OutputRoot"

# --------------------------------------------------------------------------------- publish

$commonArgs = @(
    '-c', $Configuration
    '-r', $script:Runtime
    '--self-contained', (([bool]$SelfContained).ToString().ToLowerInvariant())
    '--nologo'
    "/p:Version=$Version"
    '/p:PublishReadyToRun=false'
    '/p:PublishSingleFile=false'
)
if ($EmbedPdb) { $commonArgs += '/p:DebugType=embedded' }

Write-Step 'Publishing Arkimentum.AppMonitor.Service'
Invoke-Dotnet -Arguments (@('publish', $ServiceProject) + $commonArgs + @('-o', $ServiceOut)) `
              -FailureMessage 'Publishing the service failed.'

Write-Step 'Publishing Arkimentum.AppMonitor.Tray'
Invoke-Dotnet -Arguments (@('publish', $TrayProject) + $commonArgs + @('-o', $TrayOut)) `
              -FailureMessage 'Publishing the tray agent failed.'

Write-Step 'Publishing Arkimentum.AppMonitor.Admin'
Invoke-Dotnet -Arguments (@('publish', $AdminProject) + $commonArgs + @('-o', $AdminOut)) `
              -FailureMessage 'Publishing the admin console failed.'

Write-Step 'Verifying the publish output'
$expected = [ordered]@{
    'Service executable' = Join-Path $ServiceOut 'Arkimentum.AppMonitor.Service.exe'
    'Tray executable'    = Join-Path $TrayOut    'Arkimentum.AppMonitor.Tray.exe'
    'Admin executable'   = Join-Path $AdminOut   'Arkimentum.AppMonitor.Admin.exe'
    'App catalog'        = Join-Path $ServiceOut 'catalog.json'
}
foreach ($item in $expected.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $item.Value -PathType Leaf)) {
        Stop-WithError ("{0} missing after publish: {1}" -f $item.Key, $item.Value)
    }
    Write-Info ("OK: {0}" -f $item.Value)
}

# --------------------------------------------------------------------------------- tests

if ($SkipTests) {
    Write-Step 'Tests skipped (-SkipTests)'
} else {
    Write-Step 'Running unit tests'
    Invoke-Dotnet -Arguments @('test', $TestProject, '-c', $Configuration, '--nologo', "/p:Version=$Version") `
                  -FailureMessage 'Unit tests failed.'
}

# --------------------------------------------------------------------------------- payload

Write-Step 'Copying deployment scripts and documentation'
$payloadFiles = @(
    (Join-Path $DeployDir 'Install-ArkimentumAppMonitor.ps1'),
    (Join-Path $DeployDir 'Uninstall-ArkimentumAppMonitor.ps1'),
    (Join-Path $DeployDir 'Set-SampleConfiguration.ps1'),
    (Join-Path $DeployDir 'Sample-Configuration.reg'),
    (Join-Path $RepoRoot  'README.md')
)
foreach ($file in $payloadFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        Stop-WithError "Required payload file missing: $file"
    }
    Copy-Item -LiteralPath $file -Destination $OutputRoot -Force
    Write-Info ("Copied: {0}" -f (Split-Path -Leaf $file))
}

$policySource = Join-Path $DeployDir 'policy'
if (-not (Test-Path -LiteralPath $policySource -PathType Container)) {
    Stop-WithError "Policy folder missing: $policySource"
}
Copy-Item -LiteralPath $policySource -Destination (Join-Path $OutputRoot 'policy') -Recurse -Force
Write-Info 'Copied: policy\ (ADMX/ADML)'

$docsSource = Join-Path $RepoRoot 'docs'
if (Test-Path -LiteralPath $docsSource -PathType Container) {
    Copy-Item -LiteralPath $docsSource -Destination (Join-Path $OutputRoot 'docs') -Recurse -Force
    Write-Info 'Copied: docs\'
} else {
    Write-Warning "docs\ folder not found at $docsSource; the package will ship without it."
}

# --------------------------------------------------------------------------------- package

$zipPath = Join-Path $ArtifactsDir ("Arkimentum.AppMonitor-{0}.zip" -f $Version)
if ($SkipZip) {
    Write-Step 'Packaging skipped (-SkipZip)'
} else {
    Write-Step 'Creating the release package'
    New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Info ("Package: {0} ({1:N1} MB)" -f $zipPath, ((Get-Item -LiteralPath $zipPath).Length / 1MB))
}

Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
Write-Host "  Staged payload : $OutputRoot"
if (-not $SkipZip) { Write-Host "  Package        : $zipPath" }
Write-Host ''
Write-Host 'Next steps (on the target machine, elevated):'
Write-Host '  1. Expand the zip.'
Write-Host '  2. .\Install-ArkimentumAppMonitor.ps1'
Write-Host '  3. Configure: Start Menu -> Arkimentum -> "Arkimentum AppMonitor Admin" (the admin console),'
Write-Host '     or .\Set-SampleConfiguration.ps1 for a scripted example configuration.'
