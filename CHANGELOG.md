# Changelog

All notable changes to this project are documented here. The project follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/sntna/jellyfin-plugin-meta-tagger/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/tag/v0.1.0
