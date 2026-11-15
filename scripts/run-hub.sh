#!/usr/bin/env bash
# Builds everything (the hub hands the Manager MCP server's DLL to manager sessions, so it
# must be built too), loads .env, and runs the hub in the foreground. Ctrl-C stops it.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ ! -f .env ]]; then
  echo "No .env yet. Run scripts/setup-env.sh first." >&2
  exit 1
fi
set -a
source .env
set +a

dotnet build Switchboard.slnx --verbosity quiet --nologo
exec dotnet run --project src/Switchboard.Hub --no-build
