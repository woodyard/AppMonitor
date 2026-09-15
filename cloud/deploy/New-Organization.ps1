#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a customer organization in the Arkimentum AppMonitor cloud and prints the three registry values its
    agents need.

.DESCRIPTION
    Calls POST /api/v1/admin/organizations with an access token acquired through the signed-in Azure CLI. The caller
    must hold the AppMonitor.GlobalAdmin app role in the operator tenant; Deploy-Cloud.ps1 assigns it to whoever ran
    the deployment.

    The enrollment key is returned once and never again - store it in your secret store. If it leaks, rotate it with
    Rotate-EnrollmentKey.ps1: enrolled devices keep working (they hold their own per-device key), only new
    enrollments need the new value.

.PARAMETER ServerUrl
    Base URL of the API, e.g. https://appmon-prod-func-ab12cd.azurewebsites.net.

.PARAMETER ApiClientId
    Application (client) id of the API app registration; the token is requested for api://{ApiClientId}.

.PARAMETER Name
    Display name of the organization.

.PARAMETER EntraTenantId
    The customer's Entra tenant id. Their administrators reach this organization - and only this one - once they
    hold the AppMonitor.Admin app role. Optional, but the customer cannot use the admin console without it.

.PARAMETER AsRegFile
    Also writes a .reg file with the three values, ready for import or for an Intune platform script.

.EXAMPLE
    .\New-Organization.ps1 -ServerUrl https://appmon-prod-func-ab12cd.azurewebsites.net `
                           -ApiClientId 00000000-0000-0000-0000-000000000000 `
                           -Name "Contoso A/S" -EntraTenantId 22222222-2222-2222-2222-222222222222
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ServerUrl,
    [Parameter(Mandatory = $true)][string] $ApiClientId,
    [Parameter(Mandatory = $true)][string] $Name,
    [string] $EntraTenantId = '',
    [string] $AsRegFile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'The Azure CLI (az) is required.' }

$base = $ServerUrl.TrimEnd('/')

$tokenJson = & az account get-access-token --resource ("api://" + $ApiClientId) -o json 2>&1
if ($LASTEXITCODE -ne 0) {
    throw ("Could not get an access token for api://{0}:`n{1}`n" -f $ApiClientId, ($tokenJson -join "`n")) +
          'Sign in with "az login --tenant <operator tenant>". The Azure CLI must be pre-authorised for the AppMonitor.Access scope (Deploy-Cloud.ps1 does that).'
}
$accessToken = ($tokenJson | ConvertFrom-Json).accessToken

$headers = @{ Authorization = "Bearer $accessToken"; 'Content-Type' = 'application/json' }
$body = @{ name = $Name }
if (-not [string]::IsNullOrWhiteSpace($EntraTenantId)) { $body['entraTenantId'] = $EntraTenantId }

try {
    $created = Invoke-RestMethod -Uri ($base + '/api/v1/admin/organizations') -Method Post -Headers $headers `
        -Body ($body | ConvertTo-Json) -TimeoutSec 60
}
catch {
    $response = $_.Exception.Response
    if ($response) {
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        $text = $reader.ReadToEnd()
        throw ("The API refused the request ({0}):`n{1}" -f [int]$response.StatusCode, $text)
    }
    throw
}

$organizationId = $created.organization.organizationId
$enrollmentKey  = $created.enrollment.enrollmentKey

Write-Host ''
Write-Host ('Created organization "{0}"' -f $created.organization.name) -ForegroundColor Green
Write-Host ''
Write-Host 'Registry values for the agent (HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor):' -ForegroundColor Green
Write-Host ("  CloudServerUrl      = {0}" -f $base)
Write-Host ("  CloudOrganizationId = {0}" -f $organizationId)
Write-Host ("  CloudEnrollmentKey  = {0}" -f $enrollmentKey)
Write-Host ''
Write-Host 'The enrollment key is shown once only. Store it now.' -ForegroundColor Yellow

if (-not [string]::IsNullOrWhiteSpace($EntraTenantId)) {
    Write-Host ''
    Write-Host 'Send the customer''s Global Administrator these consent URLs, then assign AppMonitor.Admin to their IT staff:' -ForegroundColor Yellow
    Write-Host ("  https://login.microsoftonline.com/{0}/adminconsent?client_id={1}" -f $EntraTenantId, $ApiClientId)
}

if (-not [string]::IsNullOrWhiteSpace($AsRegFile)) {
    $lines = @(
        'Windows Registry Editor Version 5.00',
        '',
        '[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Arkimentum\AppMonitor]',
        ('"CloudServerUrl"="{0}"' -f $base.Replace('\', '\\')),
        ('"CloudOrganizationId"="{0}"' -f $organizationId),
        ('"CloudEnrollmentKey"="{0}"' -f $enrollmentKey),
        ''
    )
    Set-Content -Path $AsRegFile -Value $lines -Encoding Unicode
    Write-Host ''
    Write-Host ("Wrote {0} - it contains the enrollment key, so treat it as a secret." -f $AsRegFile) -ForegroundColor Yellow
}
