"""Single managed browser run owner; fixed shared-layout and waypoint profiles only."""

import argparse
import json
import os
from pathlib import Path
import secrets
import re
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

from browser_processes import Child, identity, owns_listener, port_free
from test_artifact_paths import BrowserRoot, ordinary


WAYPOINT_SPECS = (
    'tests/e2e/trip-editor/tripEditorWaypointAggregateContracts.spec.ts',
    'tests/e2e/trip-editor/tripEditorRouteWorkCompletion.spec.ts',
)
SOURCE_EXCLUDES = {'.git', 'bin', 'obj', '.local', 'node_modules', 'ChromeCache',
                   'TileCache', 'ImageCache', 'Logs', 'Uploads', 'coverage-report',
                   'TestResults', 'playwright-report', 'test-results', '__pycache__'}


def child_environment(inherited, root, profile):
    """Replace human credentials and mutable roots in a copy, preserving caller state."""
    connection = inherited.get('WAYFARER_TEST_POSTGRES_CONNECTION', '').strip()
    if not connection:
        raise ValueError('WAYFARER_TEST_POSTGRES_CONNECTION is required')
    env = {key: value for key, value in inherited.items() if not key.startswith('WAYFARER_E2E_')}
    env.update(ConnectionStrings__DefaultConnection=connection,
               ASPNETCORE_ENVIRONMENT='Development' if profile == 'shared-layout' else 'Production',
               DOTNET_ENVIRONMENT='Development' if profile == 'shared-layout' else 'Production',
               DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1',
               PYTHONDONTWRITEBYTECODE='1', Logging__LogLevel__Default='Warning',
               WAYFARER_E2E_MANAGED='1', WAYFARER_E2E_RUN_ID=root.run_id,
               WAYFARER_E2E_PASSWORD='Browser1!' + secrets.token_urlsafe(32),
               DataProtection__KeyRingPath=str(root.path / 'keys'),
               CacheSettings__TileCacheDirectory=str(root.path / 'legacy-tiles'),
               CacheSettings__ImageCacheDirectory=str(root.path / 'legacy-images'))
    # Reusable Chromium remains outside this disposable root (Playwright owns installation).
    env.setdefault('PLAYWRIGHT_BROWSERS_PATH', str(Path.home() / '.cache' / 'ms-playwright')
                   if os.name != 'nt' else str(Path(os.environ['LOCALAPPDATA']) / 'ms-playwright'))
    for name in ('DataRoot', 'CacheRoot', 'LogRoot', 'TempRoot'):
        env[f'Storage__{name}'] = str(root.path / name)
    for name in ('TMPDIR', 'TMP', 'TEMP'):
        env[name] = env['Storage__TempRoot']
    return env


def copy_source(repository, destination):
    """Copy checked-in/current source without caches, generated outputs or linked entries."""
    paths = subprocess.check_output(['git', 'ls-files', '-co', '--exclude-standard', '-z'],
                                    cwd=repository).decode().split('\0')
    destination.mkdir()
    for name in filter(None, paths):
        relative = Path(name)
        if any(part in SOURCE_EXCLUDES for part in relative.parts):
            continue
        source = ordinary(repository / relative)
        if not os.path.lexists(source):
            continue  # Git's cached list includes current task-owned deletions.
        if not source.is_file():
            raise ValueError('Source isolation requires ordinary files')
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)


def npm_command():
    """Invoke the native npm JS entry point without a shell or Windows .cmd lifecycle."""
    npm = shutil.which('npm')
    node = shutil.which('node')
    if not npm or not node:
        raise ValueError('Node/npm prerequisites are missing')
    script = Path(npm).resolve() if os.name != 'nt' else Path(npm).parent / 'node_modules/npm/bin/npm-cli.js'
    return [node, str(script)]


def wait_ready(child, url, port, timeout=180):
    """Require an HTTP response, a live retained host and exact listener ownership."""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                        urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if child.process.poll() is not None:
            raise RuntimeError('Owned host exited before readiness')
        if owns_listener(child, port):
            try:
                with opener.open(url, timeout=2) as response:
                    if response.status == 200 and owns_listener(child, port):
                        return
            except (OSError, urllib.error.URLError):
                pass
        time.sleep(0.1)
    raise TimeoutError('Owned host did not become HTTP ready')


