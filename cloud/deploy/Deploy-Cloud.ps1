#Requires -Version 5.1
<#
.SYNOPSIS
    Deploys the Arkimentum AppMonitor cloud backend: Entra ID app registrations, Azure resources, database schema,
    the Functions package and the first organization.

.DESCRIPTION
    The script is idempotent - run it again to update an existing environment. It does the following, in order:

      1. Checks the prerequisites (az CLI, .NET SDK) and selects the subscription.
      2. Creates the resource group.
      3. Creates or updates two Entra ID app registrations in the operator tenant:
           - the API: multi-tenant, identifierUri api://{appId}, delegated scope AppMonitor.Access,
             app roles AppMonitor.Admin and AppMonitor.GlobalAdmin, access token version 2;
           - the admin console: multi-tenant public client, redirect http://localhost, pre-authorised for the scope.
         The Azure CLI is pre-authorised for the same scope so New-Organization.ps1 and Rotate-EnrollmentKey.ps1
         can call the admin API with `az account get-access-token`.
      4. Assigns AppMonitor.GlobalAdmin to the signed-in operator.
      5. Deploys cloud/infra/main.bicep.
      6. Creates the Function App's managed identity as a database user (db_datareader + db_datawriter).
      7. Applies the EF Core migrations (see -MigrationMode).
      8. Publishes the Functions project and zip-deploys it.
      9. Creates the first organization and prints the three registry values the agent needs.

    Nothing is deployed from a machine without Azure credentials: every step goes through the signed-in az CLI.

.PARAMETER SubscriptionId
    Azure subscription to deploy into.

.PARAMETER ResourceGroup
    Resource group name. Created when it does not exist.

.PARAMETER Location
    Azure region, e.g. westeurope.

.PARAMETER Environment
    Environment moniker used in every resource name: dev, test, prod.

.PARAMETER OperatorTenantId
    Arkimentum's own Entra tenant. AppMonitor.GlobalAdmin is only honoured for tokens issued by this tenant.

.PARAMETER OrganizationName
    Name of the first customer organization to create. Skipped when empty.

.PARAMETER CustomerTenantId
    Entra tenant id of that first organization. Optional; can be set later with New-Organization.ps1.

.PARAMETER MigrationMode
    Auto     - run `dotnet ef database update` from this machine (needs the .NET SDK and line-of-sight to Azure SQL).
    Bundle   - build a self-contained efbundle.exe and leave it in the output folder for an operator to run.
    Skip     - do nothing; apply the schema yourself.

.PARAMETER SkipAppRegistrations
    Reuse existing app registrations without patching them.

.PARAMETER SkipPublish
    Do not build or deploy the Functions package.

.PARAMETER UseFlexConsumption
    Deploy on a Flex Consumption (FC1, Linux) plan. Use -UseFlexConsumption:$false for classic Consumption (Y1).

.PARAMETER WhatIfDeployment
    Run the Bicep deployment in what-if mode and stop.

