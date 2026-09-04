# Render

**$0 on the free plan, ~$7/month with a disk.** Simplest deploy here — commit the blueprint, point
Render at the repository, done.

## The free-plan catch

Render's free web services have **no persistent disk** and **spin down after 15 minutes of
inactivity**. For this application that combination means:

- every spin-down loses the identity database, so you re-register each time you come back,
- the first request after a spin-down waits ~50 seconds for a cold start,
- and in-memory order/position state is rebuilt from the broker.

If you want a free deployment anyway, delete the `disk:` block from `render.yaml`, set
`plan: free`, and add `Persistence__Mode=InMemory` — at least then the behaviour is honest about
itself rather than looking durable and not being.

Otherwise: `plan: starter` (~$7/month) plus a 1GB disk (~$0.25/month) gives you always-on with real
storage.

## Deploy

1. Push this repository to GitHub.
2. Render dashboard → **New** → **Blueprint** → select the repository. It reads
   `deploy/render/render.yaml`.
3. When prompted for `CredentialProtection__Keys__prod-1`, paste the output of:

   ```bash
   openssl rand -base64 32
   ```

   It must be base64 of exactly 32 bytes — the vault refuses to start otherwise, with a message
   saying so.

## Get the seeded account's password

Render dashboard → your service → **Logs**, and search for `generated password`. It is written once
at startup, at `Warning` level.

## Switching to Postgres

Render's own managed Postgres is free for 90 days, then ~$7/month. Add to `render.yaml`:

```yaml
databases:
  - name: akshaya-db
    plan: basic-256mb
```

and set `Persistence__Mode=Postgres` with `ConnectionStrings__Identity` pointed at the
connection string Render exposes. You can then drop the `disk:` block.
