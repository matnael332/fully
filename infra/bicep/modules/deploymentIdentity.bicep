metadata description = '''
Deployment identity for GitHub Actions continuous delivery.

One user-assigned identity, dedicated to GitHub CD and never attached to a running workload, trusts a
single GitHub OIDC subject: one repository, one branch. It holds exactly three role assignments: push to
the environment's registry and write on the two Container Apps whose images CD may roll. It has no access
to Key Vault, PostgreSQL, Service Bus, storage, networking, the migration job or the environment, and it
cannot create role assignments. No client secret exists anywhere: GitHub obtains short-lived tokens by
federation, and infrastructure deployments stay on the interactive eng/infra.ps1 path.
'''

@description('Identity name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('GitHub repository as owner/name, for example matnael332/fully.')
param githubRepository string

@description('GitHub numeric owner id. Part of the immutable OIDC subject.')
param githubOwnerId string

@description('GitHub numeric repository id. Part of the immutable OIDC subject.')
param githubRepositoryId string

@description('The only branch whose workflow runs may deploy.')
param githubBranch string

@description('Registry the identity may push to.')
param containerRegistryName string

@description('Web Container App whose image the identity may update.')
param webAppName string

@description('Worker Container App whose image the identity may update.')
param workerAppName string

// Repositories created after 2026-07-15 use GitHub's immutable subject format, which embeds the owner
// and repository ids: repo:OWNER@OWNER-ID/REPO@REPO-ID:ref:refs/heads/BRANCH. Verified against this
// repository's OIDC settings on 2026-09-27. The classic repo:OWNER/REPO:... form would be accepted here
// and then fail silently at every token exchange.
var owner = split(githubRepository, '/')[0]
var repository = split(githubRepository, '/')[1]
var federatedSubject = 'repo:${owner}@${githubOwnerId}/${repository}@${githubRepositoryId}:ref:refs/heads/${githubBranch}'

// Built-in role definition ids rather than names, so an assignment stays stable if a role is renamed.
var roles = {
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  containerAppsContributor: '358470bc-b998-42bd-ab17-a7e34c199c0f'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-${githubBranch}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: federatedSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: webAppName
}

resource workerApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: workerAppName
}

// Push images. Registry-wide by the nature of the legacy registry-permissions mode; pull is included.
resource registryPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, roles.acrPush)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPush)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Image-only updates on the two apps, scoped to each app rather than the resource group. Nothing on the
// migration job: its image and execution stay on the human-run Bicep path.
resource webAppContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: webApp
  name: guid(webApp.id, identity.id, roles.containerAppsContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containerAppsContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workerAppContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: workerApp
  name: guid(workerApp.id, identity.id, roles.containerAppsContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containerAppsContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output id string = identity.id
output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
output federatedSubject string = federatedSubject
