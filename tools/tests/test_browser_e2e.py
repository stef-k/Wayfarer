"""Behavioral supervisor contracts at real child/endpoint and finalizer seams."""

import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from browser_e2e import BrowserRun, child_environment, run_owned, wait_ready, WAYPOINT_SPECS


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
