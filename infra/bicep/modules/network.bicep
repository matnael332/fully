metadata description = '''
Virtual network for one environment.

Two delegated subnets: one for the Container Apps environment and one for PostgreSQL Flexible
Server. PostgreSQL is reachable only inside this network, so the operational store holding employee
work data is never exposed to the public internet. Web ingress stays public, because the monday
connector must be able to deliver webhooks; only the data plane is private.

Each environment gets its own address space, so Dev and Synthetic cannot be peered together by
accident and remain separate data boundaries.
'''

@description('Virtual network name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Virtual network address space.')
param addressPrefix string

@description('Subnet for the Container Apps environment. A Consumption environment requires at least a /23.')
param appsSubnetPrefix string

@description('Subnet delegated to PostgreSQL Flexible Server.')
param postgresSubnetPrefix string

var appsSubnetName = 'snet-apps'
var postgresSubnetName = 'snet-postgres'

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: appsSubnetName
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        name: postgresSubnetName
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'postgres-flexible-server'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
          // Flexible Server adds this endpoint to its delegated subnet when the server is created.
          // Declaring it keeps the template identical to the live subnet, so a redeployment does not
          // try to remove it (seen in the what-if before Dev deployment attempt 2, 2026-09-27).
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
          ]
        }
      }
    ]
  }
}

// Private DNS zone so the Container Apps resolve the server's private address rather than a public
// endpoint. The zone name is fixed by Azure for PostgreSQL private access.
resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresPrivateDnsZone
  name: '${name}-link'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

output id string = virtualNetwork.id
output name string = virtualNetwork.name
output appsSubnetId string = virtualNetwork.properties.subnets[0].id
output postgresSubnetId string = virtualNetwork.properties.subnets[1].id
output postgresPrivateDnsZoneId string = postgresPrivateDnsZone.id
