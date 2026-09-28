metadata description = '''
One complete Work Continuity environment, deployed into a single resource group.

Dev and Synthetic are separate deployments of this template with different parameters. They therefore
get their own resource group, database server, Service Bus namespace, Key Vault, registry, storage
account and virtual network. Nothing is shared between them: no database, no secret, no address space
and no identity. The difference between the two is parameters, not a second copy of the infrastructure.
'''

targetScope = 'resourceGroup'

@description('Environment name. Becomes part of every resource name.')
@allowed([
  'dev'
  'synthetic'
])
param environmentName string

@description('Azure region.')
param location string

@description('Short region token used in resource names, for example "ilc" for Israel Central.')
@maxLength(6)
param regionToken string

@description('Virtual network address space. Dev and Synthetic must not overlap.')
param vnetAddressPrefix string

@description('Container Apps subnet. A Consumption environment requires at least a /23.')
param appsSubnetPrefix string

@description('PostgreSQL delegated subnet.')
param postgresSubnetPrefix string

@description('PostgreSQL administrator login. The schema owner, used for migrations only.')
param postgresAdministratorLogin string

@description('PostgreSQL administrator password. Supplied at deployment time and never committed.')
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

@description('Container image for the Worker app, including tag.')
param workerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Container image for the migration job, including tag. Deliberately separate from workerImage so a Web/Worker rollout never changes the job.')
param migrationJobImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Container image for the runtime-role bootstrap job, including tag. Separate for the same reason as the migration job.')
param bootstrapJobImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Log Analytics retention in days.')
param logRetentionInDays int = 30

@description('Log Analytics daily ingestion cap in GB, so a misbehaving component cannot run up cost.')
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

@description('Enable the Web readiness probe on /health/ready. Off until the endpoint is Azure-aware (Gate 1G).')
param webReadinessProbeEnabled bool = false

// ---------------------------------------------------------------------------------------------
// Naming. Deterministic: the same inputs always produce the same names, and the unique token is
// derived from the subscription and environment so Dev and Synthetic never collide, while globally
// unique names (registry, vault, database, namespace, storage) stay valid.
// ---------------------------------------------------------------------------------------------

var workload = 'wce'
var uniqueToken = take(uniqueString(subscription().id, resourceGroup().id, environmentName), 6)
var baseName = '${workload}-${environmentName}-${regionToken}'

var names = {
  logAnalytics: 'log-${baseName}'
  applicationInsights: 'appi-${baseName}'
  virtualNetwork: 'vnet-${baseName}'
  containerRegistry: 'cr${workload}${environmentName}${uniqueToken}'
  keyVault: 'kv-${workload}-${environmentName}-${uniqueToken}'
  storageAccount: 'st${workload}${environmentName}${uniqueToken}'
  postgres: 'psql-${baseName}-${uniqueToken}'
  serviceBus: 'sb-${baseName}-${uniqueToken}'
  containerAppsEnvironment: 'cae-${baseName}'
  webApp: 'ca-${baseName}-web'
  workerApp: 'ca-${baseName}-worker'
  migrationJob: 'caj-${baseName}-migrate'
  bootstrapJob: 'caj-${baseName}-bootstrap'
  webIdentity: 'id-${baseName}-web'
  workerIdentity: 'id-${baseName}-worker'
  jobsIdentity: 'id-${baseName}-jobs'
  githubIdentity: 'id-${baseName}-github'
}

var secretNames = {
  applicationConnectionString: 'postgres-app-connection-string'
  administrativeConnectionString: 'postgres-admin-connection-string'
  runtimeRolePassword: 'postgres-runtime-role-password'
  applicationInsightsConnectionString: 'application-insights-connection-string'
}

var tags = union(
  {
    workload: 'work-continuity-engine'
    environment: environmentName
    managedBy: 'bicep'
    gate: '1E'
  },
  additionalTags
)

// ---------------------------------------------------------------------------------------------
// Observability and networking
// ---------------------------------------------------------------------------------------------

module logAnalytics 'modules/logAnalytics.bicep' = {
  name: 'log-analytics'
  params: {
    name: names.logAnalytics
    location: location
    tags: tags
    retentionInDays: logRetentionInDays
    dailyQuotaGb: logDailyQuotaGb
  }
}

module applicationInsights 'modules/applicationInsights.bicep' = {
  name: 'application-insights'
  params: {
    name: names.applicationInsights
    location: location
    tags: tags
    workspaceId: logAnalytics.outputs.id
  }
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    name: names.virtualNetwork
    location: location
    tags: tags
    addressPrefix: vnetAddressPrefix
    appsSubnetPrefix: appsSubnetPrefix
    postgresSubnetPrefix: postgresSubnetPrefix
  }
}

