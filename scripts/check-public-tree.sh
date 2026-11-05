#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

forbidden_pattern='(^|/)(\.DS_Store|credentials\.json)$|^\.agents/|^\.claude/|^\.codex/|^artwork/|^skills-lock\.json$|^CONTEXT\.md$|^docs/(adr|plans|specs|notes|[^/]+-evidence|audit-evidence|prototypes)/|^docs/.*-(plan|spec)\.md$|\.(log|patch)$'

# The shared verification skill is the only tracked agent file.
allowed_files='.claude/skills/verify/SKILL.md'

matches="$(git -C "$repo_root" ls-files | grep -E "$forbidden_pattern" | grep -vxF "$allowed_files" || true)"
if [[ -n "$matches" ]]; then
  printf 'Public tree contains internal or generated files:\n%s\n' "$matches" >&2
  exit 1
fi
