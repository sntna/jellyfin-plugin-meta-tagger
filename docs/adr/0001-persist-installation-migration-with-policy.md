# Persist installation migration with the saved policy

For issue [#20](https://github.com/sntna/jellyfin-plugin-meta-tagger/issues/20), store installation identity, migration completion and prior generation eligibility in the saved XML configuration, independently of the ownership ledger. An atomic configuration replacement commits these records with the policy before generation becomes available; a separate migration file could commit permission without its corresponding policy.

A readable legacy configuration establishes prior policy even with an empty ledger. Missing configuration with surviving plugin state, Jellyfin task records or an interrupted configuration write remains uncertain and cannot apply generation changes. Unreadable configuration and incomplete or unsupported migration records stop initialization without overwriting the saved policy. Settings updates retain the server's installation record and become active only after persistence succeeds.

The Jellyfin host requires a published installation before supplying configuration to generation operations. If plugin construction fails, surviving scheduled tasks fail before reading library items or changing tags and ownership records.

Prior eligibility records the selected item types, saved configured Apply, the explicit Apply task, and enabled post-scan Apply. The explicit Apply task remains available on established Preview installations for deliberate schedules and manual task starts. Jellyfin owns task triggers; migration leaves their keys and persisted triggers intact. This change retains the existing preview-first defaults and introduces no existing-library baseline or cleanup ownership.
