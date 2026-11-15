#!/usr/bin/env bash
# Starts the manager session through the hub. Same as: ./sb start manager <dir> --manager
# Usage: scripts/start-manager.sh [project-dir] [permission-mode]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
exec ./sb start manager "${1:-$PWD}" --manager --mode "${2:-acceptEdits}"
