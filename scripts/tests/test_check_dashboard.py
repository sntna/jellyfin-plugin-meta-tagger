import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class CheckDashboardTests(unittest.TestCase):
    def run_check(self, *, node_exit=0, dotnet_exit=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / 'scripts'
            scripts.mkdir()
            shutil.copy2(Path(__file__).resolve().parents[1] / 'check-dashboard.sh', scripts)
            tools = root / 'tools'
            tools.mkdir()
            calls = root / 'calls.jsonl'
            temporary = root / 'temporary'
            temporary.mkdir()
            for command, exit_code in [('node', node_exit), ('dotnet', dotnet_exit)]:
                executable = tools / command
                executable.write_text(
                    '#!/usr/bin/env python3\n'
                    'import json, os, pathlib, sys\n'
                    'with open(os.environ["CHECK_CALLS"], "a") as log:\n'
                    '    log.write(json.dumps(sys.argv) + "\\n")\n'
                    'if "--artifacts-path" in sys.argv:\n'
                    '    artifacts = pathlib.Path(sys.argv[sys.argv.index("--artifacts-path") + 1])\n'
                    '    assert artifacts.parent.is_dir()\n'
                    '    artifacts.mkdir()\n'
                    '    (artifacts / "build-output").write_text("generated")\n'
                    f'print("{command} result {exit_code}")\n'
                    f'sys.exit({exit_code})\n'
                )
                executable.chmod(0o755)
            result = subprocess.run(
                ['bash', str(scripts / 'check-dashboard.sh')],
                env=dict(os.environ, PATH=f'{tools}{os.pathsep}{os.environ["PATH"]}',
                         TMPDIR=str(temporary), CHECK_CALLS=str(calls)),
                capture_output=True, text=True, timeout=15,
            )
            recorded = [json.loads(line) for line in calls.read_text().splitlines()]
            self.assertEqual([], list(temporary.iterdir()), 'temporary outputs must be cleaned')
            return result, recorded, root

    def test_node_failure_fails_without_running_embedded_page_checks(self):
        result, calls, _ = self.run_check(node_exit=7)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(['node'], [Path(call[0]).name for call in calls])
        self.assertIn('node result 7', result.stdout + result.stderr)

    def test_embedded_page_failure_fails_and_cleans_build_output(self):
        result, calls, _ = self.run_check(dotnet_exit=8)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(['node', 'dotnet'], [Path(call[0]).name for call in calls])
        self.assertIn('dotnet result 8', result.stdout + result.stderr)

    def test_success_checks_dashboard_suites_and_plugin_tests_in_isolated_release_output(self):
        result, calls, root = self.run_check()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('Dashboard checks passed.', result.stdout)
        self.assertEqual(['node', 'dotnet'], [Path(call[0]).name for call in calls])
        self.assertEqual(
            ['--test', str(root / 'scripts/tests/dashboard.test.mjs'),
             str(root / 'scripts/tests/dev-client.test.mjs')], calls[0][1:],
        )
        dotnet = calls[1]
        self.assertEqual('Release', dotnet[dotnet.index('--configuration') + 1])
        self.assertEqual(
            'FullyQualifiedName~Jellyfin.Plugin.MetaTagger.Tests.PluginTests',
            dotnet[dotnet.index('--filter') + 1],
        )
        self.assertTrue(Path(dotnet[dotnet.index('--artifacts-path') + 1]).is_relative_to(root / 'temporary'))
        self.assertNotIn('--no-build', dotnet)
        self.assertNotIn('--no-restore', dotnet)


if __name__ == '__main__':
    unittest.main()
