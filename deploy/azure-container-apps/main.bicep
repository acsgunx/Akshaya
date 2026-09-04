// ==================================================================================================
// Akshaya on Azure Container Apps (Consumption).
//
// The cheapest Azure option at idle — the monthly free grant covers a low-traffic personal
// deployment outright — at the cost of a real trade-off: Container Apps wants to scale to zero, and
// this application keeps orders, positions and the kill switch in process memory.
//
// minReplicas therefore defaults to 1 here. Set it to 0 only for a demo you do not trade from; see
// "Why sleeping hurts" in ../README.md.
//
// An Azure Files share is mounted at /data for the SQLite file. Container Apps has no local
// persistent disk: without this mount, every revision starts with an empty identity database.
// ==================================================================================================

@description('Name of the container app. Becomes part of the generated FQDN.')
param appName string = 'akshaya'

@description('Azure region.')
param location string = resourceGroup().location

@description('Fully qualified image reference, e.g. myregistry.azurecr.io/akshaya:latest or docker.io/me/akshaya:latest.')
param containerImage string

@description('Master key for the saved-broker-credential vault. Generate with: openssl rand -base64 32.')
@secure()
param credentialKey string

@description('Set to 0 to scale to zero when idle. Cheaper, but every in-memory order view and open SignalR subscription is lost on each idle period.')
@allowed([ 0, 1 ])
param minReplicas int = 1

@description('Address the first account is seeded with when the identity store is empty.')
param seedUserEmail string = 'demo@akshaya.local'

var storageAccountName = toLower(take(replace('${appName}stor${uniqueString(resourceGroup().id)}', '-', ''), 24))
var fileShareName = 'akshaya-data'
var storageMountName = 'akshaya-data-mount'

// Container Apps requires a Log Analytics workspace. It is also the only place the seeded
// account's generated password is ever written — see the README for the query that reads it back.
resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    // The free grant covers 5GB/month; 30 days is more history than a single-instance deployment
    // will ever fill.
    retentionInDays: 30
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    // Locally redundant is the cheapest and is correct here: the file it holds is two tables that
    // a regional outage would take the compute down alongside anyway.
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileServices
  name: fileShareName
  properties: {
    // The smallest quota Azure Files accepts. The database is measured in kilobytes.
    shareQuota: 1
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${appName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource envStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: environment
  name: storageMountName
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: fileShareName
      // ReadWrite, obviously — but worth stating, because the default is ReadOnly and SQLite's
      // failure mode against a read-only mount is "unable to open database file", which reads like
      // a missing file rather than a permission.
      accessMode: 'ReadWrite'
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // SignalR holds a long-lived connection per client; the default 240s is fine, but sticky
        // sessions are pointless at one replica and actively wrong at more than one.
        stickySessions: {
          affinity: 'none'
        }
      }
      secrets: [
        {
          name: 'credential-key'
          value: credentialKey
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            // The smallest CPU/memory pair Container Apps allows. Consumption bills per
            // vCPU-second, so this is also the cheapest thing that runs.
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          volumeMounts: [
            {
              volumeName: 'data'
              mountPath: '/data'
            }
          ]
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'Persistence__Mode', value: 'Sqlite' }
            { name: 'Persistence__SqlitePath', value: '/data/akshaya-identity.db' }
            { name: 'Persistence__SeedUser__Email', value: seedUserEmail }
            { name: 'Cors__AllowedOrigin', value: 'none' }
            { name: 'CredentialProtection__ActiveKeyId', value: 'prod-1' }
            {
              name: 'CredentialProtection__Keys__prod-1'
              secretRef: 'credential-key'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              initialDelaySeconds: 20
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              // Generous, because this probe touches the identity store and the Azure Files mount
              // is slower to come good than a local disk on a cold revision.
              initialDelaySeconds: 30
              periodSeconds: 30
              failureThreshold: 5
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'data'
          storageType: 'AzureFile'
          storageName: storageMountName
        }
      ]
      scale: {
        minReplicas: minReplicas
        // ONE, not more. Orders, positions and the kill switch are per-process in-memory state, so
        // a second replica serves a different view of the same account. Raising this requires
        // moving those stores to Redis or Postgres first.
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    envStorage
  ]
}

output appUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output logAnalyticsWorkspaceId string = logs.properties.customerId
