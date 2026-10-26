"""Real platform filesystem refusal and exact retained-root deletion tests."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_artifact_paths import BrowserRoot, ordinary
from browser_processes import Child


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

    def retained_child(self):
        """Use the existing native owner so stopped-child authority is exercised directly."""
        child = Child([sys.executable, '-c', 'import time; time.sleep(60)'],
                      cwd=self.root.path, env=os.environ.copy(), log=self.root.path / 'child.log')
        self.addCleanup(child.stop)
        return child

    @unittest.skipUnless(sys.platform == 'linux', 'Linux CLR FIFO/socket ownership')
    def test_clr_residue_requires_stopped_retained_child(self):
        """A matching live child and a bare dead-PID filename cannot authorize removal."""
        child = self.retained_child()
        temporary = self.root.path / 'TempRoot'
        temporary.mkdir()
        entry = temporary / f'clr-debug-pipe-{child.record["pid"]}-{child.record["start"]}-in'
        os.mkfifo(entry)
        try:
            with self.assertRaises(ValueError):
                self.root.remove([child])
            self.assertIsNone(child.process.poll())
            child.stop()
            with self.assertRaises(ValueError):
                self.root.remove()
            self.assertTrue(entry.exists())
            self.root.remove([child])
            self.assertFalse(self.root.path.exists())
        finally:
            entry.unlink(missing_ok=True)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux CLR FIFO/socket ownership')
    def test_clr_names_do_not_admit_foreign_or_unsafe_entries(self):
        """Wrong identity, placement, type, links and shared inodes preserve the whole root."""
        child = self.retained_child()
        child.stop()
        temporary = self.root.path / 'TempRoot'
        temporary.mkdir()
        pid, start = child.record['pid'], child.record['start']
        name = f'clr-debug-pipe-{pid}-{start}-in'
        endpoint = Path(os.fsdecode(os.fsencode(temporary / f'dotnet-diagnostic-{pid}-{start}-socket')[:107]))
        cases = [('wrong-start', temporary / f'clr-debug-pipe-{pid}-{int(start) + 1}-in'),
                 ('unknown-pid', temporary / f'clr-debug-pipe-{os.getpid()}-{start}-in'),
                 ('misplaced', self.root.path / name), ('wrong-type', endpoint),
                 ('symlink', temporary / name), ('hardlink', temporary / name)]
        with tempfile.TemporaryDirectory() as foreign:
            alias = Path(foreign) / 'shared-fifo'
            for kind, entry in cases:
                with self.subTest(kind=kind):
                    if kind == 'symlink':
                        entry.symlink_to(self.root.marker)
                    else:
                        os.mkfifo(entry)
                    if kind == 'hardlink':
                        os.link(entry, alias)
                    try:
                        with self.assertRaises(ValueError):
                            self.root.remove([child])
                        self.assertTrue(os.path.lexists(entry))
                        self.assertTrue(self.root.marker.exists())
                        if kind == 'hardlink':
                            self.assertTrue(alias.exists())
                    finally:
                        entry.unlink()
                        alias.unlink(missing_ok=True)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux birth-token and procfs ownership')
    def test_clr_residue_requires_current_owner_and_dead_process(self):
        """Reused PIDs, inspection errors, foreign boots and foreign UIDs retain residue."""
        child = self.retained_child()
        child.stop()
        temporary = self.root.path / 'TempRoot'
        temporary.mkdir()
        entry = temporary / f'clr-debug-pipe-{child.record["pid"]}-{child.record["start"]}-in'
        os.mkfifo(entry)
        try:
            with patch('test_artifact_paths.identity', return_value=child.record):
                with self.assertRaises(ValueError):
                    self.root.remove([child])
            with patch('test_artifact_paths.identity', side_effect=PermissionError('denied')):
                with self.assertRaises(PermissionError):
                    self.root.remove([child])
            with patch.dict(child.members[child.record['pid']][0], {'boot': 'foreign-boot'}):
                with self.assertRaises(ValueError):
                    self.root.remove([child])
            with patch('test_artifact_paths.os.getuid', return_value=-1):
                with self.assertRaises(ValueError):
                    self.root.remove([child])
            self.assertTrue(entry.exists())
        finally:
            entry.unlink(missing_ok=True)

    @unittest.skipIf(os.name == 'nt', 'Pinned descriptor-relative opens unavailable on Windows')
    def test_diagnostic_append_refuses_unowned_and_replaced_targets(self):
        """Only a newly created private log may retain further diagnostic frames."""
        log = self.root.path / 'supervisor.log'
        log.write_text('foreign')
        with self.assertRaises(FileExistsError):
            self.root.append_diagnostic('first frame\n')
        self.assertEqual('foreign', log.read_text())
        log.unlink()
        self.root.append_diagnostic('first frame\n')
        self.root.append_diagnostic('second frame\n')
        self.assertEqual('first frame\nsecond frame\n', log.read_text())
        log.rename(self.root.path / 'saved.log')
        log.write_text('replacement')
        with self.assertRaises(ValueError):
            self.root.append_diagnostic('third frame\n')
        self.assertEqual('replacement', log.read_text())

    @unittest.skipIf(os.name == 'nt', 'Pinned descriptor-relative opens unavailable on Windows')
    def test_diagnostic_root_replaced_after_verification_is_refused(self):
        """An ordinary directory with a copied marker cannot replace the verified inode."""
        path = self.root.path
        saved = path.with_name(path.name + '-test-saved')
        verify = self.root.verify

        def substitute():
            verify()
            path.rename(saved)
            path.mkdir()
            (path / 'owner.json').write_bytes((saved / 'owner.json').read_bytes())

        try:
            with patch.object(self.root, 'verify', side_effect=substitute):
                with self.assertRaises(ValueError):
                    self.root.append_diagnostic('private frame\n')
            self.assertEqual({'owner.json'}, {entry.name for entry in path.iterdir()})
        finally:
            if saved.exists():
                (path / 'owner.json').unlink()
                path.rmdir()
                saved.rename(path)

    @unittest.skipIf(os.name == 'nt', 'Pinned descriptor-relative opens unavailable on Windows')
    def test_diagnostic_open_descriptors_cannot_be_redirected(self):
        """Root and log swaps after opening leave a foreign directory and sentinel untouched."""
        path = self.root.path
        saved = path.with_name(path.name + '-test-saved')
        fstat = os.fstat
        with tempfile.TemporaryDirectory() as foreign:
            sentinel = Path(foreign) / 'sentinel'
            sentinel.write_text('foreign')

            def substitute(descriptor):
                info = fstat(descriptor)
                if (info.st_dev, info.st_ino) == self.root._identity:
                    path.rename(saved)
                    path.symlink_to(foreign, target_is_directory=True)
                else:
                    (saved / 'supervisor.log').rename(saved / 'pinned.log')
                    (saved / 'supervisor.log').symlink_to(sentinel)
                return info

            try:
                with patch('test_artifact_paths.os.fstat', side_effect=substitute):
                    self.root.append_diagnostic('owned frame\n')
                self.assertEqual({'sentinel'}, {entry.name for entry in Path(foreign).iterdir()})
                self.assertEqual('foreign', sentinel.read_text())
                self.assertEqual('owned frame\n', (saved / 'pinned.log').read_text())
            finally:
                if saved.exists():
                    (saved / 'supervisor.log').unlink(missing_ok=True)
                    path.unlink()
                    saved.rename(path)

    def test_diagnostic_filesystem_errors_are_not_accepted(self):
        """Ownership/storage errors propagate from the writer for the reporter to classify."""
        with patch.object(self.root, 'verify', side_effect=PermissionError('denied')):
            with self.assertRaises(PermissionError):
                self.root.append_diagnostic('frame\n')

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
                    ordinary(link / '..' / 'owner.json')
                with self.assertRaises(ValueError):
                    self.root.remove()
                self.assertTrue(sentinel.exists())
                self.assertTrue(self.root.path.exists())
            finally:
                if os.name == 'nt':
                    link.rmdir()
                else:
                    link.unlink()

    def test_linked_root_is_preserved(self):
        """Replacing the exact lexical root with a real link/junction cannot authorize traversal."""
        path = self.root.path
        saved = path.with_name(path.name + '-test-saved')
        path.rename(saved)
        with tempfile.TemporaryDirectory() as foreign:
            sentinel = Path(foreign) / 'keep'
            sentinel.write_text('foreign')
            if os.name == 'nt':
                subprocess.run(['cmd', '/c', 'mklink', '/J', str(path), foreign], check=True,
                               capture_output=True)
            else:
                path.symlink_to(foreign, target_is_directory=True)
            try:
                with self.assertRaises(ValueError):
                    self.root.remove()
                self.assertTrue(sentinel.exists())
            finally:
                if os.name == 'nt':
                    path.rmdir()
                else:
                    path.unlink()
                saved.rename(path)

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
