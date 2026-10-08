#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts_dir="$(mktemp -d "${TMPDIR:-/tmp}/jellyfin-plugin-meta-tagger-dashboard.XXXXXX")"

cleanup() {
  rm -rf -- "$artifacts_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

printf '%s\n' 'Checking dashboard and development-client behavior…'
if node --test \
  "$repo_root/scripts/tests/dashboard.test.mjs" \
  "$repo_root/scripts/tests/dev-client.test.mjs" \
  >"$artifacts_dir/node.log" 2>&1; then
  tail -n 8 "$artifacts_dir/node.log"
else
  cat "$artifacts_dir/node.log"
  exit 1
fi

printf '%s\n' 'Checking the embedded dashboard from isolated Release artifacts…'
if dotnet test \
  "$repo_root/Jellyfin.Plugin.MetaTagger.Tests/Jellyfin.Plugin.MetaTagger.Tests.csproj" \
  --configuration Release \
  --artifacts-path "$artifacts_dir/build" \
  --filter 'FullyQualifiedName~Jellyfin.Plugin.MetaTagger.Tests.PluginTests' \
  >"$artifacts_dir/dotnet.log" 2>&1; then
  tail -n 5 "$artifacts_dir/dotnet.log"
else
  cat "$artifacts_dir/dotnet.log"
  exit 1
fi

printf '%s\n' 'Dashboard checks passed.'
