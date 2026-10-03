"""Lifecycle tests substitute only Docker and the HTTP readiness check."""
import argparse
import contextlib
import importlib.util
import io
import json
import pathlib
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

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


class InstallationFixtureResetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = pathlib.Path(self.temp.name)
        self.config = self.directory / 'config'
        self.config.mkdir()
        self.server = SimpleNamespace(directory=self.directory,
            meta={'container': 'disposable-fixture', 'image': 'jellyfin/jellyfin:12.0'},
            wait_ready=Mock(), login=Mock())
        self.calls = []

    def docker(self, *args):
        self.calls.append(args)
        if args[0] == 'run':
            self.assertIn('--rm', args)
            self.assertEqual('none', args[args.index('--network') + 1])
            self.assertEqual('0:0', args[args.index('--user') + 1])
            self.assertEqual(f'type=bind,source={self.config},target=/config',
                             args[args.index('--mount') + 1])
            self.assertEqual('/config', args[args.index('--workdir') + 1])
            # Simulate container root access, then execute the actual cleanup shell.
            for path in self.config.rglob('*'):
                if path.is_dir():
                    path.chmod(0o755)
            command = args[args.index('jellyfin/jellyfin:12.0') + 1:]
            subprocess.run(['/bin/sh', *command], cwd=self.config, check=True)
        else:
            self.assertIn(args[0], {'stop', 'start'})

    def test_reset_removes_protected_plugin_evidence_and_preserves_other_files(self):
        task_id = str(module.uuid.UUID(bytes_le=module.hashlib.md5(
            'Jellyfin.Plugin.MetaTagger.ScheduledTagTask'.encode('utf-16le')).digest()))
        removed = ['plugins/configurations/Jellyfin.Plugin.MetaTagger.xml',
                   'plugins/configurations/Jellyfin.Plugin.MetaTagger.xml.backup',
                   f'config/ScheduledTasks/{task_id}.js', f'data/ScheduledTasks/{task_id}.js',
                   'plugins/Jellyfin.Plugin.MetaTagger/meta-tagger-state.json',
                   'plugins/Jellyfin.Plugin.MetaTagger_0.1.0/runs/record.json',
                   'plugins/Meta Tagger_0.1.0/Jellyfin.Plugin.MetaTagger.dll']
        retained = ['plugins/configurations/OtherPlugin.xml',
                    'config/ScheduledTasks/other.js', 'data/ScheduledTasks/other.js',
                    'plugins/OtherPlugin/data.json', 'data/library.db']
        for name in removed + retained:
            path = self.config / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(name)
        directories = [path for path in self.config.rglob('*') if path.is_dir()]
        def restore_permissions():
            for path in directories:
                if path.exists():
                    path.chmod(0o755)
        self.addCleanup(restore_permissions)
        for path in directories:
            path.chmod(0o555)
        with self.assertRaises(PermissionError):
            (self.config / removed[0]).unlink()

        with patch.object(module, 'docker', self.docker):
            module.reset_plugin_installation_fixture(self.server)
            module.reset_plugin_installation_fixture(self.server)

        self.assertTrue(all(not (self.config / name).exists() for name in removed))
        for name in retained:
            self.assertEqual(name, (self.config / name).read_text())
        self.assertEqual(['stop', 'run', 'start'] * 2, [call[0] for call in self.calls])
        self.assertEqual(2, self.server.wait_ready.call_count)
        self.assertEqual(2, self.server.login.call_count)

    def test_reset_restarts_server_when_cleanup_container_fails(self):
        def fail_cleanup(*args):
            self.calls.append(args)
            if args[0] == 'run':
                raise subprocess.CalledProcessError(1, 'docker run')
        with patch.object(module, 'docker', fail_cleanup):
            with self.assertRaises(subprocess.CalledProcessError):
                module.reset_plugin_installation_fixture(self.server)
        self.assertEqual(['stop', 'run', 'start'], [call[0] for call in self.calls])
        self.server.wait_ready.assert_called_once()
        self.server.login.assert_called_once()


if __name__ == '__main__':
    unittest.main()
