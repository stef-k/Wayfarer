"""Behavioral supervisor contracts at real child/endpoint and finalizer seams."""

from contextlib import redirect_stderr
import io
import os
from pathlib import Path
import socket
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from browser_e2e import BrowserRun, child_environment, run_owned, wait_ready, WAYPOINT_SPECS
from browser_processes import Child


class SupervisorTests(unittest.TestCase):
    """Prove failure/finalization without needing an application or disposable database."""

    def setUp(self):
        """Use an owned root and a copied synthetic connection solely for child-boundary tests."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        environment = patch.dict(os.environ, {'WAYFARER_TEST_POSTGRES_CONNECTION':
                                             'Host=guarded;Database=wayfarer_import_tests'})
        environment.start()
        self.addCleanup(environment.stop)
        self.run = BrowserRun(Path(self.directory.name), 'shared-layout')
        self.run.source.mkdir()
        self.addCleanup(self.recover)

    def recover(self):
        """Finalize only this test's children/root if a deliberately refused phase retained it."""
        for child in self.run.children:
            child.stop()
        self.run.root.remove()

    def host(self):
        """Start a real loopback listener so native endpoint ownership is exercised."""
        self.run.port = self.run.free_port()
        script = f'import http.server; http.server.HTTPServer(("127.0.0.1",{self.run.port}),http.server.SimpleHTTPRequestHandler).serve_forever()'
        self.run.host = self.run.start('host', [sys.executable, '-c', script])
        self.run.started_endpoint = True
        wait_ready(self.run.host, f'http://127.0.0.1:{self.run.port}', self.run.port, timeout=10)

    def test_normal_host_and_endpoint_finalization(self):
        """An owned ready host is stopped, reaped, released and its exact root removed."""
        self.host()
        self.assertTrue(self.run.finalize())
        self.run.verify_release(self.run.port)
        self.assertFalse(self.run.root.path.exists())

    @unittest.skipUnless(sys.platform == 'linux', 'CLR diagnostic residue is Linux-specific')
    def test_finalization_removes_stopped_host_clr_residue(self):
        """The retained host's exact FIFO/socket residue does not fail successful cleanup."""
        self.host()
        record = self.run.host.record
        temporary = Path(self.run.env['TMPDIR'])
        for suffix in ('in', 'out'):
            os.mkfifo(temporary / f'clr-debug-pipe-{record["pid"]}-{record["start"]}-{suffix}')
        endpoint = os.fsencode(temporary / f'dotnet-diagnostic-{record["pid"]}-{record["start"]}-socket')[:107]
        with socket.socket(socket.AF_UNIX) as diagnostic:
            diagnostic.bind(endpoint)
        try:
            self.assertTrue(self.run.finalize())
            self.assertFalse(self.run.root.path.exists())
        finally:
            for entry in temporary.glob('*'):
                entry.unlink()

    def test_occupied_endpoint_fails_before_source_or_fixture_mutation(self):
        """A foreign listener is never accepted as readiness or stopped by finalization."""
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            port = listener.getsockname()[1]
            self.run.profile = 'waypoint'
            with patch.object(self.run, 'free_port', return_value=port), patch('browser_e2e.copy_source') as copy:
                with self.assertRaisesRegex(RuntimeError, 'occupied'):
                    self.run.execute([])
                copy.assert_not_called()
            self.assertTrue(self.run.finalize())
            self.assertEqual(port, listener.getsockname()[1])

    def test_failed_startup_still_finalizes_owned_child(self):
        """The actual early-exit child fails readiness and leaves no process/root residue."""
        child = self.run.start('host', [sys.executable, '-c', 'raise SystemExit(9)'])
        self.assertEqual(9, child.wait(10))
        with self.assertRaisesRegex(RuntimeError, 'before readiness'):
            wait_ready(child, 'http://127.0.0.1:1', 1, timeout=1)
        self.assertTrue(self.run.finalize())
        self.assertTrue(child.log.closed)

    def test_cancellation_during_playwright_stops_host_and_child(self):
        """Keyboard interruption goes through the same finalizer while real writers are alive."""
        def execute(_):
            self.host()
            child = self.run.start('playwright', [sys.executable, '-c', 'import time; time.sleep(60)'])
            self.assertIsNone(child.process.poll())
            raise KeyboardInterrupt
        self.run.execute = execute
        self.assertEqual(130, run_owned(self.run, []))
        self.assertTrue(all(child.closed for child in self.run.children))
        self.assertFalse(self.run.root.path.exists())

    def test_cancellation_between_launch_and_registration_still_reaps_writer(self):
        """A real signal at the launch boundary cannot bypass ownership registration/finalization."""
        def launch(*args, **kwargs):
            child = Child(*args, **kwargs)
            signal.raise_signal(signal.SIGINT)
            return child
        self.run.execute = lambda _: self.run.start(
            'playwright', [sys.executable, '-c', 'import time; time.sleep(60)'])
        with patch('browser_e2e.Child', side_effect=launch):
            self.assertEqual(130, run_owned(self.run, []))
        self.assertEqual(1, len(self.run.children))
        self.assertTrue(self.run.children[0].closed)
        self.assertFalse(self.run.root.path.exists())

    def test_primary_failure_cleanup_failure_and_independent_verify(self):
        """Browser status stays primary; cleanup verification runs even when deletion fails."""
        self.run.provision_attempted = True
        self.run.manifest.write_text('{}')
        phases = []
        def fixture(command, manifest=None):
            phases.append(command)
            if command == 'cleanup-layout':
                raise RuntimeError('injected cleanup failure')
        def execute(_):
            raise subprocess.CalledProcessError(7, 'playwright')
        self.run.fixture = fixture
        self.run.execute = execute
        self.assertEqual(7, run_owned(self.run, []))
        self.assertEqual(['cleanup-layout', 'verify-cleanup-layout'], phases)
        self.assertEqual('fixture-cleanup', self.run.cleanup_errors[0][0])
        self.assertTrue(self.run.root.path.exists())

    def test_unexpected_failure_retains_frames_without_private_values(self):
        """Real finalization preserves sanitized diagnostics after removing the run root."""
        def execute(_):
            raise RuntimeError('fixture-secret', {'environment': self.run.env, 'private': self.run.root.path})

        self.run.execute = execute
        output = io.StringIO()
        with redirect_stderr(output):
            self.assertEqual(1, run_owned(self.run, []))
        retained = self.run.repository / '.local/browser-e2e' / self.run.root.run_id / 'supervisor.log'
        if os.name == 'nt':
            self.assertFalse(retained.exists())
            self.assertIn('Browser diagnostic retention failed: NotImplementedError', output.getvalue())
            diagnostic = output.getvalue().split('Browser diagnostic retention failed:')[0]
        else:
            diagnostic = retained.read_text()
        self.assertIn(diagnostic, output.getvalue())
        self.assertIn('Browser execution failed: RuntimeError', diagnostic)
        self.assertIn('File "browser_e2e.py", line ', diagnostic)
        self.assertIn('in run_owned', diagnostic)
        self.assertIn('File "test_browser_e2e.py", line ', diagnostic)
        self.assertIn('in execute', diagnostic)
        for private in ('fixture-secret', str(self.run.root.path), str(Path(__file__).parent),
                        self.run.env['WAYFARER_E2E_PASSWORD'], 'raise RuntimeError', 'Host=guarded'):
            self.assertNotIn(private, diagnostic)
        self.assertFalse(self.run.root.path.exists())

    def test_diagnostic_storage_failure_preserves_status_and_cleanup(self):
        """Storage errors stay visible without overriding execution status or cleanup phases."""
        def execute(_):
            raise subprocess.CalledProcessError(7, 'playwright')

        phases = []
        self.run.execute = execute
        self.run.provision_attempted = True
        self.run.manifest.write_text('{}')

        def fixture(command, manifest=None):
            phases.append(command)
            if command == 'cleanup-layout':
                raise RuntimeError('private-cleanup-failure')

        self.run.fixture = fixture
        with patch.object(self.run.root, 'append_diagnostic', side_effect=PermissionError('private-path')):
            output = io.StringIO()
            with redirect_stderr(output):
                self.assertEqual(7, run_owned(self.run, []))
        self.assertEqual(['cleanup-layout', 'verify-cleanup-layout'], phases)
        self.assertEqual([('fixture-cleanup', 'RuntimeError')], self.run.cleanup_errors)
        self.assertIn('Browser diagnostic retention failed: PermissionError', output.getvalue())
        self.assertNotIn('private-path', output.getvalue())

    def test_recursive_diagnostic_failure_preserves_status_and_independent_cleanup(self):
        """Injected recursion cannot escape execution/cleanup reporting or touch foreign files."""
        sentinel = self.run.repository / 'sentinel'
        sentinel.write_text('foreign')
        before = set(self.run.repository.iterdir())
        self.run.provision_attempted = True
        self.run.manifest.write_text('{}')
        self.run.preparation.write_text('{}')
        for error, status in ((subprocess.CalledProcessError(7, 'playwright'), 7),
                              (RuntimeError('private-execution-failure'), 1)):
            with self.subTest(status=status):
                phases = []
                self.run.cleanup_errors.clear()

                def execute(_):
                    raise error

                def fixture(command, manifest=None):
                    phases.append(command)
                    if command == 'cleanup-layout':
                        raise RuntimeError('private-cleanup-failure')

                self.run.execute = execute
                self.run.fixture = fixture
                output = io.StringIO()
                failure = RecursionError('private-retention-failure', self.run.root.path,
                                         {'connection': 'Host=guarded',
                                          'password': self.run.env['WAYFARER_E2E_PASSWORD']})
                with patch.object(self.run.root, 'append_diagnostic', side_effect=failure) as append:
                    with redirect_stderr(output):
                        self.assertEqual(status, run_owned(self.run, []))
                self.assertEqual(1 if status == 7 else 2, append.call_count)
                self.assertEqual(['cleanup-layout', 'verify-cleanup-layout',
                                  'cleanup-environment', 'verify-environment'], phases)
                self.assertEqual([('fixture-cleanup', 'RuntimeError')], self.run.cleanup_errors)
                self.assertIn('Browser execution failed: ' + ('playwright (exit 7)' if status == 7
                                                             else 'RuntimeError'), output.getvalue())
                self.assertIn('Browser diagnostic retention failed: RecursionError', output.getvalue())
                diagnostic = output.getvalue().split('Owned residue retained:')[0]
                for private in ('private-retention-failure', 'private-execution-failure',
                                'private-cleanup-failure', self.run.env['WAYFARER_E2E_PASSWORD'],
                                'Host=guarded', str(Path(__file__).parent), str(self.run.root.path),
                                'raise error'):
                    self.assertNotIn(private, diagnostic)
                self.assertEqual(before, set(self.run.repository.iterdir()))
                self.assertEqual('foreign', sentinel.read_text())

    @unittest.skipIf(os.name == 'nt', 'Unix symbolic-link diagnostic target')
    def test_linked_diagnostic_never_changes_foreign_sentinel(self):
        """Execution and finalizer failures cannot append through a linked log target."""
        sentinel = self.run.repository / 'sentinel'
        sentinel.write_text('foreign')
        log = self.run.root.path / 'supervisor.log'
        log.symlink_to(sentinel)
        self.addCleanup(log.unlink)

        def execute(_):
            raise RuntimeError('private-failure')

        self.run.execute = execute
        output = io.StringIO()
        with redirect_stderr(output):
            self.assertEqual(1, run_owned(self.run, []))
        self.assertEqual('foreign', sentinel.read_text())
        self.assertTrue(log.is_symlink())
        self.assertEqual([('private-evidence', 'ValueError')], self.run.cleanup_errors)
        self.assertIn('Browser execution failed: RuntimeError', output.getvalue())
        self.assertIn('Browser cleanup (private-evidence) failed: ValueError', output.getvalue())

    def test_finalizer_diagnostics_never_write_through_replaced_root(self):
        """Refused symlink/junction roots retain foreign entries without creating a log."""
        path = self.run.root.path
        saved = path.with_name(path.name + '-test-saved')
        sentinel = self.run.repository / 'sentinel'
        sentinel.write_text('foreign')
        path.rename(saved)
        try:
            if os.name == 'nt':
                subprocess.run(['cmd', '/c', 'mklink', '/J', str(path), str(self.run.repository)],
                               check=True, capture_output=True)
            else:
                path.symlink_to(self.run.repository, target_is_directory=True)
            before = set(self.run.repository.iterdir())
            try:
                output = io.StringIO()
                with redirect_stderr(output):
                    self.assertFalse(self.run.finalize())
                self.assertEqual([('artifact-removal', 'ValueError')], self.run.cleanup_errors)
                self.assertEqual(before, set(self.run.repository.iterdir()))
                self.assertEqual('foreign', sentinel.read_text())
                self.assertIn('Browser cleanup (artifact-removal) failed: ValueError', output.getvalue())
            finally:
                path.rmdir() if os.name == 'nt' else path.unlink()
        finally:
            saved.rename(path)

    def test_environment_replaces_human_identity_and_scopes_every_storage_root(self):
        """Caller secrets/key authority stay unchanged; child uses only run-owned credentials/writes."""
        original = {'WAYFARER_TEST_POSTGRES_CONNECTION': 'guarded', 'WAYFARER_E2E_USERNAME': 'human',
                    'WAYFARER_E2E_PASSWORD': 'human-secret', 'Storage__DataRoot': '/persistent',
                    'DataProtection__KeyRingPath': '/persistent-keys', 'PLAYWRIGHT_BROWSERS_PATH': '/shared-cache'}
        result = child_environment(original, self.run.root, 'shared-layout')
        self.assertNotIn('WAYFARER_E2E_USERNAME', result)
        self.assertNotEqual('human-secret', result['WAYFARER_E2E_PASSWORD'])
        for name in ('DataRoot', 'CacheRoot', 'LogRoot', 'TempRoot'):
            self.assertTrue(Path(result[f'Storage__{name}']).is_relative_to(self.run.root.path))
        self.assertEqual('/persistent', original['Storage__DataRoot'])
        self.assertEqual('/persistent-keys', original['DataProtection__KeyRingPath'])
        self.assertEqual('/shared-cache', result['PLAYWRIGHT_BROWSERS_PATH'])
        self.assertEqual('guarded', result['ConnectionStrings__DefaultConnection'])
        self.assertEqual(2, len(WAYPOINT_SPECS))
        with self.assertRaises(ValueError):
            child_environment({}, self.run.root, 'shared-layout')


if __name__ == '__main__':
    unittest.main()
