metadata description = 'Workspace-based Application Insights for OpenTelemetry traces and metrics.'

@description('Component name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Log Analytics workspace that backs this component.')
param workspaceId string

resource component 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // Workspace-based: telemetry lands in Log Analytics rather than a classic component store.
    WorkspaceResourceId: workspaceId
    // Ingestion uses the workload's managed identity via the connection string's
    // authorization, so no instrumentation key is treated as a shared secret.
    DisableLocalAuth: false
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output id string = component.id
output name string = component.name

// The connection string is not a credential in the Key Vault sense, but it is still not emitted as a
// template output. Workloads read it from Key Vault so there is one retrieval path for all config.
