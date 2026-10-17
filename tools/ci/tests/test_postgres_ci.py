"""Exercise the ordinary workflow's PostgreSQL selection and real TRX assertion."""

import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
import xml.etree.ElementTree as ET


WORKFLOW = Path(__file__).resolve().parents[3] / '.github/workflows/tests.yml'


class OrdinaryPostgresCiTests(unittest.TestCase):
    """Reject missing relational evidence without imposing unrelated zero-skip policy."""

    def setUp(self):
        """Extract the shipped assertion rather than duplicating its skip logic."""
        self.workflow = WORKFLOW.read_text()
        step = self.workflow.split('      - name: Verify ordinary PostgreSQL execution\n', 1)[1]
        step = step.split('\n      - name:', 1)[0]
        self.script = textwrap.dedent(step.split('        run: |\n', 1)[1])

    def test_service_and_connection_follow_existing_dotnet_selection(self):
        """Only selected .NET runs get a service; discovery keeps its original exclusions."""
        service = self.workflow.split('    services:\n', 1)[1].split('    steps:\n', 1)[0]
        self.assertRegex(service, re.compile(
            r"image: \$\{\{ needs\.classify\.outputs\.run_dotnet == 'true' && "
            r"'postgis/postgis:18-3\.6@sha256:[a-f0-9]{64}' \|\| '' }}"))
        self.assertIn('127.0.0.1:5432:5432', service)
        ordinary = self.workflow.split('      - name: Run ordinary tests\n', 1)[1]
        ordinary = ordinary.split('\n      #', 1)[0]
        self.assertIn("if: needs.classify.outputs.run_dotnet == 'true'", ordinary)
        self.assertIn('WAYFARER_TEST_POSTGRES_CONNECTION:', ordinary)
        self.assertIn('--filter "Category!=RequiresSpatialite&Category!=RequiresPlaywright&Category!=RequiresRoot"', ordinary)
        self.assertEqual(self.workflow.count('- name: Run ordinary tests\n'), 1)

    def run_evidence(self, outcome=None, reason=None):
        """Run the workflow assertion against a minimal runner-format result in an owned tree."""
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / 'wayfarer-ordinary-results/ordinary.trx'
            if outcome is not None:
                report.parent.mkdir()
                root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
                results = ET.SubElement(root, 'Results')
                if outcome:
                    result = ET.SubElement(results, 'UnitTestResult',
                                           testName='FixtureGuardTests.NoPostgresSuffix', outcome=outcome)
                    if reason:
                        output = ET.SubElement(result, 'Output')
                        ET.SubElement(output, 'StdOut').text = reason
                ET.ElementTree(root).write(report)
            return subprocess.run([sys.executable, '-c', self.script],
                                  env=dict(os.environ, RUNNER_TEMP=directory), capture_output=True, text=True)

    def test_passes_and_unrelated_skips_are_retained(self):
        """An intentional unrelated prerequisite skip is valid evidence, not a database failure."""
        for outcome, reason in [('Passed', None), ('NotExecuted', 'Requires a separately attached device')]:
            with self.subTest(outcome=outcome):
                result = self.run_evidence(outcome, reason)
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_postgres_prerequisite_skips_fail_regardless_of_test_name(self):
        """Attribute and runtime skip diagnostics must fail even outside PostgresTests classes."""
        for reason in ['Set WAYFARER_TEST_POSTGRES_CONNECTION to run fixture tests.',
                       'The dedicated wayfarer_import_tests database is absent.', 'PostGIS is unavailable.']:
            with self.subTest(reason=reason):
                result = self.run_evidence('NotExecuted', reason)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('PostgreSQL prerequisite skips:', result.stderr)

    def test_missing_or_empty_results_fail_closed(self):
        """No result artifact cannot stand in for a real ordinary test run."""
        for outcome in [None, '']:
            with self.subTest(outcome=outcome):
                self.assertNotEqual(self.run_evidence(outcome).returncode, 0)


if __name__ == '__main__':
    unittest.main()
