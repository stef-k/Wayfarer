"""Keep patched Node selection and signed host trust ahead of guarded downloads."""
from pathlib import Path
import re
import textwrap
import unittest


WORKFLOW = Path(__file__).resolve().parents[3] / '.github/workflows/tests.yml'


class RunnerTrustTests(unittest.TestCase):
    """AMD64 test/image and native ARM64 jobs share the same prerequisite boundary."""

    def test_host_correction_and_guard_precede_setup_actions(self):
        """Checkout remains earlier; signed APT must succeed before the guard and setup."""
        workflow = WORKFLOW.read_text()
        for job in ['test', 'application-image', 'application-image-arm64']:
            with self.subTest(job=job):
                body = re.split(r'\n  (?=\S)', workflow.split(f'\n  {job}:\n', 1)[1], 1)[0]
                self.assertIn('NODE_OPTIONS: --use-openssl-ca', body)
                self.assertIn('SSL_CERT_FILE: /etc/ssl/certs/ca-certificates.crt', body)
                self.assertIn('SSL_CERT_DIR: /etc/ssl/certs', body)
                name = '      - name: Correct and verify host trust before setup downloads\n'
                before, after = body.split(name, 1)
                self.assertIn('uses: actions/checkout@', before)
                self.assertNotIn('uses: actions/setup-', before)
                self.assertNotIn('uses: docker/setup-', before)
                script = textwrap.dedent(re.search(r'        run: \|\n((?:          .+\n)+)', after)[1])
                self.assertEqual(script.strip().splitlines(), [
                    'sudo apt-get -o APT::Update::Error-Mode=any update',
                    'sudo apt-get install -y --no-install-recommends ca-certificates',
                    'bash deployment/check-trust.sh'])
                self.assertIn('uses: actions/setup-', after)

    def test_node_selection_pins_verified_patch_before_node_guard(self):
        """A major-only selector must not accept the runner's cached Node 24.21.0."""
        root = WORKFLOW.parents[2]
        version = (root / '.nvmrc').read_text().strip()
        self.assertEqual(version, '24.22.1')
        self.assertIn(f'FROM node:{version}-bookworm-slim@sha256:',
                      (root / 'Dockerfile').read_text())
        workflow = WORKFLOW.read_text()
        setups = re.findall(r'uses: actions/setup-node@[^\n]+\n(.*?)(?=\n      -)',
                            workflow, re.DOTALL)
        self.assertEqual(len(setups), 2)
        for setup in setups:
            self.assertIn('node-version-file: .nvmrc', setup)
            self.assertNotRegex(setup, r'\bnode-version:|check-latest:')
        job = workflow.split('\n  test:\n', 1)[1].split('\n  cleanup-safety:', 1)[0]
        self.assertLess(job.index('uses: actions/setup-node@'),
                        job.index('bash deployment/check-trust.sh --node'))
