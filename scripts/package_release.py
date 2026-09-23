#!/usr/bin/env python3
"""Build release assets for the Jellyfin plugin catalog and GitHub Release."""

from __future__ import annotations

from datetime import datetime, timezone
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
from typing import NamedTuple
import xml.etree.ElementTree as ElementTree
import zipfile


ROOT = Path(__file__).resolve().parents[1]
PROJECT_PATH = ROOT / "Jellyfin.Plugin.MetaTagger" / "Jellyfin.Plugin.MetaTagger.csproj"
PLUGIN_PATH = ROOT / "Jellyfin.Plugin.MetaTagger" / "Plugin.cs"
MANIFEST_PATH = ROOT / "Jellyfin.Plugin.MetaTagger" / "manifest.json"
PLUGIN_IMAGE_PATH = ROOT / "docs/brand/assets/plugin.png"


class ReleaseAssets(NamedTuple):
    package_path: Path
    manifest_path: Path
    checksums_path: Path


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_release_assets(
    *,
    dll_path: Path,
    manifest_path: Path,
    output_dir: Path,
    repository: str,
    tag: str,
    timestamp: str,
    source_url_base: str | None = None,
    version_override: str | None = None,
) -> ReleaseAssets:
    manifest = json.loads(manifest_path.read_text())
    entry = manifest[0]
    version = entry["versions"][0]
    if version_override is not None:
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.0", version_override):
            raise ValueError("Version override must use the x.y.z.0 form.")
        version["version"] = version_override
    four_part_version = version["version"]
    version_parts = four_part_version.split(".")
    if len(version_parts) != 4 or version_parts[-1] != "0":
        raise ValueError("The manifest version must use the SemVer-compatible x.y.z.0 form.")

    expected_tag = "v" + ".".join(version_parts[:3])
    if tag != expected_tag:
        raise ValueError(f"Release tag {tag!r} does not match {expected_tag!r}.")
    if not re.fullmatch(r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be an https://github.com/owner/name URL.")
    if not dll_path.is_file():
        raise FileNotFoundError(dll_path)

    output_dir.mkdir(parents=True, exist_ok=True)
    package_name = f"meta-tagger_{four_part_version}.zip"
    package_path = output_dir / package_name
    with zipfile.ZipFile(package_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as package:
        for name, content in [("Jellyfin.Plugin.MetaTagger.dll", dll_path.read_bytes()),
                              ("meta-tagger.png", PLUGIN_IMAGE_PATH.read_bytes())]:
            member = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            member.compress_type = zipfile.ZIP_DEFLATED
            member.external_attr = 0o644 << 16
            package.writestr(member, content)

    package_base_url = source_url_base or f"{repository}/releases/download/{tag}"
    if not re.fullmatch(r"https?://[^\s/]+(?::[0-9]+)?(?:/[^\s]*)?", package_base_url):
        raise ValueError("Package source base must be an HTTP or HTTPS URL.")
    image_path = output_dir / "meta-tagger.png"
    image_path.write_bytes(PLUGIN_IMAGE_PATH.read_bytes())
    entry["imageUrl"] = f"{package_base_url.rstrip('/')}/{image_path.name}"
    version["sourceUrl"] = f"{package_base_url.rstrip('/')}/{package_name}"
    version["checksum"] = hashlib.md5(package_path.read_bytes()).hexdigest()
    version["timestamp"] = timestamp
    release_manifest_path = output_dir / "manifest.json"
    release_manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")

    checksums_path = output_dir / "SHA256SUMS"
    checksum_paths = [release_manifest_path, package_path, image_path]
    checksums_path.write_text(
        "".join(f"{_sha256(path)}  {path.name}\n" for path in checksum_paths)
    )
    return ReleaseAssets(package_path, release_manifest_path, checksums_path)


def _project_versions() -> tuple[str, str]:
    project = ElementTree.parse(PROJECT_PATH).getroot()
    version = project.findtext(".//Version")
    assembly_version = project.findtext(".//AssemblyVersion")
    if not version or not assembly_version:
        raise ValueError("Project version metadata is incomplete.")

    if (project.findtext(".//PackageVersion") != version
            or assembly_version != f"{version}.0"
            or project.findtext(".//FileVersion") != assembly_version):
        raise ValueError("Project package, assembly, and file versions do not match.")

    source_match = re.search(r'PluginVersion\s*=\s*"([^"]+)"', PLUGIN_PATH.read_text())
    if source_match is None or source_match.group(1) != version:
        raise ValueError("Plugin.cs and project versions do not match.")
    manifest = json.loads(MANIFEST_PATH.read_text())
    if manifest[0]["versions"][0]["version"] != assembly_version:
        raise ValueError("Manifest and assembly versions do not match.")
    return version, assembly_version


def _build_plugin(version_override: str | None = None) -> Path:
    build_command = ["dotnet", "build", str(PROJECT_PATH), "--configuration", "Release", "--nologo"]
    if version_override is not None:
        build_command.extend([
            f"-p:Version={version_override}",
            f"-p:PackageVersion={version_override}",
            f"-p:AssemblyVersion={version_override}.0",
            f"-p:FileVersion={version_override}.0",
        ])
    subprocess.run(
        build_command,
        cwd=ROOT,
        check=True,
    )
    target_command = [
            "dotnet",
            "msbuild",
            str(PROJECT_PATH),
            "-nologo",
            "-property:Configuration=Release",
            "-getProperty:TargetPath",
        ]
    if version_override is not None:
        target_command.extend([
            f"-property:Version={version_override}",
            f"-property:AssemblyVersion={version_override}.0",
        ])
    result = subprocess.run(
        target_command,
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    candidates = [Path(line.strip()) for line in result.stdout.splitlines() if line.strip()]
    dll_path = next((path for path in reversed(candidates) if path.suffix == ".dll"), None)
    if dll_path is None:
        raise RuntimeError("The Release DLL path could not be resolved.")
    return dll_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-url-base")
    parser.add_argument("--test-fixture-version", choices=["0.0.0"])
    args = parser.parse_args()

    version, _ = _project_versions()
    package_version = args.test_fixture_version or version
    expected_tag = f"v{package_version}"
    if args.tag != expected_tag:
        parser.error(f"tag must be {expected_tag}")
    dll_path = _build_plugin(args.test_fixture_version)
    timestamp = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    write_release_assets(
        dll_path=dll_path,
        manifest_path=MANIFEST_PATH,
        output_dir=args.output,
        repository=args.repository,
        tag=args.tag,
        timestamp=timestamp,
        source_url_base=args.source_url_base,
        version_override=f"{package_version}.0" if args.test_fixture_version else None,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
