# Azure Container Apps

**$0/month within the Consumption free grant** (180,000 vCPU-seconds and 360,000 GiB-seconds per
month, which a single 0.25-vCPU replica does not exhaust) — but read the trade-off first.

## The trade-off

Container Apps is built around scaling to zero. This application keeps orders, positions, the risk
policy and the kill switch **in process memory**. Every scale-to-zero throws them away and drops
open SignalR subscriptions.

`main.bicep` therefore defaults `minReplicas` to **1**, which keeps one replica warm and puts you
just over the free grant (roughly $8–12/month). Pass `minReplicas=0` if this is a demo you do not
trade from:

```bash
--parameters minReplicas=0
```

Accounts and saved broker credentials survive either way — they are on the Azure Files mount, not
in memory.

## Storage

Container Apps has no local persistent disk. An Azure Files share is mounted at `/data` and the
SQLite file lives there. Without that mount, every new revision starts with an empty identity
database and you would be re-registering after each deploy.

Azure Files is slower than a local disk. For two tables read on sign-in it does not matter; it is
the reason the readiness probe is given 30 seconds before its first check.

## Deploy

```bash
az group create --name akshaya-rg --location centralindia

# Build and push the image somewhere the environment can pull from.
az acr create -g akshaya-rg -n akshayaacr --sku Basic --admin-enabled true
az acr build --registry akshayaacr --image akshaya:latest --file deploy/Dockerfile .

az deployment group create \
  --resource-group akshaya-rg \
  --template-file deploy/azure-container-apps/main.bicep \
  --parameters containerImage=akshayaacr.azurecr.io/akshaya:latest \
               credentialKey="$(openssl rand -base64 32)" \
               minReplicas=1
```

If the registry is private, grant the app pull access (`az containerapp registry set`) or use a
public image reference.

## Get the seeded account's password

Container Apps sends stdout to Log Analytics, which is the only place it is written:

```bash
az containerapp logs show -g akshaya-rg -n akshaya --tail 200 | grep "generated password"
```

## Costs

| Item | Monthly |
|---|---|
| One 0.25 vCPU / 0.5GiB replica, always on | ~$8–12 after the free grant |
| Same, `minReplicas=0`, low traffic | $0 — within the free grant |
| Azure Files, 1GiB | ~$0.06 |
| Log Analytics | $0 — within the 5GB/month free tier |

## Switching to Postgres

```bash
az containerapp update -g akshaya-rg -n akshaya \
  --set-env-vars Persistence__Mode=Postgres \
                 ConnectionStrings__Identity="Host=...;Database=akshaya;Username=...;Password=...;SSL Mode=Require"
```

With Postgres you can drop the Azure Files mount entirely — nothing else writes to disk.
