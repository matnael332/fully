metadata description = 'Log Analytics workspace: the single telemetry sink for an environment.'

@description('Workspace name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Retention in days. Dev and Synthetic keep telemetry briefly; this is not the raw work-data lifecycle.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

@description('Daily ingestion cap in GB. A cap is set so a misbehaving component cannot run up unbounded cost.')
param dailyQuotaGb int = 1

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    // Local (shared-key) authentication stays enabled on purpose. The Container Apps environment's
    // log-analytics destination ingests console and system logs with the workspace shared key, so
    // disabling it here would silently break container logging. The key is read inside the
    // deployment via listKeys() and is never committed or output. Application telemetry still flows
    // through Application Insights, which the workloads reach with their managed identities.
  }
}

output id string = workspace.id
output name string = workspace.name
output customerId string = workspace.properties.customerId
