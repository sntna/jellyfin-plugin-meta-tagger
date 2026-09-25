# Jellyfin Meta Tagger

[![Verify](https://github.com/sntna/jellyfin-plugin-meta-tagger/actions/workflows/verify.yml/badge.svg)](https://github.com/sntna/jellyfin-plugin-meta-tagger/actions/workflows/verify.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Create tags from the metadata already in your Jellyfin library. A Science Fiction
genre becomes `meta:genre:science-fiction`; an English audio track becomes
`meta:audio-language:eng`. You can use these tags in Jellyfin user access rules.
The plugin does not decide who should see an item.

New installations preview changes without saving tags. Genres, parental rating,
and audio languages are enabled for movies and series. You can also tag studios,
countries, years, subtitle languages, metadata providers, and existing keywords.

## Requirements

Jellyfin Server **12.0.0** and an administrator account. Other server versions
are not currently supported.

## Installation

1. Open **Dashboard → Plugins → Repositories** in Jellyfin.
2. Add a repository named **Meta Tagger** with this URL:

   ```text
   https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/latest/download/manifest.json
   ```

3. Install **Meta Tagger** from the plugin catalog and restart Jellyfin when prompted.

For manual installation, download the versioned ZIP from
[GitHub Releases](https://github.com/sntna/jellyfin-plugin-meta-tagger/releases),
extract it into its own directory under Jellyfin's plugin directory, and restart
Jellyfin. Do not copy the ZIP itself into the plugin directory.

## Get started

1. Open Meta Tagger in Jellyfin's plugin settings.
2. Choose your metadata sources and item types, then save. Leave
   **Preview scheduled and post-scan runs** selected while trying the plugin.
3. Select **Preview tag changes** to review differences without saving tags.
4. Choose an item, select **Preview this item**, review its current changes,
   then confirm and apply them.

To apply across your libraries, use **Apply metadata tag changes** in Jellyfin's
**Scheduled Tasks**. This task writes tags using your saved settings even when
scheduled and post-scan runs default to Preview. Save any draft settings first.

Automatic runs after library scans start off. Once you're happy with previews,
you can configure automation and run limits in Settings.

See the [settings guide](docs/meta-tagger-plugin.md) for all options, or the
[dashboard guide](docs/dashboard.md) for previews, item browsing, and history.

## Your tags stay under your control

- Previews do not change item tags. Outdated tags are kept by default.
- The plugin only removes tags recorded as belonging to it for that item.
  Tags with the manual prefix and separator are always kept.
- Jellyfin item locks, Tags-field locks, and `manual:tagger:skip` protect items
  from plugin changes.
- Generated tags describe metadata. They do not infer audience, tone, or risk.

Existing tags matching the current generated output can become recorded plugin
tags. **Include tags from an earlier installation** can also record other tags
with the generated prefix, including hand-entered ones. Review the
[tag preservation guide](docs/meta-tagger-plugin.md#tag-preservation) before enabling it.

To remove plugin tags, turn off **Turn on Meta Tagger** and save, then open
**Maintenance**, preview the removal, and confirm it. Uninstalling the plugin
leaves media tags in place. See the [removal guide](docs/meta-tagger-plugin.md#remove-plugin-tags).

## Help and updates

- [Settings and maintenance](docs/meta-tagger-plugin.md)
- [Data handling and logs](docs/meta-tagger-plugin.md#data-handling-and-logs)
- [Release notes](CHANGELOG.md)
- [Report a bug](https://github.com/sntna/jellyfin-plugin-meta-tagger/issues/new?template=bug.yml)

For display problems, reproduce the issue with Jellyfin's built-in Light or Dark
theme before reporting it. Review and redact logs before sharing them. Report
suspected vulnerabilities privately using [SECURITY.md](SECURITY.md).

Development and release instructions are in [docs/](docs/README.md).
Jellyfin Meta Tagger is available under the [MIT License](LICENSE).