class BrowserRun:
    """Retains all child references and one finalizer for a fixed managed profile."""

    def __init__(self, repository, profile):
        if not os.environ.get('WAYFARER_TEST_POSTGRES_CONNECTION', '').strip():
            raise ValueError('WAYFARER_TEST_POSTGRES_CONNECTION is required')
        self.repository = repository
        self.profile = profile
        self.root = BrowserRoot(profile, identity(os.getpid()))
        self.children = []
        try:
            self.env = child_environment(os.environ, self.root, profile)
            Path(self.env['Storage__TempRoot']).mkdir(mode=0o700)
        except Exception:
            self.root.remove()
            raise
        self.source = self.root.path / 'source'
        self.helper = self.root.path / 'fixture' / 'Wayfarer.WaypointBrowserFixture.dll'
        self.manifest = self.root.path / 'fixture.json'
        self.preparation = self.root.path / 'environment.json'
        self.host = None
        self.port = None
        self.started_endpoint = False
        self.cleanup_errors = []
        self.provision_attempted = False
        self.completed = []

    def start(self, phase, command, cwd=None, env=None):
        """Launch one tracked native child; diagnostics never include its environment."""
        print(f'Browser {self.profile}: {phase}', flush=True)
        # Defer cancellation through creation/registration so no launched writer can miss finally.
        # Finalization's ignored signals stay ignored rather than reactivating interruption.
        pending = []
        handlers = {value: signal.getsignal(value) for value in (signal.SIGINT, signal.SIGTERM)}
        for value, handler in handlers.items():
            if handler != signal.SIG_IGN:
                signal.signal(value, lambda number, _: pending.append(number))
        try:
            child = Child(command, cwd=cwd or self.source, env=env or self.env,
                          log=self.root.path / f'{len(self.children):02d}-{phase}.log')
            self.children.append(child)
            records = [{'identity': item.record, 'command': item.command} for item in self.children]
            (self.root.path / 'children.json').write_text(json.dumps(records), encoding='utf-8')
            return child
        finally:
            for value, handler in handlers.items():
                signal.signal(value, handler)
            if pending and sys.exc_info()[0] is None:
                raise KeyboardInterrupt

    def checked(self, phase, command, *, cwd=None, env=None, timeout=180, watch=None):
        """Keep command status primary and close all owned descendants after completion."""
        child = self.start(phase, command, cwd, env)
        status = child.wait(timeout, watch)
        if status:
            raise subprocess.CalledProcessError(status, phase)
        child.stop()
        self.completed.append(phase)
        if phase == 'playwright':
            summary = re.findall(r'\b\d+ (?:passed|failed|skipped)\b', Path(child.log.name).read_text())
            print('Playwright summary: ' + ', '.join(summary), flush=True)
        print(f'Browser {self.profile}: {phase} passed', flush=True)

    def fixture(self, command, manifest=None):
        """Use the existing C# data owner for seed, probes and separate cleanup checks."""
        self.checked(command, ['dotnet', self.helper, command, manifest or self.manifest])

    def execute(self, selection):
        """Prepare before Playwright discovery and preserve each profile's host behavior."""
        self.port = 7150 if self.profile == 'shared-layout' else self.free_port()
        if not port_free(self.port):
            raise RuntimeError('Browser endpoint is already occupied')
        if self.profile == 'shared-layout' and not port_free(5173):
            raise RuntimeError('Managed Vite endpoint 5173 is already occupied')
        copy_source(self.repository, self.source)
        # Ordinary-only disposable trees cannot contain npm's optional .bin symlinks.
        self.checked('npm-ci', [*npm_command(), 'ci', '--bin-links=false', '--no-audit', '--no-fund'])
        configuration = 'Debug' if self.profile == 'shared-layout' else 'Release'
        # Avoid compiler/MSBuild daemons escaping the retained command session/job.
        build = ['-c', configuration, '--nologo', '-p:UseSharedCompilation=false', '-nodeReuse:false']
        self.checked('fixture-build', ['dotnet', 'publish',
                     'tools/Wayfarer.WaypointBrowserFixture/Wayfarer.WaypointBrowserFixture.csproj',
                     *build, '-p:MvcFrontendKitEnabled=false', '-o', self.helper.parent])
        # Resolve existing authority with the caller's storage/key selection, read-only.
        key_env = self.env.copy()
        for name in ('DataProtection__KeyRingPath', 'Storage__DataRoot'):
            key_env.pop(name, None)
            if name in os.environ:
                key_env[name] = os.environ[name]
        self.checked('key-ring', ['dotnet', self.helper, 'key-ring', self.root.path / 'key-ring.json'], env=key_env)
        selected = json.loads((self.root.path / 'key-ring.json').read_text())['path']
        keys = Path(self.env['DataProtection__KeyRingPath'])
        if os.path.lexists(selected):
            ordinary(Path(selected), tree=True)
            shutil.copytree(selected, keys)
        else:
            keys.mkdir(mode=0o700)
        # Guard validates the actual app configuration/DB and key round-trip before writes.
        # Explicit maintained schema preparation runs only after both effective connection guards.
        self.fixture('migrate', self.preparation)
        self.fixture('check', self.preparation)
        self.fixture('prepare', self.preparation)
        self.provision_attempted = True
        self.fixture('provision-layout' if self.profile == 'shared-layout' else 'provision')
        manifest = json.loads(self.manifest.read_text())
        self.env.update(WAYFARER_E2E_USERNAME=manifest['username'],
                        WAYFARER_E2E_TRIP_ID=str(manifest['tripId']))
        if self.profile == 'waypoint':
            self.env.update(WAYFARER_E2E_WAYPOINT_FIXTURE=str(self.manifest),
                            WAYFARER_E2E_WAYPOINT_HELPER=str(self.helper))
            self.checked('vite-build', ['node', self.source / 'node_modules/vite/bin/vite.js', 'build'])
            # Publish consumes existing MvcFrontendKit output; helper compilation disables its build target.
            self.checked('frontend-tools', ['dotnet', 'tool', 'restore'])
            self.checked('frontend-build', ['dotnet', 'frontend', 'build'])
        self.checked('host-build', ['dotnet', 'build' if self.profile == 'shared-layout' else 'publish',
                                   'Wayfarer.csproj', *build, '-o', self.root.path / 'host'])
        if self.profile == 'shared-layout':
            vite = self.start('vite', ['node', self.source / 'node_modules/vite/bin/vite.js',
                                      '--host', '127.0.0.1', '--port', '5173'])
            wait_ready(vite, 'http://127.0.0.1:5173/@vite/client', 5173)
        scheme = 'https' if self.profile == 'shared-layout' else 'http'
        url = f'{scheme}://127.0.0.1:{self.port}'
        self.env.update(ASPNETCORE_URLS=url, WAYFARER_E2E_BASE_URL=url,
                        WAYFARER_E2E_OUTPUT=str(self.root.path / 'playwright'),
                        WAYFARER_E2E_REPORT=str(self.root.path / 'report'))
        cwd = self.source if self.profile == 'shared-layout' else self.root.path / 'host'
        self.checked('host-configuration', ['dotnet', self.helper, 'check', self.preparation], cwd=cwd)
        self.host = self.start('host', ['dotnet', self.root.path / 'host/Wayfarer.dll', '--urls', url], cwd)
        self.started_endpoint = True
        wait_ready(self.host, url + '/Home/Privacy', self.port)
        cli = ['node', self.source / 'node_modules/@playwright/test/cli.js', 'test']
        if self.profile == 'shared-layout':
            cli += ['--config=playwright.shared-layout.config.ts', *selection]
        else:
            if selection:
                raise ValueError('Waypoint runs its fixed two-spec selection')
            cli += [*WAYPOINT_SPECS, '--config=playwright.config.ts']
        self.checked('playwright', [*cli, '--project=chromium', '--workers=1', '--retries=0'],
                     timeout=1200, watch=self.host)

    @staticmethod
    def free_port():
        """Select a loopback endpoint, then still fail if another owner wins binding."""
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            return listener.getsockname()[1]

    def cleanup_phase(self, name, action):
        """Continue independently safe phases while preserving phase-labelled failures."""
        try:
            action()
            return True
        except Exception as error:
            self.cleanup_errors.append((name, type(error).__name__))
            return False

    def finalize(self, failed=False):
        """Stop/reap writers, verify release, clean fixtures, then remove the exact root."""
        stopped = all([self.cleanup_phase('child-stop', child.stop) for child in reversed(self.children)])
        if self.started_endpoint:
            self.cleanup_phase('endpoint-release', lambda: self.verify_release(self.port))
        if self.provision_attempted and stopped and self.manifest.exists():
            suffix = '-layout' if self.profile == 'shared-layout' else ''
            self.cleanup_phase('fixture-cleanup', lambda: self.fixture('cleanup' + suffix))
            self.cleanup_phase('fixture-verify', lambda: self.fixture('verify-cleanup' + suffix))
        if stopped and self.preparation.exists():
            self.cleanup_phase('environment-cleanup', lambda: self.fixture('cleanup-environment', self.preparation))
            self.cleanup_phase('environment-verify', lambda: self.fixture('verify-environment', self.preparation))
        # Cleanup helpers are children too. Never remove a root with unproved live writers.
        stopped = all([self.cleanup_phase('final-child-stop', child.stop) for child in reversed(self.children)]) and stopped
        if stopped and not self.cleanup_errors:
            if failed:
                self.cleanup_phase('private-evidence', self.retain_evidence)
            if not self.cleanup_errors:
                self.cleanup_phase('artifact-removal', self.root.remove)
        if self.cleanup_errors:
            print(f'Owned residue retained: {self.root.path}', file=sys.stderr)
            for phase, kind in self.cleanup_errors:
                print(f'Cleanup failure: {phase} ({kind})', file=sys.stderr)
        return not self.cleanup_errors

    @staticmethod
    def verify_release(port):
        """Verify the endpoint is released after every owned writer stops."""
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(('127.0.0.1', port), timeout=0.2):
                    pass
            except (ConnectionRefusedError, TimeoutError):
                return  # TIME_WAIT is not a listener or a remaining endpoint owner.
            time.sleep(0.1)
        raise RuntimeError('Browser endpoint was not released')

    def retain_evidence(self):
        """Retain private diagnostics only; never copy manifests, key rings or raw traces."""
        destination = ordinary(self.repository / '.local' / 'browser-e2e' / self.root.run_id)
        destination.mkdir(parents=True, mode=0o700)
        for log in self.root.path.glob('*.log'):
            ordinary(log)
            shutil.copy2(log, destination / log.name)
        print(f'Private failed-run logs: {destination}', file=sys.stderr)


