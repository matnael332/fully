metadata description = '''
One Container Apps Job, reusable for scheduled and manually triggered work.

This is the foundation for the scheduled and synthetic jobs the architecture calls for. Gate 1E
defines one concrete job — the schema migration — because Gate 1D established migration as an explicit
deployment step rather than something an application does while starting up. Synthetic pilot jobs are
defined by their own Gate; this module exists so they need no new infrastructure pattern.
'''

@description('Job name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Container Apps environment id.')
param environmentId string

@description('User-assigned managed identity resource id.')
param identityId string

@description('Registry login server.')
param registryLoginServer string

@description('Container image reference, including tag.')
param image string

@description('Trigger type. Manual jobs run only when invoked, which suits a migration.')
@allowed([
  'Manual'
  'Schedule'
])
param triggerType string = 'Manual'

@description('Cron expression, used only when the trigger type is Schedule.')
param cronExpression string = '0 3 * * *'

@description('Command arguments passed to the container entry point.')
param commandArguments array = []

@description('Plain (non-secret) environment variables as a list of { name, value }.')
param environmentVariables array = []

@description('Secret references as a list of { name, keyVaultUri, environmentVariable }.')
param secretReferences array = []

@description('Seconds a single execution may run before it is stopped.')
param replicaTimeoutSeconds int = 600

@description('Retry attempts for a failed execution.')
@minValue(0)
param replicaRetryLimit int = 1

@description('CPU cores.')
param cpu string = '0.5'

@description('Memory, for example 1Gi.')
param memory string = '1Gi'

var secretDefinitions = [
  for reference in secretReferences: {
    name: reference.name
    keyVaultUrl: reference.keyVaultUri
    identity: identityId
  }
]

var secretEnvironmentVariables = [
  for reference in secretReferences: {
    name: reference.environmentVariable
    secretRef: reference.name
  }
]

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: triggerType
      replicaTimeout: replicaTimeoutSeconds
      replicaRetryLimit: replicaRetryLimit
      manualTriggerConfig: triggerType == 'Manual' ? {
        parallelism: 1
        replicaCompletionCount: 1
      } : null
      scheduleTriggerConfig: triggerType == 'Schedule' ? {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      } : null
      registries: [
        {
          server: registryLoginServer
          identity: identityId
        }
      ]
      secrets: secretDefinitions
    }
    template: {
      containers: [
        {
          name: name
          image: image
          args: commandArguments
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(environmentVariables, secretEnvironmentVariables)
        }
      ]
    }
  }
}

output id string = job.id
output name string = job.name
