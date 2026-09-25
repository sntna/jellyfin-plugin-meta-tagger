#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
if [[ "$(git branch --show-current)" != main ]]; then
  echo 'Prepare local releases from main.' >&2
  exit 1
fi
release_commit="$(git rev-parse HEAD)"
release_tag="$(python3 scripts/release_tag.py)"
python3 scripts/release_notes.py > /dev/null
./scripts/build-and-test.sh
python3 scripts/disposable-jellyfin.py create
./scripts/test-release-lifecycle.sh
release_output="artifacts/release/${release_tag}"
python3 scripts/package_release.py \
  --repository https://github.com/sntna/jellyfin-plugin-meta-tagger \
  --tested-repository .jellyfin-test/repository \
  --tag "$release_tag" --output "$release_output"
cmp "$release_output"/meta-tagger_*.zip .jellyfin-test/repository/meta-tagger_*.zip
if [[ "$(git rev-parse HEAD)" != "$release_commit" ]]; then
  echo 'HEAD changed during verification; no tag was created.' >&2
  exit 1
fi
python3 scripts/release_notes.py > "${release_output}-notes.md"
python3 scripts/release_tag.py --create
printf 'Release assets: %s\nPublish after pushing main: git push origin %s\n' "$release_output" "$release_tag"
