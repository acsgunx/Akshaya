// ==================================================================================================
// Akshaya on Azure App Service (Linux container).
//
// Provisions the smallest thing that can serve the application: one App Service plan, one web app,
// and a container registry to push the image to. NO DATABASE — Persistence:Mode defaults to Sqlite
// and the file lives under /home, which App Service backs with Azure Storage on every tier.
// ==================================================================================================

@description('Globally unique name for the web app. Becomes <appName>.azurewebsites.net.')
param appName string

@description('Azure region. Put it near the broker\'s API, not near yourself.')
param location string = resourceGroup().location

@description('F1 is free but idles out and cannot set alwaysOn. B1 is the cheapest always-on tier.')
@allowed([ 'F1', 'B1', 'B2', 'S1' ])
param sku string = 'B1'

@description('Master key for the saved-broker-credential vault. Generate with: openssl rand -base64 32. Losing it makes every saved broker credential unreadable.')
@secure()
param credentialKey string

@description('Address the first account is seeded with when the identity store is empty. Its password is generated at startup and written to the log once.')
param seedUserEmail string = 'demo@akshaya.local'

var registryName = replace('${appName}acr', '-', '')
var isFreeTier = sku == 'F1'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    // Basic is ~$5/month and is the only tier that matters for a single image. The alternative —
    // pushing to Docker Hub and pulling from there — is free; see the README if $5 is the
    // difference that matters.
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  sku: {
    name: sku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/akshaya:latest'

      // Not available on F1, and setting it there fails the deployment rather than being ignored.
      alwaysOn: !isFreeTier

      // SignalR streams ticks over a WebSocket. Without this the client silently falls back to
      // long polling and every tick costs an HTTP round trip.
      webSocketsEnabled: true

      // The whole application is one process holding in-memory order and position state. Two
      // instances would serve different views of the same account depending on which one the load
      // balancer picked, so pin affinity off and instance count to one.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/health/ready'

      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: '8080'
        }
        {
          // THE SETTING THAT MAKES /home DURABLE for a container. Without it the SQLite file lands
          // on the container's own layer and every restart is a fresh, empty database.
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'true'
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'Persistence__Mode'
          value: 'Sqlite'
        }
        {
          name: 'Persistence__SqlitePath'
          value: '/home/data/akshaya-identity.db'
        }
        {
          name: 'Persistence__SeedUser__Email'
          value: seedUserEmail
        }
        {
          // The Angular app is served from this same origin — there is no cross-origin caller.
          name: 'Cors__AllowedOrigin'
          value: 'none'
        }
        {
          name: 'CredentialProtection__ActiveKeyId'
          value: 'prod-1'
        }
        {
          name: 'CredentialProtection__Keys__prod-1'
          value: credentialKey
        }
        {
          name: 'DOCKER_REGISTRY_SERVER_URL'
          value: 'https://${registry.properties.loginServer}'
        }
        {
          name: 'DOCKER_REGISTRY_SERVER_USERNAME'
          value: registry.listCredentials().username
        }
        {
          name: 'DOCKER_REGISTRY_SERVER_PASSWORD'
          value: registry.listCredentials().passwords[0].value
        }
      ]
    }
  }
}

// Session affinity off: there is one instance, so there is nothing to be affine to, and the cookie
// only confuses caches.
resource affinity 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'web'
  properties: {
    clientAffinityEnabled: false
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
