#!/usr/bin/env python3
"""Download a published release into a local plugin install catalog.

The release lifecycle test upgrades over this catalog so it exercises a
genuinely older plugin instead of the current tree relabelled. Published
assets are untrusted data: every file is verified against the release's own
SHA256SUMS before it is used, nothing from the package is executed, and only
the verified package is served to the disposable Jellyfin container.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request
import zipfile

PLUGIN_GUID = "4851185f-7284-4cad-9eeb-2c73576bb214"
IMAGE_NAME = "meta-tagger.png"
PACKAGE_MEMBERS = ["Jellyfin.Plugin.MetaTagger.dll", IMAGE_NAME]
DEFAULT_REPOSITORY = "https://github.com/sntna/jellyfin-plugin-meta-tagger"
# EX_TEMPFAIL. The caller skips an unreachable optional release instead of failing.
UNAVAILABLE_EXIT_CODE = 75


class ReleaseUnavailable(Exception):
    """The published release does not provide the assets this upgrade needs."""


def _download(download_base: str, name: str, timeout: float) -> bytes:
    url = f"{download_base.rstrip('/')}/{name}"
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:
            return response.read()
    except (urllib.error.URLError, OSError) as error:
        raise ReleaseUnavailable(f"{url}: {error}") from error


def parse_checksums(text: str) -> dict[str, str]:
    checksums: dict[str, str] = {}
    for line in text.splitlines():
        if not line.strip():
            continue
        entry = re.fullmatch(r"([0-9a-f]{64})\s+\*?(\S+)", line.strip())
        if not entry:
            raise ValueError(f"Published SHA256SUMS is not a list of checksums: {line!r}")
        checksums[entry.group(2)] = entry.group(1)
    return checksums


def fetch_released_catalog(
    *,
    tag: str,
    download_base: str,
    output_dir: Path,
    source_url_base: str,
    timeout: float = 60.0,
) -> dict[str, str]:
    """Write a verified install catalog for a published release into output_dir."""
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", tag):
        raise ValueError("Release tag must use the vX.Y.Z form.")
    if not re.fullmatch(r"https?://[^\s/]+(?::[0-9]+)?(?:/[^\s]*)?", source_url_base):
        raise ValueError("Package source base must be an HTTP or HTTPS URL.")
    four_part_version = f"{tag[1:]}.0"
    package_name = f"meta-tagger_{four_part_version}.zip"

    published_checksums = _download(download_base, "SHA256SUMS", timeout)
    checksums = parse_checksums(published_checksums.decode("utf-8"))
    uncovered = [name for name in ("manifest.json", package_name) if name not in checksums]
    if uncovered:
        # An asset the release never vouched for is unusable rather than evidence
        # of tampering: an older release may simply not carry it. The caller skips.
        raise ReleaseUnavailable(f"Published SHA256SUMS does not cover {', '.join(uncovered)}.")

    verified: dict[str, bytes] = {}
    for name in ("manifest.json", package_name):
        content = _download(download_base, name, timeout)
        digest = hashlib.sha256(content).hexdigest()
        if digest != checksums[name]:
            raise ValueError(f"Downloaded {name} does not match its published SHA256SUMS entry.")
        verified[name] = content

    manifest = json.loads(verified["manifest.json"].decode("utf-8"))
    if len(manifest) != 1 or len(manifest[0].get("versions", [])) != 1:
        raise ValueError("A published release catalog must hold one plugin and one version.")
    entry = manifest[0]
    version = entry["versions"][0]
    if entry.get("guid", "").replace("-", "").lower() != PLUGIN_GUID.replace("-", ""):
        raise ValueError("Published catalog is not this plugin's identity.")
    if version.get("version") != four_part_version:
        raise ValueError(f"Published catalog version is not {four_part_version}.")
    if hashlib.md5(verified[package_name]).hexdigest() != version.get("checksum"):
        raise ValueError("Published package does not match the checksum in its own catalog.")
    with zipfile.ZipFile(io.BytesIO(verified[package_name])) as package:
        if package.namelist() != PACKAGE_MEMBERS:
            raise ValueError("Published package does not hold the expected release layout.")
        # Read one named member. Never extract paths chosen by the archive.
        image = package.read(IMAGE_NAME)

    # Serve the verified copy locally so the container installs these exact bytes.
    base = source_url_base.rstrip("/")
    version["sourceUrl"] = f"{base}/{package_name}"
    entry["imageUrl"] = f"{base}/{IMAGE_NAME}"
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / package_name).write_bytes(verified[package_name])
    (output_dir / IMAGE_NAME).write_bytes(image)
    (output_dir / "SHA256SUMS").write_bytes(published_checksums)
    (output_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return {"version": four_part_version, "package": package_name,
            "packageSha256": checksums[package_name]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-url-base", required=True)
    parser.add_argument("--repository", default=DEFAULT_REPOSITORY)
    parser.add_argument("--download-base", help="Override the release asset base URL.")
    parser.add_argument("--timeout", type=float, default=60.0)
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
            raise ValueError("Repository must be an https://github.com/owner/name URL.")
        download_base = args.download_base or f"{args.repository}/releases/download/{args.tag}"
        result = fetch_released_catalog(
            tag=args.tag,
            download_base=download_base,
            output_dir=args.output,
            source_url_base=args.source_url_base,
            timeout=args.timeout,
        )
    except ReleaseUnavailable as error:
        print(f"Published release {args.tag} is unavailable: {error}", file=sys.stderr)
        return UNAVAILABLE_EXIT_CODE
    except ValueError as error:
        print(f"Published release {args.tag} failed verification: {error}", file=sys.stderr)
        return 1
    print(f"Verified published {args.tag} package {result['package']} "
          f"sha256 {result['packageSha256']}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
