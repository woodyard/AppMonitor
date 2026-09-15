#Requires -Version 5.1
<#
.SYNOPSIS
    Rotates an organization's enrollment key and prints the new one.

.DESCRIPTION
    Calls POST /api/v1/admin/organizations/{id}/enrollment/rotate with an access token from the signed-in Azure CLI.
    The caller must hold AppMonitor.Admin for the organization (or AppMonitor.GlobalAdmin in the operator tenant).

    Rotation invalidates the old key immediately. Devices that are already enrolled are unaffected - they
    authenticate with their own per-device key - but any machine that has not enrolled yet needs the new value in
    CloudEnrollmentKey before it can join.

    Rotate when: the key has been in a script, a ticket or a shared drive; an employee with access has left; or a
    device image containing the key has escaped the organization.

.PARAMETER ServerUrl
    Base URL of the API.

.PARAMETER ApiClientId
    Application (client) id of the API app registration.

.PARAMETER OrganizationId
    The organization to rotate.

.EXAMPLE
    .\Rotate-EnrollmentKey.ps1 -ServerUrl https://appmon-prod-func-ab12cd.azurewebsites.net `
                               -ApiClientId 00000000-0000-0000-0000-000000000000 `
                               -OrganizationId 7b6b2b1e-1f0e-4a1a-8b1e-0f0e1a2b3c4d
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string] $ServerUrl,
    [Parameter(Mandatory = $true)][string] $ApiClientId,
    [Parameter(Mandatory = $true)][string] $OrganizationId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'The Azure CLI (az) is required.' }

$base = $ServerUrl.TrimEnd('/')

if (-not $PSCmdlet.ShouldProcess($OrganizationId, 'Rotate the enrollment key (the current key stops working immediately)')) {
    return
}

$tokenJson = & az account get-access-token --resource ("api://" + $ApiClientId) -o json 2>&1
if ($LASTEXITCODE -ne 0) {
    throw ("Could not get an access token for api://{0}:`n{1}" -f $ApiClientId, ($tokenJson -join "`n"))
}
$accessToken = ($tokenJson | ConvertFrom-Json).accessToken
$headers = @{ Authorization = "Bearer $accessToken"; 'Content-Type' = 'application/json' }

try {
    $rotated = Invoke-RestMethod -Method Post -TimeoutSec 60 -Headers $headers `
        -Uri ("{0}/api/v1/admin/organizations/{1}/enrollment/rotate" -f $base, $OrganizationId)
}
catch {
    $response = $_.Exception.Response
    if ($response) {
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        throw ("The API refused the request ({0}):`n{1}" -f [int]$response.StatusCode, $reader.ReadToEnd())
    }
    throw
}

Write-Host ''
Write-Host ('Rotated the enrollment key for organization {0}' -f $rotated.organizationId) -ForegroundColor Green
Write-Host ("  Rotated at         : {0}" -f $rotated.keyRotatedUtc)
Write-Host ("  CloudServerUrl     : {0}" -f $rotated.serverUrl)
Write-Host ("  CloudEnrollmentKey : {0}" -f $rotated.enrollmentKey)
Write-Host ''
Write-Host 'Shown once. Update the policy/Intune assignment that carries CloudEnrollmentKey.' -ForegroundColor Yellow
Write-Host 'Devices that have already enrolled keep working - they use their own device key.' -ForegroundColor DarkGray
