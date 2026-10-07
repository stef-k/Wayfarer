"""Actual subprocess regression evidence for Linux birth tokens and Windows jobs."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

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

    def test_early_exit_is_reaped_and_log_closed(self):
        """An already exited child is not reopened or signaled."""
        child = self.child('raise SystemExit(7)')
        self.assertEqual(7, child.wait(10))
        child.stop()
        self.assertTrue(child.closed)

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
