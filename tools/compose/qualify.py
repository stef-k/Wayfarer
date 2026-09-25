"""Disposable integration of the real Compose substrate; requires Docker, Python3 and curl."""
import argparse
import json
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import uuid
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / 'deploy/compose'
TRIP = '64400000-0000-0000-0000-000000000001'


def run(*args, data=None, check=True):
    """Capture diagnostics without echoing protected stdin or secret contents."""
    result = subprocess.run(args, input=data, capture_output=True, text=True)
    if check and result.returncode:
        raise RuntimeError(f'{args[0]} failed ({result.returncode}): {result.stderr[-3000:]}')
    return result


class Stack:
    """Own exactly one random Compose project and its temporary qualification files."""
    def __init__(self, directory, image):
        self.directory = Path(directory)
        self.project = 'wayfarer-644-' + uuid.uuid4().hex[:10]
        self.image = image
        self.env = self.directory / 'deployment.env'
        self.override = self.directory / 'test.yaml'
        self.password = secrets.token_hex(24) + '!aA9'
        self.mode = 'managed'
        self.port = None

    def compose(self, *args, data=None, check=True):
        """Use production preflight; only tests override image identity and public TLS/ports."""
        return run(str(BUNDLE / 'compose.sh'), str(self.env), '-p', self.project,
                   '-f', str(self.override), *args, data=data, check=check)

    def sql(self, sql, database='wayfarer'):
        """Administer only the task-owned DB over its local Unix socket."""
        return self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', database,
                            '-At', '-v', 'ON_ERROR_STOP=1', data=sql).stdout.strip()

    def prepare(self):
        """Create disposable secrets, map the local built image, and retain production networking."""
        app_password = secrets.token_hex(32)
        for name, value in [('db-password', secrets.token_hex(32)), ('app-password', app_password),
                            ('db-app-password', app_password)]:
            path = self.directory / name
            path.write_text(value)
            path.chmod(0o600)
        # Ownership is real, not Compose uid/gid metadata (which cannot remap bind mounts).
        run('docker', 'run', '--rm', '--user', '0', '--network', 'none', '--entrypoint', 'sh',
            '-v', f'{self.directory}:/qualification', self.image, '-ec',
            'chown 1654:1654 /qualification/app-password; chown 70:70 /qualification/db-app-password')
        self.write_env()
        caddy = self.directory / 'Caddyfile'
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            self.port = listener.getsockname()[1]
        caddy.write_text('{\n skip_install_trust\n https_port ' + str(self.port) + '\n}\n' + (BUNDLE / 'caddy/Caddyfile').read_text().replace(
            '{$PUBLIC_HOST} {', '{$PUBLIC_HOST} {\n\ttls internal'))
        # !override replaces production listeners rather than appending public test ports.
        self.override.write_text('services:\n  wayfarer:\n    image: ' + self.image +
            '\n    pull_policy: never\n  caddy:\n    ports: !override\n'
            f'      - "127.0.0.1:{self.port}:{self.port}"\n'
            '    networks:\n      edge:\n        aliases: [wayfarer.example.org]\n'
            f'    volumes:\n      - {caddy}:/etc/caddy/Caddyfile:ro\n')

    def write_env(self):
        """Keep secrets outside interpolation and select a bounded host loopback endpoint."""
        self.env.write_text(f'PUBLIC_HOST=wayfarer.example.org\nWAYFARER_DIGEST=sha256:{"0" * 64}\n'
            f'PROXY_MODE={self.mode}\nEDGE_PREFIX=172.30.65\n'
            f'DB_PASSWORD_FILE={self.directory}/db-password\n'
            f'APP_PASSWORD_FILE={self.directory}/app-password\n'
            f'DB_APP_PASSWORD_FILE={self.directory}/db-app-password\n'
            'EXTERNAL_PROXY_ADDRESS=172.30.65.1\nLOOPBACK_ADDRESS=127.0.0.1\nLOOPBACK_PORT=18464\n')

    def config_checks(self):
        """Check both production topologies and reject malformed deployment inputs before launch."""
        original = self.env.read_text()
        for mode in ['managed', 'external']:
            self.mode = mode
            self.write_env()
            config = json.loads(run(str(BUNDLE / 'compose.sh'), str(self.env), 'config', '--format', 'json').stdout)
            services = config['services']
            assert not services['db'].get('ports')
            assert set(services['db']['networks']) == {'backend'}
            assert services['wayfarer']['read_only']
            if mode == 'managed':
                assert not services['wayfarer'].get('ports')
                assert set(services['caddy']['networks']) == {'edge'}
                assert not services['caddy'].get('secrets')
            else:
                assert 'caddy' not in services
                assert services['wayfarer']['ports'][0]['host_ip'] == '127.0.0.1'
        for old, new in [('sha256:' + '0' * 64, 'latest'), ('wayfarer.example.org', 'https://bad/path'),
                         ('DB_PASSWORD_FILE=', 'UNKNOWN=')]:
            self.env.write_text(original.replace(old, new))
            assert run(str(BUNDLE / 'compose.sh'), str(self.env), 'config', check=False).returncode != 0
        self.mode = 'managed'
        self.env.write_text(original)

    def initialize(self):
        """Explicitly prepare writable volumes, then migrate, seed and protected bootstrap."""
        self.compose('up', '-d', '--wait', '--wait-timeout', '120', 'db')
        self.compose('run', '--rm', '--no-deps', '--user', '0', '--entrypoint', 'sh', 'wayfarer', '-ec',
            'chown 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer; '
            'chmod 700 /var/lib/wayfarer; chmod 750 /var/cache/wayfarer /var/log/wayfarer')
        assert self.sql("SELECT current_setting('server_version'), postgis_lib_version();") == '17.11|3.5.7'
        assert self.sql("SELECT 'Α'::citext = 'α'::citext, lower('É');") == 't|é'
        assert self.sql("SELECT rolsuper FROM pg_roles WHERE rolname='wayfarer';") == 'f'
        for command in [('database', 'migrate'), ('database', 'seed'), ('database', 'seed')]:
            self.compose('run', '--rm', '-T', 'wayfarer', *command)
        self.compose('run', '--rm', '-T', 'wayfarer', 'admin', 'bootstrap', 'compose-admin', '--stdin',
                     data=self.password + '\n')
        self.sql(f'''INSERT INTO "Trips" ("Id","UserId","Name","IsPublic","ShareProgressEnabled","UpdatedAt","CenterLat","CenterLon","Zoom")
            SELECT '{TRIP}',"Id",'Compose qualification',true,false,now(),37.9,23.7,3 FROM "AspNetUsers" WHERE "UserName"='compose-admin';''')
        self.sql('UPDATE "ApplicationSettings" SET "ProxyImageRateLimitEnabled"=true, "ProxyImageRateLimitPerMinute"=1;')
        self.compose('up', '-d', '--wait', '--wait-timeout', '180')
        self.connect()
        assert self.compose('exec', '-T', 'caddy', 'caddy', 'version').stdout.startswith('v2.11.4 ')
        for service in ['db', 'caddy']:
            info = json.loads(run('docker', 'inspect', self.container(service)).stdout)[0]
            image_info = json.loads(run('docker', 'image', 'inspect', info['Image']).stdout)[0]
            assert image_info['Os'] == 'linux' and image_info['Architecture'] == 'amd64'
            print(service + ': ' + info['Config']['Image'], flush=True)

    def container(self, service):
        """Resolve by Compose service identity; never depend on generated names."""
        return self.compose('ps', '-q', service).stdout.strip()

    def connect(self):
        """Trust only this disposable Caddy's generated local CA for HTTPS probes."""
        container = self.container('caddy')
        inspected = json.loads(run('docker', 'inspect', container).stdout)[0]
        self.port = next(binding[0]['HostPort'] for binding in inspected['NetworkSettings']['Ports'].values() if binding)
        for _ in range(30):
            copied = run('docker', 'cp', f'{container}:/data/caddy/pki/authorities/local/root.crt',
                         str(self.directory / 'ca.crt'), check=False)
            if copied.returncode == 0:
                return
            time.sleep(1)
        raise RuntimeError('Caddy local CA did not become available')

    def curl(self, path, *args, timeout=120, check=True):
        """Exercise the real TLS endpoint with CA validation and explicit disposable DNS."""
        return run('curl', '--silent', '--show-error', '--fail', '--noproxy', '*',
                   '--cacert', str(self.directory / 'ca.crt'), '--resolve',
                   f'wayfarer.example.org:{self.port}:127.0.0.1', '--max-time', str(timeout),
                   *args, f'https://wayfarer.example.org:{self.port}{path}', check=check)

    def functional(self):
        """Prove real public/static/export/SSE/browser routes through the managed proxy."""
        assert self.curl('/health/ready').stdout == 'ready'
        page = self.curl('/Public/Trips', '-H', 'X-Forwarded-Host: spoof.invalid',
                         '-H', 'X-Forwarded-Proto: http', '-H', 'X-Forwarded-For: 203.0.113.99').stdout
        assert f'https://wayfarer.example.org:{self.port}/Public/Trips/{TRIP}' in page
        assert self.curl('/css/site.css').stdout
        assert '<kml' in self.curl(f'/Trip/ExportWayfarerKml/{TRIP}').stdout
        stream = self.curl(f'/Trip/ExportProgress/{TRIP}?sessionId=compose', timeout=23, check=False)
        assert stream.returncode == 28 and ':\n\n' in stream.stdout, stream.stdout
        self.curl(f'/Public/Trips/{TRIP}/MapSnapshot', '-o', str(self.directory / 'map.jpg'))
        assert (self.directory / 'map.jpg').read_bytes().startswith(b'\xff\xd8')
        self.curl(f'/Trip/ExportPdf/{TRIP}', '-o', str(self.directory / 'trip.pdf'))
        assert (self.directory / 'trip.pdf').read_bytes().startswith(b'%PDF')
        # Different forged client IPs must not escape the same real-client bucket.
        for spoof in ['203.0.113.98', '203.0.113.99']:
            response = self.curl(f'/Public/Trips/{TRIP}/MapSnapshot', '-H', f'X-Forwarded-For: {spoof}',
                                 '-o', '/dev/null', '-w', '%{http_code}', check=False)
            assert response.stdout == '429'
        logs = run('docker', 'logs', self.container('wayfarer')).stdout
        assert 'Map snapshot rate limit exceeded for IP: 172.30.65.1' in logs
        self.authenticate()
        print('Managed TLS/page/static/spoofed-host/export/SSE/thumbnail/PDF passed', flush=True)

    def authenticate(self):
        """Use the real antiforgery login and retain its encrypted cookie across replacement."""
        cookies = str(self.directory / 'cookies')
        page = self.curl('/Identity/Account/Login', '-c', cookies).stdout
        token = re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', page).group(1)
        body = self.directory / 'login-body'
        body.write_text(urlencode({'Input.Username': 'compose-admin', 'Input.Password': self.password,
                                   '__RequestVerificationToken': token}))
        body.chmod(0o600)
        try:
            self.curl('/Identity/Account/Login', '-b', cookies, '-c', cookies,
                      '-H', 'Content-Type: application/x-www-form-urlencoded', '--data-binary', '@' + str(body))
        finally:
            body.unlink()
        assert self.curl('/Admin/Users', '-b', cookies, '-o', '/dev/null', '-w', '%{http_code}').stdout == '200'

    def identities(self):
        """Capture durable/key/upload identity separately from rebuildable cache and operational logs."""
        files = self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
            r'find /var/lib/wayfarer -type f -exec sha256sum {} \; | sort').stdout
        assert 'key-' in files and 'qualification-upload' in files
        return files, self.sql(f'''SELECT "Id","Name" FROM "Trips" WHERE "Id"='{TRIP}';''')

    def persistence(self):
        """Replace all state-owning containers; additionally prove a logical DB dump/restore."""
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
            'mkdir -p /var/lib/wayfarer/uploads/imports; '
            'printf durable > /var/lib/wayfarer/uploads/imports/qualification-upload; '
            'test -n "$(find /var/cache/wayfarer -type f -name "*.jpg")"; '
            'test -n "$(find /var/log/wayfarer -type f)"')
        before = self.identities()
        tls = run('docker', 'exec', self.container('caddy'), 'sh', '-ec',
                  r'find /data -type f -exec sha256sum {} \; | sort').stdout
        self.compose('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '180', 'wayfarer', 'caddy')
        self.connect()
        assert self.identities() == before
        assert tls == run('docker', 'exec', self.container('caddy'), 'sh', '-ec',
                          r'find /data -type f -exec sha256sum {} \; | sort').stdout
        self.compose('stop', 'caddy', 'wayfarer')
        self.compose('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120', 'db')
        self.compose('up', '-d', '--wait', '--wait-timeout', '180')
        self.connect()
        assert self.identities() == before
        assert self.curl('/Admin/Users', '-b', str(self.directory / 'cookies'),
                         '-o', '/dev/null', '-w', '%{http_code}').stdout == '200'
        # Backup tools run inside the selected PG17 image; restore into another disposable DB.
        self.compose('exec', '-T', 'db', 'sh', '-ec',
            'pg_dump -U postgres -Fc wayfarer > /tmp/qualification.dump; '
            'createdb -U postgres restored; pg_restore -U postgres --exit-on-error -d restored /tmp/qualification.dump; '
            'rm /tmp/qualification.dump')
        assert self.sql(f'''SELECT "Name" FROM "Trips" WHERE "Id"='{TRIP}';''', 'restored') == 'Compose qualification'
        self.sql('DROP DATABASE restored;', 'postgres')
        print('DB/key ring/cookie/durable upload/TLS persistence and logical restore passed', flush=True)

    def exposure(self):
        """Assert actual runtime mounts, privilege, publication and network boundaries."""
        for service in ['db', 'wayfarer', 'caddy']:
            cid = self.container(service)
            info = json.loads(run('docker', 'inspect', cid).stdout)[0]
            assert not info['HostConfig']['Privileged']
            assert all(m['Destination'] != '/var/run/docker.sock' for m in info['Mounts'])
            if service != 'caddy':
                assert not any(info['NetworkSettings']['Ports'].values())
            else:
                assert len(info['NetworkSettings']['Networks']) == 1
                assert not any(m['Destination'].startswith('/run/secrets') for m in info['Mounts'])
            if service == 'wayfarer':
                assert info['HostConfig']['ReadonlyRootfs']
                assert self.compose('exec', '-T', service, 'id', '-u').stdout.strip() == '1654'
                diff = run('docker', 'diff', cid).stdout
                assert set(diff.splitlines()) <= {'C /run', 'A /run/secrets', 'A /run/secrets/app-password',
                    'C /usr', 'C /usr/sbin', 'A /usr/sbin/docker-init'}, diff
        print('Managed network/mount/non-root/read-only exposure assertions passed', flush=True)

    def external(self):
        """Switch modes and qualify a real host-native proxy against the loopback endpoint."""
        run('docker', 'cp', self.container('caddy') + ':/usr/bin/caddy', str(self.directory / 'caddy'))
        self.compose('stop', 'caddy', 'wayfarer')
        self.compose('rm', '-f', 'caddy')
        self.mode = 'external'
        self.write_env()
        self.compose('up', '-d', '--wait', '--wait-timeout', '180')
        info = json.loads(run('docker', 'inspect', self.container('wayfarer')).stdout)[0]
        assert info['NetworkSettings']['Ports']['8080/tcp'][0]['HostIp'] == '127.0.0.1'
        assert not self.container('caddy')
        # Run the pinned Caddy binary as a separate, unprivileged host-native proxy.
        # This proves the real loopback/NAT hop rather than simulating headers with curl.
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            self.port = listener.getsockname()[1]
        config = self.directory / 'external.Caddyfile'
        config.write_text('{\n admin off\n skip_install_trust\n auto_https disable_redirects\n}\n'
            f'https://wayfarer.example.org:{self.port} {{\n bind 127.0.0.1\n tls internal\n'
            ' reverse_proxy 127.0.0.1:18464 {\n flush_interval -1\n }\n}\n')
        with (self.directory / 'external.log').open('w') as log:
            process = subprocess.Popen([str(self.directory / 'caddy'), 'run', '--config', str(config),
                '--adapter', 'caddyfile'], stdout=log, stderr=log,
                env={'HOME': str(self.directory), 'XDG_DATA_HOME': str(self.directory / 'external-data'),
                     'XDG_CONFIG_HOME': str(self.directory / 'external-config')})
            try:
                ca = self.directory / 'external-data/caddy/pki/authorities/local/root.crt'
                for _ in range(30):
                    if ca.exists():
                        (self.directory / 'ca.crt').write_bytes(ca.read_bytes())
                        break
                    assert process.poll() is None, 'External proxy exited during startup'
                    time.sleep(1)
                assert self.curl('/health/ready').stdout == 'ready'
                page = self.curl('/Public/Trips', '-H', 'X-Forwarded-Proto: http',
                                 '-H', 'X-Forwarded-Host: spoof.invalid').stdout
                assert f'https://wayfarer.example.org:{self.port}/Public/Trips/{TRIP}' in page
                assert '<kml' in self.curl(f'/Trip/ExportWayfarerKml/{TRIP}').stdout
                assert self.identities()[1].endswith('Compose qualification')
            finally:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                    raise
        print('External host-native TLS proxy/loopback/forwarding/persistence passed', flush=True)

    def cleanup(self):
        """Remove only resources labelled for this random project; no down -v or global prune."""
        self.mode = 'managed'
        self.write_env()
        self.compose('down', '--remove-orphans', check=False)
        for kind in ['container', 'network', 'volume']:
            ids = run('docker', kind, 'ls', '-q', '--filter', f'label=com.docker.compose.project={self.project}').stdout.split()
            if ids:
                run('docker', kind, 'rm', *ids)


def main():
    """Run the bounded integration; retain failure logs without printing secret values."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', required=True, help='locally built image ID from image dry-run')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='wayfarer-compose-') as directory:
        stack = Stack(directory, args.image)
        try:
            stack.prepare()
            stack.config_checks()
            stack.initialize()
            stack.functional()
            stack.exposure()
            stack.persistence()
            stack.external()
        except Exception:
            # Logs may include operator content; keep captured material local, never dump secrets to CI.
            print(stack.compose('ps', check=False).stdout)
            raise
        finally:
            stack.cleanup()


if __name__ == '__main__':
    main()
