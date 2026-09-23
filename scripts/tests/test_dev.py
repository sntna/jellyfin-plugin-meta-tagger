"""Exercise the loopback proxy against a disposable HTTP upstream, without Docker."""
import http.server
import importlib.util
import json
import pathlib
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('dev', pathlib.Path(__file__).parents[1] / 'dev.py')
dev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dev)


class ProxyTests(unittest.TestCase):
    def setUp(self):
        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                denied = self.headers.get('Authorization') != 'fixture' and 'ConfigurationPage' in self.path
                self.send_response(401 if denied else 200)
                self.send_header('Content-Type', 'text/html')
                self.end_headers()
                self.wfile.write(b'denied' if denied else b'<body>installed copy</body>')
        self.upstream = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
        self.proxy = http.server.ThreadingHTTPServer(('127.0.0.1', 0),
            dev.handler_for(f'http://127.0.0.1:{self.upstream.server_port}', dev.State()))
        for server in [self.upstream, self.proxy]:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            self.addCleanup(server.server_close)
            self.addCleanup(server.shutdown)
        self.base = f'http://127.0.0.1:{self.proxy.server_port}'

    def get(self, path, headers=None):
        return urllib.request.urlopen(urllib.request.Request(self.base + path, headers=headers or {}))

    def test_source_edits_are_served_without_build_and_still_require_upstream_authorization(self):
        with tempfile.TemporaryDirectory() as directory:
            page = pathlib.Path(directory) / 'page.html'
            with patch.object(dev, 'PAGE', page):
                for text in ['first edit', 'second edit']:
                    page.write_text(text)
                    with self.get('/web/ConfigurationPage?name=Meta%20Tagger', {'Authorization': 'fixture'}) as response:
                        self.assertEqual(text.encode(), response.read())
                        self.assertEqual('no-store', response.headers['Cache-Control'])
                with self.assertRaises(urllib.error.HTTPError) as error:
                    self.get('/web/ConfigurationPage?name=Meta%20Tagger')
                self.assertEqual(401, error.exception.code)

    def test_only_host_shell_gets_dev_client_and_status_is_available(self):
        with self.get('/web/index.html') as response:
            self.assertIn(b'/__meta_tagger_dev/client.js', response.read())
        with self.get('/__meta_tagger_dev/status') as response:
            self.assertIn('revision', json.load(response))
        with self.get('/web/ConfigurationPage?name=Meta%20Tagger', {'Authorization': 'fixture'}) as response:
            self.assertNotIn(b'/__meta_tagger_dev/client.js', response.read())

    def test_rebinding_host_is_rejected(self):
        with self.assertRaises(urllib.error.HTTPError) as error:
            self.get('/__meta_tagger_dev/status', {'Host': 'untrusted.example'})
        self.assertEqual(403, error.exception.code)


class WatchTests(unittest.TestCase):
    def test_catalog_image_changes_trigger_backend_install(self):
        self.assertIn(dev.ROOT / 'docs/brand/assets/plugin.png', dev.backend_files())

    def test_rebuild_uses_build_entry_point_before_install(self):
        with patch.object(dev.subprocess, 'run') as run, patch.object(dev.fixture, 'install') as install:
            dev.rebuild('fixture')
        run.assert_called_once_with([str(dev.ROOT / 'scripts/build.sh')], cwd=dev.ROOT, check=True)
        self.assertEqual('fixture', install.call_args.args[0].directory)
        self.assertEqual(dev.PROJECT / 'bin/Release/net10.0/Jellyfin.Plugin.MetaTagger.dll',
                         install.call_args.args[0].dll)

    def test_dashboard_only_edits_do_not_restart_backend(self):
        stop = threading.Event()
        state = dev.State()
        calls = []
        versions = iter(['ui1', 'cs1', 'ui1', 'cs1', 'ui2', 'cs1', 'ui2', 'cs1'])
        def fingerprint(_):
            try:
                return next(versions)
            except StopIteration:
                stop.set()
                return 'done'
        with patch.object(dev, 'fingerprint', fingerprint), patch.object(dev, 'backend_files', return_value=[]):
            dev.watch(state, 'fixture', stop, build=lambda directory: calls.append(directory), interval=.001)
        self.assertEqual(['fixture'], calls)

    def test_failed_backend_build_is_retried_on_next_edit(self):
        stop = threading.Event()
        calls = []
        versions = iter(['ui1', 'cs1', 'ui1', 'cs1', 'ui2', 'cs1', 'ui2', 'cs1'])
        def fingerprint(_):
            try:
                return next(versions)
            except StopIteration:
                stop.set()
                return 'done'
        def build(directory):
            calls.append(directory)
            if len(calls) == 1:
                raise RuntimeError('fixture compile failure')
        with patch.object(dev, 'fingerprint', fingerprint), patch.object(dev, 'backend_files', return_value=[]):
            dev.watch(dev.State(), 'fixture', stop, build=build, interval=.001)
        self.assertEqual(['fixture', 'fixture'], calls)
