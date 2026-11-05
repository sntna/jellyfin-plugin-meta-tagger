#!/usr/bin/env bash
# The one command that must pass before any PR. Agents (worker and reviewer) run it; same gate as the Verify workflow's
# main step. Packaging, Docker and browser checks stay in CI (see AGENTS.md for when to run them locally).
set -euo pipefail
cd "$(dirname "$0")/.."

command -v dotnet >/dev/null || { echo "check.sh: dotnet not found (global.json pins the SDK)" >&2; exit 1; }
exec ./scripts/build-and-test.sh
