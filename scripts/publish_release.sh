#!/usr/bin/env bash
# Called by Release only after verification, ZIP lifecycle tests, and tag validation.
set -euo pipefail

release_tag="${1:?release tag required}"
release_assets="${2:?tested assets directory required}"
release_notes="${3:?reviewed release notes file required}"

if release_draft="$(gh release view "$release_tag" --json isDraft --jq '.isDraft')"; then
  if [[ "$release_draft" == false ]]; then
    echo "$release_tag is already published; leaving it unchanged."
    exit 0
  fi
  if [[ "$release_draft" != true ]]; then
    echo 'Unexpected release state; refusing to publish.' >&2
    exit 1
  fi
else
  # A lookup failure never authorizes an overwrite. Creation fails if it exists.
  gh release create "$release_tag" --verify-tag --draft \
    --title "$release_tag" --notes-file "$release_notes"
fi
# Only drafts reach this point. A failed upload can safely be retried.
gh release upload "$release_tag" "$release_assets"/* --clobber
gh release edit "$release_tag" --draft=false --notes-file "$release_notes"
