#!/usr/bin/env python3
"""Watch Meta Tagger and serve its dashboard from source on a loopback dev proxy."""
import argparse
import hashlib
import http.client
import http.server
import importlib.util
import json
import pathlib
import select
import socket
import subprocess
import threading
import time
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Jellyfin.Plugin.MetaTagger'
PAGE = PROJECT / 'Configuration/configPage.html'
CLIENT = ROOT / 'scripts/dev-client.js'
SPEC = importlib.util.spec_from_file_location('disposable', ROOT / 'scripts/disposable-jellyfin.py')
fixture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fixture)
HOP_HEADERS = {'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
               'te', 'trailer', 'transfer-encoding', 'upgrade', 'content-length'}


def fingerprint(paths):
    digest = hashlib.sha256()
    for path in sorted(paths):
        digest.update(str(path).encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def backend_files():
    return [p for p in PROJECT.rglob('*') if p.is_file()
            and not {'bin', 'obj'}.intersection(p.relative_to(PROJECT).parts)
            and p.suffix in {'.cs', '.csproj', '.props', '.targets'}] + [ROOT / 'global.json', ROOT / 'docs/brand/assets/plugin.png']


class State:
    def __init__(self):
        self.revision = str(time.time_ns())
        self.status = 'Starting development server…'
        self.ready = False

    def changed(self):
        self.revision = str(time.time_ns())

    def payload(self):
        return json.dumps(vars(self)).encode()


def rebuild(directory):
    subprocess.run([str(ROOT / 'scripts/build.sh')], cwd=ROOT, check=True)
    fixture.install(argparse.Namespace(directory=directory,
                    dll=PROJECT / 'bin/Release/net10.0/Jellyfin.Plugin.MetaTagger.dll'))


def watch(state, directory, stop, build=rebuild, interval=.5):
    previous = None
    installed_backend = None
    while not stop.is_set():
        try:
            current = (fingerprint([PAGE, CLIENT]), fingerprint(backend_files()))
            if current != previous:
                # Wait for editor save bursts to settle before reading or compiling.
                if stop.wait(interval):
                    break
                settled = (fingerprint([PAGE, CLIENT]), fingerprint(backend_files()))
                if settled != current:
                    continue
                backend_changed = current[1] != installed_backend
                previous = current
                if backend_changed:
                    state.ready = False
                    state.status = 'Building plugin and restarting disposable Jellyfin…'
                    print(state.status, flush=True)
                    build(directory)
                    installed_backend = current[1]
                state.ready = True
                state.status = 'Watching. Dashboard changes reload directly; C# changes rebuild and restart Jellyfin.'
                state.changed()
                print(state.status, flush=True)
        except (OSError, subprocess.CalledProcessError, RuntimeError, AssertionError) as error:
            state.ready = False
            state.status = 'Build or reload failed. See the dev terminal, fix the problem, and save again.'
            print(f'{state.status}\n{error}', flush=True)
        stop.wait(interval)


def handler_for(upstream, state):
    target = urllib.parse.urlsplit(upstream)
    if target.hostname != '127.0.0.1' or target.scheme != 'http':
        raise ValueError('Development proxy requires the validated loopback test server')

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass  # Never log query strings or authorization headers.

        def reply(self, code, body, content_type):
            self.send_response(code)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            if self.command != 'HEAD':
                self.wfile.write(body)

        def proxy(self):
            # Reject DNS rebinding and absolute-form proxy requests.
            expected = f'127.0.0.1:{self.server.server_port}'
            if self.headers.get('Host') != expected or not self.path.startswith('/') or self.path.startswith('//'):
                self.reply(403, b'Use the printed loopback development URL.', 'text/plain')
                return
            parsed = urllib.parse.urlsplit(self.path)
            if parsed.path == '/__meta_tagger_dev/status':
                self.reply(200, state.payload(), 'application/json')
                return
            if parsed.path == '/__meta_tagger_dev/client.js':
                self.reply(200, CLIENT.read_bytes(), 'text/javascript')
                return
            is_page = parsed.path.lower() == '/web/configurationpage' and urllib.parse.parse_qs(parsed.query).get('name') == ['Meta Tagger']
            connection = http.client.HTTPConnection(target.hostname, target.port, timeout=30)
            try:
                headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP_HEADERS | {'host', 'accept-encoding', 'if-none-match', 'if-modified-since'}}
                headers['Host'] = target.netloc
                headers['Accept-Encoding'] = 'identity'
                if self.headers.get('Upgrade', '').lower() == 'websocket':
                    self.tunnel(headers)
                    return
                body = self.rfile.read(int(self.headers.get('Content-Length', 0))) or None
                connection.request(self.command, self.path, body=body, headers=headers)
                response = connection.getresponse()
                data = response.read()
                # Keep Jellyfin's authentication/authorization decision before serving source.
                if response.status == 200 and is_page:
                    data = PAGE.read_bytes()
                elif response.status == 200 and 'text/html' in response.getheader('Content-Type', ''):
                    data = data.replace(b'</body>', b'<script src="/__meta_tagger_dev/client.js"></script></body>')
                self.send_response(response.status)
                for key, value in response.getheaders():
                    if key.lower() not in HOP_HEADERS | {'cache-control', 'etag', 'last-modified'}:
                        self.send_header(key, value)
                self.send_header('Cache-Control', 'no-store')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                if self.command != 'HEAD':
                    self.wfile.write(data)
            except (OSError, http.client.HTTPException):
                self.reply(503, b'Jellyfin is restarting. The development page will reconnect.', 'text/plain')
            finally:
                connection.close()

        def tunnel(self, headers):
            headers['Connection'] = 'Upgrade'
            headers['Upgrade'] = 'websocket'
            with socket.create_connection((target.hostname, target.port), timeout=30) as remote:
                request = f'{self.command} {self.path} HTTP/1.1\r\n' + ''.join(f'{k}: {v}\r\n' for k, v in headers.items()) + '\r\n'
                remote.sendall(request.encode('latin-1'))
                while True:
                    readable, _, _ = select.select([self.connection, remote], [], [], 30)
                    if not readable:
                        continue
                    for source in readable:
                        data = source.recv(65536)
                        if not data:
                            return
                        (remote if source is self.connection else self.connection).sendall(data)

        do_GET = do_HEAD = do_POST = do_PUT = do_DELETE = do_PATCH = do_OPTIONS = proxy
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8099, help='Loopback development URL port, default 8099')
    args = parser.parse_args()
    state = State()
    # Reserve the port before changing the disposable server; a second watcher fails early.
    server = http.server.ThreadingHTTPServer(('127.0.0.1', args.port), handler_for('http://127.0.0.1:8097', state))
    stop = threading.Event()
    try:
        directory = fixture.create(argparse.Namespace(port=8097, server_version='12.0.0'))
        validated = fixture.Server(directory)
        server.RequestHandlerClass = handler_for(validated.base, state)
        threading.Thread(target=watch, args=(state, directory, stop), daemon=True).start()
        print(f'Development URL: http://127.0.0.1:{args.port}/web/#/configurationpage?name=Meta%20Tagger', flush=True)
        print('Use the generated credentials printed above. Ctrl+C stops watching; Jellyfin remains available.', flush=True)
        server.serve_forever(poll_interval=.25)
    except KeyboardInterrupt:
        print('\nStopped development server.', flush=True)
    finally:
        stop.set()
        server.server_close()


if __name__ == '__main__':
    main()
