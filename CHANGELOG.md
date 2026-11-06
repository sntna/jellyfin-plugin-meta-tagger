# Changelog

All notable changes to this project are documented here. The project follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-10-27

### Added

- add manual release preparation automation (#9)
- automate local implementation ticket pickup (#13)
- enforce existing-library baseline and explicit backfill (#30)
- complete unattended ticket review and merge workflow
- enable safe fresh automatic tagging defaults (#33)
- support direct Apply and optional checking (#34)
- explain metadata source results and recorded audio languages (#35)
- retain immutable run details (#25) (#36)
- build the compact tagging workspace (#37)
- complete on-demand inspection, history, and maintenance (#38)
- build the four-tab dashboard with the Activity tab (#51)
- regroup settings and maintenance into cards and a Maintenance area (#52)

### Changed

- Fresh installations now apply tags automatically and run after library
  scans, instead of starting in Preview with post-scan runs off. Upgrades and
  reinstalls with retained configuration keep their saved mode and triggers.
  A missing configuration with signs of an earlier installation still starts
  in Preview.
- Before its first automatic write, the plugin records the items already in
  the library. Those items receive tags only after an explicit Apply; items
  added later are tagged automatically.
- The dashboard is organized into four tabs: Activity, Items, Settings, and
  Maintenance. Tab switches keep draft edits, item selection, search, and
  pagination, and opening the page lands on Activity.
- Activity shows the run controls, the latest run, proposed tag changes, and
  an Earlier activity feed with All, Changed tags, and Problems filters that
  replaces Recent runs and the History view.
- Settings are grouped into five cards: Sources & item types, Automation, Tag
  format, Outdated tags, and Limits & privacy. Try unsaved settings stays
  visible beside them.
- A save bar appears on every tab while settings have unsaved edits, with
  Discard, Save & apply tags now, and Save settings. Saving is still explicit.
- Maintenance holds the next-run actions, tag removal, and a single
  Off / Next scheduled run / Every library run choice for tags from an
  earlier installation, which replaces the two separate checkboxes without
  changing the stored settings.

### Removed

- Back buttons, Escape-to-return, the Saved tag results panel, the Advanced
  behavior section, and the Tracking and legacy tag settings shortcut.

### Fixed

- Automatic runs restore tags for an existing library item the plugin already
  manages after something outside the plugin removes them, such as a Jellyfin
  **Replace all metadata** refresh. Items the plugin has never tagged still
  require an explicit Apply. A check or a **Rebuild plugin tag records** run that
  changes no tags no longer forgets the tags the plugin recorded for an item, so
  neither forfeits that restoration.
- implement scheduled tickets in the Codex app (#28)
- preserve established installations during migration (#29)
- preserve exact bytes in review packet comparisons (#41)
- name current dashboard controls in guidance messages (#53)
- keep used one-time actions off, protect an unreadable ledger, and resume cycles after item approval (#54)
- keep dashboard checks responsive during library runs (#56)
- let release preparation skip older GitHub merge commits (#58)
- keep maintenance edits made while a save is in progress (#60)
- let an unconfirmed installation be recovered (#61)
- keep expired run history cleanup from failing a publish (#62)
- restore managed tags after an external metadata refresh (#63)

## [0.1.0] - 2026-09-19

### Added

- Metadata-derived tags for genres, ratings, keywords, studios, countries,
  providers, years, audio languages, and subtitle languages.
- Initial defaults with genres, parental rating, and audio languages enabled
  for movies and series. Other sources start off under collapsed More sources;
  episodes, generic videos, series inheritance, and post-scan runs start off.
- Manual GitHub releases and a local release command that verify the package,
  create the version tag, and generate release assets with SHA256 checksums.
- Preview-first library runs, item-level preview and apply, and explicit
  scheduled apply tasks.
- Exact per-item plugin tag records so cleanup preserves manual and unmanaged
  tags.
- Dashboard workflows for overview, review, item inspection, run history,
  settings, and a dedicated Maintenance destination for confirmed tag removal.
- Automatic post-scan runs, incremental checks, resource budgets, cancellation,
  quiet logging, and recovery tools.
- Release-ZIP lifecycle coverage for install, upgrade, disable, enable,
  configuration retention, uninstall, and preview-first reinstall behavior.
- Support for Jellyfin Server 12.0.0, plugin ABI 12.0.0.0, and .NET 10.

### Security

- Elevated Jellyfin authorization for dashboard endpoints.
- Expiring single-use approvals for item updates and cleanup.
- Atomic state writes with backup recovery and preview-safe failure behavior.

[Unreleased]: https://github.com/sntna/jellyfin-plugin-meta-tagger/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/sntna/jellyfin-plugin-meta-tagger/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/tag/v0.1.0