def run_owned(run, selection):
    """Preserve execution status when finalization also fails, including cancellation."""
    status = 0
    try:
        run.execute(selection)
    except KeyboardInterrupt:
        status = 130
        print('Browser execution cancelled', file=sys.stderr)
    except subprocess.CalledProcessError as error:
        status = error.returncode if error.returncode > 0 else 1
        print(f'Browser execution failed: {error.cmd} (exit {status})', file=sys.stderr)
    except Exception as error:
        status = 1
        print(f'Browser execution failed: {type(error).__name__}: {error}', file=sys.stderr)
    finally:
        # A second console signal cannot interrupt the single safe finalizer.
        handlers = {value: signal.signal(value, signal.SIG_IGN) for value in (signal.SIGINT, signal.SIGTERM)}
        try:
            if not run.finalize(failed=bool(status)) and not status:
                status = 1
        finally:
            for value, handler in handlers.items():
                signal.signal(value, handler)
    return status


def main():
    """Accept profiles and optional shared-layout Playwright selection, never deletion roots."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('profile', choices=('shared-layout', 'waypoint'))
    parser.add_argument('--grep', help='Bound a shared-layout journey using its existing test title')
    args = parser.parse_args()
    if args.profile == 'waypoint' and args.grep:
        parser.error('waypoint has a fixed two-spec selection')
    selection = ['--grep', args.grep] if args.grep else []
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    repository = Path(__file__).absolute().parents[1]
    try:
        run = BrowserRun(repository, args.profile)
    except Exception as error:
        print(f'Browser prerequisite failed: {type(error).__name__}: {error}', file=sys.stderr)
        return 1
    return run_owned(run, selection)


if __name__ == '__main__':
    sys.exit(main())
