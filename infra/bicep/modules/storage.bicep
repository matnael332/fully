metadata description = '''
Storage account for large or temporary approved content, as named in the frozen stack.

Blob-only, no public access, TLS 1.2 minimum, and shared-key authorization disabled so every
workload must use its managed identity.
'''

@description('Account name. Globally unique, 3-24 lowercase alphanumeric characters.')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Container for large or temporary approved content.')
param contentContainerName string = 'work-content'

resource account 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // No account keys: identity-based access only, so there is no storage key to leak or rotate.
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: account
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource contentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: contentContainerName
  properties: {
    publicAccess: 'None'
  }
}

output id string = account.id
output name string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
output contentContainerName string = contentContainer.name
