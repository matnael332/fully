metadata description = '''
Subscription-scope entry point. Creates one resource group per environment and deploys the whole
environment into it.

Deploying at subscription scope is what makes the Dev and Synthetic data boundaries structural rather
than a naming convention: each environment owns a separate resource group, and every resource inside
it is created fresh for that environment. There is no shared database, secret, identity or address
space between them.
'''

targetScope = 'subscription'

@description('Environment name. Selects the Dev or Synthetic boundary.')
@allowed([
  'dev'
  'synthetic'
])
param environmentName string

@description('''
Azure region. The canonical architecture does not fix a region. North Europe is the Gate 1E default:
Israel Central does not offer Azure Container Apps, and West Europe is not accepting new customers on
the deployment subscription (both verified 2026-09-27). Every required service and SKU must be
confirmed available in the target region before a real deployment.
''')
param location string = 'northeurope'

@description('Short region token used in resource names.')
@maxLength(6)
param regionToken string = 'neu'

@description('Virtual network address space. Dev and Synthetic must not overlap.')
param vnetAddressPrefix string

@description('Container Apps subnet. A Consumption environment requires at least a /23.')
param appsSubnetPrefix string

@description('PostgreSQL delegated subnet.')
param postgresSubnetPrefix string

@description('PostgreSQL administrator login. The schema owner, used for migrations only.')
param postgresAdministratorLogin string

@description('PostgreSQL administrator password. Supplied at deployment time; never committed.')
@secure()
param postgresAdministratorPassword string

@description('Password for the non-owner runtime role that row-level security depends on.')
@secure()
param postgresRuntimeRolePassword string

@description('Application database name.')
param databaseName string = 'workcontinuity'

@description('Non-owner runtime role name. Must match the role the application connects as.')
param postgresRuntimeRoleName string = 'workcontinuity_app'

@description('PostgreSQL compute SKU.')
param postgresSkuName string = 'Standard_B1ms'

@description('PostgreSQL compute tier.')
param postgresSkuTier string = 'Burstable'

@description('PostgreSQL storage in GB.')
param postgresStorageSizeGb int = 32

@description('Container image for the Web/API app, including tag.')
param webImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Container image for the Worker app and the migration job, including tag.')
param workerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Log Analytics retention in days.')
param logRetentionInDays int = 30

@description('Log Analytics daily ingestion cap in GB.')
param logDailyQuotaGb int = 1

@description('Maximum replicas per app.')
param maxReplicas int = 2

@description('Additional resource tags.')
param additionalTags object = {}

@description('GitHub repository (owner/name) allowed to deploy images through CD. Empty disables the deployment identity.')
param githubRepository string = ''

@description('GitHub numeric owner id, part of the immutable OIDC subject.')
param githubOwnerId string = ''

@description('GitHub numeric repository id, part of the immutable OIDC subject.')
param githubRepositoryId string = ''

@description('The only branch whose workflow runs may deploy.')
param githubBranch string = 'main'

var resourceGroupName = 'rg-wce-${environmentName}-${regionToken}'

resource environmentResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
  tags: union(
    {
      workload: 'work-continuity-engine'
      environment: environmentName
      managedBy: 'bicep'
    },
    additionalTags
  )
}

module environment 'environment.bicep' = {
  scope: environmentResourceGroup
  name: 'wce-${environmentName}'
  params: {
    environmentName: environmentName
    location: location
    regionToken: regionToken
    vnetAddressPrefix: vnetAddressPrefix
    appsSubnetPrefix: appsSubnetPrefix
    postgresSubnetPrefix: postgresSubnetPrefix
    postgresAdministratorLogin: postgresAdministratorLogin
    postgresAdministratorPassword: postgresAdministratorPassword
    postgresRuntimeRolePassword: postgresRuntimeRolePassword
    databaseName: databaseName
    postgresRuntimeRoleName: postgresRuntimeRoleName
    postgresSkuName: postgresSkuName
    postgresSkuTier: postgresSkuTier
    postgresStorageSizeGb: postgresStorageSizeGb
    webImage: webImage
    workerImage: workerImage
    logRetentionInDays: logRetentionInDays
    logDailyQuotaGb: logDailyQuotaGb
    maxReplicas: maxReplicas
    additionalTags: additionalTags
    githubRepository: githubRepository
    githubOwnerId: githubOwnerId
    githubRepositoryId: githubRepositoryId
    githubBranch: githubBranch
  }
}

// Outputs the CI/CD Gate needs. No secret value is ever returned.
output resourceGroupNameOut string = environmentResourceGroup.name
output location string = location
output containerRegistryName string = environment.outputs.containerRegistryName
output containerRegistryLoginServer string = environment.outputs.containerRegistryLoginServer
output containerAppsEnvironmentName string = environment.outputs.containerAppsEnvironmentName
output webAppName string = environment.outputs.webAppName
output webAppFqdn string = environment.outputs.webAppFqdn
output workerAppName string = environment.outputs.workerAppName
output migrationJobName string = environment.outputs.migrationJobName
output keyVaultName string = environment.outputs.keyVaultName
output keyVaultUri string = environment.outputs.keyVaultUri
output postgresServerName string = environment.outputs.postgresServerName
output postgresFullyQualifiedDomainName string = environment.outputs.postgresFullyQualifiedDomainName
output postgresDatabaseName string = environment.outputs.postgresDatabaseName
output postgresRuntimeRoleName string = environment.outputs.postgresRuntimeRoleNameOut
output serviceBusNamespaceName string = environment.outputs.serviceBusNamespaceName
output serviceBusFullyQualifiedNamespace string = environment.outputs.serviceBusFullyQualifiedNamespace
output serviceBusQueueNames array = environment.outputs.serviceBusQueueNames
output storageAccountName string = environment.outputs.storageAccountName
output logAnalyticsWorkspaceName string = environment.outputs.logAnalyticsWorkspaceName
output applicationInsightsName string = environment.outputs.applicationInsightsName
output webIdentityClientId string = environment.outputs.webIdentityClientId
output workerIdentityClientId string = environment.outputs.workerIdentityClientId
output jobsIdentityClientId string = environment.outputs.jobsIdentityClientId
output deploymentIdentityClientId string = environment.outputs.deploymentIdentityClientId
output deploymentIdentityFederatedSubject string = environment.outputs.deploymentIdentityFederatedSubject
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