// ---------------------------------------------------------------------------------------------
// Platform resources
// ---------------------------------------------------------------------------------------------

module containerRegistry 'modules/containerRegistry.bicep' = {
  name: 'container-registry'
  params: {
    name: names.containerRegistry
    location: location
    tags: tags
  }
}

module keyVault 'modules/keyVault.bicep' = {
  name: 'key-vault'
  params: {
    name: names.keyVault
    location: location
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: names.storageAccount
    location: location
    tags: tags
  }
}

module serviceBus 'modules/serviceBus.bicep' = {
  name: 'service-bus'
  params: {
    name: names.serviceBus
    location: location
    tags: tags
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    name: names.postgres
    location: location
    tags: tags
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGb: postgresStorageSizeGb
    administratorLogin: postgresAdministratorLogin
    administratorPassword: postgresAdministratorPassword
    databaseName: databaseName
    delegatedSubnetId: network.outputs.postgresSubnetId
    privateDnsZoneId: network.outputs.postgresPrivateDnsZoneId
  }
}

// ---------------------------------------------------------------------------------------------
// Secrets. Values come from secure parameters, so no credential exists in source or in any output.
// ---------------------------------------------------------------------------------------------

module applicationConnectionSecret 'modules/keyVaultSecret.bicep' = {
  name: 'secret-app-connection'
  params: {
    vaultName: keyVault.outputs.name
    secretName: secretNames.applicationConnectionString
    // The runtime role, not the owner. This is the credential the Web and Worker apps use, and it is
    // subject to row-level security precisely because it is neither owner nor superuser.
    secretValue: 'Host=${postgres.outputs.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${postgresRuntimeRoleName};Password=${postgresRuntimeRolePassword};SSL Mode=Require;Trust Server Certificate=true'
  }
}

module administrativeConnectionSecret 'modules/keyVaultSecret.bicep' = {
  name: 'secret-admin-connection'
  params: {
    vaultName: keyVault.outputs.name
    secretName: secretNames.administrativeConnectionString
    // The schema owner. Only the jobs identity is granted read access to this secret.
    secretValue: 'Host=${postgres.outputs.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${postgresAdministratorLogin};Password=${postgresAdministratorPassword};SSL Mode=Require;Trust Server Certificate=true'
  }
}

module runtimeRolePasswordSecret 'modules/keyVaultSecret.bicep' = {
  name: 'secret-runtime-role-password'
  params: {
    vaultName: keyVault.outputs.name
    secretName: secretNames.runtimeRolePassword
    // Needed by database bootstrap to CREATE ROLE, which is why the password cannot live in a
    // migration: migrations are committed and replayed everywhere.
    secretValue: postgresRuntimeRolePassword
  }
}

// Read back through an existing-resource reference rather than a module output, so the connection
// string never becomes a deployment output of any module.
resource applicationInsightsExisting 'Microsoft.Insights/components@2020-02-02' existing = {
  name: names.applicationInsights
  dependsOn: [
    applicationInsights
  ]
}

module applicationInsightsSecret 'modules/keyVaultSecret.bicep' = {
  name: 'secret-appinsights-connection'
  params: {
    vaultName: keyVault.outputs.name
    secretName: secretNames.applicationInsightsConnectionString
    secretValue: applicationInsightsExisting.properties.ConnectionString
  }
}

// ---------------------------------------------------------------------------------------------
// Workload identities. One per workload, so no single identity can reach everything.
// ---------------------------------------------------------------------------------------------

module webIdentity 'modules/managedIdentity.bicep' = {
  name: 'identity-web'
  params: {
    name: names.webIdentity
    location: location
    tags: tags
  }
}

module workerIdentity 'modules/managedIdentity.bicep' = {
  name: 'identity-worker'
  params: {
    name: names.workerIdentity
    location: location
    tags: tags
  }
}

module jobsIdentity 'modules/managedIdentity.bicep' = {
  name: 'identity-jobs'
  params: {
    name: names.jobsIdentity
    location: location
    tags: tags
  }
}

module roleAssignments 'modules/roleAssignments.bicep' = {
  name: 'role-assignments'
  params: {
    containerRegistryName: containerRegistry.outputs.name
    keyVaultName: keyVault.outputs.name
    serviceBusNamespaceName: serviceBus.outputs.name
    storageAccountName: storage.outputs.name
    webPrincipalId: webIdentity.outputs.principalId
    workerPrincipalId: workerIdentity.outputs.principalId
    jobsPrincipalId: jobsIdentity.outputs.principalId
    applicationConnectionSecretName: secretNames.applicationConnectionString
    administrativeConnectionSecretName: secretNames.administrativeConnectionString
    runtimeRolePasswordSecretName: secretNames.runtimeRolePassword
    applicationInsightsConnectionSecretName: secretNames.applicationInsightsConnectionString
  }
  dependsOn: [
    applicationConnectionSecret
    administrativeConnectionSecret
    runtimeRolePasswordSecret
    applicationInsightsSecret
  ]
}

