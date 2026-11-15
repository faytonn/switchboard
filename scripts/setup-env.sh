#!/usr/bin/env bash
# Creates .env from .env.example with random secrets. If .env exists, adds only the keys it
# is missing and never changes existing values (the database was created with that password).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

password="$(openssl rand -hex 24)"
token="$(openssl rand -hex 32)"
fill() { sed -e "s/CHANGE_ME_TOKEN/$token/g" -e "s/CHANGE_ME/$password/g"; }

if [[ ! -f .env ]]; then
  fill < .env.example > .env
  echo "Created .env with random secrets."
  exit 0
fi

# Reuse the existing password, so a newly added SWITCHBOARD_DB line matches the database.
existing_password="$(grep -E '^POSTGRES_PASSWORD=' .env | cut -d= -f2- || true)"
[[ -n "$existing_password" ]] && password="$existing_password"

added=0
while IFS= read -r line; do
  [[ "$line" =~ ^([A-Z_]+)= ]] || continue
  key="${BASH_REMATCH[1]}"
  if ! grep -qE "^$key=" .env; then
    printf '%s\n' "$line" | fill >> .env
    echo "Added $key to .env"
    added=1
  fi
done < .env.example

[[ $added -eq 0 ]] && echo ".env already has every key; left it unchanged."
exit 0
