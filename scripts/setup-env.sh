#!/usr/bin/env bash
# Creates .env from .env.example with a random Postgres password.
# Leaves an existing .env alone, because the database was created with that password.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ -f .env ]]; then
  echo ".env already exists; left it unchanged."
  exit 0
fi

password="$(openssl rand -hex 24)"
sed "s/CHANGE_ME/$password/g" .env.example > .env
echo "Created .env with a random Postgres password."
