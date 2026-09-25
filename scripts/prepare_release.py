#!/usr/bin/env python3
"""Prepare version metadata and a changelog draft without committing or publishing."""
from __future__ import annotations

import argparse
from datetime import date
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = 'Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj'
PLUGIN = 'Jellyfin.Plugin.MetaTagger/Plugin.cs'
MANIFEST = 'Jellyfin.Plugin.MetaTagger/manifest.json'
CONTRACT = 'Jellyfin.Plugin.MetaTagger.Tests/PluginVersionContractTests.cs'


def git(*args: str) -> str:
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


SEMVER = r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'


def version_parts(value: str) -> tuple[int, int, int]:
    if not re.fullmatch(SEMVER, value):
        raise ValueError('Release version must be major.minor.patch without leading zeros or suffixes.')
    return tuple(map(int, value.split('.')))


def prepare(args) -> str:
    from package_release import _project_versions
    current, _ = _project_versions()
    version_parts(current)
    contract = (ROOT / CONTRACT).read_text()
    for key, expected in [('ExpectedDevelopmentVersion', current), ('ExpectedFourPartVersion', current + '.0')]:
        matches = re.findall(fr'{key}\s*=\s*"([^"]+)"', contract)
        if matches != [expected]:
            raise ValueError('Version contract expectations do not match project metadata.')
    prior = git('describe', '--tags', '--first-parent', '--abbrev=0', '--match', 'v[0-9]*')
    titles = git('log', '--first-parent', '--reverse', '--format=%s', f'{prior}..HEAD').splitlines()
    baseline = ET.fromstring(git('show', f'{prior}:{PROJECT}')).findtext('.//Version')
    if prior != 'v' + baseline:
        raise ValueError('Prior release tag does not match its project version.')
    major, minor, patch = version_parts(baseline)
    if not titles:
        raise ValueError('The release range is empty.')
    groups = {'Added': [], 'Changed': [], 'Fixed': []}
    breaking = False
    for title in titles:
        match = re.fullmatch(r'([a-z]+)(?:\(([^()]+)\))?(!)?: (\S.*)', title)
        if not match:
            raise ValueError(f'Invalid Conventional Commit title: {title!r}')
        dependency = match[2] in ('deps', 'deps-dev')
        if not dependency and (match[1] in ('feat', 'fix') or match[3]):
            if match[3]:
                breaking = True
                groups['Changed'].append('- Breaking: ' + match[4])
            else:
                groups['Added' if match[1] == 'feat' else 'Fixed'].append('- ' + match[4])
        elif args.include_maintenance or (dependency and args.bump != 'auto'):
            groups['Changed'].append('- ' + match[4])
    if not any(groups.values()):
        raise ValueError('No release-worthy changes. Select an explicit bump for dependencies or include maintenance.')
    if args.bump == 'auto' and not (breaking or groups['Added'] or groups['Fixed']):
        raise ValueError('Maintenance and dependency changes require an explicit bump.')
    if args.bump == 'patch':
        version = f'{major}.{minor}.{patch + 1}'
    elif args.bump == 'minor':
        version = f'{major}.{minor + 1}.0'
    elif args.bump != 'auto':
        version = args.bump
    elif breaking and major >= 1:
        version = f'{major + 1}.0.0'
    elif breaking or groups['Added']:
        version = f'{major}.{minor + 1}.0'
    else:
        version = f'{major}.{minor}.{patch + 1}'
    if version_parts(version) <= version_parts(baseline):
        raise ValueError('Target version must be greater than the current version.')
    if current == version:
        committed = ET.fromstring(git('show', f'HEAD:{PROJECT}')).findtext('.//Version')
        if committed == version:
            prepared_commit = git('log', '-1', '--format=%H', '--', PROJECT)
            later_titles = git('log', '--format=%s', f'{prepared_commit}..HEAD').splitlines()
            # Changelog wording may be reviewed, but a prepared release must not
            # silently absorb more product changes without a new draft.
            if any(not title.startswith(('docs:', 'chore:')) for title in later_titles):
                raise ValueError('Release already prepared; review new changes and prepare again from main before the version commit.')
        changelog = (ROOT / 'CHANGELOG.md').read_text()
        if f'## [{version}] - ' not in changelog:
            raise ValueError('Prepared version has no changelog section.')
        return version
    if current != baseline:
        raise ValueError('Current version differs from the prior release and requested target.')
    if git('status', '--porcelain'):
        raise ValueError('Commit changes before preparing a new release.')
    updates = {}
    project = (ROOT / PROJECT).read_text()
    for key in ('Version', 'PackageVersion', 'AssemblyVersion', 'FileVersion'):
        value = version if key in ('Version', 'PackageVersion') else version + '.0'
        project = re.sub(fr'<{key}>[^<]+</{key}>', f'<{key}>{value}</{key}>', project)
    updates[PROJECT] = project
    updates[PLUGIN] = re.sub(r'(PluginVersion\s*=\s*")[^"]+', lambda m: m[1] + version, (ROOT / PLUGIN).read_text())
    updates[MANIFEST] = re.sub(r'("version"\s*:\s*")[^"]+', lambda m: m[1] + version + '.0', (ROOT / MANIFEST).read_text())
    contract = (ROOT / CONTRACT).read_text()
    for key, value in [('ExpectedDevelopmentVersion', version), ('ExpectedFourPartVersion', version + '.0')]:
        contract = re.sub(fr'({key}\s*=\s*")[^"]+', lambda m: m[1] + value, contract)
    updates[CONTRACT] = contract
    changelog = (ROOT / 'CHANGELOG.md').read_text()
    if changelog.count('## [Unreleased]\n') != 1 or not re.search(r'^\[Unreleased\]: https://github\.com/[^/\s]+/[^/\s]+/compare/[^\s]+$', changelog, re.M):
        raise ValueError('Changelog needs one Unreleased heading and a GitHub comparison link.')
    unreleased = re.search(r'^## \[Unreleased\]\n(.*?)(?=^## |^\[|\Z)', changelog, re.M | re.S)
    pieces = re.split(r'^### ([^\n]+)\n', unreleased[1], flags=re.M)
    sections = {}
    for heading, body in zip(pieces[1::2], pieces[2::2]):
        sections[heading] = (sections.get(heading, '') + '\n\n' + body.strip()).strip()
    for heading, entries in groups.items():
        body = sections.get(heading, '')
        additions = [entry for entry in entries if entry not in body.splitlines()]
        sections[heading] = (body + '\n\n' + '\n'.join(additions)).strip()
    order = ['Added', 'Changed', 'Deprecated', 'Removed', 'Fixed', 'Security']
    order.extend(heading for heading in sections if heading not in order)
    notes = '\n\n'.join(filter(None, [pieces[0].strip(), *[
        f'### {heading}\n\n{sections[heading]}' for heading in order if sections.get(heading)
    ]]))
    changelog = (changelog[:unreleased.start()] +
                 f'## [Unreleased]\n\n## [{version}] - {args.date}\n\n{notes}\n\n' +
                 changelog[unreleased.end():])
    repository = re.search(r'\[Unreleased\]: (.+)/compare/', changelog)[1]
    changelog = re.sub(r'\[Unreleased\]: .*', f'[Unreleased]: {repository}/compare/v{version}...HEAD\n[{version}]: {repository}/compare/{prior}...v{version}', changelog)
    updates['CHANGELOG.md'] = changelog
    for name, content in updates.items():
        (ROOT / name).write_text(content)
    return version


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--date', type=date.fromisoformat, default=date.today())
    parser.add_argument('--bump', default='auto', help='auto, patch, minor, or an explicit major.minor.patch version')
    parser.add_argument('--include-maintenance', action='store_true', help='Include maintenance and dependency titles in the draft')
    args = parser.parse_args()
    try:
        print(prepare(args))
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.error(str(error))


if __name__ == '__main__':
    main()
