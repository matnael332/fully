// Dev environment.
//
// Everything that differs between Dev and Synthetic is a parameter here. The infrastructure itself is
// one set of templates. The two environments never share a database, a secret, an identity or an
// address space: each gets its own resource group and its own instance of every resource, and the
// secrets are read from environment-specific variables so a Dev password can never be handed to
// Synthetic by accident.

using '../main.bicep'

param environmentName = 'dev'
// North Europe, product-owner decision of 2026-09-27: Israel Central does not offer Azure Container
// Apps, and West Europe is not accepting new customers on this subscription. Validation passed here.
param location = 'northeurope'
param regionToken = 'neu'

// 10.10.0.0/16 for Dev. Synthetic uses 10.20.0.0/16, so the two can never be peered into one network.
param vnetAddressPrefix = '10.10.0.0/16'
param appsSubnetPrefix = '10.10.0.0/23'
param postgresSubnetPrefix = '10.10.2.0/28'

param postgresAdministratorLogin = 'wce_admin'
param databaseName = 'workcontinuity'
param postgresRuntimeRoleName = 'workcontinuity_app'

// Secrets are supplied by the operator's environment at deployment time and are never written here.
// The variable names are environment-specific on purpose. The empty default lets the parameter file
// compile for local validation; eng/infra.ps1 refuses to validate, what-if or deploy while either
// variable is unset, and Azure itself rejects an empty administrator password.
param postgresAdministratorPassword = readEnvironmentVariable('WCE_DEV_POSTGRES_ADMIN_PASSWORD', '')
param postgresRuntimeRolePassword = readEnvironmentVariable('WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD', '')

// Smallest supported footprint. Dev exists to prove the foundation, not to carry load.
param postgresSkuName = 'Standard_B1ms'
param postgresSkuTier = 'Burstable'
param postgresStorageSizeGb = 32
param maxReplicas = 2
param logRetentionInDays = 30
param logDailyQuotaGb = 1

// Images come from the operator's environment, like the passwords, and default to the placeholder.
// eng/infra.ps1 adopts the live image when a variable is unset and the resource already runs a real
// image, so an infrastructure redeploy can never silently revert Web, Worker or the migration job
// (Gate 1F image-drift guard). The migration job image is deliberately separate: GitHub CD and a
// Web/Worker rollout never touch it.
param webImage = readEnvironmentVariable('WCE_DEV_WEB_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')
param workerImage = readEnvironmentVariable('WCE_DEV_WORKER_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')
param migrationJobImage = readEnvironmentVariable('WCE_DEV_MIGRATION_JOB_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')

// GitHub Actions continuous delivery (Gate 1F). The numeric ids make the OIDC subject immutable.
param githubRepository = 'matnael332/fully'
param githubOwnerId = '223243154'
param githubRepositoryId = '1390662248'
param githubBranch = 'main'

param additionalTags = {
  dataBoundary: 'dev'
  purpose: 'foundation-development'
}
