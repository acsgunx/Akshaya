# Railway

**~$5/month** on the Hobby plan, which includes $5 of usage credit — a single small always-on
container plus a volume typically lands inside it. Volumes are included at no extra charge, which
is what makes Railway a reasonable alternative to Fly.

## Deploy

```bash
npm i -g @railway/cli
railway login
railway init
railway up
```

Railway reads `deploy/railway/railway.json` for the build. If it does not pick it up automatically,
set **Settings → Config as code** to `deploy/railway/railway.json`.

## Attach a volume — do this before the first real sign-up

Railway's container filesystem is ephemeral. Without a volume, the identity database is recreated
empty on every deploy.

Dashboard → your service → **Variables/Volumes** → **New Volume**, mount path `/data`. Or:

```bash
railway volume add --mount-path /data
```

## Environment variables

```bash
railway variables set \
  ASPNETCORE_ENVIRONMENT=Production \
  Persistence__Mode=Sqlite \
  Persistence__SqlitePath=/data/akshaya-identity.db \
  Persistence__SeedUser__Email=demo@akshaya.local \
  Cors__AllowedOrigin=none \
  CredentialProtection__ActiveKeyId=prod-1 \
  CredentialProtection__Keys__prod-1="$(openssl rand -base64 32)"
```

Railway injects `PORT` and expects the container to bind it. The image binds 8080 via
`ASPNETCORE_URLS`; set `PORT=8080` as well so Railway's router agrees:

```bash
railway variables set PORT=8080
```

Then generate the public URL: dashboard → **Settings** → **Networking** → **Generate Domain**.

## Get the seeded account's password

```bash
railway logs | grep "generated password"
```

## Switching to Postgres

```bash
railway add --database postgres
```

Railway exposes `DATABASE_URL` in the libpq URL form, which Npgsql does **not** accept directly.
Set the connection string in Npgsql's keyword form instead:

```bash
railway variables set \
  Persistence__Mode=Postgres \
  ConnectionStrings__Identity="Host=<host>;Port=<port>;Database=railway;Username=postgres;Password=<pw>;SSL Mode=Require"
```

Adds roughly $5–10/month depending on usage. You can drop the volume afterwards.
