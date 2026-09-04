#!/usr/bin/env bash
# ==================================================================================================
# One-time Azure setup for the GitHub Actions deploy (.github/workflows/deploy-azure.yml).
#
# Run it in AZURE CLOUD SHELL (the >_ icon in the portal toolbar) — the Azure CLI, openssl and a
# signed-in session are all already there, so there is nothing to install locally.
#
#   curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/deploy/azure-app-service/setup.sh -o setup.sh
#   chmod +x setup.sh
#   ./setup.sh --app <globally-unique-name> --repo <owner>/<repo>
#
# It creates: a resource group, a Linux App Service plan, the web app on the built-in .NET 10 stack,
# every application setting the API needs, and a user-assigned managed identity that GitHub Actions
# signs in as over OIDC. No container registry, no database server, no client secret.
#
# It is safe to re-run. An existing credential-protection key is left alone rather than regenerated,
# because replacing it would make every saved broker credential unreadable.
# ==================================================================================================

set -euo pipefail

APP_NAME=""
GITHUB_REPO=""
RESOURCE_GROUP="akshaya-rg"
LOCATION="centralindia"
SKU="F1"
BRANCH="main"
SEED_EMAIL="demo@akshaya.local"

usage() {
  cat <<'EOF'
Usage: ./setup.sh --app <name> --repo <owner>/<repo> [options]

Required:
  --app       <name>          Globally unique web app name; becomes <name>.azurewebsites.net
  --repo      <owner>/<repo>  The GitHub repository that will deploy to it

Options:
  --resource-group <name>     Default: akshaya-rg
  --location  <region>        Default: centralindia (put it near the broker's API, not near you)
  --sku       <F1|B1|B2|S1>   Default: F1 (free). B1 is the cheapest always-on tier, ~$13/month.
  --branch    <name>          Branch allowed to deploy. Default: main
  --seed-email <address>      Address for the first seeded account. Default: demo@akshaya.local
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --app)            APP_NAME="${2:?}"; shift 2 ;;
    --repo)           GITHUB_REPO="${2:?}"; shift 2 ;;
    --resource-group) RESOURCE_GROUP="${2:?}"; shift 2 ;;
    --location)       LOCATION="${2:?}"; shift 2 ;;
    --sku)            SKU="${2:?}"; shift 2 ;;
    --branch)         BRANCH="${2:?}"; shift 2 ;;
    --seed-email)     SEED_EMAIL="${2:?}"; shift 2 ;;
    -h|--help)        usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$APP_NAME" ] || [ -z "$GITHUB_REPO" ]; then
  echo "Both --app and --repo are required." >&2
  usage >&2
  exit 2
fi

