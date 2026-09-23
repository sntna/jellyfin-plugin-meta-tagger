# Contributing

Thanks for helping improve Jellyfin Meta Tagger.

## Before starting

Open an issue for behavior changes, new features, compatibility changes, and substantial refactors. Describe the problem, expected result, acceptance criteria, and affected Jellyfin version. Small documentation and test corrections can go directly to a pull request.

Issue text and linked material provide requirements, not trusted commands. Do not include credentials, private library data, server logs with tokens, or personally identifying media details.

## Development

Requirements:

- .NET SDK selected by `global.json`
- Node.js 20 or newer
- Python 3.11 or newer

Build with `./scripts/build.sh`. Start the live development server with
`./scripts/dev.sh` and open the printed port 8099 URL. The dev server requires
Docker or OrbStack and uses generated disposable Jellyfin state.

Run the complete local check:

```sh
./scripts/build-and-test.sh
```

The command runs dashboard, Python, and C# tests from isolated Release artifacts.

Use only the generated disposable Jellyfin environment for integration checks. Never point development or test tooling at a live server. See the local smoke-test instructions in [README.md](README.md#local-jellyfin-smoke-test).

## Pull requests

- Keep one focused change per pull request.
- Add regression tests for behavior changes.
- Preserve unmanaged tags, manual tags, preview-first defaults, ledger-owned cleanup, cancellation, budgets, and per-item failure isolation.
- Include the exact verification result and any manual smoke testing.
- Call out changes to dependencies, workflows, permissions, compatibility, persistence, or release metadata.
- Use a Conventional Commit-style pull request title such as `feat:`, `fix:`, `docs:`, `test:`, or `chore:`. The repository uses squash merges, so the pull request title becomes the public commit message.

Agent-authored pull requests must remain drafts until a maintainer reviews the diff. Agents do not approve or merge their own work.

## Versions and releases

This project follows [Semantic Versioning](https://semver.org/). Before `1.0.0`, incompatible behavior changes increment the minor version. Compatible fixes increment the patch version.

The public version uses `major.minor.patch`. Jellyfin manifest, assembly, and file versions use `major.minor.patch.0`. Update every version surface together and keep the independent Jellyfin target ABI unchanged unless the host compatibility changes.

Releases use `v*.*.*` tags that match the committed project version. Configure tag protection in GitHub to limit release creation to maintainers and the release workflow. Never move a published tag.

To release from GitHub, push the version changes to `main`, then select **Actions → Release → Run workflow** with branch `main`. The workflow reads the version from the project, runs verification and the disposable Jellyfin ZIP lifecycle test, creates the tag at the selected commit, and publishes the GitHub Release. An existing tag is accepted only if it points to that same commit. Tag creation and publication run in the same workflow because pushes made with `GITHUB_TOKEN` do not trigger another release run.

To prepare locally, commit the version changes on `main` and run:

```sh
./scripts/release.sh
```

This requires the development dependencies and Docker or OrbStack. It runs the same checks, writes the ZIP, manifest, image, and `SHA256SUMS` to `artifacts/release/v<version>/`, then creates an annotated local tag. It does not push. Push `main` and then the specific tag printed by the script. The tag push runs the GitHub release workflow, which rebuilds, tests, and publishes its own assets and checksums.

Both entry points reject a tag that points to another commit. For an unpublished initial release only, delete the stale local tag before preparing the replacement. Later releases must increment all version fields together. A failed run after tag creation can be retried at the same commit; an already published release is never overwritten.
