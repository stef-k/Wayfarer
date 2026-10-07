"""Real platform filesystem refusal and exact retained-root deletion tests."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_artifact_paths import BrowserRoot, ordinary


class ArtifactTests(unittest.TestCase):
    """Keep foreign sentinels alive while the actual owned removal seam runs."""

    def setUp(self):
        """Create one fresh owner rather than accepting test-selected deletion paths."""
        self.root = BrowserRoot('shared-layout', {'pid': os.getpid()})
        self.addCleanup(self.root.remove)

    def test_owned_cleanup_is_idempotent(self):
        """Ordinary nested output is removed, and a second finalization is harmless."""
        (self.root.path / 'nested').mkdir()
        (self.root.path / 'nested' / 'output').write_text('owned')
        self.root.remove()
        self.root.remove()
        self.assertFalse(self.root.path.exists())

    def test_foreign_root_and_forged_marker_are_preserved(self):
        """Neither a path substitution nor a JSON record grants deletion authority."""
        original = self.root.path
        with tempfile.TemporaryDirectory() as foreign:
            sentinel = Path(foreign) / 'keep'
            sentinel.write_text('foreign')
            self.root.path = Path(foreign)
            with self.assertRaises(ValueError):
                self.root.remove()
            self.assertTrue(sentinel.exists())
            self.root.path = original
        marker = self.root.marker.read_text()
        self.root.marker.write_text(json.dumps({'run': 'forged'}))
        with self.assertRaises(ValueError):
            self.root.remove()
        self.assertTrue(self.root.path.exists())
        self.root.marker.write_text(marker)

    def test_linked_entry_and_ancestor_are_preserved(self):
        """Reject real Unix links or Windows junctions before walking or deleting."""
        with tempfile.TemporaryDirectory() as foreign:
            sentinel = Path(foreign) / 'keep'
            sentinel.write_text('foreign')
            link = self.root.path / 'link'
            if os.name == 'nt':
                subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), foreign], check=True,
                               capture_output=True)
            else:
                link.symlink_to(foreign, target_is_directory=True)
            try:
                with self.assertRaises(ValueError):
                    ordinary(link / 'keep')
                with self.assertRaises(ValueError):
                    self.root.remove()
                self.assertTrue(sentinel.exists())
                self.assertTrue(self.root.path.exists())
            finally:
                if os.name == 'nt':
                    link.rmdir()
                else:
                    link.unlink()

    @unittest.skipIf(os.name == 'nt', 'FIFO is a Linux filesystem boundary')
    def test_dangling_link_and_special_entry_are_preserved(self):
        """Dangling links and FIFOs must survive rejected recursive deletion."""
        entry = self.root.path / 'special'
        for create in (lambda: entry.symlink_to(self.root.path / 'missing'), lambda: os.mkfifo(entry)):
            create()
            try:
                with self.assertRaises(ValueError):
                    self.root.remove()
                self.assertTrue(os.path.lexists(entry))
            finally:
                entry.unlink()


if __name__ == '__main__':
    unittest.main()
