import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class PrTitleTests(unittest.TestCase):
    def check_title(self, title):
        with tempfile.TemporaryDirectory() as directory:
            event = Path(directory) / 'event.json'
            event.write_text(json.dumps({'pull_request': {'title': title}}))
            return subprocess.run(
                [sys.executable, str(ROOT / 'scripts/check_pr_title.py'), str(event)],
                capture_output=True, text=True)

    def test_accepts_release_and_dependabot_titles(self):
        for title in ('feat: add a rule', 'fix(tags): preserve manual tags',
                      'feat!: change configuration', 'fix(api)!: remove endpoint',
                      'chore(deps): bump a dependency', 'chore(deps-dev): bump tooling',
                      'build(deps): bump an action', 'chore: prepare release 0.2.0'):
            with self.subTest(title=title):
                result = self.check_title(title)
                self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_titles_that_break_release_parsing(self):
        for title in ('Bump a dependency', 'Fix: tags', 'fix:', 'fix: ',
                      'fix:  tags', 'fix:no space', 'fix(): tags', 'fix: tags\nextra'):
            with self.subTest(title=title):
                self.assertNotEqual(0, self.check_title(title).returncode)

    def test_shell_syntax_is_inert_and_not_echoed(self):
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / 'executed'
            title = f'fix: $(touch {marker}) `touch {marker}` "quoted"'
            result = self.check_title(title)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertFalse(marker.exists())
            self.assertNotIn(title, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