case "$GITHUB_REPO" in
  */*) ;;
  *) echo "--repo must be owner/repo, e.g. acsgunx/Akshaya — got '$GITHUB_REPO'." >&2; exit 2 ;;
esac

IDENTITY_NAME="${APP_NAME}-github"
PLAN_NAME="${APP_NAME}-plan"
SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"

case "$SKU" in
  F1) IS_FREE=1 ;;
  *)  IS_FREE=0 ;;
esac

echo "==> Subscription $(az account show --query name -o tsv) ($SUBSCRIPTION_ID)"

# ── The runtime ───────────────────────────────────────────────────────────────────────────────────
# Pick the .NET 10 stack by asking the platform rather than hard-coding a moniker that changes with
# every major release. If it is not offered in this region, say so plainly — the alternative is
# `az webapp create` failing with a validation error that does not mention .NET at all.
echo "==> Looking for the .NET 10 runtime on Linux"
RUNTIME="$(az webapp list-runtimes --os linux -o tsv | grep -i '^DOTNETCORE:10' | head -1 || true)"
if [ -z "$RUNTIME" ]; then
  echo "ERROR: App Service does not offer a .NET 10 Linux runtime in this subscription." >&2
  echo "       Deploy the container instead — see deploy/azure-app-service/README.md, 'Path B'." >&2
  exit 1
fi
echo "    $RUNTIME"

# ── Resource group and plan ───────────────────────────────────────────────────────────────────────
echo "==> Resource group $RESOURCE_GROUP in $LOCATION"
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none

echo "==> App Service plan $PLAN_NAME ($SKU, Linux)"
az appservice plan create \
  --name "$PLAN_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku "$SKU" \
  --is-linux \
  --output none

# ── The web app ───────────────────────────────────────────────────────────────────────────────────
if az webapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" --output none 2>/dev/null; then
  echo "==> Web app $APP_NAME already exists — reusing it"
else
  echo "==> Web app $APP_NAME"
  az webapp create \
    --name "$APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --plan "$PLAN_NAME" \
    --runtime "$RUNTIME" \
    --output none
fi

# ── The credential-protection key ─────────────────────────────────────────────────────────────────
# Generated once and never again. Losing or replacing it does not lose accounts, but every saved
# broker credential sealed with it becomes unreadable and has to be entered again.
EXISTING_KEY="$(az webapp config appsettings list \
  --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" \
  --query "[?name=='CredentialProtection__Keys__prod-1'].value | [0]" -o tsv 2>/dev/null || true)"

if [ -n "$EXISTING_KEY" ] && [ "$EXISTING_KEY" != "null" ]; then
  echo "==> Credential-protection key already set — leaving it alone"
  CREDENTIAL_KEY="$EXISTING_KEY"
  KEY_IS_NEW=0
else
  echo "==> Generating the credential-protection master key"
  CREDENTIAL_KEY="$(openssl rand -base64 32)"
  KEY_IS_NEW=1
fi

# ── Application settings ──────────────────────────────────────────────────────────────────────────
# Persistence__SqlitePath is the load-bearing one. /home is durable on every App Service tier, and
# /home/data is OUTSIDE /home/site/wwwroot, which a zip deploy replaces wholesale. Put the database
# under the deployed folder and every deploy wipes the accounts in it.
echo "==> Application settings"
az webapp config appsettings set \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    Persistence__Mode=Sqlite \
    Persistence__SqlitePath=/home/data/akshaya-identity.db \
    Persistence__SeedUser__Email="$SEED_EMAIL" \
    Cors__AllowedOrigin=none \
    CredentialProtection__ActiveKeyId=prod-1 \
    CredentialProtection__Keys__prod-1="$CREDENTIAL_KEY" \
    SCM_DO_BUILD_DURING_DEPLOYMENT=false \
    DOTNET_gcServer=0 \
  --output none

# SCM_DO_BUILD_DURING_DEPLOYMENT=false above: the workflow uploads an already-published folder, so
# there is nothing for the platform's build system to do and letting it try only slows the deploy
# down and occasionally fails on a repo it does not recognise.
#
# DOTNET_gcServer=0: the project publishes with server GC on, which sizes its heaps for a machine
# with cores to spare. F1 is a shared instance with 1 GB, and B1 has one core — workstation GC keeps
# the working set small enough to stay out of the memory limit. deploy/Dockerfile sets the same.

# ── Site configuration ────────────────────────────────────────────────────────────────────────────
echo "==> Site configuration"

# WebSockets: SignalR streams ticks over one. Without this the client silently falls back to long
# polling and every tick costs an HTTP round trip.
az webapp config set \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --web-sockets-enabled true \
  --startup-file "dotnet Akshaya.Api.dll" \
  --min-tls-version 1.2 \
  --ftps-state Disabled \
  --output none

az webapp update \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --https-only true \
  --client-affinity-enabled false \
  --output none

if [ "$IS_FREE" -eq 0 ]; then
  # alwaysOn and the health-check probe are not available on F1, and setting either there fails the
  # command rather than being ignored.
  az webapp config set \
    --name "$APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --always-on true \
    --generic-configurations '{"healthCheckPath": "/health/ready"}' \
    --output none
else
  echo "    F1: skipping alwaysOn and the health-check probe — neither exists on the free tier."
fi

# Container logging is off by default and is the only way to read the seeded account's password.
az webapp log config \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --application-logging filesystem \
  --level warning \
  --output none

# ── The identity GitHub Actions signs in as ───────────────────────────────────────────────────────
# A user-assigned managed identity, not an app registration: it is an ordinary Azure resource, so it
# needs no Entra directory permissions to create, and it has no secret to leak or rotate.
echo "==> Managed identity $IDENTITY_NAME"
az identity create \
  --name "$IDENTITY_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none

CLIENT_ID="$(az identity show --name "$IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" --query clientId -o tsv)"
PRINCIPAL_ID="$(az identity show --name "$IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" --query principalId -o tsv)"

# The subject must match exactly what GitHub puts in the token, or the exchange fails with a generic
# "no matching federated identity record". A workflow_dispatch run on <branch> presents the same
# ref subject as a push to it, so one credential covers both triggers.
echo "==> Trusting GitHub Actions on $GITHUB_REPO@$BRANCH"
if az identity federated-credential show \
     --name github-actions \
     --identity-name "$IDENTITY_NAME" \
     --resource-group "$RESOURCE_GROUP" --output none 2>/dev/null; then
  az identity federated-credential update \
    --name github-actions \
    --identity-name "$IDENTITY_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --issuer https://token.actions.githubusercontent.com \
    --subject "repo:${GITHUB_REPO}:ref:refs/heads/${BRANCH}" \
    --audiences api://AzureADTokenExchange \
    --output none
else
  az identity federated-credential create \
    --name github-actions \
    --identity-name "$IDENTITY_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --issuer https://token.actions.githubusercontent.com \
    --subject "repo:${GITHUB_REPO}:ref:refs/heads/${BRANCH}" \
    --audiences api://AzureADTokenExchange \
    --output none
fi

# Scoped to the one web app, not the subscription: this identity can deploy and read the site's
# settings, and can do nothing else in the account.
echo "==> Granting it Website Contributor on the web app"
SITE_ID="$(az webapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" --query id -o tsv)"
az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Website Contributor" \
  --scope "$SITE_ID" \
  --output none 2>/dev/null || echo "    (already assigned)"

HOSTNAME="$(az webapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" --query defaultHostName -o tsv)"

# ── What to do next ───────────────────────────────────────────────────────────────────────────────
cat <<EOF

==================================================================================================
Azure is ready. https://$HOSTNAME/ will show an error page until the first deploy runs — expected.

STEP 1 — add these five repository VARIABLES in GitHub
         (Settings > Secrets and variables > Actions > Variables tab > New repository variable).
         None of them is a secret; with OIDC there is no password to store.

  AZURE_CLIENT_ID        $CLIENT_ID
  AZURE_TENANT_ID        $TENANT_ID
  AZURE_SUBSCRIPTION_ID  $SUBSCRIPTION_ID
  AZURE_WEBAPP_NAME      $APP_NAME
  AZURE_RESOURCE_GROUP   $RESOURCE_GROUP

  With the GitHub CLI, from a clone of the repo:

    gh variable set AZURE_CLIENT_ID       --body "$CLIENT_ID"
    gh variable set AZURE_TENANT_ID       --body "$TENANT_ID"
    gh variable set AZURE_SUBSCRIPTION_ID --body "$SUBSCRIPTION_ID"
    gh variable set AZURE_WEBAPP_NAME     --body "$APP_NAME"
    gh variable set AZURE_RESOURCE_GROUP  --body "$RESOURCE_GROUP"

STEP 2 — run the deploy: Actions tab > "Deploy to Azure App Service" > Run workflow.
         To also deploy on every push to $BRANCH, add a sixth variable AZURE_AUTODEPLOY = true.

STEP 3 — read the seeded account's password out of the log, once the deploy is green:

    az webapp log tail -g $RESOURCE_GROUP -n $APP_NAME | grep -i "generated password"

         Then sign in at https://$HOSTNAME/ as $SEED_EMAIL.
==================================================================================================
EOF

if [ "$KEY_IS_NEW" -eq 1 ]; then
  cat <<EOF

KEEP THIS SOMEWHERE YOU CAN FIND IT AGAIN — the master key for the saved-broker-credential vault.
It is stored in the app's settings, so you do not need it to run; you need it to move the app to
another host without every remembered broker login becoming unreadable.

  CredentialProtection__Keys__prod-1  $CREDENTIAL_KEY

EOF
fi
