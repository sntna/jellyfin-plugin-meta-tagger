---
name: verify
description: Exercise a Meta Tagger change in the real plugin (tests, built DLL, disposable Jellyfin 12 in Docker) and collect evidence. Use before claiming a change works, and when reviewing a PR.
---

# Verify Meta Tagger changes

Run everything from the repository root. Paste the PASS/summary lines you got as evidence.
If a prerequisite is missing or a step cannot run, say so in the PR or review. Never fake or
paraphrase evidence you did not produce.

## Prerequisites

- .NET SDK per `global.json` (10.0.4xx), Node 20+, Python 3.11+. No npm packages for unit tests.
- Docker usable without sudo, for the disposable Jellyfin steps only. No secrets or env vars needed.
- On Linux Docker (not OrbStack) always `export META_TAGGER_DOCKER_CONTEXT=` (empty) first.
- Proxy tests bind loopback ports; `PermissionError: [Errno 1]` means the sandbox blocks sockets.
- Never point any script at a live Jellyfin. Only the generated `.jellyfin-test/` server.

## Gate

Must pass before every PR:

```bash
./scripts/check.sh   # public-tree check, Node tests, Python tests, C# build + xUnit (~30 s)
```
Correct output ends with `Passed!  - Failed:     0, Passed:   N` and Python `OK`.

## Start and stop

### Build the plugin

```bash
./scripts/build.sh   # -> Jellyfin.Plugin.MetaTagger/bin/Release/net10.0/Jellyfin.Plugin.MetaTagger.dll
```
Expect `0 Warning(s)  0 Error(s)`. Building does not install it anywhere.

### Run in a real Jellyfin 12 (Docker, ~2 min)

The server is the single container `jellyfin-plugin-meta-tagger` at http://127.0.0.1:8097,
state under `.jellyfin-test/v12-*/` (admin `tagger-admin`, password in that dir's
`credentials.json`; never print it). Seeded: 1 movie, 2 series, 4 episodes in 3 libraries.
If `create` says the container "is not owned by this repository", another checkout owns it:
do not remove it; report the live steps as not run.

```bash
export META_TAGGER_DOCKER_CONTEXT=
python3 scripts/disposable-jellyfin.py create      # prints Created/Reusing <DIR>, URL
META_TAGGER_REPOSITORY_BIND_ADDRESS=0.0.0.0 ./scripts/test-release-lifecycle.sh
D=$(ls -d .jellyfin-test/v12-*)
```
Lifecycle builds the release ZIP, serves catalogs on ports 8098/8099, installs through
Jellyfin's catalog and must print `PASS catalog ZIP install, upgrade ... reinstall lifecycle`.
This is the only working install path on Linux Docker (see "Not verifiable here").

Stop and clean up when done (config files are root-owned, so delete them via the image):
```bash
docker rm -f jellyfin-plugin-meta-tagger
docker run --rm -v "$PWD/.jellyfin-test:/t" --entrypoint /bin/sh jellyfin/jellyfin:12.0 -c 'rm -rf /t/*'
rm -rf .jellyfin-test artifacts/dashboard-browser
```

## Feature map

| Area | Command | Correct output |
| --- | --- | --- |
| Tag normalization (`meta:genre:science-fiction`) | `dotnet test Jellyfin.Plugin.MetaTagger.Tests -c Release --filter 'FullyQualifiedName~TagNormalizerTests'` | `Failed: 0` |
| Metadata sources -> tags | same, `~MetadataProjectionServiceTests`, `~MetadataTagProcessorTests` | `Failed: 0` |
| Manual/unowned tag preservation, ledger | same, `~TagMergeServiceTests`, `~MetaTaggerStateStoreTests` | `Failed: 0` |
| Runs, budgets, cancel, resume, eligibility, scheduled tasks, fresh-install/upgrade migration | same, `~MetaTaggerRunnerTests`, `~MetaTaggerRunBudgetTests`, `~MetaTaggerResumableCycleTests` | `Failed: 0` |
| Dashboard REST API | same, `~MetaTaggerDashboardControllerTests` | `Failed: 0` |
| Dashboard page (`configPage.html`) | `./scripts/check-dashboard.sh` | `Dashboard checks passed.` |
| Live tagging end to end (fresh server + lifecycle above) | `python3 scripts/disposable-jellyfin.py test-basic "$D"` | 16 `PASS` lines, exit 0 |
| Installed dashboard in a real browser | see below | `PASS dashboard browser installed-package` |
| Dev reload contract, no Jellyfin | `node scripts/check-dashboard-browser.mjs --development-only` (after browser setup) | `PASS dashboard browser development` |
| Packaging, manifest, release scripts | `python3 -m unittest discover -s scripts/tests -p 'test_package_release.py'` (or `test_release_*.py`) | `OK` |
| Plugin install/upgrade/disable/uninstall | `./scripts/test-release-lifecycle.sh` (above) | `PASS catalog ZIP install ...` |

