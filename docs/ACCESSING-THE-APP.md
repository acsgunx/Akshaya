# Accessing the app

What to open, what to sign in as, and where the password comes from — for a deployment on any
target in [`deploy/`](../deploy/), and for a checkout running locally.

If you are looking for how to *deploy* it, start at [`deploy/README.md`](../deploy/README.md).

---

## One origin, one process

There is no separate front end to find. The Angular bundle is built into the API's `wwwroot`, and
the API serves it: the same host and port answer `/dashboard`, `/api/account/me` and
`/hubs/market-data`. That is why no target in `deploy/` configures CORS, and why there is only ever
one URL to remember.

The wiring is [`Program.cs`](../src/Akshaya.Api/Program.cs):

| Path | Served by | Anonymous? |
|---|---|---|
| `/` and every Angular route | `MapFallbackToFile("index.html")` | Yes — the client router decides what to show |
| `/api/**` | Minimal API endpoints | No, except sign-in and register |
| `/hubs/market-data` | SignalR, over a WebSocket | No |
| `/health/live`, `/health/ready` | Health checks | Yes |
| `/openapi/v1.json` | The OpenAPI document | Yes |
| `/scalar/v1` | Scalar API reference UI | Yes |

An unmatched `/api/**` or `/hubs/**` path returns a real 404 rather than `index.html` — without
those two fallbacks a typo'd endpoint would hand the client an HTML page with a 200 and surface as
a JSON parse error instead of a status code.

**The SPA block only turns on if `wwwroot` exists in the publish output.** A publish without it
serves the API alone and is still perfectly valid — that is exactly what you get when a developer
runs `ng serve` on :4200 against the API on :5080.

---

## Step 1 — find the URL

| Target | URL |
|---|---|
| Azure App Service | `https://<appName>.azurewebsites.net` — the Bicep path also prints it as the `appUrl` output |
| Azure Container Apps | `az containerapp show -g <rg> -n <app> --query properties.configuration.ingress.fqdn -o tsv` |
| Fly.io | `https://<app>.fly.dev` |
| Render / Railway | The URL on the service's dashboard page |
| MonsterASP.NET | The site hostname from the control panel |
| Local (`scripts/rerun.sh`) | `http://localhost:4200` for the Angular dev server, `http://localhost:5080` for the API |
| Local (one container) | `http://localhost:8080` |

Check it is alive before you worry about anything else:

```bash
curl -fsS https://<your-host>/health/ready && echo OK
```

`/health/ready` exercises the identity store, so it fails when the database path is unwritable —
which is the failure you want surfaced here rather than at the first sign-up.

---

## Step 2 — sign in as the seeded account

On an **empty** identity store the API creates one account at startup so that a fresh deployment is
immediately usable instead of presenting a sign-in form nobody has an account for.

- **Address** — `demo@akshaya.local` by default, or whatever `Persistence:SeedUser:Email` was set
  to. Every deploy target exposes it: `seedUserEmail` in the Bicep, `--seed-email` in `setup.sh`.
- **Password** — generated at startup from a cryptographic RNG and **written to the log exactly
  once**, at `Warning` so it survives a production log level. It is never stored anywhere you can
  read it back.
- **Display name** — `Demo`.

Seeding happens **only when the users table is empty**, not "when this address is missing". Deleting
the seed account on purpose does not bring it back on the next restart.

It is also **off by default under Postgres**, where an account nobody created appearing in a shared
database is a security event rather than a convenience. Set `Persistence:SeedUser:Enabled` to
override either way.

### Reading the password out of the log

**Azure App Service.** Logging is off by default and is the only way to see this value:

```bash
az webapp log tail -g akshaya-rg -n <app-name> | grep -i "generated password"
```

`deploy/azure-app-service/setup.sh` turns filesystem logging on for you. **The Bicep path does
not** — enable it first, or the command above will sit there printing nothing:

```bash
az webapp log config -g akshaya-rg -n <app-name> --docker-container-logging filesystem --level warning
```

**Fly.io** — `fly logs -a <app>`. **Container Apps** — `az containerapp logs show -n <app> -g <rg>
--tail 200`. **Render / Railway** — the Logs tab. **Docker** — `docker logs <container>`.
**Local** — it is on the console `scripts/rerun.sh` is writing to.

### Choosing the password instead of inheriting one

