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
