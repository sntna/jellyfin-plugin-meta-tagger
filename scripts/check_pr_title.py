#!/usr/bin/env python3
"""Validate a PR event's title without evaluating its contents as code."""

import json
from pathlib import Path
import re
import sys


def main():
    event = json.loads(Path(sys.argv[1]).read_text())
    title = event['pull_request']['title']
    # Keep compatible with the Conventional Commit release preparation format.
    if not re.fullmatch(r'([a-z]+)(?:\(([^()]+)\))?(!)?: (\S.*)', title):
        print('PR title must use Conventional Commit syntax, for example '
              'fix: preserve manual tags or chore(deps): update dependencies.',
              file=sys.stderr)
        return 1
    print('PR title uses Conventional Commit syntax.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
