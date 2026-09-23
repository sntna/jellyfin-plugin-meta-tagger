# Jellyfin Meta Tagger

[![Verify](https://github.com/sntna/jellyfin-plugin-meta-tagger/actions/workflows/verify.yml/badge.svg)](https://github.com/sntna/jellyfin-plugin-meta-tagger/actions/workflows/verify.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Jellyfin Meta Tagger creates tags from the metadata already in Jellyfin.
For example, a Science Fiction genre becomes `meta:genre:science-fiction`.
You can use these tags in Jellyfin user access rules; the plugin does not decide
who should see an item.

Examples with the default tag format:

- `meta:genre:science-fiction`
- `meta:rating:tv-y`
- `meta:keyword:friendship`
- `meta:studio:apple-tv-plus`
- `meta:country:united-states`
- `meta:provider:tmdb`
- `meta:year:2023`
- `meta:audio-language:eng` with audio languages selected
- `meta:subtitle-language:spa` with subtitle languages selected

New installations preview changes without saving tags and keep outdated tags.
The plugin records which tags belong to it for each item. It only removes
recorded tags, and always keeps tags with the manual prefix and separator.

## Compatibility

| Component | Supported and tested |
| --- | --- |
| Jellyfin Server | `12.0.0` |
| Jellyfin plugin ABI | `12.0.0.0` |
| Plugin target framework | .NET `10.0` |

An administrator account is required to configure and run the plugin. Other
Jellyfin server versions are unsupported until this table lists them. Use the
.NET runtime supplied with the supported Jellyfin installation.

## Installation

In Jellyfin, open `Dashboard -> Plugins -> Repositories`, add a repository named
`Meta Tagger`, and use this URL:

```text
https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/latest/download/manifest.json
```

Install **Meta Tagger** from the plugin catalog and restart Jellyfin when
prompted. The release manifest points to the matching ZIP and includes the
checksum Jellyfin verifies during installation.

For a manual installation, download the versioned ZIP from
[GitHub Releases](https://github.com/sntna/jellyfin-plugin-meta-tagger/releases),
extract it into its own directory under Jellyfin's plugin directory, and restart
Jellyfin. Do not copy the ZIP itself into the plugin directory.

## Build and test

Use these commands from the repository root:

| Command | Purpose |
| --- | --- |
| `./scripts/build.sh` | Build the Release plugin DLL. Does not install it. |
| `./scripts/dev.sh` | Build, install into disposable Jellyfin, and watch for changes at port 8099. |
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
Use the .NET SDK selected by `global.json`. The plugin version remains `0.1.0`;
its Jellyfin target ABI is `12.0.0.0`.

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
  reload to discard them. The selected plugin tab is restored after reload;
  item selections and unsubmitted action confirmations are cleared.
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

The test installs a temporary `0.0.0` fixture, upgrades to the exact `0.1.0`
candidate ZIP, disables and enables the plugin, uninstalls it, and reinstalls
it. It uses only the generated disposable Jellyfin server. The temporary
fixture exercises Jellyfin's update path; it is not a supported release.

## Dashboard

The plugin bundles its dashboard layout and styles, scoped to Meta Tagger.
All six tabs use charcoal backgrounds, ivory text, cyan actions, slate borders,
and 8 px corners. Dashboard styles are defined in `configPage.html`.
Controls inherit Jellyfin's font; metadata uses a system monospace stack.
The plugin retains this palette in Jellyfin's light and dark themes. Error and
warning feedback keep distinct colors and text labels. Panels stack on narrow
screens, and keyboard focus remains visible. No server Custom CSS is required.

Meta Tagger has six primary destinations inside Jellyfin's dashboard:

- **Overview** shows the latest run and saved settings, including the scheduled
  run action and whether post-scan runs are on. **Preview tag changes** always previews
  the selected item types across all libraries without saving tags.
  **Stop preview** requests cancellation. **Edit settings** opens Settings.
- **Preview results** opens the latest available generation-preview differences. Its date,
  run identity, settings freshness, and incomplete or unavailable details remain
  visible. A later Apply or cleanup run does not replace these differences with
  unrelated results. When the latest-summary preview is no longer available,
  retained generation history supplies its own clearly identified results.
- **Browse items** opens search, library filtering, pages of 25, and the item
  inspector. The visible **Filter** dropdown filters preview status and applies
  only to the loaded page. A preview item's **Preview this item** action selects that item.
  Selecting an item automatically requests a fresh preview using saved settings.
  Confirmation becomes available only with a current approval; Apply requires
  selecting the confirmation checkbox. Unsaved settings must be saved first.
- **History** shows recorded runs and item results in separate scrolling lists.
  Selecting a run keeps keyboard focus and list position on that run.
  Counts appear separately from status guidance. Preview results and history items include thumbnails.
- **Settings** shows tagging and sources, with the names of every selected source.
  Genres, parental rating, and audio languages are prominent; the other six choices remain in
  **More sources**. **Try these settings** sits beside the form on wide screens
  and below it on narrow screens. It searches up to 25 choices and calculates
  tags using the current draft while Settings is visible. Leaving Settings stops
  requests and keeps the query and selected item. Returning uses the latest draft.
  **Reload example** recalculates after metadata changes elsewhere in
  Jellyfin. Tag format and item types have compact draft summaries; automation,
  outdated tags, and Advanced remain available in disclosures. Hidden choices
  still save, and validation opens their editors when necessary.
- **Maintenance** shows removal controls directly for tags recorded as plugin-owned. It requires
  a fresh preview and explicit confirmation. Leaving Maintenance invalidates
  that preview and confirmation. The Browse items shortcut opens Maintenance with
  the selected item ready to preview.

**History** is always available as a tab and from Overview and Preview results.
It retains the latest 20 attempts, including failures, cancellations, and
incomplete runs. Each run keeps up to 1,000 item details and 5 MiB of detail
data. Missing or shortened results are identified. History never authorizes an
item update or tag removal. Plugin tag removal still requires a separate preview
and confirmation; leaving Maintenance clears that confirmation. In Browse items,
**Preview tag removal** opens the removal destination with the selected item and
requests its removal list. **Remove listed tags** requires separate confirmation.

For library-wide changes, use the visible **Apply changes across all libraries**
section in Preview results, then **Scheduled Tasks** and choose **Apply metadata tag changes**. The link
only navigates. That task uses current saved settings and current metadata for
selected item types across all libraries, subject to protections and run limits.
It can write tags even when the default action is Preview. Save draft edits first.
A recorded preview does not authorize this task. Runs after library scans being
Off does not disable triggers configured separately in Jellyfin Scheduled Tasks.

Switching destinations preserves draft edits. Save before running a preview.
New installations enable genres, parental rating, and audio languages for movies and series. Other sources, episodes, and generic videos start off. Existing saved selections are preserved.

The main labels distinguish differences from saved changes:

| Label | Meaning |
| --- | --- |
| Tag differences | Tags to add or remove, plus outdated tags listed for review that will be kept. |
| Items with tag differences | Items where a run found differences before any updates. This does not mean tags were saved. |
| Items updated | Item tag updates the run reports as saved. One update can change several tags. An unconfirmed run can have further updates that were not counted. |
| Tags to add / Tags to remove | Proposed tag changes. A confirmed item update uses Tags added / Tags removed. |
| Outdated tags to keep | Tags no longer produced by the metadata and settings used for the result. Applying changes keeps them. |
| Plugin tags | Tags recorded for an item after the plugin added them or recognized matching tags already there. A matching prefix alone does not allow removal. |
| Not checked | This preview has no recorded result for the item. It may not have reached the item, or its details may not have been retained. |
| No tag differences | That recorded check found no differences. It does not describe the item's current state. |
| Outdated results | Preview settings differ from the current saved settings or unsaved edits. |

Checkbox instructions use **select** and **clear**. Saved setting states use
**On** and **Off**. **Disabled** means a control cannot currently be used.

An item preview can be confirmed and applied once, within 15 minutes. A server
restart or changes to the item's metadata, tags, locks, plugin tag records, or
settings require a new preview. Apply rechecks the item before saving. If an
update cannot be confirmed, check the item and preview again before retrying.

Dashboard HTTP smoke checks run only against the designated generated server:

```sh
python3 scripts/dashboard-smoke.py TEST_DIRECTORY
```

The script checks authorization, browsing, tag examples, item preview/apply,
task lifecycle, tag removal, and persistence. It restores the generated movie's
metadata, tags, plugin tag records, and saved configuration. Run history remains
in the disposable test environment.

## Plugin settings

- **Turn on Meta Tagger** allows tag generation and previews. Clear it and
  save to stop tagging. Removing plugin tags remains available.
- **Metadata sources** selects genres, parental rating, existing tags as keywords,
  studios, production countries, metadata providers, production year, audio
  languages, and subtitle languages. **More sources** contains all choices except
  genres, parental rating, and audio languages. These three sources start on; all other sources start off. The source summary names every enabled source,
  including choices inside the closed disclosure. Keyword limits and exclusions
  have a nested disclosure; closing it preserves the values.
  Parental rating uses Custom rating when set, otherwise Jellyfin's Parental rating.
- **Audio languages** starts on; **Subtitle languages** starts off. They copy recorded
  track codes, such as `eng` into `meta:audio-language:eng` and `spa` into
  `meta:subtitle-language:spa`. They normalize and deduplicate values without
  translating language codes. Blank values and `und` are skipped. Recorded tracks
  can include commentary and forced subtitles. Jellyfin's metadata language
  preference does not describe these tracks and is not used.
- **Tag format** sets the generated prefix, manual prefix, and separator. Defaults
  are `meta`, `manual`, and `:`, producing tags such as `meta:genre:animation`.
- **Item types** starts with movies and series selected. Episodes and other videos start off. Episodes can
  include their series' metadata as well as their own; this option starts off. Track languages always
  come from the item itself, including for episodes.
- **Items to check per run** defaults to **New or changed items**, with **All selected
  item types** available optionally. Both respect Jellyfin locks, skip tags, and run limits.
- **Preview scheduled and post-scan runs** starts on and prevents those runs from saving tags. Clear it
  to apply changes during Generate metadata tags, Check all items for metadata
  tag changes, and automatic runs. The explicit Preview and Apply tasks always
  perform the action in their names.
- **Run after a library scan** starts off. Enable it to run tagging after scans.
  **Minimum time between post-scan runs** skips runs that would be too close together.
- **Outdated tags** controls tags the current metadata and settings no longer
  produce: **Keep** by default, **Keep and show in results**, or **Remove when applying changes**.
- **Run limits** caps items checked, items updated, and elapsed time. A pause
  after each update can reduce server load. `0` means no limit or no pause.
- **Hide item names and tag values in routine logs** uses item IDs and counts instead.

See the [settings guide](docs/meta-tagger-plugin.md) for examples and maintenance options.

## Tag preservation

The plugin adds tags derived from metadata. It only removes tags recorded for
that item, according to the Outdated tags setting or a confirmed Remove plugin
tags action. Tags with the manual prefix and separator are always kept. It does not generate
interpretations such as `audience:kids`, `tone:intense`, or `risk:high`.

Existing tags that match the current generated output can be recorded as plugin
tags without changing the tags themselves. **Include tags from an earlier
installation** also records other tags with the current generated prefix and
separator, including outdated or hand-entered matching tags. They can then be
removed according to the Outdated tags setting. This option applies to library
tagging runs, not single-item previews or Remove plugin tags.

## Maintenance and tag removal

1. Clear **Turn on Meta Tagger** and save if you want to prevent tags from
   being recreated by later runs.
2. Open **Maintenance**, then choose **All libraries** or find **One item**.
3. Select **Preview tag removal**. This lists tags that can be removed without
   changing them.
4. Review the list, select the confirmation checkbox, then select **Remove listed tags**.

This action can include previously tagged item types that are no longer selected
in Settings. It keeps manual tags and tags absent from the plugin's records.
Jellyfin locks and skip tags also prevent removal. If an item, update, or time
limit stops the run, preview again and confirm the remaining removals.

For one item, pause tagging as above, remove its plugin tags, then add
`manual:tagger:skip` in Jellyfin before turning tagging back on. Use your configured
manual prefix and separator if different. Remove any existing lock or skip tag
before attempting tag removal.

Manual control tags with the default format:

- `manual:tagger:skip` skips the item without changing its tags or plugin records.
- `manual:tagger:lock` is the older name for the same skip action. It does not
  lock metadata in Jellyfin.
- `manual:tagger:force` rechecks an unchanged item. Locks and skip tags still take priority.

Jellyfin's **Lock this item to prevent future changes** and a lock on the **Tags**
field both protect an item from plugin changes.

## Resource controls

For large libraries:

- New or changed items skips items whose metadata, tags, and settings match the plugin records.
- Jellyfin item and Tags-field locks skip tag processing.
- Item types limits tagging to the selected media types.
- Selecting fewer metadata sources reduces tag generation work.
- Track languages require one media-stream lookup per item when either language
  source is On. A failed lookup fails that item without removing its tags or
  ownership records; library runs continue with other items. With both sources
  Off, existing fingerprints and pending run identities remain compatible.
- Keyword limits and excluded prefixes limit how many existing tags are copied.
- Item, update, and time limits bound each run. Automatic run intervals and update
  pauses reduce how often work happens.
- Runs process items sequentially and honor Jellyfin cancellation tokens.
- Preview shows tag differences without changing tags.
- Preview runs show item-by-item additions and removals on the plugin dashboard
  and can export `last-preview-changes.json` in the plugin data folder.
  `last-run-summary.json` stores the last saved summary; Run history includes
  attempts that failed or stopped before saving that file.
- Full scans and record rebuilds remove records for items no longer seen
  when the run completes without hitting a run limit.
- Plugin tag records are saved through a temporary file and keep `meta-tagger-state.json.bak` for
  backup recovery.

Start with **Preview scheduled and post-scan runs** selected, outdated tags set to **Keep**
or **Keep and show in results**, and **New or changed items**. If library scans
happen often, set a minimum time between automatic runs before selecting
**Run after a library scan**.

## Data handling and logs

Meta Tagger reads the selected Jellyfin item's ID, name, path, type, tags,
genres, custom or official parental rating, studios, production countries,
provider names and IDs, production year, locks, recorded audio and subtitle
languages, and optional parent-series metadata. Provider IDs help detect
metadata changes, but generated provider tags contain the provider name only.

The plugin writes only the item's Jellyfin Tags field. It also stores its XML
configuration and JSON files for tag ownership, previews, summaries, and run
history in Jellyfin's plugin data directories. Those JSON records can contain
item IDs, names, paths, and tag values. Treat the Jellyfin configuration and
backup directories as private data.

Routine logs always include operational counts and may include item IDs. When
**Hide item names and tag values in routine logs** is off, logs can also contain
item names and previewed tag values. Exceptions raised by Jellyfin or another
component may contain more context than the plugin's routine messages. Review
and redact logs before sharing them.

Uninstalling the plugin never edits media tags. Run the confirmed removal
workflow before uninstalling if you want recorded plugin tags removed. Settings
and plugin data persist across restart, disable, enable, and upgrade. Retention
after uninstall depends on Jellyfin and is not part of the plugin's contract. A
reinstall remains preview-first.

## Support boundaries

The dashboard supports Jellyfin's built-in Light and Dark themes and responsive
layouts. Third-party themes and server Custom CSS are best-effort only; report a
problem after reproducing it with a built-in theme. Unsupported Jellyfin server
versions, modified server builds, and manual packages that do not match a
published checksum fall outside the support boundary.

## Project layout

```text
Jellyfin.Plugin.MetaTagger/        Plugin source
Jellyfin.Plugin.MetaTagger.Tests/  xUnit tests
docs/meta-tagger-plugin.md         User and settings guide
scripts/                               Verification, packaging, and smoke tools
.github/                               Issues, CI, dependency, and release automation
```

## Releases

The project uses Semantic Versioning. Plugin releases use `major.minor.patch`;
Jellyfin manifest and assembly versions use the matching `major.minor.patch.0`.
Protected version tags run the full verification suite and publish the plugin
ZIP, installable manifest, and SHA-256 hashes to GitHub Releases.

See [CHANGELOG.md](CHANGELOG.md) for release notes.

## Contributing and security

Logo files and update instructions are in the
[artwork guide](docs/brand/README.md).

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Report
suspected vulnerabilities through the private process in
[SECURITY.md](SECURITY.md), not through a public issue.

Jellyfin Meta Tagger is available under the [MIT License](LICENSE).
