# Jellyfin Meta Tagger plugin

## Scope

`Jellyfin.Plugin.MetaTagger` is a Jellyfin server plugin for Jellyfin
`12.0.0`. It reads metadata from Jellyfin `BaseItem` objects through server
services and adds generated tags such as `meta:genre:kids` and
`meta:rating:tv-y`.

Tag format is controlled by generated prefix, manual prefix, and separator
settings. Defaults are generated prefix `meta`, manual prefix `manual`, and
separator `:`, enter `meta` without the separator in Generated tag prefix.

Prefix and separator values are normalized before use. Empty values, whitespace
or control characters, excessive length, and equal generated/manual namespaces
fall back to safe effective defaults. The dashboard shows effective tag
examples before saving.

The plugin does not generate interpreted policy tags such as `audience:kids`,
`tone:intense`, or `risk:high`.

## Quick start

1. Leave **Preview scheduled and post-scan runs** selected.
2. Choose the Jellyfin details and item types you want to tag.
3. Save the settings.
4. Select **Preview tag changes** in Meta Tagger. It always previews the saved item types
   across all libraries without changing tags and opens **Preview results** when results arrive.
5. Use a preview item's **Preview this item** action, preview its current changes, confirm
   them, and apply to that item.
   An approval lasts 15 minutes and requires a new preview if relevant data
   changes. Library-wide **Apply metadata tag changes** remains available in
   Jellyfin **Scheduled Tasks**.

**Overview** shows the latest run, saved tagging settings, scheduled run action,
and post-scan settings. **Preview results** shows the latest generation
preview, using its retained history when a later Apply or cleanup replaced the
latest-summary preview. It identifies outdated settings and incomplete or
unavailable details. **Browse items** opens the browser; its visible **Filter**
dropdown filters preview status on the loaded page. **History**
is secondary to Overview and Preview results and retains recent attempts and their
available details. Selecting a run marks it and focuses its details heading below
the list. Recorded results never grant approval to apply tags.