// ---------------------------------------------------------------------------------------------
// Compute
// ---------------------------------------------------------------------------------------------

module containerAppsEnvironment 'modules/containerAppsEnvironment.bicep' = {
  name: 'container-apps-environment'
  params: {
    name: names.containerAppsEnvironment
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    logAnalyticsCustomerId: logAnalytics.outputs.customerId
    infrastructureSubnetId: network.outputs.appsSubnetId
  }
}

var commonEnvironmentVariables = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: environmentName == 'dev' ? 'Development' : 'Production'
  }
  {
    name: 'WORKCONTINUITY_ENVIRONMENT'
    value: environmentName
  }
  {
    name: 'ServiceBus__FullyQualifiedNamespace'
    value: serviceBus.outputs.fullyQualifiedNamespace
  }
  {
    name: 'Storage__BlobEndpoint'
    value: storage.outputs.blobEndpoint
  }
]

// The placeholder image listens on port 80 and serves no health endpoints; the real Web/API image
// listens on 8080 and exposes /health/live and /health/ready. Container Apps refuses to provision a
// revision whose target port does not match the listening port, so port and probes follow the image:
// the placeholder can provision now, and the production configuration applies automatically as soon
// as a real image is supplied. The Worker has no ingress and no probes, so it needs no switch.
var webUsesPlaceholderImage = startsWith(webImage, 'mcr.microsoft.com/k8se/quickstart')
var webTargetPort = webUsesPlaceholderImage ? 80 : 8080
var webLivenessPath = webUsesPlaceholderImage ? '' : '/health/live'
// Readiness stays off until /health/ready is Azure-aware (Gate 1G); today it probes local-only targets.
var webReadinessPath = (webUsesPlaceholderImage || !webReadinessProbeEnabled) ? '' : '/health/ready'

module webApp 'modules/containerApp.bicep' = {
  name: 'container-app-web'
  params: {
    name: names.webApp
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: webIdentity.outputs.id
    registryLoginServer: containerRegistry.outputs.loginServer
    image: webImage
    enableIngress: true
    // External: the monday connector delivers webhooks from the internet. The data plane stays private.
    externalIngress: true
    targetPort: webTargetPort
    livenessPath: webLivenessPath
    readinessPath: webReadinessPath
    minReplicas: 1
    maxReplicas: maxReplicas
    environmentVariables: union(
      commonEnvironmentVariables,
      [
        {
          name: 'AZURE_CLIENT_ID'
          value: webIdentity.outputs.clientId
        }
      ]
    )
    secretReferences: [
      {
        name: 'postgres-app-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.applicationConnectionString}'
        environmentVariable: 'ConnectionStrings__Postgres'
      }
      {
        name: 'appinsights-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.applicationInsightsConnectionString}'
        environmentVariable: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
      }
    ]
  }
  dependsOn: [
    roleAssignments
  ]
}

module workerApp 'modules/containerApp.bicep' = {
  name: 'container-app-worker'
  params: {
    name: names.workerApp
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: workerIdentity.outputs.id
    registryLoginServer: containerRegistry.outputs.loginServer
    image: workerImage
    // No ingress: the Worker is reached by nothing from outside.
    enableIngress: false
    externalIngress: false
    minReplicas: 1
    maxReplicas: maxReplicas
    environmentVariables: union(
      commonEnvironmentVariables,
      [
        {
          name: 'AZURE_CLIENT_ID'
          value: workerIdentity.outputs.clientId
        }
      ]
    )
    secretReferences: [
      {
        name: 'postgres-app-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.applicationConnectionString}'
        environmentVariable: 'ConnectionStrings__Postgres'
      }
      {
        name: 'appinsights-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.applicationInsightsConnectionString}'
        environmentVariable: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
      }
    ]
  }
  dependsOn: [
    roleAssignments
  ]
}

