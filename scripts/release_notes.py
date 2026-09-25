#!/usr/bin/env python3
"""Print the reviewed changelog section used as the GitHub Release body."""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from package_release import ROOT, _project_versions


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--changelog', type=Path, default=ROOT / 'CHANGELOG.md')
    parser.add_argument('--version')
    args = parser.parse_args()
    version = args.version or _project_versions()[0]
    text = args.changelog.read_text()
    section = re.search(r'^## \[' + re.escape(version) + r'\] - \d{4}-\d{2}-\d{2}\n(.*?)(?=^## |^\[|\Z)', text, re.M | re.S)
    if section is None or len(re.findall(r'^## \[' + re.escape(version) + r'\]', text, re.M)) != 1:
        parser.error(f'No reviewed changelog section for {version}.')
    if not re.search(r'^- \S', section[1], re.M):
        parser.error(f'Changelog section for {version} has no release entries.')
    print(section[1].strip())


if __name__ == '__main__':
    main()
