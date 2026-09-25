from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ReleaseNotesTests(unittest.TestCase):
    def test_cli_outputs_only_the_reviewed_version_section(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'CHANGELOG.md'
            path.write_text('''# Changelog

## [Unreleased]

- Future work.

## [0.2.0] - 2026-09-25

### Added

- Reviewed wording, including `code` and (#42).

## [0.1.0] - 2026-09-19

- Older notes.

[Unreleased]: https://example.invalid/compare
[0.2.0]: https://example.invalid/tag
''')
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/release_notes.py'),
                                     '--changelog', str(path), '--version', '0.2.0'],
                                    text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual('### Added\n\n- Reviewed wording, including `code` and (#42).\n', result.stdout)

    def test_cli_rejects_missing_empty_or_duplicate_sections(self):
        for text in ('# Changelog\n', '## [0.2.0] - 2026-09-25\n\n### Added\n',
                     '## [0.2.0] - 2026-09-25\n\n- One.\n\n## [0.2.0] - 2026-09-25\n\n- Two.\n'):
            with self.subTest(text=text), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'CHANGELOG.md'
                path.write_text(text)
                result = subprocess.run([sys.executable, str(ROOT / 'scripts/release_notes.py'),
                                         '--changelog', str(path), '--version', '0.2.0'],
                                        text=True, capture_output=True)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual('', result.stdout)


if __name__ == '__main__':
    unittest.main()