`test-basic` asserts fresh-install defaults: run it on a newly created server right after the
lifecycle step, before anything else changes plugin settings. To rerun, clean up and recreate.

Browser check (one-time setup installs Chromium into `~/.cache/ms-playwright`, no sudo):
```bash
npm ci --prefix scripts/browser --ignore-scripts --no-audit --no-fund
npm exec --prefix scripts/browser -- playwright install chromium
node scripts/check-dashboard-browser.mjs "$D"
```

## Single tests

```bash
# C#: class or single method
dotnet test Jellyfin.Plugin.MetaTagger.Tests --configuration Release --filter 'FullyQualifiedName~TagNormalizerTests'
dotnet test Jellyfin.Plugin.MetaTagger.Tests --configuration Release --filter 'FullyQualifiedName~TagNormalizerTests.NormalizeValue_ReturnsStableAsciiSlug'
# Node: one file, optionally by test name
node --test scripts/tests/dashboard.test.mjs
node --test --test-name-pattern='reload' scripts/tests/dev-client.test.mjs
# Python: one file, optionally by test name
python3 -m unittest discover -s scripts/tests -p 'test_pr_title.py' -k test_accepts_release_and_dependabot_titles
```
Gotcha: `AutomaticTaskTests.cs`, `InstallationMigrationTests.cs`, `ItemExecutionContractTests.cs`,
`SourceExplanationTests.cs`, `Generation*Tests.cs`, `ItemGenerationRunTests.cs`, `DashboardDuringRunTests.cs`
and `RetainedRunDetailTests.cs` are `partial class MetaTaggerRunnerTests`. Filtering by those file
names matches nothing (`No test matches the given testcase filter`); filter by method instead:
`--filter 'FullyQualifiedName~MetaTaggerRunnerTests.ScheduledTasks_UseMetaTaggerCategoryAndPreserveKeysAndDefaultTriggers'`.
A "No test matches" run is a wrong filter, never a pass or a valid failure.

## Fail-before-fix

A test for new or fixed behaviour must fail on the base branch. Check that in a temporary
worktree. Never revert or stash files in your own working tree to do it.

```bash
base=$(git merge-base origin/main HEAD); head=$(git rev-parse HEAD)
tmp=$(mktemp -d)/base && git worktree add --detach "$tmp" "$base"
git -C "$tmp" checkout "$head" -- Jellyfin.Plugin.MetaTagger.Tests/TagNormalizerTests.cs  # the new/changed test file(s) only
(cd "$tmp" && dotnet test Jellyfin.Plugin.MetaTagger.Tests --configuration Release --filter 'FullyQualifiedName~TagNormalizerTests.NewTestName')
git worktree remove --force "$tmp"
```
Expect the test to fail on the base (an assertion failure, or a compile error because the new API
does not exist yet; say which). Then run the same single test in your own tree on the PR head and
expect it to pass. Node and Python tests work the same way: copy the test file, run it with the
single-test command above inside `"$tmp"`.

## Not verifiable here

Not verifiable headless on this machine (Linux Docker, no OrbStack):

- `./scripts/dev.sh` and `disposable-jellyfin.py install DIR DLL`: copy the DLL into the
  container's config dir, which Linux Docker makes root-owned (`PermissionError`; works only
  with OrbStack). Substitute: the catalog install via `test-release-lifecycle.sh`.
- `scripts/dashboard-smoke.py "$D"`: its HTTP scenarios run and print `PASS` (11 lines), then
  its final restore step fails with the same root-owned `PermissionError`. Quote the PASS
  lines, state that the restore failed, and recreate the server before other live checks.
- Visual review of the dashboard by a human: substitute the browser check above, whose
  diagnostics land in `artifacts/dashboard-browser/`.
- Real metadata providers (TMDb etc.): seeded libraries are offline; providers are disabled.
