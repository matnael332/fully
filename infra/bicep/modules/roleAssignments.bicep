metadata description = '''
Least-privilege role assignments for the workload identities.

Each workload has its own identity and receives only the roles it needs:

- Web and Worker can pull images and read two secrets: the *application* database connection string
  and the Application Insights connection string. Neither can read the administrative credential, so
  a compromise of a request-handling container cannot obtain the schema owner's password.
- The jobs identity can read the administrative credential and the runtime-role password, because it
  performs migration and database bootstrap. It is never attached to an app that serves traffic.
- Key Vault access is granted per secret rather than per vault, which is the difference between "can
  read its own configuration" and "can read everything in the environment".
'''

@description('Container registry name.')
param containerRegistryName string

@description('Key Vault name.')
param keyVaultName string

@description('Service Bus namespace name.')
param serviceBusNamespaceName string

@description('Storage account name.')
param storageAccountName string

@description('Web workload principal id.')
param webPrincipalId string

@description('Worker workload principal id.')
param workerPrincipalId string

@description('Jobs workload principal id.')
param jobsPrincipalId string

@description('Secret holding the application (runtime role) connection string.')
param applicationConnectionSecretName string

@description('Secret holding the administrative connection string.')
param administrativeConnectionSecretName string

@description('Secret holding the runtime role password.')
param runtimeRolePasswordSecretName string

@description('Secret holding the Application Insights connection string, read by Web and Worker for telemetry.')
param applicationInsightsConnectionSecretName string

// Built-in role definition ids rather than names, so an assignment stays stable if a role is renamed.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource applicationConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: applicationConnectionSecretName
}

resource administrativeConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: administrativeConnectionSecretName
}

resource runtimeRolePasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: runtimeRolePasswordSecretName
}

resource applicationInsightsConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: applicationInsightsConnectionSecretName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

// --- Registry pull. All three workloads run the same two images. ---

resource webAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, webPrincipalId, roles.acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, workerPrincipalId, roles.acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource jobsAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, jobsPrincipalId, roles.acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: jobsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Key Vault, scoped to individual secrets. ---

resource webApplicationSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: applicationConnectionSecret
  name: guid(applicationConnectionSecret.id, webPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerApplicationSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: applicationConnectionSecret
  name: guid(applicationConnectionSecret.id, workerPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Telemetry: both apps reference the Application Insights connection string as a Container Apps
// secret, so each needs read access to exactly that secret. Without these two assignments, revision
// provisioning fails with "unable to fetch secret using managed identity" (Dev attempt 1, 2026-09-27).
resource webApplicationInsightsSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: applicationInsightsConnectionSecret
  name: guid(applicationInsightsConnectionSecret.id, webPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerApplicationInsightsSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: applicationInsightsConnectionSecret
  name: guid(applicationInsightsConnectionSecret.id, workerPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Only the jobs identity reaches the administrative credential.
resource jobsAdministrativeSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: administrativeConnectionSecret
  name: guid(administrativeConnectionSecret.id, jobsPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: jobsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The runtime role password is needed to create that role inside the database during bootstrap.
resource jobsRuntimeRolePassword 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: runtimeRolePasswordSecret
  name: guid(runtimeRolePasswordSecret.id, jobsPrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: jobsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Service Bus. Local authentication is disabled, so these roles are the only access path. ---

// Web receives inbound webhooks and enqueues work: send only.
resource webServiceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, webPrincipalId, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Worker consumes and, after processing, publishes onward.
resource workerServiceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerPrincipalId, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerServiceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerPrincipalId, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Blob storage for large or temporary approved content. Web never writes it directly. ---

resource workerStorageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, workerPrincipalId, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output assignedRoleCount int = 13
