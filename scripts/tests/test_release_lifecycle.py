"""Exercise the lifecycle shell script's real repository HTTP servers.

Only package compilation and Jellyfin are replaced with fixtures. The client
uses IPv6 loopback to verify that an explicit bind address replaces IPv4 loopback.
"""
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ReleaseRepositoryTests(unittest.TestCase):
    def run_repositories(self, address, override):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / 'scripts'
            scripts.mkdir()
            shutil.copyfile(ROOT / 'scripts/test-release-lifecycle.sh', scripts / 'test-release-lifecycle.sh')
            (scripts / 'package_release.py').write_text(textwrap.dedent('''\
                import argparse
                import json
                from pathlib import Path

                def _project_versions():
                    return '0.1.0', '0.1.0.0'

                if __name__ == '__main__':
                    parser = argparse.ArgumentParser()
                    parser.add_argument('--output', type=Path)
                    parser.add_argument('--tag')
                    parser.add_argument('--source-url-base')
                    args, _ = parser.parse_known_args()
                    name = f'meta-tagger_{args.tag[1:]}.0.zip'
                    (args.output / name).write_bytes(b'fixture-package')
                    (args.output / 'manifest.json').write_text(json.dumps({
                        'sourceUrl': args.source_url_base + '/' + name,
                    }))
                '''))
            (scripts / 'disposable-jellyfin.py').write_text(textwrap.dedent('''\
                import json
                import os
                import sys
                import time
                import urllib.parse
                import urllib.request

                client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                def fetch(url):
                    parsed = urllib.parse.urlsplit(url)
                    target = f"http://{os.environ['TEST_CATALOG_ADDRESS']}:{parsed.port}{parsed.path}"
                    for attempt in range(100):
                        try:
                            with client.open(target, timeout=.2) as response:
                                return response.read()
                        except OSError:
                            if attempt == 99:
                                raise
                            time.sleep(.02)

                previous = sys.argv[sys.argv.index('--previous-manifest-url') + 1]
                for url in [sys.argv[2], previous]:
                    manifest = json.loads(fetch(url))
                    assert fetch(manifest['sourceUrl']) == b'fixture-package'
                print('PASS both catalogs and ZIPs reachable')
                '''))
            ports = []
            family = socket.AF_INET6 if ':' in address else socket.AF_INET
            with socket.socket(family) as current, socket.socket(family) as previous:
                for listener in [current, previous]:
                    listener.bind((address, 0))
                    ports.append(str(listener.getsockname()[1]))
            env = dict(os.environ, TEST_CATALOG_ADDRESS=f'[{address}]' if ':' in address else address)
            env.pop('META_TAGGER_REPOSITORY_BIND_ADDRESS', None)
            if override:
                env['META_TAGGER_REPOSITORY_BIND_ADDRESS'] = address
            result = subprocess.run(
                ['bash', str(scripts / 'test-release-lifecycle.sh'), '', *ports],
                env=env, capture_output=True, text=True, timeout=15,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn('PASS both catalogs and ZIPs reachable', result.stdout)
            for port in ports:
                with socket.socket(family) as probe:
                    probe.settimeout(.2)
                    self.assertNotEqual(0, probe.connect_ex((address, int(port))))

    def test_default_catalogs_stay_on_loopback(self):
        self.run_repositories('127.0.0.1', override=False)

    def test_catalogs_and_packages_use_configured_bind_address(self):
        self.run_repositories('::1', override=True)


if __name__ == '__main__':
    unittest.main()
