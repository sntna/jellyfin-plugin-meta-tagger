"""Exercise the lifecycle shell script's real repository HTTP servers.

Only package compilation, Jellyfin, and the published release download host are
replaced with fixtures. The client uses IPv6 loopback to verify that an explicit
bind address replaces IPv4 loopback. No test reaches the network.
"""
import functools
import hashlib
import http.server
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import textwrap
import threading
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PLUGIN_GUID = "4851185f-7284-4cad-9eeb-2c73576bb214"

spec = importlib.util.spec_from_file_location('fetch_released_catalog', ROOT / 'scripts/fetch_released_catalog.py')
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


def write_published_release(directory, version='0.1.0.0', tamper=False):
    """Write the assets a published release would carry, with its SHA256SUMS."""
    directory.mkdir(parents=True, exist_ok=True)
    image = b'fixture-image'
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as package:
        package.writestr('Jellyfin.Plugin.MetaTagger.dll', b'fixture-assembly')
        package.writestr('meta-tagger.png', image)
    package_bytes = buffer.getvalue()
    package_name = f'meta-tagger_{version}.zip'
    manifest = json.dumps([{
        'guid': PLUGIN_GUID, 'name': 'Meta Tagger',
        'versions': [{'version': version, 'targetAbi': '12.0.0.0',
                      'sourceUrl': f'https://example.invalid/{package_name}',
                      'checksum': hashlib.md5(package_bytes).hexdigest()}],
        'imageUrl': 'https://example.invalid/meta-tagger.png',
    }], indent=2) + '\n'
    files = {'manifest.json': manifest.encode(), package_name: package_bytes,
             'meta-tagger.png': image}
    (directory / 'SHA256SUMS').write_text(
        ''.join(f'{hashlib.sha256(content).hexdigest()}  {name}\n' for name, content in files.items()))
    if tamper:
        files[package_name] = package_bytes + b'tampered'
    for name, content in files.items():
        (directory / name).write_bytes(content)
    return package_name, files


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def serve_directory(test, directory):
    """Serve a fixture release over loopback for the duration of one test."""
    handler = functools.partial(QuietHandler, directory=str(directory))
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    test.addCleanup(thread.join)
    test.addCleanup(server.shutdown)
    test.addCleanup(server.server_close)
    return f'http://127.0.0.1:{server.server_address[1]}'


def closed_loopback_url():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return f'http://127.0.0.1:{probe.getsockname()[1]}'


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


class PublishedPreviousReleaseTests(unittest.TestCase):
    """The opt-in switch upgrades over a published release, or skips cleanly."""

    def run_lifecycle(self, download_base, tag='v0.1.0', project_version='0.2.0',
                      seed_downloads=False):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        scripts = root / 'scripts'
        scripts.mkdir()
        for name in ['test-release-lifecycle.sh', 'fetch_released_catalog.py']:
            shutil.copyfile(ROOT / 'scripts' / name, scripts / name)
        (scripts / 'package_release.py').write_text(textwrap.dedent('''\
            import argparse
            import json
            import os
            from pathlib import Path

            def _project_versions():
                version = os.environ.get('TEST_PROJECT_VERSION', '0.2.0')
                return version, version + '.0'

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
            import hashlib
            import json
            import os
            import pathlib
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

            manifest = json.loads(fetch(sys.argv[sys.argv.index('--previous-manifest-url') + 1]))
            entry = manifest[0] if isinstance(manifest, list) else manifest
            version = entry['versions'][0] if 'versions' in entry else entry
            pathlib.Path(os.environ['TEST_OBSERVED_PATH']).write_text(json.dumps({
                'argv': sys.argv[1:],
                'previousVersion': version.get('version'),
                'previousPackageSha256': hashlib.sha256(fetch(version['sourceUrl'])).hexdigest(),
            }))
            print('PASS stub lifecycle reached the previous catalog')
            '''))
        ports = []
        with socket.socket() as current, socket.socket() as previous:
            for listener in [current, previous]:
                listener.bind(('127.0.0.1', 0))
                ports.append(str(listener.getsockname()[1]))
        sentinel = root / '.jellyfin-test/sentinel.txt'
        sentinel.parent.mkdir(parents=True, exist_ok=True)
        sentinel.write_text('keep')
        if seed_downloads:
            # A previous opt-in run leaves this behind, which is what lets a
            # traversal tag resolve through it to a path outside.
            (root / '.jellyfin-test/release-downloads').mkdir()
        observed_path = root / 'observed.json'
        env = dict(os.environ, TEST_CATALOG_ADDRESS='127.0.0.1', TEST_OBSERVED_PATH=str(observed_path),
                   TEST_PROJECT_VERSION=project_version,
                   META_TAGGER_PREVIOUS_RELEASE_TAG=tag,
                   META_TAGGER_RELEASE_DOWNLOAD_BASE=download_base, no_proxy='*')
        env.pop('META_TAGGER_REPOSITORY_BIND_ADDRESS', None)
        result = subprocess.run(['bash', str(scripts / 'test-release-lifecycle.sh'), '', *ports],
                                env=env, capture_output=True, text=True, timeout=60)
        observed = json.loads(observed_path.read_text()) if observed_path.is_file() else None
        return result, root, observed

    def test_published_release_is_verified_downloaded_and_served_as_the_previous_catalog(self):
        release = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, release)
        package_name, files = write_published_release(release / 'v0.1.0')
        base = serve_directory(self, release)
        result, root, observed = self.run_lifecycle(base + '/v0.1.0')

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        digest = hashlib.sha256(files[package_name]).hexdigest()
        self.assertIn(f'Verified published v0.1.0 package {package_name} sha256 {digest}', result.stdout)
        self.assertEqual('0.1.0.0', observed['previousVersion'])
        self.assertEqual(digest, observed['previousPackageSha256'])
        self.assertIn('--previous-release-tag', observed['argv'])
        self.assertEqual('v0.1.0', observed['argv'][observed['argv'].index('--previous-release-tag') + 1])
        self.assertEqual('0.1.0.0', observed['argv'][observed['argv'].index('--previous-version') + 1])
        # Downloads stay in their own disposable directory and never replace the fixture path.
        self.assertTrue((root / '.jellyfin-test/release-downloads/v0.1.0' / package_name).is_file())
        self.assertFalse((root / '.jellyfin-test/repository-previous').exists())

    def test_unavailable_release_skips_and_keeps_the_synthetic_fixture_upgrade(self):
        result, root, observed = self.run_lifecycle(closed_loopback_url() + '/v0.1.0')

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('SKIP upgrade from published v0.1.0', result.stderr)
        self.assertNotIn('--previous-release-tag', observed['argv'])
        self.assertEqual('0.0.0.0', observed['argv'][observed['argv'].index('--previous-version') + 1])
        self.assertEqual(hashlib.sha256(b'fixture-package').hexdigest(), observed['previousPackageSha256'])
        self.assertTrue((root / '.jellyfin-test/repository-previous/manifest.json').is_file())

    def test_tag_outside_the_version_form_is_refused_before_anything_is_removed(self):
        # The download directory is removed by name, so a tag that walks out of it
        # deletes elsewhere once an earlier run has created it. Check the tag first.
        result, root, observed = self.run_lifecycle(closed_loopback_url(),
                                                    tag='../sentinel.txt', seed_downloads=True)

        self.assertTrue((root / '.jellyfin-test/sentinel.txt').is_file(),
                        'the tag reached a path outside the download directory')
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('must use the vX.Y.Z form', result.stderr)
        self.assertIsNone(observed)

    def test_tag_holding_the_tree_version_is_refused_as_an_upgrade(self):
        result, root, observed = self.run_lifecycle(closed_loopback_url(),
                                                    tag='v0.1.0', project_version='0.1.0')

        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('holds this tree version 0.1.0.0', result.stderr)
        self.assertFalse((root / '.jellyfin-test/release-downloads').exists())
        self.assertIsNone(observed)

    def test_release_missing_an_asset_skips_and_keeps_the_synthetic_fixture_upgrade(self):
        release = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, release)
        write_published_release(release / 'v0.1.0')
        sums = release / 'v0.1.0/SHA256SUMS'
        sums.write_text(''.join(line + '\n' for line in sums.read_text().splitlines()
                                if 'meta-tagger_' not in line))
        base = serve_directory(self, release)
        result, root, observed = self.run_lifecycle(base + '/v0.1.0')

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('SKIP upgrade from published v0.1.0', result.stderr)
        self.assertNotIn('--previous-release-tag', observed['argv'])
        self.assertTrue((root / '.jellyfin-test/repository-previous/manifest.json').is_file())

    def test_tampered_release_package_stops_the_lifecycle_test(self):
        release = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, release)
        write_published_release(release / 'v0.1.0', tamper=True)
        base = serve_directory(self, release)
        result, _, observed = self.run_lifecycle(base + '/v0.1.0')

        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn('failed verification', result.stderr)
        self.assertIsNone(observed)


class ReleaseCatalogVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / 'catalog'

    def fetch(self, download_base, **overrides):
        return catalog.fetch_released_catalog(
            tag=overrides.pop('tag', 'v0.1.0'), download_base=download_base,
            output_dir=self.output, source_url_base='http://host.docker.internal:8099',
            timeout=2, **overrides)

    def test_verified_catalog_points_the_container_at_the_downloaded_package(self):
        package_name, files = write_published_release(self.root / 'release')
        result = self.fetch(serve_directory(self, self.root / 'release'))

        self.assertEqual('0.1.0.0', result['version'])
        self.assertEqual(files[package_name], (self.output / package_name).read_bytes())
        manifest = json.loads((self.output / 'manifest.json').read_text())
        self.assertEqual(f'http://host.docker.internal:8099/{package_name}',
                         manifest[0]['versions'][0]['sourceUrl'])
        self.assertEqual('http://host.docker.internal:8099/meta-tagger.png', manifest[0]['imageUrl'])
        self.assertEqual(files['meta-tagger.png'], (self.output / 'meta-tagger.png').read_bytes())
        self.assertEqual((self.root / 'release/SHA256SUMS').read_text(),
                         (self.output / 'SHA256SUMS').read_text())

    def test_package_that_does_not_match_published_checksums_is_rejected(self):
        write_published_release(self.root / 'release', tamper=True)
        base = serve_directory(self, self.root / 'release')

        with self.assertRaisesRegex(ValueError, 'does not match its published SHA256SUMS'):
            self.fetch(base)
        self.assertFalse(self.output.exists())

    def test_asset_outside_the_published_checksums_is_unavailable_not_a_failure(self):
        write_published_release(self.root / 'release')
        sums = self.root / 'release/SHA256SUMS'
        sums.write_text(''.join(line + '\n' for line in sums.read_text().splitlines()
                                if 'meta-tagger_' not in line))
        base = serve_directory(self, self.root / 'release')

        # Unverifiable, so unusable, but an old release may simply not carry it.
        with self.assertRaisesRegex(catalog.ReleaseUnavailable,
                                    'does not cover meta-tagger_0.1.0.0.zip'):
            self.fetch(base)
        self.assertFalse(self.output.exists())

    def test_unreachable_release_is_reported_as_unavailable(self):
        with self.assertRaises(catalog.ReleaseUnavailable):
            self.fetch(closed_loopback_url())


if __name__ == '__main__':
    unittest.main()