**Settings** keeps all configuration controls. The
**Try these settings** aside uses the current draft while Settings is visible,
without saving settings or changing tags. It sits beside the form on wide screens
and below it on narrow screens. Leaving Settings stops searches and calculations
while retaining the query and selected item. Returning uses the latest draft.
See the [dashboard guide](../README.md#dashboard) for scope, approval, and
history limits.

## Settings guide

Genres, parental rating, and audio languages are shown first and enabled by
default. The other six sources start off inside the collapsed **More sources**
section. The source summary names every enabled choice even while that section
is closed. Existing saved selections are preserved.

New installations select movies and series, use **New or changed items**, keep
preview-only enabled, and keep outdated tags. Episodes, generic videos,
parent-series inheritance, and automatic post-scan runs start off.
Tag format and item types are separate disclosures with draft summaries.
The item-type summary says whether episodes include series metadata. Native
validation opens hidden editors so their values can be corrected.
**Edit settings** on Overview opens Settings. Saved automation remains visible
on Overview; a draft does not change those saved values.

For library tagging, Preview results' **Apply changes across all libraries** guidance
names **Apply metadata tag changes** and links to Jellyfin's Scheduled Tasks.
The link does not save or launch anything. The task uses current saved settings
and metadata across all libraries, with selected item types, protections, and
run limits. Save the draft first. Default Preview does not prevent this explicit
Apply task from writing tags. Turning off post-scan runs does not remove any
independently configured Scheduled Tasks triggers.

**Maintenance** shows **Remove plugin tags** controls directly. The inspector's
**Preview tag removal** action opens Maintenance and selects the correct target.
Leaving Maintenance clears its confirmation; a fresh removal preview is
required before confirming again. Advanced limits, log privacy, temporary
maintenance options, all outdated-tag choices, and ownership-recovery controls
remain in Settings.

### Tag format

- **Generated tag prefix** is the first part of a generated tag. Enter
  `meta` without the separator.
- **Separator between tag parts** joins the prefix, source, and value. The
  default `:` produces `meta:genre:animation`.
- **Manual tag prefix** and the separator identify tags to keep and exclude
  from keyword sources. Enter `manual` without the separator. Manual control tags use this prefix too.

The generated and manual prefixes must be different. Prefixes cannot contain
spaces or control characters.

Manual control tags use the chosen manual prefix and separator. With the
defaults, add one of these tags to an item by hand:

- `manual:tagger:skip` skips the item. The plugin does not change its tags or
  plugin tag record.
- `manual:tagger:lock` is an older name for `manual:tagger:skip`. It remains
  supported for existing libraries but is not a Jellyfin metadata lock.
- `manual:tagger:force` checks the item during a scan of new or changed items even when it
  looks unchanged. Remove this tag when the extra check is no longer needed.

Use Jellyfin's **Lock this item to prevent future changes** option for a full
metadata lock, or lock the **Tags** metadata field when only tags need
protection. The plugin skips an item protected by either lock without changing its tags or plugin tag records.

### Metadata sources

The default sources are **Genres**, **Parental rating**, and **Audio languages**.
Studios, production countries, production year, subtitle languages, existing
tags as keywords, and metadata providers are optional and start off.

- **Genres**, **parental rating**, **studios**, **production countries**, and
  **production year** copy those Jellyfin values into normalized tags.
- **Parental rating** uses Jellyfin's **Custom rating** when it is set and
  otherwise uses **Parental rating** (`OfficialRating` in the server API).
- **Audio languages** copies recorded audio track codes, including commentary.
  **Subtitle languages** copies recorded subtitle track codes, including forced
  subtitles. For example, `eng` becomes `meta:audio-language:eng`.
  Blank and undetermined (`und`) codes are skipped. The plugin does not infer
  the original language or use Jellyfin's metadata language preference.
- **Metadata providers** creates a tag for each provider with an ID, such as
  `meta:provider:tmdb`. It does not copy the provider ID itself.
- **Existing tags as keywords** copies existing source tags into
  `meta:keyword:*` tags. Tags already recorded by the plugin and tags with the generated or manual prefix and separator are excluded.
- **Maximum keyword tags per item** limits how many existing tags are copied. `0`
  means no limit.
- **Skip existing tags that start with** ignores keyword sources that begin with any listed
  prefix. Separate entries with commas or semicolons.

### Item types

Movies and series start selected; episodes and generic videos start off.
**Include series metadata in episode tags** also starts off. When enabled, it
adds the parent series' metadata alongside the episode's own metadata. Track
languages always come from the item itself.

### Automation

The defaults are **New or changed items**, preview-only on, and post-scan runs
off. The minimum interval between post-scan runs is 30 minutes.

- **Items to check per run** offers **New or changed items** to skip items whose
  metadata, tags, and settings match the plugin records, or **All selected item
  types** to recheck them. Both respect Jellyfin locks, skip tags, and run limits.
  Generate, Preview, Apply, and runs after library scans use this choice unless
  a temporary maintenance action overrides it.
- **Preview scheduled and post-scan runs** keeps tags unchanged during Generate metadata tags,
  Check all items for metadata tag changes, and runs after library scans. Clear
  the checkbox to let these runs apply changes. Preview metadata tag changes
  always previews; Apply metadata tag changes always applies allowed changes.
- **Run after a library scan** starts the configured default
  run after a scan. **Minimum time between post-scan runs** skips a run if the previous post-scan
  run was too recent.

### Outdated tags

Outdated tags are plugin tags that the current metadata and settings no longer
generate. A genre tag can become outdated when you edit the genre in Jellyfin.
**Keep** is the default; removing outdated tags requires an explicit selection.

- **Keep** leaves these tags alone.
- **Keep and show in results** also lists them under **Outdated tags to keep**.
  They contribute to **Items with tag differences**, but Apply keeps them.
- **Remove when applying changes** lists them under **Tags to remove** and
  allows their removal during Apply. Preview never changes tags.

**Include tags from an earlier installation** records tags with the current
generated prefix and separator that the plugin has no records for, including
outdated and hand-entered matching tags. They can then be removed according to
the Outdated tags setting. This applies only to library tagging runs. Tags that
match the current generated output can become plugin tags without this option.

### Remove plugin tags

Plugin tags are recorded for an item after the plugin adds them or recognizes
existing matching tags. A matching prefix alone does not allow removal.

Choose all libraries or find one item, then select **Preview tag removal**.
It lists tags that can be removed without changing tags. Review the list,
select the confirmation checkbox, then select **Remove listed tags**.
Changed settings require saving and a new preview before removal.

This action creates no replacement tags. It works while Meta Tagger is off
and includes previously tracked item types that are no longer selected. It keeps
tags with the manual prefix and separator and tags absent from the plugin's records. Jellyfin
locks and skip tags also prevent removal.

To prevent tags from returning, clear **Turn on Meta Tagger** and save before
removing tags. For one item, remove its plugin tags, add the configured manual
skip tag in Jellyfin, then turn tagging back on and save. The default skip tag is
`manual:tagger:skip`. Remove any existing lock or skip tag before tag removal.

Run limits also apply to removal. If a run stops at a limit, select **Review
removal** again and confirm the remaining removals.

Editing settings, changing the selected item, or leaving Maintenance clears the
affected removal preview and confirmation. Leaving the dashboard ends the
preview session; requests already sent to Jellyfin can still finish. Edits made
while saving remain unsaved until you save again.

### Advanced settings

**Maximum items checked per run**, **Maximum items updated per run**, and
**Maximum minutes per run** stop a run at the chosen limit. `0` means no limit.
One item update can change several tags. **Pause after each item update** reduces
server load. **Hide item names and tag values in routine logs** uses item IDs
and counts instead.

Temporary maintenance checkboxes apply to Generate metadata tags and runs after
library scans. They do not affect the separate Preview metadata tag changes,
Apply metadata tag changes, Check all items for metadata tag changes, or Rebuild
plugin tag records tasks. A checkbox may apply to several runs; it clears only
after Generate metadata tags finishes without errors or reaching a run limit.

- **Recheck all selected item types** also checks unchanged items.
- **Rebuild plugin tag records** updates the records used to identify removable
  tags and skip unchanged items. It does not change tags in Jellyfin.
- **Include tags from an earlier installation temporarily** has the same effect
  as the option under Outdated tags, but clears after a successful Generate
  metadata tags run.

## Tag preservation

The plugin keeps manual tags and tags it has not recorded for the item. It can
remove recorded tags even after the generated prefix changes. It keeps other
tags with an old prefix. Include tags from an earlier installation can record
matching tags only under the current generated prefix and separator.

`PreviewOnly` defaults to `true`. Generate metadata tags, Check all items for
metadata tag changes, and runs after library scans honor it. In previews, item
tags remain unchanged, but plugin records and run history may be updated.

Runs after library scans are off by default. When selected,
`MinimumMinutesBetweenAutoRuns` skips an automatic run if the previous one was
too recent; it does not queue that run for later.

## Resource controls

- `MaxItemsPerRun`, `MaxWritesPerRun`, and `MaxRunMinutes` stop long runs before
  they can monopolize the server.
- `WriteDelayMilliseconds` adds a delay after each Jellyfin tag write.
- `MaxKeywordTagsPerItem` and `ExcludedKeywordPrefixes` limit how many existing
  tags are copied into keyword tags.
- `QuietLogging` omits item names and previewed tag names from routine item
  change logs.
- Preview runs show an item-by-item change list on the plugin dashboard and
  write `last-preview-changes.json` in the plugin data folder. The dashboard
  distinguishes loading, empty, outdated, incomplete, unavailable, and ready
  generation results. A later Apply or cleanup can use the retained generation
  history fallback; the export itself is unchanged.
- `last-run-summary.json` stores the last saved summary, including counts,
  run limits, estimated updates, and the preview export location. Run history
  also includes attempts that failed or stopped before saving that file.
- The plugin saves tag records through a temporary file and keeps
  `meta-tagger-state.json.bak` as a backup if the main records file cannot
  be read.
- Completed full scans and record rebuilds remove records for items no longer
  returned by Jellyfin. Runs stopped by a limit keep those records.

## Verification

```bash
./scripts/build-and-test.sh
```

Real-server verification uses a disposable OrbStack container and generated
movie, series, and anime fixtures. Never point the verification scripts at a
live Jellyfin server.
