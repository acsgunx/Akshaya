# Azure App Service

**$0 on the F1 free tier, ~$13/month on B1.** The best-supported .NET target, and the only one in
`deploy/` where the free tier still gives you durable storage.

There are two ways in. They produce the same running application.

| | **Path A — code, from GitHub Actions** | **Path B — container, from Bicep** |
|---|---|---|
| What is deployed | A published .NET folder, zipped | The image built by `deploy/Dockerfile` |
| Runtime | App Service's built-in .NET 10 stack | Your own image |
| Extra cost | None | ~$5/month for the container registry |
| Works on F1 | Yes | Depends on region and tier — check before committing |
| Deploys on `git push` | Yes | Push the image yourself, or wire up a webhook |
| Setup | One script in Cloud Shell, five GitHub variables | `az deployment group create`, then `az acr build` |

**Start with Path A.** Path B is worth it when you want the deployed artefact to be byte-identical
to what you run locally with `docker run`, or when you need a runtime App Service does not offer.

---

# Path A — deploy from GitHub Actions

[`.github/workflows/deploy-azure.yml`](../../.github/workflows/deploy-azure.yml) builds the Angular
app, publishes the API, drops the bundle into the publish output's `wwwroot`, and zips the folder up
to App Service. One service, one origin, no CORS, no database server, no registry.

Authentication is OIDC: GitHub mints a short-lived token for each run and Azure trades it for an
access token. **There is no password, publish profile or client secret stored in this repository**,
and nothing that expires and has to be rotated.

## Before you start

