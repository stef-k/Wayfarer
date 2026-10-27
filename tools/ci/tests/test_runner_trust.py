"""Keep workflow-controlled acquisition behind the existing host trust boundary."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / '.github/workflows/tests.yml'
TRUST_STEP = 'Correct and verify host trust before setup downloads'
CONSUMERS = {
    'tests.yml': ['test', 'application-image', 'application-image-arm64', 'cleanup-safety'],
    'application-release.yml': ['publish', 'anonymous-pull', 'publish-index',
                                'deployment-bundle', 'public-compose-acceptance'],
    'database-image.yml': ['publish', 'anonymous-pull', 'publish-index'],
    'public-lifecycle-acceptance.yml': ['public-lifecycle-amd64'],
    'public-operator-acceptance.yml': ['public-operator-amd64'],
}


def job_body(workflow, job):
    """Read one job from the repository's ordinary two-space workflow layout."""
    return re.split(r'\n  (?=\S)', workflow.split(f'\n  {job}:\n', 1)[1], 1)[0]


def step_script(body, name):
    """Extract a shipped multiline step, retaining its executable failure behavior."""
    step = body.split(f'      - name: {name}\n', 1)[1]
    step = re.split(r'\n      - ', step, 1)[0] + '\n'
    return textwrap.dedent(re.search(r'        run: \|\n((?: {10}[^\n]*\n|\n)+)', step)[1])