.EXAMPLE
    .\Deploy-Cloud.ps1 -SubscriptionId 00000000-... -ResourceGroup appmon-prod-rg -Location westeurope `
                       -Environment prod -OperatorTenantId 11111111-... -OrganizationName "Contoso A/S"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $SubscriptionId,
    [Parameter(Mandatory = $true)][string] $ResourceGroup,
    [Parameter(Mandatory = $true)][string] $Location,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9]{2,8}$')][string] $Environment,
    [Parameter(Mandatory = $true)][string] $OperatorTenantId,

    [string] $OrganizationName = '',
    [string] $CustomerTenantId = '',

    [ValidateSet('Auto', 'Bundle', 'Skip')]
    [string] $MigrationMode = 'Auto',

    [string] $SqlAdminObjectId = '',
    [string] $SqlAdminLogin = '',
    [ValidateSet('User', 'Group', 'Application')][string] $SqlAdminPrincipalType = 'User',

    [switch] $SkipAppRegistrations,
    [switch] $SkipPublish,
    [bool]   $UseFlexConsumption = $true,
    [switch] $WhatIfDeployment
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Stable identifiers for the scope and the two app roles. They must never change once a tenant has consented.
$ScopeId          = 'a1f0c2e4-8b3d-4f7a-9c2e-5d6b7a8c9e01'
$RoleAdminId      = 'b2e1d3f5-9c4e-4a8b-8d3f-6e7c8b9d0f12'
$RoleGlobalAdminId= 'c3d2e4a6-0d5f-4b9c-9e4a-7f8d9c0e1a23'
$AzureCliClientId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'   # well-known Microsoft Azure CLI client

$CloudRoot  = Split-Path -Parent $PSScriptRoot                                              # <repo>\cloud
$ApiProject = Join-Path $CloudRoot 'api\Arkimentum.AppMonitor.Api\Arkimentum.AppMonitor.Api.csproj'
$BicepFile  = Join-Path $CloudRoot 'infra\main.bicep'
$OutputRoot = Join-Path (Split-Path -Parent $CloudRoot) ('artifacts\cloud\' + $Environment)   # <repo>\artifacts\cloud\{env}

# ---------------------------------------------------------------------------------------------------- helpers

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host ('==> ' + $Message) -ForegroundColor Cyan
}

function Write-Detail([string] $Message) {
    Write-Host ('    ' + $Message) -ForegroundColor DarkGray
}

function Invoke-Az {
    <# Runs az and returns the parsed JSON. Throws with the raw output when az fails. #>
    param([Parameter(Mandatory = $true)][string[]] $Arguments, [switch] $AllowFailure)

    # Windows PowerShell 5.1 turns anything a native command writes to stderr into a terminating error when the
    # stream is merged and $ErrorActionPreference is Stop - and az writes its WARNING lines to stderr. Relax it
    # for the call itself; the exit code decides success.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $raw = & az @Arguments 2>&1 }
    finally { $ErrorActionPreference = $previousPreference }
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        if ($AllowFailure) { return $null }
        throw ("az " + ($Arguments -join ' ') + " failed with exit code $exit`n" + ($raw -join "`n"))
    }
    $text = ($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $text }
}

function Write-TempJson([object] $Object) {
    $path = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ([System.Guid]::NewGuid().ToString() + '.json'))
    $json = $Object | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

function Invoke-Graph {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('GET', 'POST', 'PATCH', 'DELETE')][string] $Method,
        [Parameter(Mandatory = $true)][string] $Uri,
        [object] $Body,
        [switch] $AllowFailure
    )

    $arguments = @('rest', '--method', $Method, '--uri', $Uri, '--headers', 'Content-Type=application/json')
    $file = $null
    if ($null -ne $Body) {
        $file = Write-TempJson $Body
        $arguments += @('--body', ('@' + $file))
    }
    try {
        return Invoke-Az -Arguments $arguments -AllowFailure:$AllowFailure
    }
    finally {
        if ($file -and (Test-Path $file)) { Remove-Item $file -Force }
    }
}

function Test-Command([string] $Name) {
    return [bool] (Get-Command $Name -ErrorAction SilentlyContinue)
}

# ---------------------------------------------------------------------------------------------------- 1. preflight

Write-Step 'Checking prerequisites'

if (-not (Test-Command 'az')) { throw 'The Azure CLI (az) is required. See https://aka.ms/azure-cli.' }
if (-not $SkipPublish -and -not (Test-Command 'dotnet')) {
    throw 'The .NET SDK is required to publish the Functions project. See BUILD-ENV.md, or pass -SkipPublish.'
}

$account = Invoke-Az -Arguments @('account', 'show', '-o', 'json') -AllowFailure
if ($null -eq $account) { throw 'Not signed in. Run "az login --tenant <operator tenant>" first.' }

if ($account.tenantId -ne $OperatorTenantId) {
    Write-Warning ("The signed-in tenant ({0}) is not the operator tenant ({1}). The app registrations are created " +
        "in the signed-in tenant - sign in to the operator tenant unless that is what you want." -f $account.tenantId, $OperatorTenantId)
}

Invoke-Az -Arguments @('account', 'set', '--subscription', $SubscriptionId) | Out-Null
Write-Detail ("Subscription: {0}" -f $SubscriptionId)

$signedInUser = Invoke-Az -Arguments @('ad', 'signed-in-user', 'show', '-o', 'json') -AllowFailure
if ($null -eq $signedInUser) { throw 'Could not read the signed-in user from Microsoft Graph. Sign in as a user, not a service principal.' }
Write-Detail ("Operator:     {0}" -f $signedInUser.userPrincipalName)

if ([string]::IsNullOrWhiteSpace($SqlAdminObjectId)) {
    $SqlAdminObjectId = $signedInUser.id
    $SqlAdminLogin = $signedInUser.userPrincipalName
    $SqlAdminPrincipalType = 'User'
}
Write-Detail ("SQL admin:    {0} ({1})" -f $SqlAdminLogin, $SqlAdminPrincipalType)

# ---------------------------------------------------------------------------------------------- 2. resource group

Write-Step ("Ensuring resource group {0} in {1}" -f $ResourceGroup, $Location)
Invoke-Az -Arguments @('group', 'create', '-n', $ResourceGroup, '-l', $Location, '-o', 'json') | Out-Null

# ------------------------------------------------------------------------------------------ 3. app registrations

function Get-AppByDisplayName([string] $DisplayName) {
    $apps = Invoke-Graph -Method GET -Uri ("https://graph.microsoft.com/v1.0/applications?`$filter=displayName eq '{0}'" -f $DisplayName)
    if ($null -ne $apps -and $apps.value.Count -gt 0) { return $apps.value[0] }
    return $null
}

