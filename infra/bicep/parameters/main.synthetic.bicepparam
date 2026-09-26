// Synthetic environment.
//
// Same templates as Dev, different parameters. Synthetic is a separate data boundary: its own resource
// group, database server, Service Bus namespace, Key Vault, registry, storage account, network and
// identities. Its secrets come from Synthetic-specific variables, so nothing here can ever resolve to a
// Dev credential. Production/Human-Pilot and Synthetic data must not share a trust boundary, and this
// file is where that separation is expressed for the Synthetic side.

using '../main.bicep'

param environmentName = 'synthetic'
param location = 'israelcentral'
param regionToken = 'ilc'

// 10.20.0.0/16 for Synthetic. Dev uses 10.10.0.0/16, so the two can never be peered into one network.
param vnetAddressPrefix = '10.20.0.0/16'
param appsSubnetPrefix = '10.20.0.0/23'
param postgresSubnetPrefix = '10.20.2.0/28'

param postgresAdministratorLogin = 'wce_admin'
param databaseName = 'workcontinuity'
param postgresRuntimeRoleName = 'workcontinuity_app'

// Synthetic-specific variables. See main.dev.bicepparam for why the default is empty and why
// eng/infra.ps1 refuses to proceed while either is unset.
param postgresAdministratorPassword = readEnvironmentVariable('WCE_SYNTHETIC_POSTGRES_ADMIN_PASSWORD', '')
param postgresRuntimeRolePassword = readEnvironmentVariable('WCE_SYNTHETIC_POSTGRES_RUNTIME_ROLE_PASSWORD', '')

// The synthetic pilot replays 20 workers over 31 days and runs fault-injection and security probes,
// so it is given more headroom than Dev. Still Burstable: this proves mechanics, not scale.
param postgresSkuName = 'Standard_B2s'
param postgresSkuTier = 'Burstable'
param postgresStorageSizeGb = 64
param maxReplicas = 4
param logRetentionInDays = 30
param logDailyQuotaGb = 2

// Placeholder images until the CI/CD Gate publishes real ones to the environment's registry.
param webImage = 'mcr.microsoft.com/k8se/quickstart:latest'
param workerImage = 'mcr.microsoft.com/k8se/quickstart:latest'

param additionalTags = {
  dataBoundary: 'synthetic'
  purpose: 'synthetic-pilot'
}
