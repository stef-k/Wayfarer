"""Keep signed host-CA correction ahead of workflow-controlled setup downloads."""
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
