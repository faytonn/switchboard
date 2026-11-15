#!/usr/bin/env bash
# Always-on entry point for the hub: the one command a service manager runs.
#   Today:  Windows Task Scheduler at logon (deploy/windows/install-task.ps1)
#   Later:  systemd on a Linux server or Raspberry Pi (deploy/systemd/switchboard-hub.service)
# It runs the hub in the foreground and logs to stdout/stderr, which is what systemd expects.
# With --log FILE it appends to FILE instead (Task Scheduler has nowhere to show output).
#
# Usage: scripts/serve.sh [--log FILE]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ "${1:-}" == "--log" && -n "${2:-}" ]]; then
  mkdir -p "$(dirname "$2")"
  # Keep the log from growing forever: rotate once it passes 10 MB.
  if [[ -f "$2" && $(stat -c %s "$2") -gt 10485760 ]]; then mv -f "$2" "$2.1"; fi
  exec >>"$2" 2>&1
fi

log() { echo "$(date -Iseconds) serve.sh: $*"; }

# Service managers start us without a login shell, so ~/.profile hasn't set PATH.
[[ -d "$HOME/.dotnet" ]] && export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
[[ -d "$HOME/.local/bin" ]] && export PATH="$HOME/.local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

[[ -f .env ]] || { log "no .env here; run scripts/setup-env.sh first"; exit 1; }
set -a; source .env; set +a

port_open() { (exec 3<>"/dev/tcp/$1/$2") 2>/dev/null; }

# One hub per machine. If another one already holds the port (say, started by hand in tmux),
# wait instead of exiting: on Windows this process is also what keeps WSL running, and when
# the other hub stops, this one takes over.
hub_port="${SWITCHBOARD_HUB_URL:-http://127.0.0.1:7700}"; hub_port="${hub_port##*:}"; hub_port="${hub_port%%/*}"
while port_open 127.0.0.1 "$hub_port"; do
  log "a hub is already running on port $hub_port; checking again in 60s"
  sleep 60
done

# Postgres must be up first: the hub migrates the database on startup. On Windows it runs in
# Docker Desktop, which may still be starting at logon.
db_host=$(sed -n 's/.*Host=\([^;]*\).*/\1/p' <<<"$SWITCHBOARD_DB"); db_port=$(sed -n 's/.*Port=\([0-9]*\).*/\1/p' <<<"$SWITCHBOARD_DB")
for attempt in $(seq 1 60); do
  port_open "${db_host:-localhost}" "${db_port:-5432}" && break
  if (( attempt % 6 == 1 )) && command -v docker >/dev/null && [[ -f docker-compose.yml ]]; then
    log "Postgres not reachable yet; trying docker compose up -d"
    docker compose up -d >/dev/null 2>&1 || true
  fi
  sleep 5
done
port_open "${db_host:-localhost}" "${db_port:-5432}" || { log "Postgres still unreachable after 5 minutes"; exit 1; }

# Build if the code changed since the last run (quick when nothing did).
dotnet build Switchboard.slnx --verbosity quiet --nologo
log "starting the hub on port $hub_port"
exec dotnet run --project src/Switchboard.Hub --no-build
