# Dashboard guide

Meta Tagger has four tabs: **Activity**, **Items**, **Settings**, and
**Maintenance**. Activity opens first. Use the Left and Right arrow keys, Home, or End to move
between tabs. Switching tabs keeps unsaved settings, the item search, the selected
item, pagination, and series breadcrumbs. Leaving a tab discards unfinished read
requests for it; returning retries the selected page or run.

## Activity

The scope card names the configured item types across all libraries and the
saved **Rules in effect**. **Edit rules ›** opens Settings.

- **Apply tags now** recalculates current metadata and saved settings and writes
  permitted changes. It explicitly authorizes existing items in that scope, even
  when automatic runs only preview. Item protections and run limits still apply.
- **Check changes first** writes no tags and never saves a draft.
- **Stop run** requests cancellation.

A browsing library filter in Items never narrows this scope. When the last check
used older settings, **Check again** runs a new one. When Settings has unsaved
edits, **Review settings** opens them; runs always use saved settings.

**Latest run** shows the most recent run, its time, and its counts.
**View run details** opens that run in Earlier activity.

**Proposed tag changes** shows the latest available generation preview with its
date, an **Outdated** chip when settings changed after it, and incomplete or
unavailable details. **Preview this item** opens that item in Items.
**Review all in Items ›** opens Items filtered to items with tag differences.
**Reload preview results** retries a failed or stale result without starting a run.

**Earlier activity** lists recorded runs, newest first. Filter it with **All**,
**Changed tags**, or **Problems**, and use **Refresh activity** to reload it.
Select a run to expand its summary and item results, including confirmed and
unconfirmed writes, failures, and missing details. **Run details** lists its
recorded rules, selected item types, trigger, times, authorization, and processing
coverage. **Skipped: cooldown** means a post-scan trigger checked no items and
changed no tags. It neither extends the cooldown nor queues a later run.

Earlier activity keeps the latest 20 runs, including failures, cancellations,
and incomplete runs. Each run keeps up to 1,000 item details and 5 MiB of detail
data. Missing or shortened results are identified. Recorded runs never authorize
an item update or tag removal.

## Items

**Search items**, **Library**, and the **Filter** dropdown browse items in pages
of 25. The filter applies only to the loaded page. **Browse seasons** opens a
series; breadcrumbs return to its library. Selecting an item does not start a check.

- **Apply tags now to this item** recalculates its tags from current metadata and
  saved settings without a preview or confirmation.
- **Check changes first** optionally shows proposed tag changes. While another
  run is in progress it says so instead of waiting; check again afterward.
- **Stop item run** cancels this request, even while it waits behind another run.
- **Preview tag removal** opens Maintenance with this item selected and previews
  its removal. **‹ Back to Items** returns to the item.

Progress and the final saved-write count appear beside the actions. Save unsaved
settings first.

## Settings

**Turn on Meta Tagger** stops generation when cleared. Existing tags stay and
Maintenance remains available. Settings are grouped into cards, each with a
one-line summary of the current draft: **Sources & item types**, **Automation**,
**Tag format**, **Outdated tags**, and **Limits & privacy**. **Edit** opens one
card at a time and **Close** collapses it. Validation opens every card with an
invalid value and focuses the first one.

**Try unsaved settings** searches item choices and calculates an example for one
item using the current draft, without saving. It runs only while Settings is
visible and keeps the query and selection when you leave. **Reload example**
reads current metadata again.

Changes are saved only when you select **Save settings**. While any setting is
unsaved, a sticky bar stays at the bottom of the page on every tab:

- **Save settings** saves the draft.
- **Save & apply tags now** starts Apply only after a successful save.
- **Discard** restores the saved settings.

Edits made while saving remain a separate unsaved draft. Settings on the
Maintenance tab are saved the same way.

## Maintenance

Maintenance holds rarely needed choices. Manual tags, locks, and skip controls are
always respected.

**Next-run actions** apply to the daily Generate metadata tags task and post-scan
runs, not **Apply tags now** or **Check changes first**. Each clears after a daily
run that finishes without errors or hitting a run limit.

- **Recheck all selected item types** also checks unchanged items.
- **Rebuild plugin tag records** refreshes plugin tag records without changing
  tags in Jellyfin.

**Include tags from an earlier installation** records unrecorded tags that match
the current generated prefix and separator, including outdated and hand-entered
ones, so they can be removed according to the Outdated tags setting:

- **Off** manages only recorded tags and tags matching current generated output.
- **Next scheduled run** records them once, on the next daily or post-scan run.
  It clears after a clean daily run.
- **Every library run** records them on every library tagging run.

**Remove plugin tags** removes only tags recorded as plugin-owned. Choose
**All libraries** or **One item**, select **Preview tag removal**, review the
list, confirm **I reviewed the tags listed for removal.**, then select
**Remove listed tags**. While tagging is on, future runs can add the tags again;
**Turn off Meta Tagger** clears **Turn on Meta Tagger** so you can save before
removal. Editing settings, changing the target, or leaving Maintenance
clears the preview and confirmation. While another run is in progress, preview
and removal change nothing and ask you to preview again after it finishes.

## Labels

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
| Outdated | Preview settings differ from the current saved settings. |

Checkbox instructions use **select** and **clear**. Saved setting states use
**On** and **Off**. **Disabled** means a control cannot currently be used.

## Runs and recovery

Item checks are read-only and expire as evidence whenever metadata or settings change.
Apply always recalculates current data. Item run IDs identify cancellation and status;
an old run cannot cancel a newer one. Reloading the page recovers the most recent
retained item run. After a server restart, use Earlier activity if its live status
is unavailable. If a write or ownership checkpoint cannot be confirmed, inspect the
item before retrying.

Jellyfin's **Apply metadata tag changes** scheduled task remains available.
Recorded checks never authorize writes. Post-scan being Off does not disable
separate Jellyfin task triggers.

New installations enable genres, parental rating, and audio languages for movies
and series. Other sources, episodes, and generic videos start off. Existing saved
selections are preserved.

Fresh installations use automatic Apply and post-scan runs. The existing library
baseline is saved before automatic writes; existing items need explicit Apply
for backfill. Saved settings and task triggers remain unchanged on upgrades.
Find all five visible tasks under **Meta Tagger** in Scheduled Tasks. The default
daily Generate metadata tags task follows automatic mode; post-scan is a separate
hook. Incremental runs can enumerate the full selected scope and skip unchanged
items.

See the [settings guide](meta-tagger-plugin.md) for configuration and tag removal.
