"""Qualify final pinned Caddy SSE configuration on disposable loopback HTTP/1.1 (Linux/Docker)."""
import json
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]


def port():
    """Reserve an unused loopback port for the disposable processes."""
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def fetch(url):
    """Read one finite JSON control/diagnostic request."""
    with urllib.request.urlopen(url, timeout=2) as response:
        return json.load(response)


def ready(url, process):
    """Bound process startup polling; failure is qualification infrastructure evidence."""
    until = time.monotonic() + 30
    while time.monotonic() < until:
        if process.poll() is not None:
            raise RuntimeError('Disposable process exited during startup')
        try:
            return fetch(url)
        except (OSError, ValueError):
            time.sleep(0.1)
    raise TimeoutError('Disposable endpoint did not start')


def subscription(upstream, proxy, *, heartbeat_required):
    """Observe one stream, actively close it, and require its MVC cancellation and full retirement."""
    ident = uuid.uuid4().hex
    started = time.monotonic()
    with socket.create_connection(('127.0.0.1', proxy), timeout=5) as connection:
        connection.sendall(f'GET /sse/{ident} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n'.encode())
        connection.settimeout(5)
        data = b''
        while b'data: {"ready":true}\n\n' not in data:
            chunk = connection.recv(4096)
            if not chunk:
                raise RuntimeError('EOF before first event')
            data += chunk
        event = time.monotonic() - started
        assert event < 5, event
        heartbeat = None
        if heartbeat_required:
            connection.settimeout(25)
            while b':\n\n' not in data:
                chunk = connection.recv(4096)
                if not chunk:
                    raise RuntimeError('EOF before heartbeat')
                data += chunk
            heartbeat = time.monotonic() - started
            assert 19 <= heartbeat < 25, heartbeat
        connection.shutdown(socket.SHUT_RDWR)
    closed = time.monotonic()
    state = None
    while time.monotonic() - closed < 3:
        state = fetch(f'http://127.0.0.1:{upstream}/state/{ident}')
        if state['cancelled'] and state['clients'] == 0 and state['channels'] == 0:
            break
        time.sleep(0.01)
    assert state == {'cancelled': True, 'sameToken': True, 'clients': 0, 'channels': 0}, state
    disconnect = time.monotonic() - closed
    return {'subscription': ident, 'event_ms': event * 1000, 'heartbeat_s': heartbeat,
            'disconnect_ms': disconnect * 1000, 'state': state}


def probe(upstream, proxy):
    """Require full first retirement before finite control and a distinct reconnect on the same endpoints."""
    first = subscription(upstream, proxy, heartbeat_required=True)
    print(json.dumps({'first': first}), flush=True)
    assert fetch(f'http://127.0.0.1:{proxy}/normal') == {'normal': True}
    print(json.dumps({'finite_request': 'passed'}), flush=True)
    reconnect = subscription(upstream, proxy, heartbeat_required=False)
    assert reconnect['subscription'] != first['subscription']
    print(json.dumps({'reconnect': reconnect}), flush=True)


def proxy_identity(name):
    """Read bounded Docker process identity so restart/replacement cannot count as reconnect recovery."""
    result = subprocess.run(['docker', 'inspect', '--format',
                             '{{.Id}} {{.State.StartedAt}} {{.RestartCount}} {{.State.Running}}', name],
                            check=True, capture_output=True, text=True)
    identity = result.stdout.strip()
    assert identity.endswith(' 0 true'), identity
    return identity


def main():
    """Reuse production Caddyfile and image pin, replacing only test address/upstream; clean owned resources."""
    bundle = ROOT / 'deploy/compose'
    config = (bundle / 'caddy/Caddyfile').read_text()
    assert 'flush_interval' not in config
    assert 'flush_interval' not in (ROOT / 'tools/compose/qualify.py').read_text()
    image = re.search(r'image: (caddy@sha256:[a-f0-9]+)', (bundle / 'compose.yaml').read_text()).group(1)
    project = ROOT / 'tools/compose/sse-probe/SseProbe.csproj'
    subprocess.run(['dotnet', 'build', str(project), '--nologo'], check=True, capture_output=True)
    upstream, proxy = port(), port()
    name = 'wayfarer-sse-' + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix='wayfarer-sse-') as directory:
        caddyfile = Path(directory) / 'Caddyfile'
        caddyfile.write_text(config.replace('{$PUBLIC_HOST}', f'http://127.0.0.1:{proxy}')
                            .replace('wayfarer:8080', f'127.0.0.1:{upstream}'))
        with (Path(directory) / 'host.log').open('w+') as log:
            host = subprocess.Popen(['dotnet', str(project.parent / 'bin/Debug/net10.0/SseProbe.dll'),
                                     '--urls', f'http://127.0.0.1:{upstream}'], stdout=log, stderr=log)
            try:
                ready(f'http://127.0.0.1:{upstream}/normal', host)
                subprocess.run(['docker', 'run', '-d', '--rm', '--name', name, '--network', 'host',
                                '--platform', 'linux/amd64', '-v', f'{caddyfile}:/etc/caddy/Caddyfile:ro',
                                image], check=True, capture_output=True)
                ready(f'http://127.0.0.1:{proxy}/normal', host)
                print('Caddy image:', image, flush=True)
                identity = proxy_identity(name)
                probe(upstream, proxy)
                assert host.poll() is None, 'Upstream exited during qualification'
                assert proxy_identity(name) == identity, 'Caddy restarted/replaced during qualification'
                print(json.dumps({'upstream_pid': host.pid, 'caddy_identity': identity,
                                  'same_proxy_upstream': True}), flush=True)
            except Exception:
                log.seek(0)
                print(log.read())
                raise
            finally:
                subprocess.run(['docker', 'rm', '-f', name], capture_output=True)
                host.terminate()
                try:
                    host.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    host.kill()
                    host.wait()


if __name__ == '__main__':
    main()
