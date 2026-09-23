"""Lifecycle tests substitute only Docker and the HTTP readiness check."""
import argparse
import contextlib
import importlib.util
import io
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('disposable', pathlib.Path(__file__).parents[1] / 'disposable-jellyfin.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReusableServerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name).resolve()
        self.directory = self.root / '.jellyfin-test' / 'generated'
        self.directory.mkdir(parents=True)
        self.meta = {'purpose': module.LABEL, 'id': 'fixture',
                     'container': 'jellyfin-plugin-meta-tagger', 'url': 'http://127.0.0.1:8097',
                     'serverVersion': '12.0.0'}
        (self.directory / 'disposable.json').write_text(json.dumps(self.meta))
        self.inspected = {'Config': {'Labels': {module.LABEL: 'fixture'}},
                          'HostConfig': {'PortBindings': {'8096/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '8097'}]}},
                          'Mounts': [{'Source': str(self.directory / 'config'), 'Destination': '/config'}]}
        self.calls = []

    def docker(self, *args):
        self.calls.append(args)
        if args[0] == 'ps':
            return 'jellyfin-plugin-meta-tagger\n'
        if args[0] == 'inspect':
            return json.dumps([self.inspected])
        if args[0] in {'start', 'update'}:
            return ''
        self.fail('Unexpected Docker mutation: ' + repr(args))

    def create(self, port=8097):
        with patch.object(module, 'ROOT', self.root), patch.object(module, 'docker', self.docker), \
                patch.object(module.Server, 'wait_ready', return_value={'Version': '12.0.0'}), \
                contextlib.redirect_stdout(io.StringIO()):
            return module.create(argparse.Namespace(port=port, server_version='12.0.0'))

    def test_repeated_create_starts_same_server_without_creating_another(self):
        self.assertEqual(self.directory, self.create())
        self.assertEqual(self.directory, self.create())
        self.assertIn(('start', 'jellyfin-plugin-meta-tagger'), self.calls)
        self.assertEqual([self.directory], list((self.root / '.jellyfin-test').iterdir()))

    def test_reuse_rejects_a_different_port_without_modifying_server(self):
        with self.assertRaisesRegex(RuntimeError, '8097'):
            self.create(port=18107)
        self.assertTrue(all(call[0] in {'ps', 'inspect'} for call in self.calls))

    def test_name_collision_with_unlabelled_server_is_never_started(self):
        self.inspected['Config']['Labels'] = {}
        with self.assertRaisesRegex(RuntimeError, 'label'):
            self.create()
        self.assertTrue(all(call[0] in {'ps', 'inspect'} for call in self.calls))


if __name__ == '__main__':
    unittest.main()