class RunnerTrustTests(unittest.TestCase):
    """Every selected Linux acquisition shares signed APT and the effective-root guard."""

    def test_host_correction_and_guard_precede_acquisition(self):
        """Only checkout/source identity may precede correction; setup/API work follows it."""
        guard = (ROOT / 'deployment/check-trust.sh').read_text()
        host_check = guard.split("python3 - <<'PY'\n", 1)[1].split('\nPY\n', 1)[0]
        for filename, jobs in CONSUMERS.items():
            workflow = (WORKFLOW.parent / filename).read_text()
            for job in jobs:
                with self.subTest(workflow=filename, job=job):
                    body = job_body(workflow, job)
                    # Cleanup's shared matrix selects these same paths only on Linux.
                    environment = workflow.split('jobs:\n', 1)[0] + body.split('    steps:\n', 1)[0]
                    for key, value in [('NODE_OPTIONS', '--use-openssl-ca'),
                                       ('SSL_CERT_FILE', '/etc/ssl/certs/ca-certificates.crt'),
                                       ('SSL_CERT_DIR', '/etc/ssl/certs')]:
                        expected = f"${{{{ matrix.os == 'ubuntu-latest' && '{value}' || '' }}}}" if job == 'cleanup-safety' else value
                        self.assertIn(f'{key}: {expected}', environment)
                    before, after = body.split(f'      - name: {TRUST_STEP}\n', 1)
                    self.assertNotRegex(before, r'uses: (?:actions/setup-|docker/setup-)|\bcurl\b|docker login|tools/(?:release|compose)/')
                    script = step_script(body, TRUST_STEP)
                    commands = script.strip().splitlines()
                    self.assertEqual(commands[:2], [
                        'sudo apt-get -o APT::Update::Error-Mode=any update',
                        'sudo apt-get install -y --no-install-recommends ca-certificates'])
                    self.assertNotRegex(after.split('\n      - ', 1)[0], r'continue-on-error:|if: always\(\)')
                    if job == 'public-compose-acceptance':
                        self.assertNotIn('actions/checkout@', body)
                        self.assertIn(host_check, script)
                        self.assertLess(body.index(TRUST_STEP), body.index('curl --fail'))
                    else:
                        self.assertIn('uses: actions/checkout@', before)
                        self.assertEqual(commands[2:], ['bash deployment/check-trust.sh'])

    def test_failed_prerequisite_stops_acquisition(self):
        """Execute both shipped prerequisite forms; failed APT or trust cannot reach a download."""
        scripts = [step_script(job_body(WORKFLOW.read_text(), 'test'), TRUST_STEP),
                   step_script(job_body((WORKFLOW.parent / 'application-release.yml').read_text(),
                                        'public-compose-acceptance'), TRUST_STEP)]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            sudo = path / 'sudo'
            sudo.write_text('#!/bin/bash\nif [[ "$*" == *"$FAIL_APT"* && -n "$FAIL_APT" ]]; then exit 31; fi\n')
            sudo.chmod(0o755)
            empty = path / 'empty.pem'
            empty.touch()
            ca_dir = path / 'certs'
            ca_dir.mkdir()
            for script in scripts:
                for fail_apt, trusted in [('update', True), ('install', True), ('', False), ('', True)]:
                    with self.subTest(inline='check-trust.sh' not in script, fail_apt=fail_apt, trusted=trusted):
                        env = dict(os.environ, PATH=f'{path}:{os.environ["PATH"]}', FAIL_APT=fail_apt,
                                   SSL_CERT_FILE='/etc/ssl/certs/ISRG_Root_X1.pem' if trusted else str(empty),
                                   SSL_CERT_DIR=str(ca_dir))
                        result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c',
                                                 script + 'echo ACQUISITION_REACHED'],
                                                cwd=ROOT, env=env, text=True, capture_output=True)
                        success = not fail_apt and trusted
                        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                        self.assertEqual('ACQUISITION_REACHED' in result.stdout, success)

    def test_windows_cleanup_uses_only_validated_tool_cache(self):
        """A missing cache must throw; setup actions are Linux-only and tests stay offline."""
        body = job_body(WORKFLOW.read_text(), 'cleanup-safety')
        for action in ['node', 'python']:
            step = re.search(rf'      - (?:name: [^\n]+\n)?[^\n]*uses: actions/setup-{action}@.*?(?=\n      - )', body, re.DOTALL)[0]
            self.assertIn("if: runner.os == 'Linux'", step)
        step = body.split('      - name: Select provisioned Windows cleanup tools\n', 1)[1]
        step = step.split('\n      - ', 1)[0]
        self.assertIn("if: runner.os == 'Windows'", step)
        self.assertIn('shell: pwsh', step)
        for required in ['$env:RUNNER_TOOL_CACHE', 'Get-Content .nvmrc', '^3\\.12\\.\\d+$',
                         'node.exe', 'python.exe', '$LASTEXITCODE', 'throw', '$env:GITHUB_PATH']:
            self.assertIn(required, step)
        self.assertNotRegex(step, r'setup-(?:node|python)|Invoke-WebRequest|\bpip\b|\binstall\b')
        self.assertLess(body.index('Select provisioned Windows cleanup tools'),
                        body.index('npm run test:cleanup:safety'))
        self.assertIn('python -B -m unittest discover -s tools/tests -v', body)

    def test_rustup_downloads_require_the_assessed_backend(self):
        """An explicit rustls selection follows host/version/configuration checks on all users."""
        workflow = WORKFLOW.read_text()
        for job in ['test', 'application-image', 'application-image-arm64']:
            body = job_body(workflow, job)
            self.assertIn('bash deployment/check-trust.sh --rustup\n          RUSTUP_USE_RUSTLS=1 rustup toolchain install 1.90.0', body)

    def test_unassessed_rustup_configuration_stops_download(self):
        """The existing guard rejects changed downloader, source or rustup before acquisition."""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            tool = path / 'rustup'
            tool.write_text('#!/bin/bash\necho "rustup ${TEST_RUSTUP_VERSION:-1.29.1} (fixture)"\n')
            tool.chmod(0o755)
            cases = [{}, {'TEST_RUSTUP_VERSION': '1.28.2'}, {'RUSTUP_USE_CURL': '1'},
                     {'RUSTUP_USE_CURL': ''}, {'RUSTUP_USE_RUSTLS': '0'},
                     {'RUSTUP_DIST_SERVER': 'https://unassessed.invalid'},
                     {'RUSTUP_DIST_ROOT': 'https://unassessed.invalid/dist'},
                     {'RUSTUP_UPDATE_ROOT': 'https://unassessed.invalid/rustup'}]
            for overrides in cases:
                with self.subTest(overrides=overrides):
                    env = {key: value for key, value in os.environ.items() if not key.startswith('RUSTUP_')}
                    env.update(PATH=f'{path}:{os.environ["PATH"]}',
                               SSL_CERT_FILE='/etc/ssl/certs/ISRG_Root_X1.pem', SSL_CERT_DIR=directory)
                    env.update(overrides)
                    result = subprocess.run(['bash', '-e', '-c',
                        'bash deployment/check-trust.sh --rustup; echo ACQUISITION_REACHED'],
                        cwd=ROOT, env=env, text=True, capture_output=True)
                    self.assertEqual(result.returncode == 0, not overrides, result.stdout + result.stderr)
                    self.assertEqual('ACQUISITION_REACHED' in result.stdout, not overrides)

    def test_public_bootstrap_receipt_hash_precedes_extraction(self):
        """The public endpoint never supplies the sole expected bootstrap digest."""
        body = job_body((WORKFLOW.parent / 'application-release.yml').read_text(),
                        'public-compose-acceptance')
        self.assertLess(body.index('actions/download-artifact@'), body.index('curl --fail'))
        self.assertLess(body.index('publication/publication.json'), body.index('sha256sum --check'))
        self.assertLess(body.index('sha256sum --check'), body.index('sudo tar --extract'))
        self.assertLess(body.index('sudo tar --extract'), body.index('setup --hostname'))

    def test_public_bootstrap_wrong_hash_stops_before_extraction(self):
        """Run the retained receipt check against intended and substituted archive bytes."""
        body = job_body((WORKFLOW.parent / 'application-release.yml').read_text(),
                        'public-compose-acceptance')
        script = step_script(body, 'Install from the public lean bootstrap, then exercise fresh setup and doctor')
        check = script.split('expected=', 1)[1]
        check = 'expected=' + check.split('sudo install -d -m 700 /tmp/wayfarer-public-acceptance/bootstrap', 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'publication').mkdir()
            archive = root / 'wayfarerctl-linux-amd64.tar.gz'
            expected = hashlib.sha256(b'intended').hexdigest()
            (root / 'publication/publication.json').write_text(json.dumps({'assets': {archive.name: 'sha256:' + expected}}))
            for payload in [b'intended', b'substituted']:
                archive.write_bytes(payload)
                result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c',
                    'asset=wayfarerctl-linux-amd64.tar.gz\n' + check + 'echo EXTRACTION_REACHED'],
                    env=dict(os.environ, RUNNER_TEMP=directory), text=True, capture_output=True)
                self.assertEqual(result.returncode == 0, payload == b'intended', result.stderr)
                self.assertEqual('EXTRACTION_REACHED' in result.stdout, payload == b'intended')

    def test_node_selection_pins_verified_patch_before_node_guard(self):
        """A major-only selector must not accept the runner's cached Node 24.21.0."""
        version = (ROOT / '.nvmrc').read_text().strip()
        self.assertEqual(version, '24.22.1')
        self.assertIn(f'FROM node:{version}-bookworm-slim@sha256:',
                      (ROOT / 'Dockerfile').read_text())
        workflow = WORKFLOW.read_text()
        setups = re.findall(r'uses: actions/setup-node@[^\n]+\n(.*?)(?=\n      -)',
                            workflow, re.DOTALL)
        self.assertEqual(len(setups), 2)
        for setup in setups:
            self.assertIn('node-version-file: .nvmrc', setup)
            self.assertNotRegex(setup, r'\bnode-version:|check-latest:')
        job = job_body(workflow, 'test')
        self.assertLess(job.index('uses: actions/setup-node@'),
                        job.index('bash deployment/check-trust.sh --node'))
