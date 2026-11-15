#!/usr/bin/env bash
# Asks the hub to start the manager session. The manager works from this repo's folder
# unless you pass another one.
# Usage: scripts/start-manager.sh [project-dir] [permission-mode]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
set -a
source .env
set +a

dir="${1:-$PWD}"
mode="${2:-acceptEdits}"
hub="${SWITCHBOARD_HUB_URL:-http://127.0.0.1:7700}"

# jq would be nicer, but isn't installed by default. python3 builds the JSON safely instead,
# so a folder name with quotes in it can't break the request.
body="$(python3 -c 'import json,sys; print(json.dumps({"id": "manager", "projectDir": sys.argv[1], "permissionMode": sys.argv[2], "manager": True}))' "$dir" "$mode")"

curl --silent --show-error --fail-with-body \
  -H "Authorization: Bearer $SWITCHBOARD_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "$body" "$hub/api/sessions"
echo
echo "Manager started. Watch it: tmux attach -t switchboard  (Ctrl-b w, pick 'manager')"
