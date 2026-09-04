# Fly.io

**~$2–4/month, always on, with a persistent volume.** The recommended target: it is the only
option here where "always-on with real storage" lands under $5.

## One-time setup

```bash
brew install flyctl && fly auth login
```

## Deploy

```bash
fly launch --no-deploy --copy-config --config deploy/fly/fly.toml
```

Answer **no** when it offers to add a Postgres or Redis database — neither is needed, and both
would multiply the bill for two tables of user accounts.

Create the volume the SQLite file lives on. It must be in the same region as the machine:

```bash
fly volumes create akshaya_data --size 1 --region sin
```

Set the credential-vault master key. Losing it makes saved broker credentials unreadable:

```bash
fly secrets set \
  CredentialProtection__ActiveKeyId=prod-1 \
  CredentialProtection__Keys__prod-1="$(openssl rand -base64 32)"
```

Deploy:

```bash
fly deploy --config deploy/fly/fly.toml --dockerfile deploy/Dockerfile
```

## Get the seeded account's password

```bash
fly logs | grep "generated password"
```

Sign in at `https://<app>.fly.dev` and change it.

## Costs

| Item | Monthly |
|---|---|
| `shared-cpu-1x`, 512MB, always on | ~$3.19 |
| 1GB volume | ~$0.15 |
| Outbound bandwidth | Free allowance covers a personal deployment |

Dropping to 256MB memory saves about a dollar and is workable if you are not loading a large
instrument master.

## Notes

- **Do not scale past one machine.** Orders, positions and the kill switch are in-memory and
  per-process; a second instance serves a different view of the same account. `fly scale count 1`
  is the correct setting until those stores move to Redis.
- **Back up the volume**: `fly volumes snapshots list <volume-id>`. Fly takes daily snapshots and
  keeps them 5 days by default.
- **Switching to Postgres** (`fly postgres create`, then set `Persistence__Mode=Postgres` and
  `ConnectionStrings__Identity` as secrets) removes the volume dependency and adds roughly $12/mo.
