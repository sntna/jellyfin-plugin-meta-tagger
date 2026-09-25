# Releases and versioning

Prepare release metadata with `scripts/prepare_release.py` or the **Prepare release**
workflow. Preparation calculates a proposed version from squash-merged Conventional
Commit titles and drafts an editable changelog. Maintainers review and merge the
proposal before publishing. The **Release** workflow reads that committed version.
Pushing `main` alone runs verification, not publication.

This project follows [Semantic Versioning](https://semver.org/). Before `1.0.0`, incompatible behavior changes increment the minor version. Compatible fixes increment the patch version. Use a minor bump for new
features. At `1.0.0` and later, incompatible changes increment the major version.

| Change | Example from `0.1.0` |
| --- | --- |
| Compatible bug fix | `0.1.1` |
| New feature or incompatible change before 1.0 | `0.2.0` |
| Deliberate first stable release | `1.0.0` |

Documentation-only changes can wait for the next plugin release. Conventional
Commit-style pull request titles drive the automatic preparation proposal. Dependency-only
releases require an explicit bump. See [release preparation](release-builds.md#release-preparation)
for classification rules and overrides.

## Version fields

The preparation command updates these version fields together, along with the
version contract test expectations:

| File | Fields |
| --- | --- |
| `Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj` | `Version`, `PackageVersion`, `AssemblyVersion`, `FileVersion` |
| `Jellyfin.Plugin.MetaTagger/Plugin.cs` | `PluginVersion` |
| `Jellyfin.Plugin.MetaTagger/manifest.json` | `versions[].version` |

The public version uses `major.minor.patch`. Jellyfin manifest, assembly, and file versions use `major.minor.patch.0`. Update every version surface together and keep the independent Jellyfin target ABI unchanged unless the host compatibility changes.

Releases use `v*.*.*` tags that match the committed project version. Configure tag protection in GitHub to limit release creation to maintainers and the release workflow. Never move a published tag.

## Prepare a release

From a clean checkout of `main` with release tags fetched, run:

```sh
git fetch origin --tags
python3 scripts/prepare_release.py
```

The command updates the version fields and drafts a dated, grouped changelog
section with comparison links. It edits the working tree without committing,
pushing, creating tags, or publishing. Review the diff, edit the release notes,
and open a pull request. See [release preparation](release-builds.md#release-preparation)
for explicit bumps, target versions, and dependency or maintenance releases.

To prepare on GitHub, select **Actions → Prepare release → Run workflow** on
`main`. It runs the same command and opens or updates a draft preparation pull
request. Configure the repository-scoped GitHub App first using the
[App setup instructions](release-builds.md#github-app-setup). Its token allows
normal Verify checks to run automatically. Rerunning preparation replaces the
generated proposal, so make final changelog edits after the last run.

## Release from GitHub

1. Prepare the release locally or with **Prepare release** as described above.
2. Review the proposed version and edit the notes in `CHANGELOG.md`.
3. Verify the preparation pull request and have a maintainer merge it into `main`.
4. Select **Actions → Release → Run workflow** with branch `main`.
5. Check that the workflow succeeds and the release contains the ZIP, `manifest.json`,
   `meta-tagger.png`, and `SHA256SUMS`.

The workflow reads the version from the project, runs verification and the disposable Jellyfin ZIP lifecycle test, creates the tag at the selected commit, and publishes the tested assets with release notes from the reviewed changelog
section. Publication is a separate manual step after preparation. An existing tag is accepted only if it points to that same commit. Tag creation and publication run in the same workflow because pushes made with `GITHUB_TOKEN` do not trigger another release run.

## Release from your machine

After merging the preparation pull request, update your clean `main` checkout
and run:

```sh
./scripts/release.sh
```

This requires the development dependencies and Docker or OrbStack. It runs the same checks, writes the ZIP, manifest, image, and `SHA256SUMS` to `artifacts/release/v<version>/`, writes the reviewed notes beside that directory, then creates an annotated local tag. It does not push. Push `main` and then the specific tag printed by the script. The tag push runs the GitHub release workflow, which verifies and publishes its own tested assets and checksums.

## Retries and existing tags

Both entry points reject a tag that points to another commit. Do not move or
delete a release tag. Prepare a new version when the release commit changes.
A failed run after tag creation can be retried at the same commit. Upload failures
leave a draft that a retry can complete; an already published release is never
overwritten.

## When the ZIP is created

For a manual GitHub release, the order is verification, disposable-server ZIP
lifecycle testing, release asset creation, byte-for-byte ZIP comparison, tag
creation, then GitHub Release publication. A failed check before tag creation
leaves no new tag. For example, version `0.1.0` produces tag `v0.1.0` and ZIP
`meta-tagger_0.1.0.0.zip`.

For a tag-triggered release, the tag already exists when the workflow starts.
The workflow still rebuilds and tests the package before publishing it.

Local packaging does not require a Git tag to exist. To build and serve a ZIP
for catalog testing, run `./scripts/serve-local-plugin-repository.sh`. To create
assets directly, run this from the repository root, replacing the example tag
with the committed project version:

```sh
python3 scripts/package_release.py \
  --repository https://github.com/sntna/jellyfin-plugin-meta-tagger \
  --tag v0.1.0 --output artifacts/release/v0.1.0
```

The `--tag` argument validates the version and supplies release download URLs;
it does not create a Git tag. `./scripts/build.sh` builds only the DLL.
