metadata description = '''
One Container App, reusable for the Web/API and the Worker.

The app runs under a user-assigned managed identity that it also uses to pull from the private
registry, so no registry password exists anywhere. Secrets are referenced from Key Vault by URI and
resolved at runtime with that same identity: no secret value passes through this template.
'''

@description('App name.')
param name string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Container Apps environment id.')
param environmentId string

@description('User-assigned managed identity resource id.')
param identityId string

@description('Registry login server, for example mycr.azurecr.io.')
param registryLoginServer string

@description('Container image reference, including tag.')
param image string

@description('Whether this app accepts external HTTP traffic.')
param externalIngress bool = false

@description('Container port for ingress. Ignored when ingress is disabled.')
param targetPort int = 8080

@description('Whether to expose ingress at all. The Worker has none.')
param enableIngress bool = true

@description('Plain (non-secret) environment variables as a list of { name, value }.')
param environmentVariables array = []

@description('''
Secret references as a list of { name, keyVaultUri, environmentVariable }. Each becomes a Container
Apps secret resolved from Key Vault with the app identity, then surfaced as an environment variable.
''')
param secretReferences array = []

@description('Minimum replicas. Zero lets a Consumption app scale to nothing when idle, which costs nothing.')
@minValue(0)
param minReplicas int = 0

@description('Maximum replicas.')
@minValue(1)
param maxReplicas int = 2

@description('CPU cores.')
param cpu string = '0.25'

@description('Memory, for example 0.5Gi.')
param memory string = '0.5Gi'

@description('Liveness probe path. Empty disables the probe.')
param livenessPath string = ''

@description('Readiness probe path. Empty disables the probe.')
param readinessPath string = ''

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

var probes = concat(
  empty(livenessPath) ? [] : [
    {
      type: 'Liveness'
      httpGet: {
        path: livenessPath
        port: targetPort
        scheme: 'HTTP'
      }
      initialDelaySeconds: 10
      periodSeconds: 30
      failureThreshold: 3
    }
  ],
  empty(readinessPath) ? [] : [
    {
      type: 'Readiness'
      httpGet: {
        path: readinessPath
        port: targetPort
        scheme: 'HTTP'
      }
      initialDelaySeconds: 5
      periodSeconds: 15
      failureThreshold: 6
    }
  ]
)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
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
      activeRevisionsMode: 'Single'
      ingress: enableIngress ? {
        external: externalIngress
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      } : null
      registries: [
        {
          server: registryLoginServer
          // Managed identity pull. There is deliberately no passwordSecretRef: the registry has no
          // admin user, so no credential exists to misplace.
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
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(environmentVariables, secretEnvironmentVariables)
          probes: probes
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output id string = app.id
output name string = app.name
output fqdn string = enableIngress ? app.properties.configuration.ingress.fqdn : ''
