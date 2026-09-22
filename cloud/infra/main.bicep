// =====================================================================================================================
// Arkimentum AppMonitor - cloud backend (resource group scoped).
//
//   az deployment group create -g appmon-prod-rg -f main.bicep -p @main.parameters.prod.json
//
// Everything is named appmon-{env}-... . There are no secrets in this template and none in the resulting app
// settings: the Function App reaches SQL and Blob Storage with its system-assigned managed identity.
// =====================================================================================================================

targetScope = 'resourceGroup'

@description('Environment moniker used in every resource name: dev, test, prod.')
@minLength(2)
@maxLength(8)
param environmentName string

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('Suffix that makes the globally unique names (function app, storage, SQL server) unique.')
param nameSuffix string = substring(uniqueString(resourceGroup().id), 0, 6)

@description('Application (client) id of the API app registration. Created by Deploy-Cloud.ps1.')
param apiClientId string

@description('Audience the API accepts, normally api://{apiClientId}.')
param apiAudience string = 'api://${apiClientId}'

@description('Application (client) id of the admin console app registration (the browser SPA and the WPF console share it).')
param adminClientId string

@description('Arkimentum\'s own tenant. AppMonitor.GlobalAdmin is only honoured for tokens from this tenant.')
param operatorTenantId string

@description('Authority segment for the admin console: "organizations" for the multi-tenant app, or a tenant id.')
param tenantIdMode string = 'organizations'

@description('Seconds between device configuration polls.')
@minValue(60)
@maxValue(86400)
param pollIntervalSeconds int = 900

@description('Object id of the Entra ID user or group that owns the SQL server (Entra-only authentication).')
param sqlAdminObjectId string

@description('Display name of that user or group.')
param sqlAdminLogin string

@description('Principal type of the SQL administrator.')
@allowed([
  'User'
  'Group'
  'Application'
])
param sqlAdminPrincipalType string = 'Group'

@description('Maximum vCores of the serverless database.')
@allowed([
  2
  4
  8
])
param sqlMaxVCores int = 2

@description('Minimum vCores while the database is awake.')
param sqlMinVCores string = '0.5'

@description('Minutes of inactivity before the serverless database auto-pauses. -1 disables auto-pause.')
param sqlAutoPauseDelayMinutes int = 60

@description('Days of point-in-time restore kept for the database.')
@minValue(1)
@maxValue(35)
param sqlBackupRetentionDays int = 7

@description('Flex Consumption (recommended, Linux) when true; classic Consumption (Y1, Windows) when false.')
param useFlexConsumption bool = true

@description('Maximum instances the Flex Consumption plan may scale to.')
param maximumInstanceCount int = 40

@description('Memory per Flex Consumption instance, in MB.')
@allowed([
  2048
  4096
])
param instanceMemoryMB int = 2048

@description('Days of Log Analytics retention.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 90

@description('Deploy the browser-based admin console as an Azure Static Web App (Blazor WebAssembly).')
param deployWebAdmin bool = true

@description('Region for the Static Web App. The Free SKU exists only in westus2, centralus, eastus2, westeurope and eastasia, so it normally differs from "location".')
param staticWebAppLocation string = 'westeurope'

@description('Extra browser origins allowed to call the API, e.g. https://localhost:7200 for local development of the web admin console. Full origins, no trailing slash.')
param additionalCorsOrigins array = []

@description('Tags applied to every resource.')
param tags object = {
  product: 'Arkimentum AppMonitor'
  environment: environmentName
}

// --------------------------------------------------------------------------------------------------------- names

var prefix = 'appmon-${environmentName}'
var storageAccountName = toLower(replace('appmon${environmentName}st${nameSuffix}', '-', ''))
var functionAppName = '${prefix}-func-${nameSuffix}'
var planName = '${prefix}-plan'
var sqlServerName = '${prefix}-sql-${nameSuffix}'
var staticWebAppName = '${prefix}-web-${nameSuffix}'
var sqlDatabaseName = '${prefix}-db'
var appInsightsName = '${prefix}-ai'
var workspaceName = '${prefix}-law'
var deploymentContainerName = 'function-releases'
var reportContainerName = 'reports'

// Built-in role definition ids.
var storageBlobDataOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageQueueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageTableDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var monitoringMetricsPublisher = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

// --------------------------------------------------------------------------------------------------- observability

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// -------------------------------------------------------------------------------------------------------- storage

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false          // identity-based access only: there is no key to leak
    publicNetworkAccess: 'Enabled'
    defaultToOAuthAuthentication: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      services: {
        blob: {
          enabled: true
        }
        file: {
          enabled: true
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource reportContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: reportContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (useFlexConsumption) {
  parent: blobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

// ------------------------------------------------------------------------------------------------------- Azure SQL

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sqlAdminPrincipalType
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true      // no SQL logins, no password to store
    }
  }
}

resource sqlAadOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01-preview' = {
  parent: sqlServer
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: true
  }
}

// The Function App has no fixed outbound address on Consumption plans, so the database is reached through the
// "Allow Azure services" rule. Lock this down with a private endpoint or VNet integration when you need to.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: sqlMaxVCores
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368            // 32 GB
    autoPauseDelay: sqlAutoPauseDelayMinutes
    minCapacity: json(sqlMinVCores)
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
  }
  dependsOn: [
    sqlAadOnly
  ]
}

resource sqlBackupPolicy 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = {
  parent: sqlDatabase
  name: 'default'
  properties: {
    retentionDays: sqlBackupRetentionDays
  }
}

// ---------------------------------------------------------------------------------------------------- hosting plan

resource flexPlan 'Microsoft.Web/serverfarms@2023-12-01' = if (useFlexConsumption) {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true                        // Linux
  }
}

