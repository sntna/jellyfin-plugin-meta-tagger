# Dashboard guide

Meta Tagger opens in a compact workspace, with Browse items, History, and Maintenance available as tabs.

- **Workspace** puts editable common sources, item types, automatic mode, and post-scan controls together.
  The saved scope appears beside **Apply tags now** and **Check changes first**.
  Apply uses current metadata and saved settings and explicitly authorizes existing items in that scope.
  Checking writes no tags and never saves a draft. **Stop run** requests cancellation.
  **Proposed tag changes** shows the latest available generation preview with its date,
  settings freshness, and incomplete or unavailable details. **Saved tag results** shows
  the current Apply run's retained item outcomes, including confirmed and unconfirmed
  writes, failures, and missing details. Both use Jellyfin artwork with title fallbacks.
  A failed result request offers a retry without starting another run.
  Optional sources, Tag format, Outdated tags, Run limits, and Advanced behavior remain
  in named disclosures. Validation reveals invalid controls before saving.
  **Save settings** explicitly saves the draft. **Save & apply tags now** starts Apply
  only after a successful save. Later edits remain unsaved.
  **Try unsaved settings on an item** searches up to 25 choices and calculates an example
  without saving. Collapsing it or leaving the workspace stops example requests and
  retains the query and selection. Reopening it uses the latest draft.
- **Browse items** opens search, library filtering, pages of 25, and the item
  inspector. The visible **Filter** dropdown filters preview status and applies
  only to the loaded page. A preview item's **Preview this item** action selects that item.
  Selecting an item does not start a check. **Apply tags now to this item** recalculates
  its tags from current metadata and saved settings without a preview or confirmation.
  **Check changes first** optionally shows proposed tag changes. **Stop item run**
  cancels this request even while it waits behind another run. Progress and the final
  saved-write count appear beside the action. Unsaved settings must be saved first.
- **History** shows recorded runs and item results in separate scrolling lists.
  **Skipped: cooldown** means a post-scan trigger checked no items and changed no tags.
  It neither extends the cooldown nor queues a later run.
  Selecting a run keeps keyboard focus and list position on that run.
  Counts appear separately from status guidance. Preview results and history items include thumbnails.
- **Maintenance** shows removal controls directly for tags recorded as plugin-owned. It requires
  a fresh preview and explicit confirmation. Leaving Maintenance invalidates
  that preview and confirmation. The Browse items shortcut opens Maintenance with
  the selected item ready to preview.

**History** is always available as a tab and from the workspace.
It retains the latest 20 attempts, including failures, cancellations, and
incomplete runs. Each run keeps up to 1,000 item details and 5 MiB of detail
data. Missing or shortened results are identified. History never authorizes an
item update or tag removal. Plugin tag removal still requires a separate preview
and confirmation; leaving Maintenance clears that confirmation. In Browse items,
**Preview tag removal** opens the removal destination with the selected item and
requests its removal list. **Remove listed tags** requires separate confirmation.

For library-wide changes, use **Apply tags now** at the top of the page. It uses
current saved settings and metadata for configured item types across all libraries,
subject to protections and run limits. A browsing library filter never narrows this scope.
**Save & apply tags now** saves the submitted settings first; a failed save prevents
Apply. Edits made while saving remain a separate unsaved draft. **Check changes first**
never saves a draft or enables automatic Apply. Jellyfin's **Apply metadata tag changes**
scheduled task remains available. Explicit Apply can write tags even when automatic
runs use Preview. Recorded checks and history never authorize writes. Post-scan being
Off does not disable separate Jellyfin task triggers.

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

Item checks are read-only and expire as evidence whenever metadata or settings change.
Apply always recalculates current data. Item run IDs identify cancellation and status;
an old run cannot cancel a newer one. Reloading the page recovers the most recent
retained item run. After a server restart, use History if its live status is unavailable.
If a write or ownership checkpoint cannot be confirmed, inspect the item before retrying.

See the [settings guide](meta-tagger-plugin.md) for configuration and tag removal.

Fresh installations use automatic Apply and post-scan runs. The existing library
baseline is saved before automatic writes; existing items need explicit Apply
for backfill. Saved settings and task triggers remain unchanged on upgrades.
Find all five visible tasks under **Meta Tagger** in Scheduled Tasks. The default
daily Generate metadata tags task follows automatic mode; post-scan is a separate
hook. Incremental runs can enumerate the full selected scope and skip unchanged
items.
