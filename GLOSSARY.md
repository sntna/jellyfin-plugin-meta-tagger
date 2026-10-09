# Jellyfin Meta Tagger

Meta Tagger turns existing Jellyfin metadata into normalized tags. This glossary defines the plugin's domain language; tags describe recorded metadata, not inferred audience, risk, or tone.

## Language

### Items and metadata

**Item**:
A Jellyfin movie, series, episode, or generic video that can be checked for tags. Selected item types determine which kinds participate in library tagging.

**Metadata source**:
A category of existing Jellyfin metadata used to generate tags, such as genres, parental rating, or audio languages. A source supplies recorded values rather than inferred classifications.

**Saved settings**:
The persisted choices governing metadata sources, tag format, item types, automation, and tag preservation. Dashboard references to "rules" mean these choices.
_Avoid_: Policy engine, inferred rules.

**Tagging scope**:
The items targeted by a tagging action, either one selected item or the configured item types across all libraries. A library filter used for browsing does not narrow library-wide Apply.

### Tags and ownership

**Generated tag**:
A normalized tag calculated from enabled metadata sources and saved settings, such as `meta:genre:animation`. Generated output is a proposal, not evidence that the tag was saved or is plugin-owned.

**Generated namespace**:
The configured generated prefix followed by the separator, such as `meta:`. A matching namespace alone does not establish ownership.

**Recorded plugin tag**:
A tag recorded as owned by the plugin for a particular item, either after adding it or recognizing a matching tag already present. Ownership allows removal only when the action and item protections permit it.
_Avoid_: Using "generated tag" to mean ownership.

**Plugin tag records**:
The plugin's per-item records of owned tags, generated output, and information used to recognize unchanged items. "Tracking ledger" and "ownership ledger" refer to these records; a recorded tag is not proof that the plugin originally wrote it.

**Unmanaged tag**:
An existing item tag absent from that item's plugin ownership records. It remains protected from removal unless the plugin first recognizes or explicitly claims it as a recorded plugin tag.

**Manual tag**:
A tag using the configured manual prefix and separator, such as `manual:`. Manual tags are preserved and excluded from keyword sources; a hand-entered tag outside that namespace is not automatically a manual tag in this sense.

**Keyword source tag**:
An existing item tag eligible to supply a generated keyword tag. Recorded plugin tags and tags in the generated or manual namespace are excluded.

**Outdated tag**:
A recorded plugin tag no longer produced by the current metadata and saved settings. "Stale tag" means the same thing; the default is to keep it.

**Legacy tag claim**:
Explicit recognition of previously unrecorded tags in the current generated namespace through "Include tags from an earlier installation." This can include hand-entered matching tags; existing tags matching current generated output can be recognized without this option.

### Runs and results

**Generation run**:
A check of items against metadata and saved settings to calculate tags, with permitted changes written when the run uses Apply. "Generate" alone does not identify whether a run previews or writes.

**Preview**:
A check that calculates differences without changing Jellyfin item tags. It may update plugin records and history, including recognizing already-present matching tags, but does not authorize backfill.
_Avoid_: Side-effect-free run.

**Apply**:
A run that recalculates current metadata and saved settings and writes permitted tag changes. Explicit Apply also authorizes existing items within its validated scope; it does not replay a prior preview.

**Incremental run**:
A run that skips unchanged items when their metadata, tags, and settings indicate no work. "New or changed items" is the dashboard name; the run may still enumerate the full selected scope.

**Full scan**:
A tagging run that rechecks selected items even when unchanged. It still respects item protections, backfill authorization, and run limits.

**Post-scan run**:
A tagging run triggered after a Jellyfin library scan, subject to the saved automation settings and cooldown. It is separate from scheduled task triggers.

**Rebuild plugin tag records**:
A maintenance run that refreshes plugin records without changing Jellyfin tags. It can recognize existing matching tags but does not authorize backfill.

**Plugin tag removal**:
A maintenance action that removes approved recorded plugin tags without generating replacements. Also called "cleanup," it requires a separate removal preview and confirmation and respects manual tags, locks, and skip controls.

**Tag differences**:
Proposed additions or removals, plus outdated tags listed for review that will be kept. Differences describe a recorded check, not confirmed changes to an item's current tags.

**Items updated**:
Items whose tag updates a run reports as saved, with several tag changes possible in one update. This count does not establish that every attempted write or ownership update was confirmed.

### Installation and authorization

**Fresh installation**:
An installation positively established as having no prior configuration or installation evidence. Only a proven fresh installation receives automatic Apply and post-scan defaults; later saved choices remain authoritative.

**Library baseline**:
The persisted set of existing library items captured before automatic writes begin. It establishes which items need explicit authorization for backfill, independently of tag ownership.

**Backfill**:
Tagging existing baseline items after explicit Apply authorizes them. Automatic runs and previews do not grant that authorization.

**Backfill authorization**:
Persisted permission granted by explicit Apply for one item or the configured item types across all libraries. Authorization does not mean every permitted item has been checked or updated.

### Item protections

**Manual skip control**:
A manual control tag that prevents changes to an item's tags and plugin tag records, normally `manual:tagger:skip`. The older `manual:tagger:lock` is an alias for this control, not a Jellyfin metadata lock.

**Manual force control**:
A manual control tag, normally `manual:tagger:force`, that requests checking an apparently unchanged item during an incremental run. It does not bypass locks, skip controls, authorization, or run limits.

**Jellyfin metadata lock**:
A Jellyfin item lock or Tags field lock that protects the item from plugin changes to tags and plugin tag records. It is distinct from the legacy manual lock control tag.
