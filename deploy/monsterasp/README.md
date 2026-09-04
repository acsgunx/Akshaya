# Deploying to MonsterASP.NET

MonsterASP is **Windows / IIS** hosting, not a container host. Nothing in `deploy/Dockerfile`
applies here — this is a completely separate path: GitHub Actions builds the Angular app, publishes
the API for `win-x86`, and syncs the result to IIS over Web Deploy.

Everything is automated by [`.github/workflows/deploy-monsterasp.yml`](../../.github/workflows/deploy-monsterasp.yml).
This document is the setup you do once, plus what to check when it goes wrong.

**Total cost: $0** on their Free plan, with no database server.

---

## Table of contents

1. [Why this host fits, and where it does not](#1-why-this-host-fits-and-where-it-does-not)
2. [Create the hosting account](#2-create-the-hosting-account)
3. [Enable Web Deploy and collect four values](#3-enable-web-deploy-and-collect-four-values)
4. [Add the four values as GitHub secrets](#4-add-the-four-values-as-github-secrets)
5. [Set the application's environment variables](#5-set-the-applications-environment-variables)
6. [Run the deployment](#6-run-the-deployment)
7. [First sign-in](#7-first-sign-in)
8. [Turn on automatic deploys](#8-turn-on-automatic-deploys)
9. [Verifying a deployment](#9-verifying-a-deployment)
10. [Troubleshooting](#10-troubleshooting)
11. [Deploying without GitHub Actions](#11-deploying-without-github-actions)
12. [Backing up the database](#12-backing-up-the-database)

---

## 1. Why this host fits, and where it does not

### What it gives you

MonsterASP advertises **.NET Core 10/9/8**, and **SignalR, gRPC and WebSockets** — which is the
full list of what this application needs from a host. The live tick stream is SignalR over a
WebSocket, so a host without WebSockets would silently degrade it to long polling.

| Free plan | |
|---|---|
| Websites | 1 |
| Storage | 5 GB |
| RAM | **256 MB** |
| Databases | 1 (MSSQL or MySQL) |
| Domain | free `*.runasp.net` subdomain only |
| HTTPS | Let's Encrypt, one click |

The Premium Single plan is $1.95/month and lifts RAM to 512 MB, storage to 25 GB, and allows
custom domains.

### The database question answers itself here

MonsterASP offers **MSSQL and MySQL**. This application's enterprise mode speaks **Postgres**, so
that free database is not usable by it. That is fine, and it is the whole point of the persistence
switch: `Persistence:Mode` stays on the default `Sqlite`, the identity store is a file in
`App_Data`, and no database server is involved at all.

If you later want a real server here, it would need a new `SqlServer` persistence mode plus a set
of SQL Server EF migrations. That does not exist today — see [the main deploy README](../README.md)
for what the three current modes are.

### The two real constraints

**256 MB of RAM is tight.** ASP.NET Core plus the connector catalog fits, but the instrument master
holds a searchable copy of each connector's symbol list, and a broker with a large master can push
you over. Step 5 sets `DOTNET_gcServer=0` to keep the GC in workstation mode, which matters more
on a small memory cap than anything else you can configure. If the app starts recycling, the
$1.95 plan's 512 MB is the fix.

**Shared hosting recycles idle application pools.** Orders, positions, the risk policy and the kill
switch live in process memory and are rebuilt from the broker after a recycle; open SignalR
subscriptions drop and the client reconnects. Accounts and saved broker credentials are on disk and
are unaffected. This is the same trade-off described under *Why sleeping hurts* in
[the main deploy README](../README.md).

---

## 2. Create the hosting account

1. Sign up at <https://www.monsterasp.net/> — the free plan needs no credit card.
2. In the control panel at <https://admin.monsterasp.net/app/dashboard>, create a website.
   Note the **site ID**; it looks like `site12345` and appears everywhere below.
3. Your URL will be `https://site12345.runasp.net`.
4. **Turn on HTTPS now, before anything else.** Control panel → your website → **HTTPS** →
   activate the Let's Encrypt certificate.

   This is not cosmetic. The session cookie is issued with `Secure` in Production
   (`CookieSecurePolicy.Always` in `Program.cs`), which means **over plain HTTP the browser
   discards it and sign-in appears to succeed and then immediately fails**. If sign-in "works but
   does not stick", this is why.

---

## 3. Enable Web Deploy and collect four values

Control panel → **Websites** → **Manage website** → **WebDeploy** → enable it.

Copy these four values:

| Value | Looks like | Where it comes from |
|---|---|---|
| Website name | `site12345` | your site ID |
| Server computer name | `https://site12345.siteasp.net:8172` | the WebDeploy panel — **no `/msdeploy.axd` suffix**, the action appends it |
| Username | `site12345` | same as the site ID |
| Password | `••••••••` | the WebDeploy password, shown in the panel |

The most common setup mistake is pasting the full publishing URL
(`https://site12345.siteasp.net:8172/msdeploy.axd?site=site12345`) as the computer name. The action
builds that URL itself and you would end up with it twice.

---

## 4. Add the four values as GitHub secrets

In this repository: **Settings** → **Secrets and variables** → **Actions** → **New repository
secret**. Add all four, with exactly these names:

| Secret | Value |
|---|---|
| `MONSTERASP_WEBSITE_NAME` | `site12345` |
| `MONSTERASP_SERVER_COMPUTER_NAME` | `https://site12345.siteasp.net:8172` |
| `MONSTERASP_SERVER_USERNAME` | `site12345` |
| `MONSTERASP_SERVER_PASSWORD` | your WebDeploy password |

The workflow checks all four before it builds anything and fails with the names of any that are
missing, rather than letting Web Deploy return an opaque 401 twenty minutes later.

---

## 5. Set the application's environment variables

These are **not** GitHub secrets. They live on the server, in MonsterASP's own configuration store:

**Control panel → Websites → Manage website → Scripting → Environment Variables**

ASP.NET Core reads these directly, using `__` where the JSON config would nest — so
`Cors__AllowedOrigin` sets `Cors:AllowedOrigin`. Nothing here goes in the repository.

First, generate the vault master key on your machine:

```bash
openssl rand -base64 32
```

Then add:

| Key | Value | Why |
|---|---|---|
| `CredentialProtection__ActiveKeyId` | `prod-1` | names the key that seals new records |
| `CredentialProtection__Keys__prod-1` | the generated value | **the app will not start without this** |
| `Cors__AllowedOrigin` | `none` | the Angular app is served from this same origin, so there is no cross-origin caller to permit |
| `ASPNETCORE_ENVIRONMENT` | `Production` | |
| `DOTNET_gcServer` | `0` | workstation GC — matters on a 256 MB cap |
| `Persistence__SeedUser__Email` | your email address | the account seeded into an empty store |
| `Persistence__SeedUser__Password` | a password **you** choose, at least 10 characters | see the warning below |

`Persistence__Mode` and `Persistence__SqlitePath` need no entry — the defaults are already `Sqlite`
and `App_Data/akshaya-identity.db`, which is what you want here.

### About the vault key

It must be base64 of exactly 32 bytes (AES-256). The app validates the whole key set at startup and
refuses to boot on a malformed one, deliberately — a bad key discovered at boot is better than one
discovered by the first user who ticks "remember this".

**Losing this key does not lose accounts.** It loses the ability to decrypt saved broker
credentials, which then report "re-enter this" rather than failing silently. Keep it somewhere you
can find it again.

### Why set the seed password yourself here

On hosts with a log stream you can leave `Persistence__SeedUser__Password` empty, let the app
generate a random one, and read it out of the startup log. On MonsterASP that means enabling
stdout logging and fetching a file, which is a lot of ceremony for one password.

Setting it yourself is simpler — **but be aware there is no change-password endpoint in the
application today.** The password you set here is the password that account keeps. Choose
accordingly, and prefer a password manager entry over something memorable.

If you would rather not seed at all, set `Persistence__SeedUser__Enabled` to `false` and register
through the sign-up form on first visit instead.

### One thing to know before you make this public

**Sign-up is open.** Anyone who finds the URL can register. Each account gets its own isolated
tenant, so a stranger cannot see your positions or your saved broker logins — but they can create
an account and use the paper simulator on your 256 MB. That is a property of the application as it
stands, not of this host; there is no invite or allow-list mechanism yet. Keep the subdomain to
yourself, and do not link a live broker to a deployment you have shared.

---

## 6. Run the deployment

GitHub → **Actions** → **Deploy to MonsterASP.NET** → **Run workflow** → branch `main` → **Run**.

The workflow, in order:

1. Verifies the four secrets exist.
2. `npm ci && npm run build -- --configuration production` in `apps/web`.
3. `dotnet publish src/Akshaya.Api --runtime win-x86 --self-contained false --output publish`.
4. Copies `apps/web/dist/akshaya-web/browser/*` into `publish/wwwroot`, and fails if `index.html`
   is not there — an API deployed with no UI would otherwise look like a successful deploy.
5. Stops the IIS application pool, syncs `publish/` over Web Deploy, starts the pool again.

Stopping the pool is what avoids the classic Web Deploy failure on IIS: you cannot overwrite a DLL
that a running worker process holds open. It also means the site is briefly down mid-deploy, which
on a single-instance deployment is unavoidable.

### The publish is framework-dependent, on purpose

`--self-contained false` produces about 23 MB instead of well over 100 MB. MonsterASP has the
.NET 10 runtime installed, so shipping a private copy would slow every deploy and freeze the
runtime at whatever patch level this repo was built against.

If the host's runtime ever lags behind this repo's target framework, switch the publish step in the
workflow to `--self-contained true` and it will carry its own runtime. That is the only change
required.

---

## 7. First sign-in

Open `https://site12345.runasp.net/`.

On the very first start the identity store is empty, so the API creates the seeded account and
nothing else. Sign in with `Persistence__SeedUser__Email` and the password you set in step 5.

Seeding happens **only into an empty store**. Later deploys do not re-seed, do not reset the
password, and do not recreate the account if you delete it.

---

## 8. Turn on automatic deploys

By default the workflow only runs when you click **Run workflow**. Pushes to `main` are ignored, so
a repository whose secrets are not configured yet does not fail on every commit.

To deploy automatically on every push to `main`:

**Settings** → **Secrets and variables** → **Actions** → **Variables** → **New repository
variable**:

| Variable | Value |
|---|---|
| `MONSTERASP_AUTODEPLOY` | `true` |

Remove the variable (or set it to anything else) to go back to manual deploys.

---

## 9. Verifying a deployment

```bash
curl https://site12345.runasp.net/health/live     # the process is up
curl https://site12345.runasp.net/health/ready    # the identity store is reachable, connectors loaded
```

`/health/ready` is the one that matters. It opens the identity store, so a SQLite file the
application pool cannot write to fails here rather than at the first sign-up. A healthy response
also confirms the connector catalog loaded.

Then check the UI is actually being served — not just the API:

```bash
curl -s https://site12345.runasp.net/ | grep ak-root
```

---

## 10. Troubleshooting

### HTTP 500.30 / 500.31 — the app failed to start

Almost always one of three things:

1. **The vault key is missing or malformed.** The credential cipher validates its key set in its
   constructor and `Program.cs` resolves it deliberately early, so a bad key stops startup.
   Re-check `CredentialProtection__ActiveKeyId` and `CredentialProtection__Keys__prod-1`; the value
   must be base64 of exactly 32 bytes.
2. **The runtime is missing.** Only if you targeted a .NET version this host does not have. Switch
   the publish to `--self-contained true`.
3. **`App_Data` is not writable**, so SQLite cannot create the database. See below.

To see the actual exception, enable stdout logging: edit `web.config` on the server (control panel
file manager) and set `stdoutLogEnabled="true"`. The log lands in `logs/stdout_*.log` next to the
application. **Turn it back off once you are done** — it grows without bound.

MonsterASP's control panel also exposes Application and Access logs directly.

### Sign-in succeeds, then you are immediately signed out

HTTPS is not active. The session cookie is issued `Secure` in Production, so a browser on plain
HTTP accepts the response and throws the cookie away. Activate the Let's Encrypt certificate
(step 2.4).

### `/health/ready` reports the identity store unhealthy

The application pool cannot write to `App_Data`. Check the folder exists at the site root and that
the pool identity has write access — SQLite needs to create `-wal` and `-shm` files alongside the
database, so read access to an existing file is not enough. The app creates the directory itself on
first start, so its absence usually means the whole site root is read-only.

### Web Deploy fails with 401 Unauthorized

`MONSTERASP_SERVER_COMPUTER_NAME` should be `https://site12345.siteasp.net:8172` with **no path**.
If you pasted the full `.../msdeploy.axd?site=...` URL, the action appends its own copy and the
request goes nowhere useful. Also confirm WebDeploy is still enabled in the control panel and that
the password has not been regenerated.

### Web Deploy fails with "file in use" / ERROR_FILE_IN_USE

The application pool did not stop. Retry the workflow; if it persists, stop the pool manually in
the control panel, run the deploy, and start it again. Uploading `app_offline.htm` to the site root
is MonsterASP's own documented workaround.

### The site loads but every page is blank

`publish/wwwroot` did not get the Angular bundle. The workflow fails explicitly on this, so a green
run with a blank site usually means a stale browser cache — hard-refresh first.

### The database vanished after a deploy

It should not: the workflow passes `target-delete: false`, which adds msdeploy's `DoNotDeleteRule`
so files on the server that are not in the build are left alone, and it additionally skips the
`App_Data` directory outright. If you changed either of those, change them back — a plain
`msdeploy -verb:sync` deletes "extra" files at the destination, and the database is an extra file.

---

## 11. Deploying without GitHub Actions

Useful for a first manual smoke test, or when you want to push a build without a commit.

Build locally exactly as the workflow does:

```bash
cd apps/web && npm ci && npm run build -- --configuration production && cd ../..

dotnet publish src/Akshaya.Api/Akshaya.Api.csproj \
  --configuration Release --runtime win-x86 --self-contained false --output publish

mkdir -p publish/wwwroot && cp -r apps/web/dist/akshaya-web/browser/* publish/wwwroot/
```

Then upload the contents of `publish/` to the site root by whichever route suits you:

- **FTP/SFTP** — credentials are in the control panel. Slowest, but needs no tooling.
- **ZIP upload** — the control panel accepts a zip and unpacks it.
- **Web Deploy from the command line** — MonsterASP documents an `msdeploy.exe` batch file; it is
  Windows-only. Add `-enableRule:DoNotDeleteRule` to it, or it will delete `App_Data` and every
  account with it.
- **Visual Studio** — import the publish profile from the control panel and use one-click publish.

Whichever you choose, the environment variables from step 5 still have to be set in the control
panel; they are not part of the payload.

---

## 12. Backing up the database

The whole identity store is one file. There is no database server to dump:

```
App_Data/akshaya-identity.db
```

Download it over FTP or the control panel's file manager. Take the `-wal` file alongside it if one
is present, or stop the application pool first so the write-ahead log is checkpointed into the main
file.

That file contains every account and the encrypted saved broker credentials. It is useless without
the vault master key, and the key is useless without it — **back up both, and not to the same
place.**
