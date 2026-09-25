import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class PublishReleaseTests(unittest.TestCase):
    def test_failed_upload_can_resume_draft_and_published_release_is_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            gh = root / 'gh'
            gh.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
path = pathlib.Path(os.environ['FAKE_RELEASE'])
state = json.loads(path.read_text()) if path.exists() else None
args = sys.argv[1:]
if args[:2] == ['release', 'view']:
    if state is None:
        sys.exit(1)
    print('true' if state['draft'] else 'false')
elif args[:2] == ['release', 'create']:
    assert state is None
    assert '--draft' in args and '--verify-tag' in args
    state = {'draft': True, 'notes': pathlib.Path(args[args.index('--notes-file') + 1]).read_text(), 'assets': []}
elif args[:2] == ['release', 'upload']:
    assert state['draft']
    if os.environ.get('FAIL_UPLOAD'):
        sys.exit(1)
    state['assets'] = [pathlib.Path(arg).name for arg in args[3:] if not arg.startswith('--')]
elif args[:2] == ['release', 'edit']:
    assert state['assets']
    assert '--draft=false' in args
    state['draft'] = False
else:
    raise AssertionError(args)
if state is not None:
    path.write_text(json.dumps(state))
''')
            gh.chmod(0o755)
            assets = root / 'assets'
            assets.mkdir()
            for name in ('meta-tagger_0.2.0.0.zip', 'manifest.json', 'meta-tagger.png', 'SHA256SUMS'):
                (assets / name).write_text('fixture')
            notes = root / 'notes.md'
            notes.write_text('### Fixed\n\n- Reviewed notes.\n')
            state = root / 'release.json'
            env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}', FAKE_RELEASE=str(state))
            command = ['bash', str(ROOT / 'scripts/publish_release.sh'), 'v0.2.0', str(assets), str(notes)]
            failed = subprocess.run(command, env=dict(env, FAIL_UPLOAD='1'), capture_output=True, text=True)
            self.assertNotEqual(0, failed.returncode)
            self.assertTrue(state.exists(), failed.stderr)
            self.assertTrue(json.loads(state.read_text())['draft'])
            retry = subprocess.run(command, env=env, capture_output=True, text=True)
            self.assertEqual(0, retry.returncode, retry.stderr)
            published = state.read_bytes()
            release = json.loads(published)
            self.assertFalse(release['draft'])
            self.assertEqual(notes.read_text(), release['notes'])
            self.assertCountEqual([p.name for p in assets.iterdir()], release['assets'])
            retry = subprocess.run(command, env=env, capture_output=True, text=True)
            self.assertEqual(0, retry.returncode, retry.stderr)
            self.assertEqual(published, state.read_bytes())


if __name__ == '__main__':
    unittest.main()