Set `Persistence__SeedUser__Password` before the first start. A configured password is never
logged. Leave it empty unless you have somewhere safe to put it — a generated password that exists
only in a log is strictly better than a default credential committed to a config file, because
nobody can look up what a given deployment's password is without access to its logs.

---

## Step 3 — if you missed the password

It is not recoverable, and **there is no change-password endpoint yet**, so the seeded account
keeps whatever value it was given. Three ways forward, cheapest first:

1. **Register a second account.** `/register` in the UI is anonymous and open, and registering
   signs you in. The seeded account stays as dead weight. This is almost always the right answer.
2. **Set a password you choose and start over on an empty store** — see below.
3. **Delete the identity database and restart**, which re-seeds and logs a fresh password. On App
   Service that is the file at `Persistence__SqlitePath`. *This destroys every account and every
   saved broker credential in it.*

---

## What survives a redeploy, and what does not

Identity — accounts plus the encrypted saved-credential vault — is the only persisted store.
Everything else is in-memory and is rebuilt on start.

Whether accounts survive comes down to one setting pointing somewhere durable:

- **Azure App Service** — `Persistence__SqlitePath=/home/data/akshaya-identity.db`. `/home` is
  backed by Azure Storage on every tier; `/home/site/wwwroot` is replaced wholesale by a zip
  deploy. The path must be outside `wwwroot`, and `.github/workflows/deploy-azure.yml` refuses to
  run if it is not. For the **container** path, `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` is what
  makes `/home` durable at all — the Bicep sets it.
- **Everywhere else** — a mounted volume at `/data`, which is where the image defaults.
- **MonsterASP.NET** — `App_Data`, which the deploy workflow excludes from the msdeploy sync.

The **credential-protection key** is the other half. The saved-broker-credential vault is sealed
with `CredentialProtection__Keys__<id>`, named by `CredentialProtection__ActiveKeyId`. Lose or
replace that key and the accounts still work but every remembered broker login becomes unreadable
and has to be entered again. On Linux App Service the key id must be **alphanumeric** — it lands
inside an app setting name, which becomes an environment variable name, and a hyphen is rejected
at deploy time.

---

## The session cookie

Sign-in sets `akshaya.session`: HTTP-only, `SameSite=Lax`, and `Secure` everywhere except
Development. A cookie rather than a bearer token in `localStorage`, because behind this session sit
saved broker credentials, and a token JavaScript can read is a token an XSS bug can exfiltrate.

Two consequences worth knowing:

- **Serve it over HTTPS.** Every hosted target already does. Plain HTTP on a non-Development
  environment means the cookie is never set and sign-in appears to silently do nothing.
- Deep links and refreshes work — `index.html` is returned for any unmatched route and the Angular
  router resolves it client-side.

---

## Where things are in the UI

`apps/web/src/app/app.routes.ts`. `/` redirects to `/dashboard`, and any unknown route redirects
there too.

`sign-in` · `register` · `account` · `dashboard` · `watchlist` · `positions` · `holdings` ·
`orders` · `fills` · `connectors` · `connectors/:connectorId/link` ·
`chart/:brokerLinkId/:instrument` · `trade/:brokerLinkId/:instrument`

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| The URL serves JSON or a 404 at `/`, not the app | The publish has no `wwwroot`, so the SPA block never turned on. On the container path, the Angular stage did not run or its output moved |
| Sign-in posts and nothing happens | The session cookie was rejected. Almost always plain HTTP outside Development |
| `az webapp log tail` prints nothing | Logging was never enabled — the Bicep path does not do it. See Step 2 |
| The seeded password never appears in the log | The store was not empty, so nothing was seeded. Register instead |
| Everything returns 401 after a redeploy | The database was not on a durable path and came back empty. Check `Persistence__SqlitePath` |
| Accounts fine, saved broker logins all broken | The credential-protection key changed. The vault cannot be read under a key it was not sealed with |
| Ticks arrive slowly, or not at all | WebSockets are off on the host, and SignalR silently fell back to long polling |
| `/health/ready` fails but `/health/live` passes | The identity store is unreachable — usually an unwritable SQLite path |

---

## A note on the API reference

`/openapi/v1.json` and the Scalar UI at `/scalar/v1` are mapped **unconditionally** — they are not
gated to Development, so a deployed instance publishes its API shape to anyone who asks. Nothing
behind them is anonymous, so this exposes the surface rather than the data. If that is not what you
want for a public deployment, gate both behind an environment check in `Program.cs`.
