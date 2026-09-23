#!/usr/bin/env python3
"""Validate the release commit and optionally create its local annotated tag."""

from __future__ import annotations

import argparse
import re
import subprocess

from package_release import ROOT, _project_versions


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--create", action="store_true", help="Create the tag locally; never push or replace a tag.")
    args = parser.parse_args()
    version, _ = _project_versions()
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        parser.error("Release version must be major.minor.patch.")
    tag = f"v{version}"
    if git("status", "--porcelain"):
        parser.error("Commit all changes before preparing a release.")
    exists = subprocess.run(
        ["git", "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"], cwd=ROOT,
    ).returncode == 0
    if exists and git("rev-parse", f"refs/tags/{tag}^{{}}") != git("rev-parse", "HEAD"):
        parser.error(f"{tag} already points to a different commit. Do not move a published tag.")
    if args.create and not exists:
        subprocess.run(["git", "tag", "-a", tag, "-m", f"Meta Tagger {version}"], cwd=ROOT, check=True)
    print(tag)


if __name__ == "__main__":
    main()
