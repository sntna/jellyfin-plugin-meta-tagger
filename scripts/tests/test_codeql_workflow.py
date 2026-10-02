from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class CodeQLWorkflowTests(unittest.TestCase):
    def test_codeql_steps_use_the_same_pinned_release(self):
        workflow = (ROOT / '.github/workflows/codeql.yml').read_text()
        actions = re.findall(
            r'^\s*uses:\s+github/codeql-action/([^@\s]+)@(\S+)',
            workflow,
            re.MULTILINE,
        )
        self.assertIn('init', [name for name, _ in actions])
        self.assertIn('analyze', [name for name, _ in actions])
        for name, reference in actions:
            with self.subTest(action=name):
                self.assertRegex(reference, r'^[0-9a-f]{40}$')
        self.assertEqual(
            len({reference for _, reference in actions}),
            1,
            'CodeQL steps share configuration and must use the same release',
        )


if __name__ == '__main__':
    unittest.main()
