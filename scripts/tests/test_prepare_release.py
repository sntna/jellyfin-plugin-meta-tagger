from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECT = 'Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj'
PLUGIN = 'Jellyfin.Plugin.MetaTagger/Plugin.cs'
MANIFEST = 'Jellyfin.Plugin.MetaTagger/manifest.json'
CONTRACT = 'Jellyfin.Plugin.MetaTagger.Tests/PluginVersionContractTests.cs'


class PrepareReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in (PROJECT, PLUGIN, MANIFEST, CONTRACT, 'CHANGELOG.md'):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            text = (ROOT / name).read_text()
            version = ET.parse(ROOT / PROJECT).getroot().findtext('.//Version')
            target.write_text(text.replace(version, '0.1.0'))
        shutil.copytree(ROOT / 'scripts', self.root / 'scripts', ignore=shutil.ignore_patterns('__pycache__'))
        (self.root / '.gitignore').write_text('__pycache__/\n')
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Release test')
        self.git('config', 'user.email', 'release@example.invalid')
        self.commit('chore: initial release')
        self.git('tag', 'v0.1.0')

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root, text=True, stderr=subprocess.DEVNULL).strip()

    def commit(self, title):
        self.git('add', '.')
        self.git('-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', title)

    def prepare(self, *args):
        return subprocess.run([sys.executable, 'scripts/prepare_release.py', '--date', '2026-09-25', *args],
                              cwd=self.root, text=True, capture_output=True)

    def test_feature_prepares_aligned_versions_and_reviewable_changelog(self):
        self.commit('feat: Add studio filtering (#12)')
        head = self.git('rev-parse', 'HEAD')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        project = ET.parse(self.root / PROJECT).getroot()
        for key, expected in [('Version', '0.2.0'), ('PackageVersion', '0.2.0'),
                              ('AssemblyVersion', '0.2.0.0'), ('FileVersion', '0.2.0.0')]:
            self.assertEqual(expected, project.findtext('.//' + key))
        self.assertIn('PluginVersion = "0.2.0"', (self.root / PLUGIN).read_text())
        contract = (self.root / CONTRACT).read_text()
        self.assertIn('ExpectedDevelopmentVersion = "0.2.0"', contract)
        self.assertIn('ExpectedFourPartVersion = "0.2.0.0"', contract)
        manifest = json.loads((self.root / MANIFEST).read_text())[0]['versions'][0]
        self.assertEqual('0.2.0.0', manifest['version'])
        self.assertEqual('12.0.0.0', manifest['targetAbi'])
        changelog = (self.root / 'CHANGELOG.md').read_text()
        self.assertIn('## [Unreleased]\n\n## [0.2.0] - 2026-09-25\n\n### Added\n\n- Add studio filtering (#12)\n', changelog)
        self.assertIn('[Unreleased]: https://github.com/sntna/jellyfin-plugin-meta-tagger/compare/v0.2.0...HEAD', changelog)
        self.assertIn('[0.2.0]: https://github.com/sntna/jellyfin-plugin-meta-tagger/compare/v0.1.0...v0.2.0', changelog)
        self.assertEqual(head, self.git('rev-parse', 'HEAD'))
        self.assertEqual('v0.1.0', self.git('tag', '--list'))

    def test_fix_produces_patch_and_maintenance_is_omitted(self):
        self.commit('fix(tags): Preserve manual tags (#13)')
        self.commit('docs: Update setup guide (#14)')
        self.commit('chore(deps): Update dependency (#15)')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.1.1', result.stdout.strip())
        notes = (self.root / 'CHANGELOG.md').read_text()
        self.assertIn('### Fixed\n\n- Preserve manual tags (#13)', notes)
        self.assertNotIn('Update setup guide', notes)
        self.assertNotIn('Update dependency', notes)

    def test_breaking_changes_follow_pre_and_post_one_policy(self):
        self.commit('feat!: Replace configuration format (#16)')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.2.0', result.stdout.strip())
        self.assertIn('### Changed\n\n- Breaking: Replace configuration format (#16)',
                      (self.root / 'CHANGELOG.md').read_text())
        self.git('reset', '--hard', 'HEAD')
        for name in (PROJECT, PLUGIN, MANIFEST, CONTRACT, 'CHANGELOG.md'):
            path = self.root / name
            path.write_text(path.read_text().replace('0.1.0', '1.2.3'))
        self.commit('chore: release 1.2.3')
        self.git('tag', 'v1.2.3')
        self.commit('fix(api)!: Remove legacy endpoint (#17)')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('2.0.0', result.stdout.strip())

    def test_explicit_bump_can_ship_dependencies_and_include_maintenance(self):
        self.commit('chore(deps): Update shipped library (#18)')
        self.commit('docs: Explain installation (#19)')
        result = self.prepare('--bump', 'patch', '--include-maintenance')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.1.1', result.stdout.strip())
        notes = (self.root / 'CHANGELOG.md').read_text()
        self.assertIn('### Changed\n\n- Update shipped library (#18)\n- Explain installation (#19)', notes)
        self.git('reset', '--hard', 'HEAD')
        result = self.prepare('--bump', '1.0.0', '--include-maintenance')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('1.0.0', result.stdout.strip())
        self.git('reset', '--hard', 'HEAD')
        result = self.prepare('--bump', 'minor', '--include-maintenance')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.2.0', result.stdout.strip())

    def test_invalid_or_empty_proposals_leave_working_tree_unchanged(self):
        for title, args in [
            (None, []), (None, ['--bump', 'patch']),
            ('docs: Update guide', []), ('test: Improve coverage', []),
            ('ci: Speed up checks', []), ('chore: Clean scripts', []),
            ('chore(deps): Update library', []), ('fix(deps): Update library', []),
            ('not a conventional title', []), ('fix: ', []),
            ('fix: Repair tags', ['--bump', '01.2.3']),
            ('fix: Repair tags', ['--bump', '1.0.0-rc.1']),
            ('fix: Repair tags', ['--bump', '0.1.0']),
            ('fix: Repair tags', ['--bump', '0.0.9']),
        ]:
            with self.subTest(title=title, args=args):
                self.git('reset', '--hard', 'v0.1.0')
                if title:
                    self.commit(title)
                result = self.prepare(*args)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertEqual('', self.git('status', '--porcelain'))
                self.assertEqual('v0.1.0', self.git('tag', '--list'))

    def test_rerun_preserves_reviewed_notes_and_does_not_bump_again(self):
        self.commit('feat: Add studio filtering (#12)')
        self.assertEqual(0, self.prepare().returncode)
        changelog = self.root / 'CHANGELOG.md'
        changelog.write_text(changelog.read_text().replace('Add studio filtering (#12)', 'Filter movies by studio.'))
        expected = self.git('diff')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.2.0', result.stdout.strip())
        self.assertEqual(expected, self.git('diff'))
        self.commit('chore: prepare release 0.2.0')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.2.0', result.stdout.strip())
        self.assertEqual('', self.git('status', '--porcelain'))

    def test_preparation_rejects_uncommitted_inputs_and_malformed_metadata(self):
        self.commit('fix: Repair tags')
        project = self.root / PROJECT
        project.write_text(project.read_text().replace('<PackageVersion>0.1.0</PackageVersion>', ''))
        before = self.git('diff')
        result = self.prepare()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(before, self.git('diff'))
        self.commit('chore: malformed metadata')
        result = self.prepare()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('', self.git('status', '--porcelain'))

    def test_preparation_preserves_existing_unreleased_notes(self):
        path = self.root / 'CHANGELOG.md'
        path.write_text(path.read_text().replace('## [Unreleased]\n',
                        '## [Unreleased]\n\n### Security\n\n- Reviewed security note.\n'))
        self.commit('fix: Repair tags')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        notes = path.read_text()
        self.assertIn('## [Unreleased]\n\n## [0.1.1]', notes)
        self.assertIn('### Security\n\n- Reviewed security note.', notes)
        self.assertEqual(1, notes.count('Reviewed security note.'))

    def test_rerun_refuses_to_hide_new_changes_since_preparation(self):
        self.commit('fix: Repair tags')
        self.assertEqual(0, self.prepare().returncode)
        self.commit('chore: prepare release 0.1.1')
        self.commit('fix: Repair another issue')
        result = self.prepare()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('already prepared', result.stderr)
        self.assertEqual('', self.git('status', '--porcelain'))

    def test_prepared_checkout_still_passes_existing_tag_and_package_checks(self):
        image = self.root / 'docs/brand/assets/plugin.png'
        image.parent.mkdir(parents=True)
        shutil.copyfile(ROOT / 'docs/brand/assets/plugin.png', image)
        self.commit('fix: Repair tags')
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        for module in ('test_release_tag.py', 'test_package_release.py'):
            checked = subprocess.run([sys.executable, '-m', 'unittest', 'discover',
                                      '-s', 'scripts/tests', '-p', module], cwd=self.root,
                                     capture_output=True, text=True)
            self.assertEqual(0, checked.returncode, checked.stderr)

    def test_existing_unreleased_categories_are_merged_into_the_draft(self):
        changelog = self.root / 'CHANGELOG.md'
        changelog.write_text(changelog.read_text().replace('## [Unreleased]\n',
                            '## [Unreleased]\n\n### Added\n\n- Reviewed feature.\n'))
        self.commit('feat: Add studio filtering (#12)')
        self.commit('fix: Preserve manual tags (#13)')
        self.assertEqual(0, self.prepare().returncode)
        section = changelog.read_text().split('## [0.2.0]')[1].split('## [0.1.0]')[0]
        self.assertEqual(1, section.count('### Added'))
        self.assertIn('- Reviewed feature.', section)
        self.assertIn('- Add studio filtering (#12)', section)
        self.assertIn('### Fixed\n\n- Preserve manual tags (#13)', section)


if __name__ == '__main__':
    unittest.main()
