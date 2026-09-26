metadata description = '''
Key Vault holding the environment's secrets, with RBAC authorization rather than access policies.

Secret values arrive as secure deployment parameters and are never present in source. Read access is
granted per secret rather than per vault, so the workloads that serve requests cannot read the
administrative database credential at all.
'''

@description('Vault name. Globally unique, 3-24 characters.')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('''
Purge protection. Left off for the foundation environments so they can be torn down cleanly; it
cannot be disabled once enabled. Human Pilot and production must turn this on.
''')
param enablePurgeProtection bool = false

@description('Soft-delete retention in days.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    // RBAC rather than vault access policies: role assignments can then be scoped to a single
    // secret, which access policies cannot express.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
