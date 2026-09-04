# ==================================================================================================
# AKSHAYA — one image, one process, one bill.
#
# The Angular bundle is built here and copied into the API's wwwroot, so a single container serves
# both the app and its API from one origin. That is the whole cost story: no separate static host,
# no second TLS certificate, and no CORS, because the browser never makes a cross-origin request.
#
# It also assumes NO DATABASE SERVER. Persistence:Mode defaults to Sqlite and the identity schema
# is created in-process on first start, so there is no migration job to run and nothing to
# provision. Point Persistence__SqlitePath at a mounted volume and accounts survive redeploys;
# leave it on the container filesystem and they do not. Set Persistence__Mode=Postgres to switch to
# the enterprise store — the image needs no rebuild, only the connection string.
#
#   docker build -f deploy/Dockerfile -t akshaya .
#   docker run -p 8080:8080 -v akshaya-data:/data \
#     -e CredentialProtection__ActiveKeyId=local-1 \
#     -e CredentialProtection__Keys__local-1="$(openssl rand -base64 32)" akshaya
# ==================================================================================================

# ── Stage 1: the Angular app. ─────────────────────────────────────────────────────────────────────
FROM node:22-alpine AS web
WORKDIR /src/apps/web

# package.json first, on its own layer: dependency installs are the slow step and they only need to
# re-run when the manifests change, not when a component does.
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci

COPY apps/web/ ./
RUN npm run build -- --configuration production

# ── Stage 2: the .NET publish. ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS api
WORKDIR /src

# Same trick as above: the project files alone, restored on their own layer, so editing a .cs file
# does not re-download the package set. Listed explicitly because COPY cannot glob a directory tree
# while preserving its shape — and a missing entry fails loudly at restore rather than silently
# producing a slower build.
COPY Directory.Build.props .editorconfig ./
COPY src/Akshaya.Api/Akshaya.Api.csproj                                     src/Akshaya.Api/
COPY src/Akshaya.SharedKernel/Akshaya.SharedKernel.csproj                   src/Akshaya.SharedKernel/
COPY src/Akshaya.Connectors.Abstractions/Akshaya.Connectors.Abstractions.csproj src/Akshaya.Connectors.Abstractions/
COPY src/Akshaya.Connectors.Sdk/Akshaya.Connectors.Sdk.csproj               src/Akshaya.Connectors.Sdk/
COPY src/Akshaya.Connectors.Host/Akshaya.Connectors.Host.csproj             src/Akshaya.Connectors.Host/
COPY src/Modules/Identity/Akshaya.Modules.Identity.csproj                   src/Modules/Identity/
COPY src/Modules/MarketData/Akshaya.Modules.MarketData.csproj              src/Modules/MarketData/
COPY src/Modules/Portfolio/Akshaya.Modules.Portfolio.csproj                src/Modules/Portfolio/
COPY src/Modules/Trading/Akshaya.Modules.Trading.csproj                    src/Modules/Trading/
COPY src/connectors/Akshaya.Connector.MStock/Akshaya.Connector.MStock.csproj src/connectors/Akshaya.Connector.MStock/
COPY src/connectors/Akshaya.Connector.Paper/Akshaya.Connector.Paper.csproj   src/connectors/Akshaya.Connector.Paper/
RUN dotnet restore src/Akshaya.Api/Akshaya.Api.csproj

COPY src/ ./src/

RUN dotnet publish src/Akshaya.Api/Akshaya.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish \
      /p:UseAppHost=false

# The Angular output lands in wwwroot, which is the ONLY thing that turns on the SPA hosting block
# in Program.cs. A publish without this directory serves the API alone and is still perfectly valid
# — that is how a developer running `ng serve` on :4200 works.
COPY --from=web /src/apps/web/dist/akshaya-web/browser/ /app/publish/wwwroot/

# ── Stage 3: the runtime image. ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

# Non-root. The data directory is created and chowned HERE rather than at startup, because a
# container that starts as an unprivileged user cannot create a directory it does not own, and
# "unable to open database file" is a uselessly indirect way to be told that.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin akshaya \
 && mkdir -p /data \
 && chown -R akshaya:akshaya /data

COPY --from=api --chown=akshaya:akshaya /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    # Bind on all interfaces: a container that listens on localhost is unreachable from outside it.
    # Hosts that inject their own PORT variable override this; see each deploy/<target>/README.
    ASPNETCORE_URLS=http://+:8080 \
    # The default database: a file under /data, which is where every deployment template mounts its
    # volume. Nothing else in the application is persisted.
    Persistence__Mode=Sqlite \
    Persistence__SqlitePath=/data/akshaya-identity.db \
    # The Angular app is served from this origin, so there is no cross-origin caller to allow.
    Cors__AllowedOrigin=none \
    DOTNET_gcServer=0

USER akshaya
EXPOSE 8080

# Readiness, not liveness: it exercises the identity store, so a SQLite path the container cannot
# write to shows up here instead of at the first sign-up.
#
# BASH, not sh. The /dev/tcp pseudo-device this relies on is a bash builtin, and the base image's
# /bin/sh is dash, which reports it as a missing directory — a probe that fails identically whether
# the app is down or the shell is wrong. Using bash directly avoids installing curl for one request.
#
# Every target in deploy/<target>/ also declares its own HTTP probe against the same path; this one
# is what `docker run` and Compose use.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q 200"]

ENTRYPOINT ["dotnet", "Akshaya.Api.dll"]
