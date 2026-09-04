# Deploying Akshaya

Four targets, one image. Pick a directory, follow its README.

| Target | Directory | Cost at rest | Persistent disk for SQLite | Sleeps? |
|---|---|---|---|---|
| **Fly.io** | [`fly/`](fly/) | ~$2–4/mo | Yes — a real volume | No (optional auto-stop) |
| **Azure App Service** | [`azure-app-service/`](azure-app-service/) | $0 on F1, ~$13/mo on B1 | Yes — `/home` is durable on every tier | F1 yes, B1 no |
| **Azure Container Apps** | [`azure-container-apps/`](azure-container-apps/) | $0 within the monthly free grant | Only via an Azure Files mount | Yes, scales to zero |
| **Render / Railway** | [`render/`](render/), [`railway/`](railway/) | $0 on Render free, ~$5/mo Railway | Render: paid disk only. Railway: volume included | Render free yes |
| **MonsterASP.NET** | [`monsterasp/`](monsterasp/) | $0 free plan, $1.95/mo premium | Yes — an ordinary file on the site's disk | Idle app pools recycle |

MonsterASP is the odd one out: **Windows/IIS, no containers**, so it ignores `Dockerfile` entirely
and deploys via Web Deploy from GitHub Actions. It is the cheapest option on this list and the only
genuinely free one that keeps its data — at the cost of 256 MB of RAM and a `*.runasp.net`
subdomain.

**If you just want the cheapest thing that works and keeps its data: Fly.io.** It is the only
option here where an always-on machine with a real volume lands under $5/month, and always-on
matters more for this application than the price difference — see *Why sleeping hurts* below.

---

## What actually costs money, and what this deployment avoids

Identity — user accounts and the encrypted saved-broker-credential vault — is the only persisted
store in the application. Orders, positions, risk policies and the kill switch are all in-memory
and rebuilt from the broker on restart.

That single fact is what these deployments exploit:

- **No database server.** `Persistence:Mode` defaults to `Sqlite`, a file on disk. A managed
  Postgres instance is normally the largest line on a bill this size (~$13–25/mo minimum on every
  provider here) and it is buying storage for two tables.
- **No separate frontend host.** The Dockerfile builds the Angular app into the API's `wwwroot`, so
  one container serves both from one origin. One service, one certificate, no CORS.
- **No Redis, no OTel collector, no Seq.** `deploy/docker-compose.yml` runs those for local
  development; none of them is required to serve traffic.

Switching to the enterprise topology is a configuration change, not a rebuild — see
*Switching to Postgres* below.

## Why sleeping hurts here more than it would elsewhere

Everything except identity lives in process memory. When a host scales to zero or idles a free
tier out:

- the order and position views are empty until the broker is queried again,
- open SignalR subscriptions drop and the client has to renegotiate,
- and in `Persistence:Mode=InMemory`, every account is gone.

None of that is data loss in the SQLite modes — accounts and saved credentials are on disk — but
it does mean the first request after a sleep is slow and the live tick stream has a gap. An
always-on machine is worth the few dollars for anything you intend to actually trade from.

## Before you deploy: the one secret you must set

The saved-broker-credential vault will not start without a master key. Generate one:

```bash
openssl rand -base64 32
```

Set it as two environment variables on the host — `CredentialProtection__ActiveKeyId` (any short
name, e.g. `prod-1`) and `CredentialProtection__Keys__prod-1` (the generated value). Each target's
README shows the exact command.

**Losing this key means every saved broker credential becomes unreadable.** The accounts survive;
the remembered logins report "re-enter this". Keep it somewhere you can find it again.

The dev key in `appsettings.Development.json` is committed and therefore public. It is never read
in Production, and nothing you would mind leaking should ever be sealed with it.

## The seeded account

On first start, if the identity store is empty, the API creates one account so a fresh deployment
is usable without a sign-up round trip. Configure it with `Persistence__SeedUser__Email`; leave
`Persistence__SeedUser__Password` unset and a random one is generated and written to the log once,
at `Warning` level:

```
Seeded the first account because the identity store was empty. Sign in as demo@akshaya.local
with the generated password: <20 characters> — it will not be shown again, and cannot be
changed in the app. Set Persistence:SeedUser:Password to choose it yourself instead.
```

Read it from the host's log stream (each README gives the command) and sign in.

**There is no change-password endpoint in the application today**, so a generated password is the
one that account keeps. On a host where reading the log is awkward, set
`Persistence__SeedUser__Password` yourself to a value from your password manager.

Seeding only ever happens into an **empty** store — deleting the account does not bring it back,
and it is off by default when `Persistence:Mode` is `Postgres`.

Set `Persistence__SeedUser__Enabled=false` to turn it off entirely.

## Persistence modes

| `Persistence__Mode` | Where it lives | Survives restart | Needs infrastructure |
|---|---|---|---|
| `Sqlite` *(default)* | `Persistence__SqlitePath`, default `/data/akshaya-identity.db` | Yes, if the path is on a mounted volume | No |
| `InMemory` | Process memory | No | No |
| `Postgres` | `ConnectionStrings__Identity` | Yes | A Postgres server |

`Sqlite` and `InMemory` create their schema from the EF model at startup. `Postgres` applies the
versioned EF migrations in `Akshaya.Api` at startup instead — there is no separate migration step
to run in any mode.

### Switching to Postgres

No rebuild. Set two variables and restart:

```bash
Persistence__Mode=Postgres
ConnectionStrings__Identity="Host=<host>;Port=5432;Database=akshaya;Username=<user>;Password=<pw>;SSL Mode=Require"
```

There is no automatic migration of existing SQLite data into Postgres. If you have real accounts in
the SQLite file, export them before switching — or ask people to sign up again, which for a
personal deployment is usually the cheaper answer.

## Verifying a deployment

```bash
curl https://<your-host>/health/live     # process is up
curl https://<your-host>/health/ready    # identity store is reachable, connectors loaded
```

`/health/ready` is the one that catches a SQLite path the container cannot write to. If it reports
the identity store unhealthy, the volume is not mounted where `Persistence__SqlitePath` points.

Then open `https://<your-host>/` — the Angular app is served from the same origin — and sign in
with the seeded account.
