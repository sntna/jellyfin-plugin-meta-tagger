# Security policy

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability.

Use [GitHub private vulnerability reporting](https://github.com/sntna/jellyfin-plugin-meta-tagger/security/advisories/new) to share the affected version, impact, reproduction steps, and any suggested mitigation. Remove access tokens, library names, media metadata, and other personal data from evidence.

The maintainer will acknowledge a complete report, investigate it, and coordinate disclosure after a fix or mitigation is available. Please allow time for Jellyfin compatibility and disposable-server verification before publication.

## Supported versions

Security fixes target the latest published release. Older pre-1.0 releases may require upgrading rather than receiving a backport.

## Security boundaries

Meta Tagger runs inside the Jellyfin server process with administrator-configured access. Its dashboard endpoints require elevated Jellyfin authorization. Preview changes before applying them, keep routine logs private, and use only trusted plugin packages whose checksum matches the published manifest.
