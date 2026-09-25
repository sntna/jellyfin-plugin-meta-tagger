# Release builds and dependency caching

Verify and Release cache NuGet packages using both committed `packages.lock.json`
files. CodeQL uses the plugin lockfile because it builds only the plugin. The
separate key prevents a CodeQL run from saving a shared cache that lacks test
packages. No compiled binaries or disposable Jellyfin state are cached.

`Directory.Build.props` enables dependency lockfiles. In CI, restores use locked
mode and fail if project dependencies differ from their committed lockfiles.
When intentionally updating a package, run this locally and commit both the
project changes and resulting lockfile changes:

```sh
dotnet restore Jellyfin.Plugin.MetaTagger.Tests/Jellyfin.Plugin.MetaTagger.Tests.csproj --force-evaluate
./scripts/build-and-test.sh
```

The first run for a cache key downloads dependencies and saves the cache.
Subsequent eligible runs restore it. A cache miss still performs a normal NuGet
restore. Node and Python tests use their standard libraries and need no package
cache. Docker images are not cached by these workflows.

After the disposable Jellyfin lifecycle test succeeds, Release and
`scripts/release.sh` pass `--tested-repository .jellyfin-test/repository` to the
packager. It checks the catalog's plugin identity, version, target ABI, and
package checksum, validates the ZIP contents, and copies the ZIP unchanged.
It generates a new public manifest, catalog image, and `SHA256SUMS` without
invoking a second plugin build. The final byte comparison remains in place.

Without `--tested-repository`, packaging still builds the plugin. Use that mode
to create the initial candidate or a local install catalog. Only reuse a catalog
after testing it from the same checkout; the reuse option does not run Jellyfin
or prove that a lifecycle test has completed.

## Release preparation

Both local preparation and **Actions → Prepare release** run
`scripts/prepare_release.py`. Preparation needs Python 3.11+ and Git history
with release tags. The workflow also uses GitHub CLI through its pinned pull
request action. It does not need .NET, Node, or Docker to draft metadata.

```sh
git fetch origin --tags
python3 scripts/prepare_release.py
python3 scripts/prepare_release.py --bump patch
python3 scripts/prepare_release.py --bump minor
python3 scripts/prepare_release.py --bump 1.0.0
```

Run one preparation command from a clean checkout. `--bump` defaults to `auto`.
The public version comes from the plugin project metadata at the prior release
tag. The command checks that the current version is either that version or an
already prepared matching target. It updates the project, package, assembly,
file, plugin constant, manifest, and version contract expectations together.
It leaves Jellyfin's target ABI unchanged.

| Title | Automatic result | Changelog group |
| --- | --- | --- |
| `feat: Add a capability` | Minor bump | Added |
| `fix: Correct a behavior` | Patch bump | Fixed |
| `feat!: Replace a behavior` or `fix(scope)!: Remove support` | Minor before 1.0, major from 1.0 | Changed, marked Breaking |
| `docs:`, `test:`, `chore:`, `ci:`, and other non-breaking maintenance | No release | Omitted by default |
| Any `deps` or `deps-dev` scope | No release | Changed with an explicit bump |

The largest automatic bump wins. Breaking changes are classified from `!` in
the squash commit title. Commit bodies are not classification inputs. Explicit
`patch`, `minor`, or a strictly increasing three-part target override the bump.
Prerelease suffixes, leading zeros, empty ranges, and malformed titles are
rejected before changing files.

Dependency releases require an explicit bump. For example,
`python3 scripts/prepare_release.py --bump patch` includes dependency titles in
the draft. To include documentation or other maintenance too, add
`--include-maintenance`. Maintenance-only releases require both that flag and an
explicit bump. The workflow exposes the same options as `bump` and
`include_maintenance`.

The draft moves existing Unreleased notes into the dated section, adds grouped
entries, and updates comparison links. The default date is the command's current
date; `--date YYYY-MM-DD` supplies a reproducible date. Review the wording before
merging. Rerunning the local command with the same inputs keeps the prepared
version and edited notes unchanged. If product commits arrive after preparation,
prepare a fresh draft from main before the version commit instead of silently
including changes in an already reviewed release.

The workflow always starts from `main` and owns the single branch
`codex/release-preparation`. Rerunning it regenerates that branch and updates the
same open pull request. Make final changelog edits after the last workflow run,
since regeneration replaces the generated proposal. Merge only after reviewing
the version, notes, and normal Verify checks. Then dispatch **Release** on `main`
or use `scripts/release.sh`. Preparation never starts publication automatically.

## GitHub App setup

Create a GitHub App installed only on this repository with **Contents: Read and
write** and **Pull requests: Read and write**. Store its App ID in the repository
Actions variable `RELEASE_APP_ID` and its private key in the Actions secret
`RELEASE_APP_PRIVATE_KEY`. The workflow further limits the installation token to
this repository and those permissions. Its ordinary `GITHUB_TOKEN` has only
`contents: read`. No App token is used by the publisher.

The App token is required so the preparation pull request starts normal
`pull_request` verification automatically. GitHub documents that pull requests
created with `GITHUB_TOKEN` require workflow approval, while App installation
tokens permit automatic runs. See [GitHub's workflow trigger documentation](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).
There is no fallback to an unverified pull request when App credentials are
missing. Install and configure the App before the first preparation dispatch.

## Publication and retries

Release validates the committed version and main ancestry, extracts the reviewed
changelog section, runs the full verification and disposable Jellyfin lifecycle
checks, and copies the tested ZIP before creating a tag. It fetches and validates
the remote tag again before publication. An existing tag must point to the
intended commit; no release path moves a tag.

Publication creates a draft, uploads assets, and publishes only after uploads
succeed. A retry at the same commit resumes an unfinished draft. It leaves an
already published release unchanged. The body comes from
`scripts/release_notes.py`, which rejects a missing, duplicate, or empty release
section. No independently generated GitHub release notes are used.
