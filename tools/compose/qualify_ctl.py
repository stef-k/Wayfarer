"""Disposable #648 operator journey; test-only TLS and root host-file ownership via Docker.

Requires published self-contained CLI, Docker/Compose, openssl and curl on the test
runner. The target host product requires neither Python nor these test utilities.
"""
import argparse
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import socket
import tempfile
import uuid
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[2]
DB = 'sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61'
# No .NET, Python or Node in this execution host; CLI must supply its own runtime.
HOST = 'ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3'


def run(*args, data=None, check=True):
    """Never echo protected stdin or the credential-bearing authentication body."""
    result = subprocess.run(args, input=data, text=True, capture_output=True)
    if check and result.returncode:
        raise RuntimeError(f'{args[0]} failed ({result.returncode}): {result.stdout[-5000:]} {result.stderr[-1000:]}')
    return result


class Journey:
    """One random project and one temporary directory; production/native state is never selected."""
    def __init__(self, directory, executable, digest):
        self.directory = Path(directory)
        self.bundle = self.directory / 'bundle'
        self.install = self.directory / 'installation'
        self.project = 'wayfarer-648-' + uuid.uuid4().hex[:10]
        self.executable = Path(executable).resolve()
        self.digest = digest
        self.password = secrets.token_hex(32) + '!aA9'
        self.proxy = self.project + '-proxy'
        self.port = self.free_port()
        self.loopback = self.free_port()
        endpoint = run('docker', 'context', 'inspect', '--format', '{{.Endpoints.docker.Host}}').stdout.strip()
        if not endpoint.startswith('unix://'):
            raise RuntimeError('qualification requires a local Unix-socket daemon')
        self.socket = endpoint.removeprefix('unix://')
        self.plugin = next(path for path in [Path('/usr/libexec/docker/cli-plugins'), Path('/usr/lib/docker/cli-plugins')]
                           if (path / 'docker-compose').exists())

    @staticmethod
    def free_port():
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            return listener.getsockname()[1]

    def host(self, *args, data=None, check=True):
        """Host-network fixture checks actual Docker listeners; socket access is test-runner-only."""
        return run('docker', 'run', '--rm', '-i', '--network', 'host',
                   '--add-host', 'wayfarer.example.org:127.0.0.1',
                   '-e', f'SSL_CERT_FILE={self.bundle}/caddy/tls.crt',
                   '-v', f'{self.directory}:{self.directory}',
                   '-v', f'{self.executable}:/ctl/wayfarerctl:ro',
                   '-v', '/usr/bin/docker:/usr/bin/docker:ro',
                   '-v', f'{self.plugin}:/usr/libexec/docker/cli-plugins:ro',
                   '-v', f'{self.socket}:/var/run/docker.sock', HOST, *args, data=data, check=check)

    def ctl(self, *args, data=None, check=True):
        """All ordinary work goes through the actual published operator executable."""
        result = self.host('/ctl/wayfarerctl', '--deployment-root', str(self.install), *args, data=data, check=check)
        assert self.password not in result.stdout + result.stderr
        return result

    def compose(self, *args):
        """Advanced test observation/seeding only; setup/lifecycle/recovery use ctl."""
        return self.host('docker', 'compose', '--project-name', self.project, '--env-file',
                         str(self.install / 'deployment.env'), '-f', str(self.bundle / 'compose.yaml'),
                         '-f', str(self.bundle / 'external.yaml'), *args).stdout

    def prepare(self):
        """Copy production substrate; replace only TLS provisioning for a safe local certificate."""
        shutil.copytree(ROOT / 'deploy/compose', self.bundle)
        run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
            '-subj', '/CN=wayfarer.example.org', '-addext', 'subjectAltName=DNS:wayfarer.example.org',
            '-keyout', str(self.bundle / 'caddy/tls.key'), '-out', str(self.bundle / 'caddy/tls.crt'))
        proxy = self.bundle / 'caddy/ExternalTest'
        proxy.write_text('{\n auto_https disable_redirects\n}\nhttps://wayfarer.example.org:' + str(self.port) +
                         ' {\n bind 127.0.0.1\n tls /etc/caddy/tls.crt /etc/caddy/tls.key\n reverse_proxy 127.0.0.1:' + str(self.loopback) + '\n}\n')
        caddy = self.bundle / 'caddy/Caddyfile'
        caddy.write_text(caddy.read_text().replace('{$PUBLIC_HOST} {', '{$PUBLIC_HOST} {\n tls /etc/caddy/tls.crt /etc/caddy/tls.key'))
        compose = self.bundle / 'compose.yaml'
        compose.write_text(compose.read_text().replace('      - ./caddy/Caddyfile:',
            '      - ./caddy/tls.crt:/etc/caddy/tls.crt:ro\n      - ./caddy/tls.key:/etc/caddy/tls.key:ro\n      - ./caddy/Caddyfile:'))
        self.host('sh', '-ec', f'chown -R 0:0 {self.directory}; chmod 700 {self.directory}')
        absent = self.host('sh', '-ec', 'test ! -d /usr/share/dotnet; ! command -v dotnet; ! command -v python3; ! command -v node')
        assert absent.returncode == 0
        assert 'wayfarerctl' in self.ctl('version').stdout

    def curl(self, path, *args):
        """Use validated test-only TLS, public Host and explicit fixture DNS."""
        # Fixture directory is root-only; run curl as current user with the public cert supplied via stdin-independent copy.
        cert = self.host('cat', str(self.bundle / 'caddy/tls.crt')).stdout
        with tempfile.NamedTemporaryFile(mode='w', suffix='.crt') as ca:
            ca.write(cert)
            ca.flush()
            return run('curl', '--silent', '--show-error', '--fail', '--noproxy', '*', '--max-time', '30',
                       '--cacert', ca.name, '--resolve', f'wayfarer.example.org:{self.port}:127.0.0.1',
                       *args, f'https://wayfarer.example.org:{self.port}' + path).stdout

    def authenticate(self, cookie):
        """Retain a real protected authentication cookie to prove restart/key-ring continuity."""
        page = self.curl('/Identity/Account/Login', '-c', cookie)
        token = re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', page).group(1)
        with tempfile.NamedTemporaryFile(mode='w') as body:
            body.write(urlencode({'Input.Username': 'admin', 'Input.Password': self.password, '__RequestVerificationToken': token}))
            body.flush()
            self.curl('/Identity/Account/Login', '-b', cookie, '-c', cookie,
                      '-H', 'Content-Type: application/x-www-form-urlencoded', '--data-binary', '@' + body.name)
        assert self.curl('/Admin/Users', '-b', cookie, '-o', '/dev/null', '-w', '%{http_code}') == '200'

    def qualify(self):
        """Fresh setup -> diagnostics -> restart -> user recovery through one product journey."""
        result = self.ctl('setup', '--bundle', str(self.bundle), '--hostname', 'wayfarer.example.org',
                          '--app-digest', self.digest, '--project', self.project, '--edge-prefix', '172.30.68',
                          '--mode', 'external', '--loopback-port', str(self.loopback),
                          '--password-stdin', data=self.password + '\n')
        print(result.stdout, flush=True)
        assert 'Setup complete' in result.stdout
        run('docker', 'run', '-d', '--name', self.proxy, '--network', 'host',
            '--label', f'com.docker.compose.project={self.project}',
            '-v', f'{self.bundle}/caddy:/etc/caddy:ro',
            'caddy@sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b',
            'caddy', 'run', '--config', '/etc/caddy/ExternalTest', '--adapter', 'caddyfile')
        assert self.ctl('setup', '--bundle', str(self.bundle), '--hostname', 'wayfarer.example.org',
                        '--app-digest', self.digest, '--project', self.project, '--password-stdin',
                        data=self.password + '\n', check=False).returncode == 2
        secret_check = self.host('sh', '-ec', f'stat -c "%a %u %g" {self.install}/secrets/*').stdout
        assert sorted(secret_check.splitlines()) == ['600 0 0', '600 1654 1654', '600 999 999']
        assert 'FAIL' not in self.ctl('status').stdout
        assert 'FAIL' not in self.ctl('doctor').stdout
        assert 'admin' in self.ctl('user', 'find', 'admin').stdout
        assert self.ctl('user', 'find', 'absent-' + self.project, check=False).returncode == 1
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                     'mkdir -p /var/lib/wayfarer/uploads/imports; printf durable > /var/lib/wayfarer/uploads/imports/qualification-upload')
        identity = self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                                r'find /var/lib/wayfarer -type f -exec sha256sum {} \; | sort')
        with tempfile.NamedTemporaryFile() as cookie:
            self.authenticate(cookie.name)
            self.ctl('restart')
            assert identity == self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                                            r'find /var/lib/wayfarer -type f -exec sha256sum {} \; | sort')
            assert self.curl('/Admin/Users', '-b', cookie.name, '-o', '/dev/null', '-w', '%{http_code}') == '200'
        self.password = secrets.token_hex(32) + '!aA9'
        self.ctl('user', 'reset-password', 'admin', '--password-stdin', data=self.password + '\n')
        with tempfile.NamedTemporaryFile() as cookie:
            self.authenticate(cookie.name)
        self.ctl('logs', 'wayfarer', '--tail', '10')
        self.ctl('stop')
        assert self.ctl('doctor', check=False).returncode == 1
        self.ctl('start')
        print('PASS self-contained setup/TLS/status/doctor/restart/cookie/key/upload/user recovery/stop/start', flush=True)

    def cleanup(self):
        """Delete only this unpredictable project's labelled disposable resources and owned temp directory."""
        for kind, listing, removal in [('container', ['ps', '-aq'], ['rm', '-f']),
                                       ('network', ['network', 'ls', '-q'], ['network', 'rm']),
                                       ('volume', ['volume', 'ls', '-q'], ['volume', 'rm'])]:
            ids = run('docker', *listing, '--filter', f'label=com.docker.compose.project={self.project}').stdout.split()
            if ids:
                run('docker', *removal, *ids)
        self.host('sh', '-ec', f'chown -R {os.getuid()}:{os.getgid()} {self.directory}; chmod 700 {self.directory}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', required=True)
    parser.add_argument('--app-digest', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', args.app_digest):
        parser.error('actual immutable application content digest required')
    with tempfile.TemporaryDirectory(prefix='wayfarer-648-') as directory:
        journey = Journey(directory, args.executable, args.app_digest)
        try:
            journey.prepare()
            journey.qualify()
        finally:
            journey.cleanup()


if __name__ == '__main__':
    main()
