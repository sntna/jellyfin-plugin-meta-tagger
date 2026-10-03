# Meta Tagger

Meta Tagger generates tags from selected Jellyfin metadata and records which tags it owns for cleanup.

## Language

**Installation identity**:
A durable identity for one Meta Tagger setup, retained across plugin upgrades and restarts.
_Avoid_: Run identity, ledger identity

**Established installation**:
A setup with a readable saved tagging policy from before installation migration.
_Avoid_: Nonempty ledger

**Uncertain installation**:
A setup whose previous tagging policy cannot be established from its surviving records.
_Avoid_: Fresh installation

**Prior generation eligibility**:
The item types and generation operations an established installation permitted before migration. It grants no cleanup ownership.
_Avoid_: Recorded tag ownership, processing progress

**Recorded tag ownership**:
The tags Meta Tagger recorded for an individual item and can consider for cleanup.
_Avoid_: Generation eligibility, baseline membership

**Existing library baseline**:
The persisted item IDs captured across all supported item types at the first enabled generation run, including Preview. Baseline membership requires explicit or migrated eligibility for automatic writes.
_Avoid_: Ownership ledger, processed items

**Backfill authorization**:
A durable grant for selected item types across all libraries, or one validated item. Explicit bulk Apply and Apply task invocations grant their selected scope. Single-item Apply grants only its target. Authorization grants no cleanup ownership.
_Avoid_: Completed processing, Preview approval

**Processing coverage**:
The items a run visited and actually processed, its exclusions, remaining work, failures, and confirmed media writes. Authorization can cover more items than a cancelled or limited run processed.
_Avoid_: Authorized scope
