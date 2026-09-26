metadata description = '''
Azure Database for PostgreSQL Flexible Server, private to the environment's virtual network.

pgvector is made *available* here by adding "vector" to the server's azure.extensions allowlist,
which is the server-side prerequisite. Creating the extension inside the database remains the
application migration's job, exactly as in Gate 1D; this template does not duplicate or replace that.

The Gate 1D role model is preserved unchanged: the administrator login provisioned here is the schema
owner used for migrations and maintenance only, and the non-owner runtime role that row-level security
depends on is created inside the database. Entra authentication is enabled alongside password
authentication so a later Gate can move workloads to managed-identity database access without
re-provisioning the server, but nothing here forces that change.
'''

@description('Server name. Globally unique, lowercase.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Compute SKU. Burstable is adequate for Dev and Synthetic.')
param skuName string = 'Standard_B1ms'

@description('Compute tier.')
@allowed([
  'Burstable'
  'GeneralPurpose'
  'MemoryOptimized'
])
param skuTier string = 'Burstable'

@description('Storage in GB.')
param storageSizeGb int = 32

@description('PostgreSQL major version. Matches the version Gate 1D validated locally.')
@allowed([
  '16'
  '17'
])
param postgresVersion string = '17'

@description('Backup retention in days.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

@description('Administrator login name. The schema owner used for migrations only.')
param administratorLogin string

@description('Administrator password. Supplied at deployment time; never stored in source or in template output.')
@secure()
param administratorPassword string

@description('Application database name.')
param databaseName string

@description('Delegated subnet for private access.')
param delegatedSubnetId string

@description('Private DNS zone used to resolve the server privately.')
param privateDnsZoneId string

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: postgresVersion
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: storageSizeGb
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    // Private access: the server has no public endpoint at all, so it is reachable only from the
    // delegated subnet in this environment's virtual network.
    network: {
      delegatedSubnetResourceId: delegatedSubnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
      publicNetworkAccess: 'Disabled'
    }
    highAvailability: {
      // Single zone for the foundation environments; the Human Pilot Gate revisits this.
      mode: 'Disabled'
    }
    authConfig: {
      // Password authentication carries the Gate 1D owner/runtime role split. Entra is enabled so a
      // later Gate can adopt managed-identity database access without re-provisioning.
      passwordAuth: 'Enabled'
      activeDirectoryAuth: 'Enabled'
      tenantId: subscription().tenantId
    }
    createMode: 'Default'
  }
}

// Server-side prerequisite for pgvector. Without "vector" on this allowlist, the migration's
// CREATE EXTENSION would be refused no matter what privileges the connecting role holds.
resource extensionAllowlist 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'azure.extensions'
  properties: {
    value: 'VECTOR'
    source: 'user-override'
  }
}

// Require TLS for every client connection.
resource requireSecureTransport 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'require_secure_transport'
  properties: {
    value: 'ON'
    source: 'user-override'
  }
  dependsOn: [
    extensionAllowlist
  ]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

output id string = server.id
output name string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
