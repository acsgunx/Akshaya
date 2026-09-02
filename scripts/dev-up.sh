#!/usr/bin/env bash
#
# Local infrastructure: Postgres with TimescaleDB, Redis, an OpenTelemetry collector, and Seq
# for reading structured logs.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT/deploy"

echo "==> Starting infrastructure"
docker compose up -d

echo
echo "==> Waiting for Postgres"
for _ in $(seq 1 30); do
  if docker compose exec -T postgres pg_isready -U akshaya >/dev/null 2>&1; then
    echo "    ready"
    break
  fi
  sleep 1
done

echo
echo "  Postgres   localhost:5432   (akshaya / akshaya / akshaya)"
echo "  Redis      localhost:6379"
echo "  Seq logs   http://localhost:5341"
echo "  OTLP       localhost:4317"
echo
echo "Next: dotnet run --project src/Akshaya.Api"
