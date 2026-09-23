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
