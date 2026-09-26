metadata description = 'One user-assigned managed identity. Each workload gets its own.'

@description('Identity name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output name string = identity.name
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