function New-OrGetApp([string] $DisplayName) {
    $existing = Get-AppByDisplayName $DisplayName
    if ($null -ne $existing) {
        Write-Detail ("Reusing '{0}' (appId {1})" -f $DisplayName, $existing.appId)
        return $existing
    }
    Write-Detail ("Creating '{0}'" -f $DisplayName)
    return Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/applications' -Body @{
        displayName    = $DisplayName
        signInAudience = 'AzureADMultipleOrgs'
    }
}

function Ensure-ServicePrincipal([string] $AppId) {
    $existing = Invoke-Graph -Method GET -Uri ("https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '{0}'" -f $AppId)
    if ($null -ne $existing -and $existing.value.Count -gt 0) { return $existing.value[0] }
    return Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body @{ appId = $AppId }
}

$apiAppName   = "Arkimentum AppMonitor API ($Environment)"
$adminAppName = "Arkimentum AppMonitor Admin Console ($Environment)"

Write-Step 'Ensuring the Entra ID app registrations'

$apiApp   = New-OrGetApp $apiAppName
$adminApp = New-OrGetApp $adminAppName
$apiClientId   = $apiApp.appId
$adminClientId = $adminApp.appId

if (-not $SkipAppRegistrations) {
    # Two PATCH calls, not one: Graph validates api.preAuthorizedApplications against the scopes the application
    # already has, so the scope must be stored before anything may be pre-authorised for it.
    Write-Detail 'Patching the API application (scope, app roles, token version)'
    Invoke-Graph -Method PATCH -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $apiApp.id) -Body @{
        signInAudience = 'AzureADMultipleOrgs'
        identifierUris = @("api://$apiClientId")
        api = @{
            requestedAccessTokenVersion = 2
            oauth2PermissionScopes = @(
                @{
                    id    = $ScopeId
                    value = 'AppMonitor.Access'
                    type  = 'User'
                    isEnabled = $true
                    adminConsentDisplayName = 'Manage Arkimentum AppMonitor'
                    adminConsentDescription = 'Allows the signed-in administrator to manage the organization''s AppMonitor configuration, devices and inventory.'
                    userConsentDisplayName  = 'Manage Arkimentum AppMonitor'
                    userConsentDescription  = 'Allows you to manage your organization''s AppMonitor configuration, devices and inventory.'
                }
            )
        }
        appRoles = @(
            @{
                id = $RoleAdminId
                value = 'AppMonitor.Admin'
                displayName = 'AppMonitor Administrator'
                description = 'May manage the AppMonitor organization mapped to the caller''s Entra tenant.'
                allowedMemberTypes = @('User')
                isEnabled = $true
            }
            @{
                id = $RoleGlobalAdminId
                value = 'AppMonitor.GlobalAdmin'
                displayName = 'AppMonitor Global Administrator'
                description = 'Arkimentum staff: may manage every organization. Only honoured for tokens from the operator tenant.'
                allowedMemberTypes = @('User')
                isEnabled = $true
            }
        )
        web = @{ redirectUris = @() }
    } | Out-Null

    Write-Detail 'Pre-authorising the admin console and the Azure CLI for the AppMonitor.Access scope'
    Invoke-Graph -Method PATCH -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $apiApp.id) -Body @{
        api = @{
            preAuthorizedApplications = @(
                @{ appId = $adminClientId;   delegatedPermissionIds = @($ScopeId) }
                @{ appId = $AzureCliClientId; delegatedPermissionIds = @($ScopeId) }
            )
        }
    } | Out-Null

    Write-Detail 'Patching the admin console application (public client)'
    Invoke-Graph -Method PATCH -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $adminApp.id) -Body @{
        signInAudience = 'AzureADMultipleOrgs'
        isFallbackPublicClient = $true
        publicClient = @{ redirectUris = @('http://localhost') }
        requiredResourceAccess = @(
            @{
                resourceAppId = $apiClientId
                resourceAccess = @(@{ id = $ScopeId; type = 'Scope' })
            }
        )
    } | Out-Null
}

