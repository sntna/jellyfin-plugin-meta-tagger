#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
"$repo_root/scripts/check-public-tree.sh"
node --test "$repo_root/scripts/tests/"*.test.mjs
python3 -m unittest discover -s "$repo_root/scripts/tests" -p 'test_*.py'

artifacts_dir="$(mktemp -d "${TMPDIR:-/tmp}/jellyfin-plugin-meta-tagger-verify.XXXXXX")"

cleanup() {
  rm -rf -- "$artifacts_dir"
}

trap cleanup EXIT INT TERM

dotnet test \
  "$repo_root/Jellyfin.Plugin.MetaTagger.Tests/Jellyfin.Plugin.MetaTagger.Tests.csproj" \
  --configuration Release \
  --artifacts-path "$artifacts_dir"
