"""Coverage command, retention and real platform filesystem ownership contracts."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from coverage_report import CoverageRun, generate_report


class CoverageTests(unittest.TestCase):
    """Exercise the real report owner in a disposable repository, without running .NET."""

    def setUp(self):
        """Keep every synthetic report, results tree and foreign sentinel private."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repository = Path(self.temporary.name) / 'repository'
        self.repository.mkdir()
        self.run = CoverageRun(self.repository)
        self.previous = self.run.report.parent / uuid.uuid4().hex
        self.previous.mkdir(parents=True)
        (self.previous / 'index.html').write_text('previous')

    def link(self, path, target):
        """Use a real Windows junction without requiring symlink privileges."""
        if os.name == 'nt':
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(path), str(target)],
                           check=True, capture_output=True)
            self.addCleanup(path.rmdir)
        else:
            path.symlink_to(target, target_is_directory=True)
            self.addCleanup(path.unlink)

    def test_exact_fresh_guid_paths_and_current_results_cleanup(self):
        """One generated GUID owns both outputs; existing siblings survive cleanup."""
        self.assertRegex(self.run.run_id, r'^[0-9a-f]{32}$')
        self.assertEqual(self.run.report.name, self.run.results.name)
        self.run.create()
        sibling = self.run.results.parent / uuid.uuid4().hex
        sibling.mkdir()
        (sibling / 'keep').write_text('foreign')
        self.assertEqual(self.run.finish(False), [])
        self.assertFalse(self.run.results.exists())
        self.assertFalse(self.run.report.exists())
        self.assertTrue((sibling / 'keep').exists())
        self.assertTrue(self.previous.exists())

    def test_existing_children_are_never_adopted(self):
        """A collision at either generated path must preserve its foreign content."""
        for name in ('report', 'results'):
            with self.subTest(name=name):
                run = CoverageRun(self.repository)
                target = getattr(run, name)
                target.mkdir(parents=True)
                (target / 'keep').write_text('foreign')
                with self.assertRaises(FileExistsError):
                    run.create()
                self.assertEqual(run.finish(False), [])
                self.assertTrue((target / 'keep').exists())

    def test_linked_root_and_ancestor_are_refused_before_creation(self):
        """No artifact may be written through a linked report root or results ancestor."""
        foreign = self.repository / 'foreign'
        foreign.mkdir()
        (foreign / 'keep').write_text('foreign')
        for relative in ('coverage-report', 'tests'):
            with self.subTest(relative=relative):
                repository = self.repository / relative.replace('-', '')
                repository.mkdir()
                self.link(repository / relative, foreign)
                run = CoverageRun(repository)
                with self.assertRaises(ValueError):
                    run.create()
                self.assertEqual(run.finish(False), [])
                self.assertEqual(list(foreign.iterdir()), [foreign / 'keep'])

    def test_nested_links_prevent_cleanup_and_preserve_foreign_files(self):
        """Both owned outputs refuse recursive cleanup through Unix links/Windows junctions."""
        self.run.create()
        foreign = self.repository / 'foreign'
        foreign.mkdir()
        (foreign / 'keep').write_text('foreign')
        for target in (self.run.report, self.run.results):
            self.link(target / 'nested', foreign)
        errors = self.run.finish(False)
        self.assertEqual(len(errors), 2)
        self.assertTrue(self.run.results.exists())
        self.assertTrue(self.run.report.exists())
        self.assertEqual((foreign / 'keep').read_text(), 'foreign')
        self.assertTrue(self.previous.exists())

    def test_replacement_directory_cannot_inherit_creation_authority(self):
        """Even the exact GUID path cannot authorize deletion after inode replacement."""
        self.run.create()
        saved = self.run.results.with_name('saved-results')
        self.run.results.rename(saved)
        self.run.results.mkdir()
        (self.run.results / 'keep').write_text('foreign')
        self.assertEqual(len(self.run.finish(False)), 1)
        self.assertTrue((self.run.results / 'keep').exists())
        self.assertTrue(saved.exists())

    def test_success_prunes_only_ordinary_guid_report_siblings(self):
        """Commit a validated HTML report before pruning; unknown and unsafe entries survive."""
        self.run.create()
        (self.run.report / 'index.html').write_text('current')
        root = self.run.report.parent
        preserved = [root / 'manual-evidence', root / ('A' * 32), root / uuid.uuid4().hex]
        preserved[0].mkdir()
        preserved[1].mkdir()
        preserved[2].write_text('not a directory')
        foreign = self.repository / 'foreign'
        foreign.mkdir()
        (foreign / 'keep').write_text('foreign')
        self.link(root / uuid.uuid4().hex, foreign)
        unsafe = root / uuid.uuid4().hex
        unsafe.mkdir()
        self.link(unsafe / 'nested', foreign)
        self.assertEqual(self.run.finish(True), [])
        self.assertFalse(self.previous.exists())
        self.assertTrue((self.run.report / 'index.html').exists())
        self.assertTrue(all(path.exists() for path in preserved))
        self.assertTrue(unsafe.exists())
        self.assertTrue((foreign / 'keep').exists())

    def test_invalid_cobertura_and_html_are_refused(self):
        """Require one regular, nonempty, parseable Cobertura document and a complete index."""
        self.run.create()
        xml = self.run.results / 'coverage.cobertura.xml'
        for content in ('', 'not XML', '<other/>'):
            xml.write_text(content)
            with self.subTest(content=content), self.assertRaises(ValueError):
                self.run.cobertura()
        xml.write_text('<coverage/>')
        self.assertEqual(self.run.cobertura(), xml)
        nested = self.run.results / 'nested'
        nested.mkdir()
        (nested / xml.name).write_text('<coverage/>')
        with self.assertRaises(ValueError):
            self.run.cobertura()
        index = self.run.report / 'index.html'
        index.write_text('')
        with self.assertRaises(ValueError):
            self.run.validate_report()
        self.assertEqual(len(self.run.finish(True)), 1)
        self.assertTrue(self.previous.exists())

    def test_command_success_and_primary_vs_cleanup_failure_status(self):
        """The actual orchestration retains native status and independently reports cleanup failure."""
        for failure in (None, 'build', 'test', 'reportgenerator', 'missing-xml', 'missing-html'):
            with self.subTest(failure=failure):
                run = CoverageRun(self.repository)
                commands = []

                def tool(arguments, **options):
                    """Emit only artifacts that the selected production command normally owns."""
                    self.assertTrue(options['check'])
                    self.assertEqual(options['cwd'], self.repository)
                    commands.append(arguments[1])
                    if arguments[1] == failure:
                        raise subprocess.CalledProcessError(7, arguments)
                    if arguments[1] == 'test' and failure != 'missing-xml':
                        (run.results / 'coverage.cobertura.xml').write_text('<coverage/>')
                    if arguments[1] == 'reportgenerator' and failure != 'missing-html':
                        (run.report / 'index.html').write_text('current')

                with patch('coverage_report.CoverageRun', return_value=run), \
                        patch('coverage_report.subprocess.run', side_effect=tool):
                    status = generate_report(self.repository)
                self.assertEqual(status, 0 if failure is None else 7 if failure in commands else 1)
                self.assertFalse(run.results.exists())
                self.assertEqual(run.report.exists(), failure is None)
                self.previous.mkdir(exist_ok=True)
        for primary in (None, subprocess.CalledProcessError(7, ['dotnet'])):
            with self.subTest(primary=primary), patch('coverage_report.CoverageRun') as owner, \
                    patch('coverage_report.subprocess.run', side_effect=primary):
                owner.return_value.finish.return_value = ['synthetic cleanup refusal']
                self.assertEqual(generate_report(self.repository), 1 if primary is None else 7)


if __name__ == '__main__':
    unittest.main()