$apiSp   = Ensure-ServicePrincipal $apiClientId
$adminSp = Ensure-ServicePrincipal $adminClientId
Write-Detail ("API app:           {0}" -f $apiClientId)
Write-Detail ("Admin console app: {0}" -f $adminClientId)

# ------------------------------------------------------------------------------------- 4. grant the operator role

Write-Step 'Assigning AppMonitor.GlobalAdmin to the operator'

$assignments = Invoke-Graph -Method GET -Uri ("https://graph.microsoft.com/v1.0/servicePrincipals/{0}/appRoleAssignedTo" -f $apiSp.id)
$already = $false
if ($null -ne $assignments) {
    foreach ($assignment in $assignments.value) {
        if ($assignment.principalId -eq $signedInUser.id -and $assignment.appRoleId -eq $RoleGlobalAdminId) { $already = $true }
    }
}
if ($already) {
    Write-Detail 'Already assigned.'
}
else {
    Invoke-Graph -Method POST -Uri ("https://graph.microsoft.com/v1.0/servicePrincipals/{0}/appRoleAssignedTo" -f $apiSp.id) -Body @{
        principalId = $signedInUser.id
        resourceId  = $apiSp.id
        appRoleId   = $RoleGlobalAdminId
    } | Out-Null
    Write-Detail ("Assigned to {0}." -f $signedInUser.userPrincipalName)
}

# ------------------------------------------------------------------------------------------------ 5. deploy Bicep

Write-Step 'Deploying the Azure resources'

$deploymentName = "appmon-$Environment-" + (Get-Date -Format 'yyyyMMddHHmmss')
$bicepParameters = @(
    "environmentName=$Environment",
    "location=$Location",
    "apiClientId=$apiClientId",
    "apiAudience=api://$apiClientId",
    "adminClientId=$adminClientId",
    "operatorTenantId=$OperatorTenantId",
    "sqlAdminObjectId=$SqlAdminObjectId",
    "sqlAdminLogin=$SqlAdminLogin",
    "sqlAdminPrincipalType=$SqlAdminPrincipalType",
    ("useFlexConsumption=" + $UseFlexConsumption.ToString().ToLowerInvariant())
)

