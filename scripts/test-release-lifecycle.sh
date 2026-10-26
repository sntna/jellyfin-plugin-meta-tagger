#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_directory="${1:-}"
current_port="${2:-8098}"
previous_port="${3:-8099}"
# Linux Docker's host gateway cannot reach a listener bound only to loopback.
# Keep local catalogs private by default; CI opts into a reachable address.
repository_bind_address="${META_TAGGER_REPOSITORY_BIND_ADDRESS:-127.0.0.1}"
# Opt in to upgrading over a genuinely older published release. Unset, the test
# uses the synthetic 0.0.0 fixture and never reaches the network.
previous_release_tag="${META_TAGGER_PREVIOUS_RELEASE_TAG:-}"
release_download_base="${META_TAGGER_RELEASE_DOWNLOAD_BASE:-}"
plugin_repository_url="https://github.com/sntna/jellyfin-plugin-meta-tagger"
current_repository="$repo_root/.jellyfin-test/repository"
previous_repository="$repo_root/.jellyfin-test/repository-previous"
previous_version="0.0.0.0"
previous_release=""
release_version="$(PYTHONPATH="$repo_root/scripts" python3 -c 'from package_release import _project_versions; print(_project_versions()[0])')"

for port in "$current_port" "$previous_port"; do
  if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1 || port > 65535)); then
    printf 'Ports must be integers between 1 and 65535.\n' >&2
    exit 2
  fi
done

mkdir -p -- "$current_repository"

if [[ -n "$previous_release_tag" ]]; then
  # Check the tag before it names a directory that is removed by name or
  # reaches the network. An unchecked tag would escape the downloads directory.
  if [[ ! "$previous_release_tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'META_TAGGER_PREVIOUS_RELEASE_TAG must use the vX.Y.Z form.\n' >&2
    exit 2
  fi
  previous_release_version="${previous_release_tag#v}.0"
  if [[ "$previous_release_version" == "${release_version}.0" ]]; then
    # Both plugins would install into the same versioned directory, so the
    # upgrade would be a no-op and every retention assertion would pass blindly.
    printf 'Published %s holds this tree version %s, so it cannot test an upgrade.\n' \
      "$previous_release_tag" "$previous_release_version" >&2
    exit 2
  fi
  # Downloaded release assets are untrusted data. Keep them in their own
  # disposable directory, verified against the release's published SHA256SUMS.
  download_directory="$repo_root/.jellyfin-test/release-downloads/$previous_release_tag"
  rm -rf -- "$download_directory"
  mkdir -p -- "$download_directory"
  fetch_arguments=(
    "$repo_root/scripts/fetch_released_catalog.py"
    --repository "$plugin_repository_url"
    --tag "$previous_release_tag"
    --output "$download_directory"
    --source-url-base "http://host.docker.internal:${previous_port}"
  )
  if [[ -n "$release_download_base" ]]; then
    fetch_arguments+=(--download-base "$release_download_base")
  fi
  fetch_status=0
  python3 "${fetch_arguments[@]}" || fetch_status=$?
  if ((fetch_status == 0)); then
    previous_repository="$download_directory"
    previous_version="$previous_release_version"
    previous_release="$previous_release_tag"
  elif ((fetch_status == 75)); then
    printf 'SKIP upgrade from published %s: assets unavailable. Using the synthetic 0.0.0 fixture.\n' \
      "$previous_release_tag" >&2
  else
    exit "$fetch_status"
  fi
fi

if [[ -z "$previous_release" ]]; then
  mkdir -p -- "$previous_repository"
  python3 "$repo_root/scripts/package_release.py" \
    --repository "$plugin_repository_url" \
    --tag v0.0.0 \
    --test-fixture-version 0.0.0 \
    --source-url-base "http://host.docker.internal:${previous_port}" \
    --output "$previous_repository"
fi

python3 "$repo_root/scripts/package_release.py" \
  --repository "$plugin_repository_url" \
  --tag "v${release_version}" \
  --source-url-base "http://host.docker.internal:${current_port}" \
  --output "$current_repository"

python3 -m http.server "$previous_port" --bind "$repository_bind_address" --directory "$previous_repository" >/dev/null 2>&1 &
previous_server=$!
python3 -m http.server "$current_port" --bind "$repository_bind_address" --directory "$current_repository" >/dev/null 2>&1 &
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
  --previous-version "$previous_version"
)
if [[ -n "$previous_release" ]]; then
  lifecycle_arguments+=(--previous-release-tag "$previous_release")
fi
if [[ -n "$test_directory" ]]; then
  lifecycle_arguments+=(--directory "$test_directory")
fi
python3 "$repo_root/scripts/disposable-jellyfin.py" "${lifecycle_arguments[@]}"
