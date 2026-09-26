metadata description = 'Private container registry. Admin user is disabled; pulls use managed identity.'

@description('Registry name. Alphanumeric only, globally unique.')
@minLength(5)
@maxLength(50)
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Registry SKU. Basic is sufficient for Dev and Synthetic.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param sku string = 'Basic'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  properties: {
    // No admin user: there is no registry password to store, rotate or leak. Container Apps pull
    // with a user-assigned managed identity holding AcrPull.
    adminUserEnabled: false
    dataEndpointEnabled: false
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
  }
}

output id string = registry.id
output name string = registry.name
output loginServer string = registry.properties.loginServer