if ($WhatIfDeployment) {
    & az deployment group what-if -g $ResourceGroup -f $BicepFile -p $bicepParameters
    Write-Host 'What-if only; stopping here.' -ForegroundColor Yellow
    return
}

$deployment = Invoke-Az -Arguments (@('deployment', 'group', 'create', '-g', $ResourceGroup, '-n', $deploymentName,
    '-f', $BicepFile, '-o', 'json', '-p') + $bicepParameters)

$outputs = $deployment.properties.outputs
$functionAppName = $outputs.functionAppName.value
$serverUrl       = $outputs.serverUrl.value
$sqlServerFqdn   = $outputs.sqlServerFqdn.value
$sqlDatabaseName = $outputs.sqlDatabaseName.value
$sqlConnection   = $outputs.sqlConnectionString.value

Write-Detail ("Function App: {0}" -f $functionAppName)
Write-Detail ("Plan:         {0}" -f $outputs.hostingPlanKind.value)
Write-Detail ("SQL:          {0} / {1}" -f $sqlServerFqdn, $sqlDatabaseName)
Write-Detail ("Server URL:   {0}" -f $serverUrl)

# --------------------------------------------------------------------- 6. the managed identity as a database user

Write-Step 'Granting the Function App managed identity access to the database'

$grantSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$functionAppName')
    CREATE USER [$functionAppName] FROM EXTERNAL PROVIDER;
IF IS_ROLEMEMBER('db_datareader', N'$functionAppName') = 0 ALTER ROLE db_datareader ADD MEMBER [$functionAppName];
IF IS_ROLEMEMBER('db_datawriter', N'$functionAppName') = 0 ALTER ROLE db_datawriter ADD MEMBER [$functionAppName];
"@

$sqlScriptPath = Join-Path $OutputRoot 'grant-managed-identity.sql'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Set-Content -Path $sqlScriptPath -Value $grantSql -Encoding UTF8

function Invoke-AzureSql([string] $ServerFqdn, [string] $Database, [string] $Sql) {
    <#
        Runs T-SQL against Azure SQL as the signed-in operator. Three ways, in order of preference:
          1. Invoke-Sqlcmd from the SqlServer module (works in Windows PowerShell 5.1 and pwsh 7);
          2. System.Data.SqlClient with an access token (Windows PowerShell 5.1 - the type is in the GAC);
          3. give up and tell the operator to run the generated script.
    #>
    $token = (Invoke-Az -Arguments @('account', 'get-access-token', '--resource', 'https://database.windows.net/', '-o', 'json')).accessToken

    if (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) {
        Invoke-Sqlcmd -ServerInstance $ServerFqdn -Database $Database -AccessToken $token -Query $Sql -ErrorAction Stop
        return $true
    }

    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection
        $connection.ConnectionString = "Server=tcp:$ServerFqdn,1433;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
        $connection.AccessToken = $token
        $connection.Open()
        try {
            $command = $connection.CreateCommand()
            $command.CommandText = $Sql
            $command.CommandTimeout = 120
            $command.ExecuteNonQuery() | Out-Null
        }
        finally { $connection.Close() }
        return $true
    }
    catch {
        Write-Warning ("Could not run the SQL directly: {0}" -f $_.Exception.Message)
        return $false
    }
}

if (Invoke-AzureSql -ServerFqdn $sqlServerFqdn -Database $sqlDatabaseName -Sql $grantSql) {
    Write-Detail ("{0} is now db_datareader + db_datawriter." -f $functionAppName)
}
else {
    Write-Warning ("Run {0} against {1}/{2} yourself (Azure Portal query editor, or sqlcmd -G)." -f $sqlScriptPath, $sqlServerFqdn, $sqlDatabaseName)
}

# ------------------------------------------------------------------------------------------------ 7. EF migrations

Write-Step ("Applying the database schema (mode: {0})" -f $MigrationMode)

