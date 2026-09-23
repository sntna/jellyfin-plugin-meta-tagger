# Releases and versioning

The release workflow reads the committed version. It does not calculate a version
from commits, pull request labels, or Conventional Commit titles. Maintainers
choose the bump and commit it before running Release. Pushing `main` alone runs
verification, not publication.

This project follows [Semantic Versioning](https://semver.org/). Before `1.0.0`, incompatible behavior changes increment the minor version. Compatible fixes increment the patch version. Use a minor bump for new
features. At `1.0.0` and later, incompatible changes increment the major version.

| Change | Example from `0.1.0` |
| --- | --- |
| Compatible bug fix | `0.1.1` |
| New feature or incompatible change before 1.0 | `0.2.0` |
| Deliberate first stable release | `1.0.0` |

Documentation-only changes can wait for the next plugin release. Conventional
Commit-style pull request titles describe changes; they do not automate these bumps.

## Version fields

Update these files together:

| File | Fields |
| --- | --- |
| `Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj` | `Version`, `PackageVersion`, `AssemblyVersion`, `FileVersion` |
| `Jellyfin.Plugin.MetaTagger/Plugin.cs` | `PluginVersion` |
| `Jellyfin.Plugin.MetaTagger/manifest.json` | `versions[].version` |

The public version uses `major.minor.patch`. Jellyfin manifest, assembly, and file versions use `major.minor.patch.0`. Update every version surface together and keep the independent Jellyfin target ABI unchanged unless the host compatibility changes.

Releases use `v*.*.*` tags that match the committed project version. Configure tag protection in GitHub to limit release creation to maintainers and the release workflow. Never move a published tag.

## Release from GitHub

1. Choose the next version using the rules above.
2. Update all version fields listed above and add release notes to `CHANGELOG.md`.
3. Open a pull request, verify it, and have a maintainer merge it into `main`.
4. Select **Actions → Release → Run workflow** with branch `main`.
5. Check that the workflow succeeds and the release contains the ZIP, `manifest.json`,
   `meta-tagger.png`, and `SHA256SUMS`.

The workflow reads the version from the project, runs verification and the disposable Jellyfin ZIP lifecycle test, creates the tag at the selected commit, and publishes the GitHub Release. An existing tag is accepted only if it points to that same commit. Tag creation and publication run in the same workflow because pushes made with `GITHUB_TOKEN` do not trigger another release run.

## Release from your machine

To prepare locally, commit the version changes on `main` and run:

```sh
./scripts/release.sh
```

This requires the development dependencies and Docker or OrbStack. It runs the same checks, writes the ZIP, manifest, image, and `SHA256SUMS` to `artifacts/release/v<version>/`, then creates an annotated local tag. It does not push. Push `main` and then the specific tag printed by the script. The tag push runs the GitHub release workflow, which rebuilds, tests, and publishes its own assets and checksums.

## Retries and existing tags

Both entry points reject a tag that points to another commit. For an unpublished initial release only, delete the stale local tag before preparing the replacement. Later releases must increment all version fields together. A failed run after tag creation can be retried at the same commit; an already published release is never overwritten.

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