// Runtime-role bootstrap as an explicitly invoked job (Gate 1G). It creates or updates the non-owner
// role the applications connect as, reading the role password from Key Vault through the jobs
// identity. A job entry point, never a migration: a migration is replayed everywhere and must never
// carry a credential. Only this job receives the runtime-role password; the migration job does not.
module bootstrapJob 'modules/containerAppsJob.bicep' = {
  name: 'container-apps-job-bootstrap'
  params: {
    name: names.bootstrapJob
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: jobsIdentity.outputs.id
    registryLoginServer: containerRegistry.outputs.loginServer
    image: bootstrapJobImage
    triggerType: 'Manual'
    commandArguments: [
      '--bootstrap-runtime-role'
    ]
    environmentVariables: [
      {
        name: 'AZURE_CLIENT_ID'
        value: jobsIdentity.outputs.clientId
      }
      {
        name: 'WORKCONTINUITY_ENVIRONMENT'
        value: environmentName
      }
      {
        name: 'Postgres__RuntimeRoleName'
        value: postgresRuntimeRoleName
      }
    ]
    secretReferences: [
      {
        name: 'postgres-admin-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.administrativeConnectionString}'
        environmentVariable: 'ConnectionStrings__PostgresAdmin'
      }
      {
        name: 'postgres-runtime-role-password'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.runtimeRolePassword}'
        environmentVariable: 'Postgres__RuntimeRolePassword'
      }
    ]
  }
  dependsOn: [
    roleAssignments
  ]
}

// Schema migration as an explicitly invoked job, which is how Gate 1D requires it: an application
// never alters a shared schema while starting up. This is also the pattern the scheduled and
// synthetic jobs will reuse.
module migrationJob 'modules/containerAppsJob.bicep' = {
  name: 'container-apps-job-migrate'
  params: {
    name: names.migrationJob
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: jobsIdentity.outputs.id
    registryLoginServer: containerRegistry.outputs.loginServer
    image: migrationJobImage
    triggerType: 'Manual'
    commandArguments: [
      '--migrate-database'
    ]
    environmentVariables: [
      {
        name: 'AZURE_CLIENT_ID'
        value: jobsIdentity.outputs.clientId
      }
      {
        name: 'WORKCONTINUITY_ENVIRONMENT'
        value: environmentName
      }
    ]
    secretReferences: [
      {
        name: 'postgres-admin-connection-string'
        keyVaultUri: '${keyVault.outputs.uri}secrets/${secretNames.administrativeConnectionString}'
        environmentVariable: 'ConnectionStrings__PostgresAdmin'
      }
    ]
  }
  dependsOn: [
    roleAssignments
  ]
}

// ---------------------------------------------------------------------------------------------
// GitHub Actions deployment identity (Gate 1F). Created only when a repository is configured, so
// Synthetic gets no CD identity until it is deliberately given one. Scoped to the registry and the
// two apps it may update; never attached to a workload.
// ---------------------------------------------------------------------------------------------

var deployFromGitHub = !empty(githubRepository)

module deploymentIdentity 'modules/deploymentIdentity.bicep' = if (deployFromGitHub) {
  name: 'deployment-identity-github'
  params: {
    name: names.githubIdentity
    location: location
    tags: union(tags, { gate: '1F' })
    githubRepository: githubRepository
    githubOwnerId: githubOwnerId
    githubRepositoryId: githubRepositoryId
    githubBranch: githubBranch
    containerRegistryName: containerRegistry.outputs.name
    webAppName: webApp.outputs.name
    workerAppName: workerApp.outputs.name
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs for the CI/CD Gate. Resource identity and endpoints only; never a secret value.
// ---------------------------------------------------------------------------------------------

output environmentNameOut string = environmentName
output location string = location
output resourceGroupName string = resourceGroup().name
output containerRegistryName string = containerRegistry.outputs.name
output containerRegistryLoginServer string = containerRegistry.outputs.loginServer
output containerAppsEnvironmentName string = containerAppsEnvironment.outputs.name
output webAppName string = webApp.outputs.name
output webAppFqdn string = webApp.outputs.fqdn
output workerAppName string = workerApp.outputs.name
output migrationJobName string = migrationJob.outputs.name
output bootstrapJobName string = bootstrapJob.outputs.name
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
output postgresServerName string = postgres.outputs.name
output postgresFullyQualifiedDomainName string = postgres.outputs.fullyQualifiedDomainName
output postgresDatabaseName string = postgres.outputs.databaseName
output postgresRuntimeRoleNameOut string = postgresRuntimeRoleName
output serviceBusNamespaceName string = serviceBus.outputs.name
output serviceBusFullyQualifiedNamespace string = serviceBus.outputs.fullyQualifiedNamespace
output serviceBusQueueNames array = serviceBus.outputs.queueNames
output storageAccountName string = storage.outputs.name
output logAnalyticsWorkspaceName string = logAnalytics.outputs.name
output applicationInsightsName string = applicationInsights.outputs.name
output webIdentityClientId string = webIdentity.outputs.clientId
output workerIdentityClientId string = workerIdentity.outputs.clientId
output jobsIdentityClientId string = jobsIdentity.outputs.clientId
output deploymentIdentityClientId string = deployFromGitHub ? deploymentIdentity!.outputs.clientId : ''
output deploymentIdentityFederatedSubject string = deployFromGitHub ? deploymentIdentity!.outputs.federatedSubject : ''