if ($MigrationMode -eq 'Skip') {
    Write-Detail 'Skipped by request.'
}
elseif ($MigrationMode -eq 'Bundle') {
    # A migration bundle is a self-contained executable an operator can run from a machine that has no .NET SDK.
    $bundlePath = Join-Path $OutputRoot 'efbundle.exe'
    Push-Location (Split-Path -Parent $ApiProject)
    try {
        & dotnet tool restore
        & dotnet dotnet-ef migrations bundle --self-contained -r win-x64 --force -o $bundlePath --project $ApiProject
        if ($LASTEXITCODE -ne 0) { throw 'dotnet ef migrations bundle failed.' }
    }
    finally { Pop-Location }
    Write-Host ''
    Write-Host 'Run the bundle from a machine that is signed in to Azure:' -ForegroundColor Yellow
    Write-Host ("  {0} --connection `"{1}`"" -f $bundlePath, $sqlConnection) -ForegroundColor Yellow
}
else {
    # "Active Directory Default" makes Microsoft.Data.SqlClient use DefaultAzureCredential, which picks up az login.
    Push-Location (Split-Path -Parent $ApiProject)
    try {
        & dotnet tool restore
        & dotnet dotnet-ef database update --project $ApiProject --connection $sqlConnection
        if ($LASTEXITCODE -ne 0) {
            throw ("dotnet ef database update failed. Re-run with -MigrationMode Bundle, or apply the schema by hand. " +
                   "Check that your IP address is allowed on the SQL server firewall.")
        }
    }
    finally { Pop-Location }
    Write-Detail 'Schema is up to date.'
}

# ------------------------------------------------------------------------------------------------- 8. publish code

if ($SkipPublish) {
    Write-Step 'Skipping the Functions deployment (-SkipPublish)'
}
else {
    Write-Step 'Publishing the Functions project'

    $publishDir = Join-Path $OutputRoot 'publish'
    $zipPath    = Join-Path $OutputRoot 'Arkimentum.AppMonitor.Api.zip'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $zipPath)    { Remove-Item $zipPath -Force }

    & dotnet publish $ApiProject -c Release -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    # Not Compress-Archive, and not ZipFile.CreateFromDirectory either: under Windows PowerShell 5.1 both write
    # backslashes into the entry names (powershell.exe runs the .NET Framework in its pre-4.6.1 compatibility
    # mode). The Linux Functions host then sees flat files instead of directories and rejects the package with
    # "Cannot find required .azurefunctions directory at root level". Naming every entry explicitly with forward
    # slashes behaves the same on every runtime.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $publishRoot = (Resolve-Path $publishDir).Path.TrimEnd('\') + '\'
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -Path $publishDir -Recurse -File -Force) {
            $entryName = $file.FullName.Substring($publishRoot.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Write-Detail ("Package: {0}" -f $zipPath)

    $deployed = Invoke-Az -Arguments @('functionapp', 'deployment', 'source', 'config-zip',
        '-g', $ResourceGroup, '-n', $functionAppName, '--src', $zipPath, '-o', 'json') -AllowFailure
    if ($null -eq $deployed) {
        Write-Detail 'config-zip failed; falling back to "az functionapp deploy".'
        Invoke-Az -Arguments @('functionapp', 'deploy', '-g', $ResourceGroup, '-n', $functionAppName,
            '--src-path', $zipPath, '--type', 'zip', '-o', 'json') | Out-Null
    }
    Write-Detail 'Deployed.'
}

# -------------------------------------------------------------------------------------------- 9. first organization

function Get-AdminToken([string] $ApiAppId) {
    $token = Invoke-Az -Arguments @('account', 'get-access-token', '--resource', ("api://" + $ApiAppId), '-o', 'json') -AllowFailure
    if ($null -eq $token) {
        throw ("Could not get a token for api://{0}. The Azure CLI must be pre-authorised for the AppMonitor.Access " +
               "scope (this script does that) and you may need to sign out and in again: az logout; az login --tenant {1}" -f $ApiAppId, $OperatorTenantId)
    }
    return $token.accessToken
}

function Wait-ForApi([string] $BaseUrl) {
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/api/v1/public/health') -Method Get -TimeoutSec 20
            if ($response.status -eq 'ok') { return $true }
        }
        catch { }
        Start-Sleep -Seconds 10
    }
    return $false
}

$organizationId = ''
$enrollmentKey  = ''

if (-not [string]::IsNullOrWhiteSpace($OrganizationName) -and -not $SkipPublish) {
    Write-Step ("Creating the first organization: {0}" -f $OrganizationName)

    if (-not (Wait-ForApi $serverUrl)) {
        Write-Warning ("{0}/api/v1/public/health did not answer. Create the organization later with New-Organization.ps1." -f $serverUrl)
    }
    else {
        $accessToken = Get-AdminToken $apiClientId
        $headers = @{ Authorization = "Bearer $accessToken"; 'Content-Type' = 'application/json' }
        $body = @{ name = $OrganizationName }
        if (-not [string]::IsNullOrWhiteSpace($CustomerTenantId)) { $body['entraTenantId'] = $CustomerTenantId }

        try {
            $created = Invoke-RestMethod -Uri ($serverUrl + '/api/v1/admin/organizations') -Method Post `
                -Headers $headers -Body ($body | ConvertTo-Json) -TimeoutSec 60
            $organizationId = $created.organization.organizationId
            $enrollmentKey  = $created.enrollment.enrollmentKey
        }
        catch {
            Write-Warning ("Could not create the organization: {0}" -f $_.Exception.Message)
            Write-Warning 'Run New-Organization.ps1 once the app role assignment has propagated (this can take a few minutes).'
        }
    }
}

