#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
plugin_project="$repo_root/Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj"
repository_dir="$repo_root/.jellyfin-test/repository"
prepare_only=false

if [[ "${1:-}" == "--prepare-only" ]]; then
  prepare_only=true
  shift
fi

repository_port="${1:-8098}"
if [[ ! "$repository_port" =~ ^[0-9]+$ ]] ||
  ((repository_port < 1 || repository_port > 65535)); then
  printf 'Port must be an integer between 1 and 65535.\n' >&2
  exit 2
fi

for command_name in dotnet python3; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required command not found: %s\n' "$command_name" >&2
    exit 1
  fi
done

mkdir -p -- "$repository_dir"

plugin_version="$(dotnet msbuild "$plugin_project" -nologo -getProperty:AssemblyVersion)"
if [[ ! "$plugin_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  printf 'Could not resolve the plugin assembly version.\n' >&2
  exit 1
fi

repository_base_url="http://host.docker.internal:${repository_port}"
python3 "$repo_root/scripts/package_release.py" \
  --repository "https://github.com/sntna/jellyfin-plugin-meta-tagger" \
  --tag "v${plugin_version%.*}" \
  --source-url-base "$repository_base_url" \
  --output "$repository_dir"

if ! grep -Fq "\"sourceUrl\": \"${repository_base_url}/meta-tagger_${plugin_version}.zip\"" "$repository_dir/manifest.json"; then
  printf 'Could not populate the local repository manifest.\n' >&2
  exit 1
fi

printf 'Prepared Meta Tagger repository at %s\n' "$repository_dir"
printf 'Jellyfin repository URL: %s/manifest.json\n' "$repository_base_url"
printf 'Use a catalog install for enable, disable, update, and uninstall tests; a DLL-only copy does not refresh installed plugin metadata.\n'

if "$prepare_only"; then
  exit 0
fi

exec python3 -m http.server "$repository_port" \
  --bind 127.0.0.1 \
  --directory "$repository_dir"
