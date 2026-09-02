#!/usr/bin/env bash
#
# Stops local infrastructure. Pass --clean to also delete the data volumes.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT/deploy"

if [[ "${1:-}" == "--clean" ]]; then
  echo "==> Stopping and deleting volumes (all local data will be lost)"
  docker compose down -v
else
  echo "==> Stopping (data volumes kept; use --clean to delete them)"
  docker compose down
fi