# ------------------------------------------------------------------------------------------------------- summary

Write-Host ''
Write-Host '================================================================================' -ForegroundColor Green
Write-Host ' Arkimentum AppMonitor cloud backend' -ForegroundColor Green
Write-Host '================================================================================' -ForegroundColor Green
Write-Host ("  Environment          : {0}" -f $Environment)
Write-Host ("  Resource group       : {0}" -f $ResourceGroup)
Write-Host ("  Server URL           : {0}" -f $serverUrl)
Write-Host ("  API app id           : {0}" -f $apiClientId)
Write-Host ("  Admin console app id : {0}" -f $adminClientId)
Write-Host ("  Admin scope          : api://{0}/AppMonitor.Access" -f $apiClientId)
Write-Host ''
Write-Host '  Admin consent URL for a customer tenant (send this to the customer''s Global Administrator):' -ForegroundColor Yellow
Write-Host ("    https://login.microsoftonline.com/common/adminconsent?client_id={0}" -f $apiClientId)
Write-Host ("    https://login.microsoftonline.com/common/adminconsent?client_id={0}" -f $adminClientId)
Write-Host ''

if (-not [string]::IsNullOrWhiteSpace($organizationId)) {
    Write-Host '  The three registry values for the agent' -ForegroundColor Green
    Write-Host '  (HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor, or the preferences key):' -ForegroundColor Green
    Write-Host ("    CloudServerUrl      = {0}" -f $serverUrl)
    Write-Host ("    CloudOrganizationId = {0}" -f $organizationId)
    Write-Host ("    CloudEnrollmentKey  = {0}" -f $enrollmentKey)
    Write-Host ''
    Write-Host '  The enrollment key is shown once. Store it in your secret store; rotate it with Rotate-EnrollmentKey.ps1.' -ForegroundColor Yellow
}
else {
    Write-Host '  No organization was created. Run:' -ForegroundColor Yellow
    Write-Host ("    .\New-Organization.ps1 -ServerUrl {0} -ApiClientId {1} -Name ""Contoso A/S"" -EntraTenantId <customer tenant>" -f $serverUrl, $apiClientId)
}
Write-Host '================================================================================' -ForegroundColor Green