resource consumptionPlan 'Microsoft.Web/serverfarms@2023-12-01' = if (!useFlexConsumption) {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {}
}

// ------------------------------------------------------------------------------------------- web admin console
//
// The browser admin console is a Blazor WebAssembly site - static files only, no managed functions and no
// repository link: it is published by Deploy-Cloud.ps1 (SWA CLI) or by .github/workflows/cloud-web.yml.
// It signs in with MSAL and calls the Function App cross-origin with a bearer token, which is why the Function
// App's CORS list below contains exactly this origin (plus whatever additionalCorsOrigins adds).

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = if (deployWebAdmin) {
  name: staticWebAppName
  location: staticWebAppLocation
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    stagingEnvironmentPolicy: 'Disabled'       // Free SKU has no staging environments anyway
    allowConfigFileUpdates: true               // staticwebapp.config.json in the payload decides routing/fallback
  }
}

// Safe-dereference: the resource does not exist when deployWebAdmin is false, and the template must still compile.
var webAdminHost = deployWebAdmin ? (staticWebApp.?properties.?defaultHostname ?? '') : ''
var webAdminUrl = empty(webAdminHost) ? '' : 'https://${webAdminHost}'

// Exactly the origins that are allowed to call the API from a browser. supportCredentials stays false: the
// console authenticates with an Authorization header, never with cookies, so no credentialed origin is needed.
var corsAllowedOrigins = empty(webAdminUrl) ? additionalCorsOrigins : union([webAdminUrl], additionalCorsOrigins)

// ---------------------------------------------------------------------------------------------------- function app

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
var publicServerUrl = 'https://${functionAppName}.azurewebsites.net'

var commonAppSettings = [
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'AzureWebJobsStorage__accountName'
    value: storage.name
  }
  {
    name: 'AzureWebJobsStorage__credential'
    value: 'managedidentity'
  }
  {
    name: 'SqlConnection'
    value: sqlConnectionString
  }
  {
    name: 'SqlProvider'
    value: 'SqlServer'
  }
  {
    name: 'BlobServiceUri'
    value: storage.properties.primaryEndpoints.blob
  }
  {
    name: 'ReportContainer'
    value: reportContainerName
  }
  {
    name: 'AzureAd__ClientId'
    value: apiClientId
  }
  {
    name: 'AzureAd__Audience'
    value: apiAudience
  }
  {
    name: 'AzureAd__TenantIdMode'
    value: tenantIdMode
  }
  {
    name: 'AdminClientId'
    value: adminClientId
  }
  {
    name: 'OperatorTenantId'
    value: operatorTenantId
  }
  {
    name: 'PublicServerUrl'
    value: publicServerUrl
  }
  {
    name: 'PublicWebAdminUrl'
    value: webAdminUrl                 // empty when deployWebAdmin is false; /public/auth-config then omits it
  }
  {
    name: 'PollIntervalSeconds'
    value: string(pollIntervalSeconds)
  }
]

var consumptionOnlySettings = [
  {
    name: 'FUNCTIONS_EXTENSION_VERSION'
    value: '~4'
  }
  {
    name: 'FUNCTIONS_WORKER_RUNTIME'
    value: 'dotnet-isolated'
  }
  {
    name: 'WEBSITE_RUN_FROM_PACKAGE'
    value: '1'
  }
]

resource flexFunctionApp 'Microsoft.Web/sites@2023-12-01' = if (useFlexConsumption) {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      cors: {
        allowedOrigins: corsAllowedOrigins  // the web admin console's origin, plus any extra configured one
        supportCredentials: false           // bearer tokens only - no cookies, so no credentialed origins
      }
      appSettings: commonAppSettings
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
  }
  dependsOn: [
    deploymentContainer
  ]
}

resource consumptionFunctionApp 'Microsoft.Web/sites@2023-12-01' = if (!useFlexConsumption) {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: consumptionPlan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false
      cors: {
        allowedOrigins: corsAllowedOrigins
        supportCredentials: false
      }
      appSettings: concat(commonAppSettings, consumptionOnlySettings)
    }
  }
}

// Exactly one of the two site resources is deployed; the safe-dereference operator keeps the other branch from
// being evaluated (and the template from failing) when it is not.
var functionPrincipalId = useFlexConsumption
  ? (flexFunctionApp.?identity.?principalId ?? '')
  : (consumptionFunctionApp.?identity.?principalId ?? '')

// ------------------------------------------------------------------------------------------------ role assignments

resource blobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppName, 'StorageBlobDataOwner')
  properties: {
    roleDefinitionId: storageBlobDataOwner
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource queueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppName, 'StorageQueueDataContributor')
  properties: {
    roleDefinitionId: storageQueueDataContributor
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource tableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppName, 'StorageTableDataContributor')
  properties: {
    roleDefinitionId: storageTableDataContributor
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appInsights
  name: guid(appInsights.id, functionAppName, 'MonitoringMetricsPublisher')
  properties: {
    roleDefinitionId: monitoringMetricsPublisher
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// -------------------------------------------------------------------------------------------------------- outputs

output functionAppName string = functionAppName
output functionAppPrincipalId string = functionPrincipalId
output serverUrl string = publicServerUrl
output webAdminUrl string = webAdminUrl
output staticWebAppName string = deployWebAdmin ? staticWebAppName : ''
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabaseName
output sqlConnectionString string = sqlConnectionString
output storageAccountName string = storage.name
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output reportContainerName string = reportContainerName
output appInsightsName string = appInsightsName
output logAnalyticsWorkspaceName string = workspaceName
output hostingPlanKind string = useFlexConsumption ? 'FlexConsumption (FC1, Linux)' : 'Consumption (Y1, Windows)'
