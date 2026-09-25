# Dashboard guide

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

See the [settings guide](meta-tagger-plugin.md) for configuration and tag removal.
