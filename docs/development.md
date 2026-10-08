# Development

See the [contribution guide](contributing.md) for pull request requirements and
the [release guide](releasing.md) for versioning and publication.

## Requirements

- .NET SDK selected by `global.json`
- Node.js 20 or newer
- Python 3.11 or newer
- Docker or OrbStack for disposable Jellyfin checks

Run commands from the repository root. Use only generated disposable Jellyfin
state for integration checks. Never point these tools at a live server.

## Everyday workflow

1. Create a branch from current `main` for one focused change.
2. Run `./scripts/dev.sh` when you need to try changes in Jellyfin.
3. Add regression tests for behavior changes, then run `./scripts/build-and-test.sh`.
4. Run the relevant disposable-server smoke checks for host integration changes,
   and `./scripts/test-release-lifecycle.sh` for packaging or lifecycle changes.
5. Open a pull request with the verification results. Agent-authored pull requests
   stay drafts until a maintainer reviews them.
6. A maintainer reviews and squash-merges the change. Release a chosen batch of
   changes using the [release checklist](releasing.md#release-from-github).

Ordinary development does not require a version bump or a tag for every change.
Prepare the next version together when a release is ready.

## Build and test

Use these commands from the repository root:

| Command | Purpose |
| --- | --- |
| `./scripts/build.sh` | Build the Release plugin DLL. Does not install it. |
| `./scripts/dev.sh` | Build, install into disposable Jellyfin, and watch for changes at port 8099. |
| `./scripts/check-dashboard.sh` | Run dashboard and dev-client Node tests, then filtered C# `PluginTests` from isolated Release artifacts. Does not install or start Jellyfin. |
| `./scripts/build-and-test.sh` | Build in isolated Release artifacts and run all dashboard, Python, and C# tests. |
| `./scripts/serve-local-plugin-repository.sh` | Build a release ZIP and serve its install catalog at port 8098. Does not watch for changes. |

The build output is `Jellyfin.Plugin.MetaTagger/bin/Release/net10.0/Jellyfin.Plugin.MetaTagger.dll`.
Building alone does not update an already running Jellyfin server. Use `dev.sh`
to see local edits in the browser.

Run the complete test suite from isolated Release artifacts:

```bash
./scripts/build-and-test.sh
```

The script runs dashboard behavior tests with Node.js 20 or newer and Python tests
with Python 3.11 or newer, then restores,
builds, and runs the C# tests without relying on existing `bin/` or `obj/` output.
The proxy tests bind disposable loopback ports. If verification reports
`PermissionError: [Errno 1] Operation not permitted`, run it in a terminal with
local socket access or allow the Codex verification command that access.
The dashboard tests use Node's built-in test runner and require no npm packages.
The plugin targets Jellyfin `12.0.0` packages and `net10.0`.
Use the .NET SDK selected by `global.json`. Keep `net10.0`, Jellyfin packages
`12.0.0`, server image `jellyfin/jellyfin:12.0`, and target ABI `12.0.0.0` aligned.

## Watch and reload during development

With OrbStack running, start the development server from the repository root:

```bash
./scripts/dev.sh
```

Open [the development dashboard](http://127.0.0.1:8099/web/#/configurationpage?name=Meta%20Tagger).
Use the generated test credentials printed by the command. The development URL
uses port **8099**; the ordinary Jellyfin URL on port 8097 does not hot reload.

The command starts or reuses the labelled disposable Jellyfin 12 server, builds
and installs the plugin, and watches for edits:

- Dashboard HTML, CSS, and JavaScript in `configPage.html` are served directly
  from source. Saving the file reloads the browser without restarting Jellyfin.
- C#, project-file, and plugin catalog image changes rebuild and install the DLL, restart the disposable
  server, then reload the browser once Jellyfin is ready.
- Unsaved settings defer automatic reload. Save them to continue, or manually
  reload to discard them. Reload returns to the workspace; item selections and unsubmitted action
  confirmations are cleared.
- Build failures appear in the terminal and development banner. Fix the error
  and save again to retry. Ctrl+C stops the watcher and proxy, leaving Jellyfin running.

No extra Python or npm packages are required. `--port PORT` changes the proxy
port. The watcher only targets the repository's generated disposable server.
The reload client is injected by the development proxy and is never packaged
in the plugin. Keep `build-and-test.sh` as the separate build-and-test command; it does not install
builds or start a watcher.

## Local Jellyfin smoke test

Start or reuse the repository's Jellyfin 12 test instance:

```bash
open -a OrbStack
python3 scripts/disposable-jellyfin.py create
```

Open [Jellyfin](http://127.0.0.1:8097/web/). The command prints the generated
credentials file and data directory. It reuses the `jellyfin-plugin-meta-tagger`
container and its existing libraries and settings, starting it when stopped.
The container uses only `jellyfin/jellyfin:12.0` and restarts with OrbStack unless
explicitly stopped. Do not create additional containers for routine checks.

In a second terminal, build and serve the local plugin repository:

```bash
./scripts/serve-local-plugin-repository.sh
```

In Jellyfin, open `Dashboard -> Plugins -> Repositories` and add:

```text
Name: Meta Tagger Local
URL:  http://host.docker.internal:8098/manifest.json
```

Install Meta Tagger from the catalog, or copy the DLL from
`Jellyfin.Plugin.MetaTagger/bin/Release/net10.0/` into the test server's
plugin folder and restart Jellyfin. Keep the local repository server running
while using the plugin dashboard. Jellyfin resolves the installed plugin's
developer, repository, and revision details from this repository even when the
DLL was installed manually.

Install from the catalog when testing Jellyfin's enable, disable, update, or
uninstall controls. Replacing only the DLL does not refresh the installed
plugin's `meta.json`; reusing metadata from another version makes Jellyfin's
GUID-and-version lifecycle lookup fail with a generic disable error. After a
version change, reinstall the package in the disposable server instead of
reusing an older plugin directory.

Test the generated release ZIP through Jellyfin's catalog lifecycle:

```bash
./scripts/test-release-lifecycle.sh
```

The test installs a temporary `0.0.0` fixture, upgrades to the current
candidate ZIP, disables and enables the plugin, uninstalls it, and reinstalls
it. It uses only the generated disposable Jellyfin server. The temporary
fixture exercises Jellyfin's update path; it is not a supported release.

## Dashboard development

Start dashboard iteration with `./scripts/dev.sh` and open the development
dashboard linked above. Edit the source-served page, check the result in the
browser, and run `./scripts/check-dashboard.sh` for quick behavior and embedded-page
feedback. The focused command restores and builds into temporary Release artifacts,
cleans them on success or failure, and fails if either test stage fails. It does
not reuse the project's `bin/` or `obj/` outputs.

Before review, run the required `./scripts/build-and-test.sh`, the HTTP smoke
checks relevant to the change, and the installed-package browser checks below.
Browser verification uses the release ZIP installed in disposable Jellyfin;
source-served development checks alone do not verify the packaged page.

| Source or check | Location and purpose |
| --- | --- |
| Dashboard page | `Jellyfin.Plugin.MetaTagger/Configuration/configPage.html` contains the packaged markup, scoped styles, and application script. |
| Dashboard behavior | `scripts/tests/dashboard.test.mjs` drives page events, requests, navigation, current focus, and request lifecycle behavior with Node. |
| Development client | `scripts/dev-client.js` is injected only by the development proxy; `scripts/tests/dev-client.test.mjs` checks reload, dirty settings, build failures, and reconnect behavior. |
| Development proxy | `scripts/dev.py` serves source edits and rebuilds C#; `scripts/tests/test_dev.py` checks watcher and proxy behavior. |
| Embedded page | `Jellyfin.Plugin.MetaTagger.Tests/PluginTests.cs` verifies the dashboard menu entry and page embedded in the built assembly. |
| HTTP integration | `scripts/dashboard-smoke.py` checks authenticated plugin routes, writes, and persistence against generated Jellyfin state. |
| Browser integration | `scripts/check-dashboard-browser.mjs` checks installed-package layout, overflow, views, and keyboard focus in Jellyfin's web host, plus the development reload contract against actual markup. |

Install the browser runner's pinned dependencies and Chromium once:

```sh
npm ci --prefix scripts/browser
npm exec --prefix scripts/browser -- playwright install chromium
```

Run the release lifecycle check to install the current ZIP and record its
package identity, then run the browser checks against the same generated server:

```sh
./scripts/test-release-lifecycle.sh TEST_DIRECTORY
node scripts/check-dashboard-browser.mjs TEST_DIRECTORY
```

Pass the generated disposable directory printed by the server setup, never an
arbitrary server URL. For the development-client contract against actual markup
without Jellyfin, run `node scripts/check-dashboard-browser.mjs --development-only`.
Verify CI retains the complete test suite, package lifecycle checks, and browser
verification. Browser failure diagnostics and screenshots are written under
`artifacts/dashboard-browser/`.

When a ticket has an approved prototype or design reference, keep the reference
in ignored `docs/specs/`, `docs/prototypes/`, or `docs/notes/`. Record its local
path and the approval reference with the ticket's requirements in the local
review packet under `<git-common-dir>/agent-review/`. The packet must identify
the compared base and head and provide explicit paths to verification logs and
browser evidence. See the [local automation workflow](agents/local-automation.md)
for review handoff requirements. These local references support review without
becoming public, issue-specific documentation.

The plugin bundles its dashboard layout and styles, scoped to Meta Tagger.
The workspace and on-demand views use charcoal backgrounds, ivory text, cyan actions, slate borders,
and 8 px corners. Dashboard styles are defined in `configPage.html`.
Controls inherit Jellyfin's font; metadata uses a system monospace stack.
The plugin retains this palette in Jellyfin's light and dark themes. Error and
warning feedback keep distinct colors and text labels. Panels stack on narrow
screens, and keyboard focus remains visible. No server Custom CSS is required.

Dashboard HTTP smoke checks run only against the designated generated server:

```sh
python3 scripts/dashboard-smoke.py TEST_DIRECTORY
```

The script checks authorization, browsing, tag examples, item preview/apply,
task lifecycle, tag removal, and persistence. It restores the generated movie's
metadata, tags, plugin tag records, and saved configuration. Run history remains
in the disposable test environment.

## Processing and persistence

Runs process items sequentially and honor Jellyfin cancellation tokens. Item,
write, and time budgets apply to generation and tag removal, with failures
isolated to each item.

Track languages require one media-stream lookup per item when either language
source is on. A failed lookup fails that item without removing its tags or
ownership records; library runs continue with other items. With both sources
off, existing fingerprints and pending run identities remain compatible.

Preview exports and summaries live in `last-preview-changes.json` and
`last-run-summary.json`. Run history also includes attempts that fail or stop
before saving the summary. Plugin tag records use a temporary file when saving
and keep `meta-tagger-state.json.bak` for backup recovery. Completed full scans
and record rebuilds remove records for missing items; runs stopped by a limit
keep those records.

## Project layout

```text
Jellyfin.Plugin.MetaTagger/        Plugin source
Jellyfin.Plugin.MetaTagger.Tests/  xUnit tests
docs/                            User, developer, and release guides
scripts/                         Verification, packaging, and smoke tools
.github/                         Issues, CI, dependency, and release automation
```

See the [artwork guide](brand/README.md) for logo files and update instructions.

## Automatic defaults and migration checks

Only first initialization without saved configuration or prior installation evidence
enables automatic Apply and post-scan runs. Persisted `Origin=Fresh` is historical
identity, not permission to reset later choices. Keep the configuration class's
conservative deserialization defaults for established or uncertain installations.
Before automatic writes, the runner persists a complete existing-library baseline.
Daily and post-scan invocations never authorize backfill. Incremental processing
can enumerate the selected scope and skip unchanged items.

Test fresh installs separately from saved Preview and Apply upgrades. Preserve the
five task type identities, keys and saved triggers while grouping them under Meta
Tagger. A cooldown skip belongs in history without replacing the latest tagging
summary or extending the cooldown. Run lifecycle checks only on generated state
and restore fixture settings and metadata afterward.