- An Azure account with an active subscription — you already have one at
  [portal.azure.com](https://portal.azure.com).
- Push access to this repository on GitHub, and permission to change its Actions settings.
- Nothing installed locally. Every Azure command below runs in the browser.

Total time: about ten minutes, most of it waiting for the first build.

## Step 1 — open Cloud Shell

In the Azure portal, click the **`>_`** icon in the top toolbar. Choose **Bash** if it asks. The
first launch offers to create a storage account for it — accept; it costs a few cents a month.

You now have a shell that is already signed in to your subscription, with the Azure CLI and
`openssl` installed. Nothing else needs to be.

If you have more than one subscription, pick the one to deploy into:

```bash
az account list --output table
```

```bash
az account set --subscription "<subscription name or id>"
```

## Step 2 — run the setup script

Replace `<owner>/<repo>` with this repository (for example `acsgunx/Akshaya`) and `<app-name>` with
a name that is unique across all of Azure — it becomes `<app-name>.azurewebsites.net`, so
`akshaya-` plus something of your own is a good pattern.

```bash
curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/deploy/azure-app-service/setup.sh -o setup.sh && chmod +x setup.sh
```

```bash
./setup.sh --app <app-name> --repo <owner>/<repo>
```

It takes a couple of minutes and creates:

| Resource | Why |
|---|---|
| Resource group `akshaya-rg` | One container for everything, so teardown is one command |
| Linux App Service plan, F1 | The compute. F1 is free |
| The web app, on the .NET 10 stack | Serves the API and the Angular app from one origin |
| Application settings | Persistence mode and path, the vault key, the seeded account's address |
| A user-assigned managed identity | What GitHub Actions signs in as. No secret exists |
| A federated credential on it | Trusts *this repo, this branch* and nothing else |
| A `Website Contributor` role assignment | Scoped to the one web app, not the subscription |

Useful flags: `--sku B1` for the always-on tier, `--location westeurope` to put it elsewhere,
`--branch develop` to deploy from another branch, `--seed-email you@example.com` for the first
account's address. `./setup.sh --help` lists them all.

The script is safe to re-run — it reuses an app that already exists and never regenerates the
credential-protection key.

At the end it prints five values. Leave the Cloud Shell tab open.

## Step 3 — put those five values into GitHub

In the repository on GitHub: **Settings → Secrets and variables → Actions → Variables** tab →
**New repository variable**, five times.

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | printed by the script |
| `AZURE_TENANT_ID` | printed by the script |
| `AZURE_SUBSCRIPTION_ID` | printed by the script |
| `AZURE_WEBAPP_NAME` | the `--app` name you chose |
| `AZURE_RESOURCE_GROUP` | `akshaya-rg`, unless you changed it |

These are **variables, not secrets**. With OIDC none of them grants access on its own; the client id
is useless to anyone who cannot also produce a GitHub token for this repository and branch. The
workflow reads secrets of the same names as a fallback, so putting them in the Secrets tab works
too — it is just unnecessary.

The script also prints the equivalent `gh variable set` commands if you have the GitHub CLI.

## Step 4 — run the deploy

**Actions** tab → **Deploy to Azure App Service** → **Run workflow** → **Run workflow**.

The first run takes five to eight minutes: `npm ci`, the production Angular build, the .NET publish,
the upload, and then polling `/health/ready` until the site answers. The last line of the log is the
URL.

The workflow fails fast and with an explanation if a variable is missing, if the Angular bundle did
not land in `wwwroot`, or if the identity database is configured somewhere a deploy would erase it.

## Step 5 — sign in

On an empty identity store the API creates one account and writes its generated password to the log
**once**, at `Warning` level. Back in Cloud Shell:

```bash
az webapp log tail -g akshaya-rg -n <app-name> | grep -i "generated password"
```

Leave it running for a few seconds; press `Ctrl+C` when the line appears. Then open
`https://<app-name>.azurewebsites.net/` and sign in as `demo@akshaya.local` with that password.

**There is no change-password endpoint in the application today**, so that generated password is the
one the account keeps. If you would rather choose it yourself, set it *before* the first deploy:

```bash
az webapp config appsettings set -g akshaya-rg -n <app-name> \
  --settings Persistence__SeedUser__Password="<a password from your password manager>"
```

Seeding only ever happens into an **empty** store. Deleting the account does not bring it back.

## Step 6 — deploy on every push

Once a manual run has worked, add one more repository variable:

| Variable | Value |
|---|---|
| `AZURE_AUTODEPLOY` | `true` |

Every push to `main` now deploys. Until that variable exists the workflow only runs when you ask it
to, so a fork or a half-configured clone does not fail a red X on every push.

---

## The two settings that are load-bearing

**`Persistence__SqlitePath=/home/data/akshaya-identity.db`.** App Service mounts `/home` from Azure
Storage on **every tier, including Free** — that is unusual, and it is why no volume needs
provisioning. But a zip deploy **replaces `/home/site/wwwroot` wholesale**, and the application's
default path is relative to that folder. Left on the default, every deploy would delete every
account and every saved broker credential. `/home/data` is durable *and* outside the deployment
folder. The workflow refuses to deploy if this setting is missing or points inside `wwwroot`.

**`--web-sockets-enabled true`.** SignalR streams ticks over a WebSocket. Without it the client
silently falls back to long polling, which works — every tick just costs an HTTP round trip.

## What it costs

| | F1 | B1 |
|---|---|---|
| Compute | $0 | ~$13/month |
| Always on | No — 60 CPU-minutes/day, idles out | Yes |
| Custom domain | Yes | Yes |
| Free managed TLS certificate | No | Yes |
| Health-check probe | Not available | Enabled by the script |
| Storage for the SQLite file | 1 GB on `/home` | 10 GB on `/home` |

Nothing else on the bill: no database server, no container registry, no second host for the
frontend. Cloud Shell's storage account is a few cents a month.

**On F1 the app idles out.** Accounts and saved credentials survive because they are on `/home`, but
orders, positions, risk policies and the kill switch are all in-memory and are rebuilt from the
broker on the next request, and open SignalR subscriptions have to renegotiate. That is fine for
trying the application out. For anything you intend to actually trade from, move to B1:

```bash
az appservice plan update -g akshaya-rg -n <app-name>-plan --sku B1
```

```bash
az webapp config set -g akshaya-rg -n <app-name> --always-on true \
  --generic-configurations '{"healthCheckPath": "/health/ready"}'
```

## When something goes wrong

| Symptom | Cause |
|---|---|
| `Missing repository variables: …` | Step 3 was skipped, or a name is misspelled. The message lists them |
| `AADSTS70021: No matching federated identity record found` | The branch does not match. The script trusts one branch — re-run it with `--branch <name>` |
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL` | The workflow's `permissions: id-token: write` was removed |
| `AuthorizationFailed` on the first deploy | Role assignments take a minute or two to propagate. Re-run the workflow |
| `Persistence__SqlitePath is …` and the deploy stops | Exactly the guard described above. Set the setting as the message says |
| Deploy is green, the site returns 500 | `az webapp log tail -g akshaya-rg -n <app-name>` — usually a missing `CredentialProtection` key |
| The site never becomes ready on F1 | The daily CPU quota is spent. It resets at midnight UTC, or move to B1 |
| Ticks arrive but slowly | WebSockets are off. `az webapp config set … --web-sockets-enabled true` |

Read the application's own log stream any time with:

```bash
az webapp log tail -g akshaya-rg -n <app-name>
```

## Removing it all

```bash
az group delete --name akshaya-rg --yes --no-wait
```

That deletes the app, the plan, the identity and the database with it. The GitHub variables are
harmless once the Azure side is gone, but delete them too if you are not coming back.

---

# Path B — container, from Bicep

Deploys the image `deploy/Dockerfile` builds, via a container registry the template provisions. Use
this when you want the deployed artefact to be identical to what `docker run` gives you locally.

```bash
az group create --name akshaya-rg --location centralindia
```

```bash
az deployment group create \
  --resource-group akshaya-rg \
  --template-file deploy/azure-app-service/main.bicep \
  --parameters appName=akshaya-<something-unique> \
               sku=B1 \
               credentialKey="$(openssl rand -base64 32)"
```

Then build and push the image to the registry the template created:

```bash
ACR=$(az acr list -g akshaya-rg --query "[0].name" -o tsv)
az acr build --registry "$ACR" --image akshaya:latest --file deploy/Dockerfile .
az webapp restart -g akshaya-rg -n akshaya-<something-unique>
```

The site returns an error page between the two commands — the app exists but its image does not
yet. That is expected.

Get the seeded account's password the same way as Path A:

```bash
az webapp log tail -g akshaya-rg -n akshaya-<something-unique> | grep "generated password"
```

Notes specific to this path:

- The registry is **Basic**, ~$5/month, and is the only line on the bill that Path A does not have.
  Pushing to Docker Hub instead is free; set `DOCKER_REGISTRY_SERVER_URL`/`USERNAME`/`PASSWORD`
  accordingly.
- `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` (set by the template) is what makes `/home` persistent
  **for a container**. Without it you get a container-local directory that looks like it works until
  the first restart.
- `alwaysOn` is not available on F1, and setting it there fails the deployment rather than being
  ignored — the template sets it only for paid SKUs.

---

## Switching to Postgres

Neither path needs a rebuild or a redeploy. Set two settings and restart:

```bash
az postgres flexible-server create -g akshaya-rg -n akshaya-pg \
  --tier Burstable --sku-name Standard_B1ms --storage-size 32
```

```bash
az webapp config appsettings set -g akshaya-rg -n <app-name> --settings \
  Persistence__Mode=Postgres \
  ConnectionStrings__Identity="Host=akshaya-pg.postgres.database.azure.com;Database=akshaya;Username=<u>;Password=<p>;SSL Mode=Require"
```

Adds roughly $13–15/month. The EF migrations run automatically on the next start, and the workflow's
SQLite-path guard steps aside once the mode is `Postgres`. There is no automatic migration of
existing SQLite data — export any real accounts before switching.
