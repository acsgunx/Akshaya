# Runbook — deploying to Azure App Service from a clean slate

Every command in order, what each one proves, and what to do when one fails. Written after a
deploy that took nine attempts; each failure below is one that actually happened, and the fix for
it is now in the repository.

Read [`README.md`](README.md) for why the design is what it is. This file is for getting it running.

Worked example throughout: app `akshaya-csg`, resource group `akshaya-rg`, repo `acsgunx/Akshaya`.
Substitute your own.

---

## Before you start

- Everything runs in **Azure Cloud Shell** — the `>_` icon in the portal toolbar. It is already
  signed in and has the Azure CLI, `git`, `gh` and `openssl`.
- **Path A only.** Path B builds a container with `az acr build`, which needs ACR Tasks — disabled
  on many trial, free and new pay-as-you-go subscriptions. Check before committing to it:
  `az acr check-health --yes`.

---

## 1. Clean slate

Skip only if this is a brand-new subscription with nothing provisioned.

```bash
az group delete --name akshaya-rg --yes
```

**Why:** a web app left over from Path B is configured as a *container* app pointing at an image
that was never pushed. `setup.sh` reuses an app it finds by name. It now corrects the stack when it
does, but starting clean avoids inheriting settings nobody intended — `WEBSITES_PORT` in
particular, which makes the platform probe a port nothing listens on and looks exactly like a
crashed application.

Nothing is lost unless you already have accounts you care about. If you do, skip this step.

---

## 2. Get the repository, on `main`

```bash
cd ~ && rm -rf Akshaya && git clone https://github.com/acsgunx/Akshaya.git && cd Akshaya
```

**Why `main` specifically:** the fixes for every failure in this runbook are merged there. An older
checkout reproduces them.

---

## 3. Provision

```bash
./deploy/azure-app-service/setup.sh --app akshaya-csg --repo acsgunx/Akshaya --sku B1
```

Creates the resource group, the Linux plan, the web app on the .NET 10 stack, every application
setting, the managed identity, **both** OIDC trust subjects, and container logging. Safe to re-run.

**It prints five values. Keep the terminal open.**

| If it fails with | Then |
|---|---|
| `does not offer a .NET 10 Linux runtime` | Check it yourself: `az webapp list-runtimes --os linux -o tsv \| grep -i dotnet`. If a `10.x` line is there, the matcher is at fault — report it. The script accepts both `DOTNETCORE:10.0` and `DOTNETCORE\|10.0` |
| `AuthorizationFailed` | Role assignments take a minute to propagate. Re-run |

---

## 4. Tell GitHub who to deploy as

From the same shell, substituting the three ids the script printed:

```bash
gh auth login
```

```bash
gh variable set AZURE_CLIENT_ID --body "<printed>" && gh variable set AZURE_TENANT_ID --body "<printed>" && gh variable set AZURE_SUBSCRIPTION_ID --body "<printed>" && gh variable set AZURE_WEBAPP_NAME --body "akshaya-csg" && gh variable set AZURE_RESOURCE_GROUP --body "akshaya-rg"
```

None is a secret. With OIDC there is no password to store.

---

## 5. Deploy

```bash
gh workflow run "Deploy to Azure App Service" --repo acsgunx/Akshaya
```

```bash
gh run watch --repo acsgunx/Akshaya
```

The workflow builds the Angular app, publishes the API, drops the bundle into the publish output's
`wwwroot`, zips it to App Service, then polls `/health/ready` for five minutes.

| If it fails at | Cause |
|---|---|
| `dotnet publish`, with analyzer errors | Warnings are errors when `CI=true`. Reproduce locally with `CI=true dotnet build Akshaya.sln`, not a bare `dotnet build` |
| `azure/login`, `AADSTS700213` | The subject GitHub presented is not one of the registered ones. Compare the log's `subject claim` against `az identity federated-credential list --identity-name akshaya-csg-github -g akshaya-rg --query "[].subject" -o tsv`. `setup.sh` registers both formats; a setup predating that needs the second adding by hand |
| the readiness probe, after five minutes | The app is not starting. Go to step 7 |

---

## 6. Sign in

```bash
az webapp log tail -g akshaya-rg -n akshaya-csg | grep -i "generated password"
```

Then open `https://akshaya-csg.azurewebsites.net` and sign in as `demo@akshaya.local`.

The password is written **once**, at `Warning`, and is not recoverable. If you miss it, use
`/register` — it is anonymous and signs you in on submit. There is no change-password endpoint.

[`docs/ACCESSING-THE-APP.md`](../../docs/ACCESSING-THE-APP.md) covers the rest: what survives a
redeploy, the route table, the session cookie.

---

## 7. When the site returns 503 or never becomes ready

The deploy succeeded and the application is not running. Work in this order.

**First, make sure you can see the application's output.** On Linux the app runs in a container, and
its stdout is only collected with `--docker-container-logging`. Without it, every log you read is
platform status and the app appears to fail silently:

```bash
az webapp log config -g akshaya-rg -n akshaya-csg --docker-container-logging filesystem && az webapp restart -g akshaya-rg -n akshaya-csg
```

`setup.sh` sets this now. An app provisioned before it did not have it.

**Then read what the app actually said:**

```bash
sleep 60 && az webapp log download -g akshaya-rg -n akshaya-csg --log-file /tmp/l.zip && rm -rf /tmp/l && unzip -q -o /tmp/l.zip -d /tmp/l && grep -v "ContainerStatus" /tmp/l/LogFiles/*docker*.log | tail -60
```

`grep -v ContainerStatus` matters — the platform writes thousands of status lines and the
application's own output is a handful among them.

**Confirm the stack is right** (it should be, from step 3):

```bash
az webapp config show -g akshaya-rg -n akshaya-csg --query linuxFxVersion -o tsv
```

`DOTNETCORE|10.0` is correct. `DOCKER|…` means a container app is swallowing the zip deploy:

```bash
az webapp config set -g akshaya-rg -n akshaya-csg --linux-fx-version "DOTNETCORE|10.0" && az webapp config appsettings delete -g akshaya-rg -n akshaya-csg --setting-names WEBSITES_PORT DOCKER_REGISTRY_SERVER_URL DOCKER_REGISTRY_SERVER_USERNAME DOCKER_REGISTRY_SERVER_PASSWORD && az webapp restart -g akshaya-rg -n akshaya-csg
```

**Reading the exit code.** The platform reports one when the container dies:

| Code | Means |
|---|---|
| 134 | SIGABRT — the runtime aborted. A crash with something to say, once its stdout is captured |
| 137 | SIGKILL — usually out of memory. F1 is 1 GB; B1 is 1.75 GB |
| timeout with no exit | The app is running but not listening where the platform probes |

---

## Quick reference

```bash
# What the app is saying, live
az webapp log tail -g akshaya-rg -n akshaya-csg

# Is it up?
curl -fsS https://akshaya-csg.azurewebsites.net/health/ready && echo

# Every application setting
az webapp config appsettings list -g akshaya-rg -n akshaya-csg -o table

# Which OIDC subjects are trusted
az identity federated-credential list --identity-name akshaya-csg-github -g akshaya-rg --query "[].subject" -o tsv

# Start over
az group delete --name akshaya-rg --yes
```
