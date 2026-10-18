"""Actual subprocess regression evidence for Linux birth tokens and Windows jobs."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from browser_processes import Child, identity


class ProcessTests(unittest.TestCase):
    """Exercise retained ownership, early exits, descendants and a foreign sentinel."""

    def setUp(self):
        """Keep test output owned for the complete lifetime of each child."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.counter = 0

    def child(self, script):
        """Launch the same Python executable used by platform CI."""
        self.counter += 1
        child = Child([sys.executable, '-c', script], cwd=self.directory.name,
                      env=os.environ.copy(), log=Path(self.directory.name) / f'{self.counter}.log')
        self.addCleanup(child.stop)
        return child

    def test_identity_matches_independent_inspectors_and_stops(self):
        """Separate interpreters observe the exact native birth token from #794."""
        child = self.child('import time; time.sleep(60)')
        for _ in range(3):
            script = f'from browser_processes import identity; import json; print(json.dumps(identity({child.process.pid})))'
            observed = subprocess.check_output([sys.executable, '-B', '-c', script],
                                               cwd=Path(__file__).resolve().parents[1], text=True)
            self.assertEqual(child.record, json.loads(observed))
        child.stop()
        self.assertIsNotNone(child.process.returncode)
        self.assertTrue(child.log.closed)
        child.stop()

    def test_forged_and_foreign_identity_refuse_stop(self):
        """Wrong birth tokens and a separately owned PID never grant stop authority."""
        child = self.child('import time; time.sleep(60)')
        foreign = self.child('import time; time.sleep(60)')
        original = child.record
        for forged in ({**original, 'start': 'wrong'}, foreign.record):
            child.record = forged
            with self.assertRaises(RuntimeError):
                child.stop()
            self.assertIsNone(child.process.poll())
            self.assertIsNone(foreign.process.poll())
        child.record = original
        with self.assertRaisesRegex(RuntimeError, 'PID was reused'):
            child._retain(foreign.process.pid, 'forged-birth-token')
        self.assertNotIn(foreign.process.pid, child.members)

    @unittest.skipIf(os.name == 'nt', 'Windows job handles supply complete descendant authority')
    def test_unproved_writer_refuses_stop_without_signaling_foreign_child(self):
        """Missing ancestry proof cannot turn an adopted/direct foreign child into kill authority."""
        child = self.child('import time; time.sleep(60)')
        foreign = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
        try:
            with self.assertRaisesRegex(RuntimeError, 'Unproved adopted'):
                child.stop()
            self.assertIsNone(foreign.poll())
            self.assertIsNone(child.process.poll())
        finally:
            foreign.terminate()
            foreign.wait(timeout=10)

    def test_early_exit_is_reaped_and_log_closed(self):
        """An already exited child is not reopened or signaled."""
        child = self.child('raise SystemExit(7)')
        self.assertEqual(7, child.wait(10))
        child.stop()
        self.assertTrue(child.closed)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux procfs returns ESRCH after exit/reap')
    def test_exit_during_stat_read_does_not_abort_wait_or_cleanup(self):
        """Reap between procfs open/read at retained identity and liveness checks."""
        for operation, read_number in (('wait', 2), ('_live', 1), ('_live', 2)):
            with self.subTest(operation=operation, read_number=read_number):
                child = self.child('import time; time.sleep(60)')
                stat_path = Path(f'/proc/{child.process.pid}/stat')
                read_text = Path.read_text
                reads = 0

                def disappear(path, *args, **kwargs):
                    """Force the kernel's real ESRCH rather than synthesizing its exception."""
                    nonlocal reads
                    if path == stat_path:
                        reads += 1
                        if reads == read_number:
                            with path.open() as stat:
                                child.process.terminate()
                                child.process.wait(timeout=10)
                                return stat.read()
                    return read_text(path, *args, **kwargs)

                with patch.object(Path, 'read_text', disappear):
                    result = child.wait(10) if operation == 'wait' else child._live()
                self.assertEqual(read_number, reads)
                self.assertEqual(child.process.returncode if operation == 'wait' else [], result)
                child.stop()
                self.assertTrue(child.closed)
                self.assertTrue(child.log.closed)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux retained identity checks')
    def test_identity_permission_errors_still_refuse_inspection_and_stop(self):
        """Only disappearance is tolerated; unrelated identity errors remain fatal."""
        child = self.child('import time; time.sleep(60)')
        for operation in (child.wait, child._live, child.stop):
            with self.subTest(operation=operation.__name__):
                with patch('browser_processes.identity', side_effect=PermissionError(13, 'denied')):
                    with self.assertRaises(PermissionError):
                        operation(10) if operation == child.wait else operation()
                self.assertIsNone(child.process.poll())

    def test_descendant_survives_parent_exit_until_owned_stop(self):
        """The session/job accounts for Node-like grandchildren after early parent exit."""
        script = 'import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"])'
        child = self.child(script)
        self.assertEqual(0, child.wait(10))
        child.stop()
        self.assertFalse(child._live() if os.name != 'nt' else not child.closed)

    @unittest.skipIf(os.name == 'nt', 'Windows jobs already contain detached descendants')
    def test_detached_browser_descendant_is_pinned_stopped_and_reaped(self):
        """A Playwright-style detached child remains owned even after its launcher exits."""
        script = ('import subprocess,sys,time; '
                  'subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"],start_new_session=True); '
                  'time.sleep(0.3)')
        child = self.child(script)
        self.assertEqual(0, child.wait(10))
        self.assertGreaterEqual(len(child.members), 2)
        child.stop()
        self.assertFalse(child._live())


if __name__ == '__main__':
    unittest.main()
