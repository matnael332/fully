metadata description = '''
Azure Service Bus Standard namespace and the three canonical queues.

These are the real Azure settings and are deliberately not constrained by the local emulator, whose
1-hour message TTL and 5-minute duplicate-detection ceilings are emulator limits repaired in Gate 1D.

Sessions are enabled only where the frozen ordering design needs them. Ordering is per object, not
global: event-processing must preserve order for a given work object, and an action's lifecycle must
not be reordered, so both use sessions. AI jobs are independent units of work, so requiring sessions
there would serialise unrelated work for no correctness gain.

Duplicate detection is defence in depth only. Application-level idempotency remains mandatory
regardless, because a provider may redeliver outside any broker window.
'''

@description('Namespace name. Globally unique.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Duplicate detection window. Broad enough to absorb realistic provider redelivery.')
param duplicateDetectionWindow string = 'PT1H'

@description('Maximum delivery attempts before a message is dead-lettered.')
@minValue(1)
@maxValue(2000)
param maxDeliveryCount int = 10

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    // Standard: topics, duplicate detection and sessions. Premium is not needed to prove the
    // foundation and costs substantially more.
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Local (SAS) authentication is disabled: workloads connect with managed identity, so there is
    // no namespace connection string to store or rotate.
    disableLocalAuth: true
    zoneRedundant: false
  }
}

resource eventProcessing 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: 'event-processing'
  properties: {
    // Sessions preserve per-object ordering through the pipeline.
    requiresSession: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT5M'
    enablePartitioning: false
    enableBatchedOperations: true
  }
}

resource aiJobs 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: 'ai-jobs'
  properties: {
    // No sessions: AI work units are independent, and an AI failure must not block the fact layer.
    requiresSession: false
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    defaultMessageTimeToLive: 'P1D'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT5M'
    enablePartitioning: false
    enableBatchedOperations: true
  }
}

resource actionJobs 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: 'action-jobs'
  properties: {
    // Sessions keep one action's lifecycle transitions in order.
    requiresSession: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT5M'
    enablePartitioning: false
    enableBatchedOperations: true
  }
}

output id string = namespace.id
output name string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output queueNames array = [
  eventProcessing.name
  aiJobs.name
  actionJobs.name
]
