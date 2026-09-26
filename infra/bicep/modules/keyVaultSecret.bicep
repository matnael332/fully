metadata description = '''
One Key Vault secret.

Kept as its own module so the parent template can hand each workload a role assignment scoped to a
single secret. The value is a secure parameter and is never emitted as an output.
'''

@description('Existing vault name.')
param vaultName string

@description('Secret name.')
param secretName string

@description('Secret value.')
@secure()
param secretValue string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource secret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: secretName
  properties: {
    value: secretValue
    attributes: {
      enabled: true
    }
  }
}

// The resource id is safe to output; the value is not and is never returned.
output id string = secret.id
output name string = secret.name
