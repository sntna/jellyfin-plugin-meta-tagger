#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_directory="${1:-}"
current_port="${2:-8098}"
previous_port="${3:-8099}"
current_repository="$repo_root/.jellyfin-test/repository"
previous_repository="$repo_root/.jellyfin-test/repository-previous"
release_version="$(PYTHONPATH="$repo_root/scripts" python3 -c 'from package_release import _project_versions; print(_project_versions()[0])')"

for port in "$current_port" "$previous_port"; do
  if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1 || port > 65535)); then
    printf 'Ports must be integers between 1 and 65535.\n' >&2
    exit 2
  fi
done

mkdir -p -- "$current_repository" "$previous_repository"

python3 "$repo_root/scripts/package_release.py" \
  --repository "https://github.com/sntna/jellyfin-plugin-meta-tagger" \
  --tag v0.0.0 \
  --test-fixture-version 0.0.0 \
  --source-url-base "http://host.docker.internal:${previous_port}" \
  --output "$previous_repository"

python3 "$repo_root/scripts/package_release.py" \
  --repository "https://github.com/sntna/jellyfin-plugin-meta-tagger" \
  --tag "v${release_version}" \
  --source-url-base "http://host.docker.internal:${current_port}" \
  --output "$current_repository"

python3 -m http.server "$previous_port" --bind 127.0.0.1 --directory "$previous_repository" >/dev/null 2>&1 &
previous_server=$!
python3 -m http.server "$current_port" --bind 127.0.0.1 --directory "$current_repository" >/dev/null 2>&1 &
current_server=$!

cleanup() {
  kill "$previous_server" "$current_server" 2>/dev/null || true
  wait "$previous_server" "$current_server" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

lifecycle_arguments=(
  test-release-lifecycle
  "http://host.docker.internal:${current_port}/manifest.json"
  "$current_repository/meta-tagger_${release_version}.0.zip"
  --version "${release_version}.0"
  --previous-manifest-url "http://host.docker.internal:${previous_port}/manifest.json"
)
if [[ -n "$test_directory" ]]; then
  lifecycle_arguments+=(--directory "$test_directory")
fi
python3 "$repo_root/scripts/disposable-jellyfin.py" "${lifecycle_arguments[@]}"
