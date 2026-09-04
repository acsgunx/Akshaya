# Azure App Service (Linux container)

**$0 on the F1 free tier, ~$13/month on B1.** The best-supported .NET target, and the only one
here where the free tier still gives you durable storage.

## Why `/home` matters

App Service mounts `/home` from Azure Storage on **every tier, including Free**. That is unusual —
most free tiers give you an ephemeral filesystem — and it is why the SQLite file goes there:

```
Persistence__SqlitePath=/home/data/akshaya-identity.db
```

Accounts and saved credentials then survive restarts, redeploys and tier changes with no volume to
provision. `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` (set by the Bicep template) is what makes
`/home` persistent for a container; without it you get a container-local directory that looks like
it works until the first restart.

## Deploy

```bash
az group create --name akshaya-rg --location centralindia

az deployment group create \
  --resource-group akshaya-rg \
  --template-file deploy/azure-app-service/main.bicep \
  --parameters appName=akshaya-<something-unique> \
               sku=B1 \
               credentialKey="$(openssl rand -base64 32)"
```

Then build and push the image to the registry the template created, and point the app at it:

```bash
ACR=$(az acr list -g akshaya-rg --query "[0].name" -o tsv)
az acr build --registry "$ACR" --image akshaya:latest --file deploy/Dockerfile .
az webapp restart -g akshaya-rg -n akshaya-<something-unique>
```

## Get the seeded account's password

```bash
az webapp log tail -g akshaya-rg -n akshaya-<something-unique> | grep "generated password"
```

## Tier notes

| SKU | Monthly | Always on | Custom domain + TLS |
|---|---|---|---|
| `F1` | $0 | No — 60 CPU-min/day quota, idles out | Domain yes, managed certificate no |
| `B1` | ~$13 | Yes (`alwaysOn`) | Yes, free managed certificate |

On F1 the app idles out and the in-memory order and position views are rebuilt on the next
request; accounts persist regardless because they are on `/home`. `alwaysOn` is not available on
F1 — the template sets it only for paid SKUs, because setting it on F1 fails the deployment.

## Switching to Postgres

```bash
az postgres flexible-server create -g akshaya-rg -n akshaya-pg \
  --tier Burstable --sku-name Standard_B1ms --storage-size 32

az webapp config appsettings set -g akshaya-rg -n <app> --settings \
  Persistence__Mode=Postgres \
  ConnectionStrings__Identity="Host=akshaya-pg.postgres.database.azure.com;Database=akshaya;Username=<u>;Password=<p>;SSL Mode=Require"
```

Adds roughly $13–15/month. The migrations run automatically on the next restart.
