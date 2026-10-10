metadata description = '''
The Gate 3G DEV/TEST harness vault and its audit trail (owner decisions B-2, B-3, B-4 and B-8, approved 2026-10-08),
deployed only into the dedicated harness resource group by harness-vault.bicep. Nothing in Dev or Synthetic uses it.

The vault is for the harness's monday secrets only: wce-monday-client-secret, placed by the product owner, and the token
and PKCE names the harness writes. No secret is created here.
'''

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Full resource id of the harness runtime custom role (B-8).')
param runtimeRoleDefinitionId string

@description('Object id of the harness runtime service principal. Empty: no role assignment is made.')
param runtimePrincipalObjectId string

@description('One public IPv4 address the firewall admits for an approved run window. Empty: none is admitted.')
param allowedIpv4Address string

var uniqueToken = take(uniqueString(subscription().id, resourceGroup().id, 'harness'), 6)
var names = {
  keyVault: 'kv-wce-harness-${uniqueToken}'
  auditWorkspace: 'log-wce-harness-neu'
}

// B-3: seven days, fixed when the vault is created and never changeable afterwards. A constant, not a parameter.
var softDeleteRetentionInDays = 7

// B-2: the vault's audit events go to a workspace of its own in this resource group, never to the Dev workspace.
module auditWorkspace 'logAnalytics.bicep' = {
  name: 'harness-audit-workspace'
  params: {
    name: names.auditWorkspace
    location: location
    tags: tags
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.keyVault
  location: location
  tags: tags
  properties: {
    // B-2: Standard SKU and Azure RBAC authorization; access policies are never used.
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // B-4: purge protection OFF. The property is deliberately left out: Azure does not accept false for it, and turning
    // it on could never be undone.
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    // B-2: the firewall denies by default, and no trusted Azure service may bypass it. The public endpoint stays enabled
    // only because disabling it would also override every firewall rule; with no address admitted, every data-plane
    // request is refused.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: empty(allowedIpv4Address) ? [] : [
        {
          value: allowedIpv4Address
        }
      ]
      virtualNetworkRules: []
    }
  }
}

resource auditEvents 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'harness-vault-audit'
  scope: vault
  properties: {
    workspaceId: auditWorkspace.outputs.id
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
      }
    ]
  }
}

// B-8: the one assignment of the runtime role, at the vault and nowhere else, to the principal that a separately approved
// deployment supplies. No condition.
resource runtimeAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(runtimePrincipalObjectId)) {
  scope: vault
  name: guid(vault.id, runtimePrincipalObjectId, runtimeRoleDefinitionId)
  properties: {
    roleDefinitionId: runtimeRoleDefinitionId
    principalId: runtimePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
output auditWorkspaceName string = auditWorkspace.outputs.name
