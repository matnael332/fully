metadata description = '''
Gate 3G Stage B DEV/TEST harness vault (owner decisions B-2, B-3, B-4 and B-8, approved 2026-10-08).

A subscription-scope entry point of its own: main.bicep never references it, it shares no parameter file with Dev or
Synthetic, and eng/infra.ps1 only compiles and lints it. It is deployed only by its own owner-run script, as a separately
approved step. It creates one dedicated resource group in North Europe holding one dedicated Key Vault for the harness's
monday secrets and one dedicated Log Analytics workspace for that vault's audit events, and it defines one custom role
for the harness runtime service principal.

Nothing here stores a secret, creates an Entra object or grants the product owner anything. The Entra application, its
service principal and its certificate credential are created by a separate owner-run script (B-9), and the owner's own
access to the vault is a separate decision.
'''

targetScope = 'subscription'

@description('''
Object id of the harness runtime service principal (B-8). Empty, the default, makes no role assignment; only a separately
approved deployment supplies it.
''')
@maxLength(36)
param runtimePrincipalObjectId string = ''

@description('''
One public IPv4 address the vault firewall admits, for an approved run window only. Empty, the default: no address is
admitted, so the firewall refuses every data-plane request (B-2: the firewall denies by default).
''')
@maxLength(15)
param allowedIpv4Address string = ''

// B-2: one dedicated resource group in North Europe. Fixed here, never a parameter.
var location = 'northeurope'
var resourceGroupName = 'rg-wce-harness-neu'
var tags = {
  workload: 'work-continuity-engine'
  environment: 'devtest-harness'
  managedBy: 'bicep'
}

resource harnessResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// B-8: exactly three secret data actions, and no control-plane action, exclusion, wildcard or condition. The role can be
// assigned only inside the dedicated resource group, and its one assignment, in modules/harnessVault.bicep, is made at
// the vault and nowhere else.
resource runtimeRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, resourceGroupName, 'wce-harness-keyvault-secrets-runtime')
  properties: {
    roleName: 'wce-harness-keyvault-secrets-runtime'
    description: 'Gate 3G DEV/TEST harness runtime (owner decision B-8): get, set and delete secrets in the dedicated harness vault, and nothing else.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.KeyVault/vaults/secrets/getSecret/action'
          'Microsoft.KeyVault/vaults/secrets/setSecret/action'
          'Microsoft.KeyVault/vaults/secrets/delete'
        ]
        notDataActions: []
      }
    ]
    assignableScopes: [
      harnessResourceGroup.id
    ]
  }
}

module harnessVault 'modules/harnessVault.bicep' = {
  scope: harnessResourceGroup
  name: 'harness-vault'
  params: {
    location: location
    tags: tags
    runtimeRoleDefinitionId: runtimeRole.id
    runtimePrincipalObjectId: runtimePrincipalObjectId
    allowedIpv4Address: allowedIpv4Address
  }
}

// Identifiers only; no secret value is ever an output.
output harnessResourceGroupName string = harnessResourceGroup.name
output keyVaultName string = harnessVault.outputs.keyVaultName
output keyVaultUri string = harnessVault.outputs.keyVaultUri
output auditWorkspaceName string = harnessVault.outputs.auditWorkspaceName
output runtimeRoleDefinitionId string = runtimeRole.id
output tenantId string = subscription().tenantId
