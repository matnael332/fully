metadata description = '''
Container Apps environment, joined to the environment's virtual network.

Egress runs through the apps subnet so the applications can reach the private PostgreSQL server, while
ingress stays external because the monday connector must be able to deliver webhooks from the internet.
Only the data plane is private.
'''

@description('Environment name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Log Analytics workspace id for container console and system logs.')
param logAnalyticsWorkspaceId string

@description('Log Analytics customer id.')
param logAnalyticsCustomerId string

@description('Subnet delegated to Microsoft.App/environments.')
param infrastructureSubnetId string

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = {
  name: last(split(logAnalyticsWorkspaceId, '/'))
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      // External ingress: the Web app must be reachable for inbound webhooks. The database is
      // private regardless, because it lives in its own delegated subnet with no public endpoint.
      internal: false
    }
    zoneRedundant: false
  }
}

output id string = environment.id
output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
output staticIp string = environment.properties.staticIp
