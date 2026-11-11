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
    'tests.yml': ['test', 'application-image', 'application-image-arm64', 'cleanup-safety', 'caddy-replacement'],
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
        for required in ['$env:RUNNER_TOOL_CACHE', '[version]\'24.22.1\'', '^3\\.12\\.\\d+$',
                         'node.exe', 'python.exe', '$LASTEXITCODE', 'throw', '$env:GITHUB_PATH']:
            self.assertIn(required, step)
        self.assertNotRegex(step, r'setup-(?:node|python)|Invoke-WebRequest|\bpip\b|\binstall\b')
        self.assertLess(body.index('Select provisioned Windows cleanup tools'),
                        body.index('npm run test:cleanup:safety'))
        self.assertIn('python -B -m unittest discover -s tools/tests -v', body)

    def test_rustup_downloads_require_the_assessed_backend(self):
        """An explicit rustls selection follows host/version/configuration checks on all users."""
        workflow = WORKFLOW.read_text()
        for job in ['test', 'application-image', 'application-image-arm64', 'caddy-replacement']:
            body = job_body(workflow, job)
            self.assertIn('bash deployment/check-trust.sh --rustup\n          RUSTUP_USE_RUSTLS=1 rustup toolchain install 1.90.0', body)

    def test_unassessed_rustup_configuration_stops_download(self):
        """The existing guard rejects changed downloader, source or rustup before acquisition."""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            tool = path / 'rustup'
            tool.write_text('#!/bin/bash\necho "rustup ${TEST_RUSTUP_VERSION:-1.29.1} (fixture)"\n')
            tool.chmod(0o755)
            cases = [{}, {'TEST_RUSTUP_VERSION': '1.29.2'},
                     {'TEST_RUSTUP_VERSION': '1.29.3'}, {'TEST_RUSTUP_VERSION': '1.28.2'}, {'RUSTUP_USE_CURL': '1'},
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
                    admitted = not overrides or overrides == {'TEST_RUSTUP_VERSION': '1.29.2'}
                    self.assertEqual(result.returncode == 0, admitted, result.stdout + result.stderr)
                    self.assertEqual('ACQUISITION_REACHED' in result.stdout, admitted)
                    self.assertIn('Rustup manager: rustup ' + overrides.get('TEST_RUSTUP_VERSION', '1.29.1'), result.stdout)
                    if not admitted:
                        rejected = next(iter(overrides))
                        self.assertIn('manager-version' if rejected == 'TEST_RUSTUP_VERSION' else rejected, result.stderr)
                        self.assertNotIn('unassessed.invalid', result.stdout + result.stderr)

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

    def test_dotnet_consumers_select_stable_major_ten(self):
        """Host setup accepts stable servicing/minors without selecting another major or previews."""
        for filename in ['tests.yml', 'application-release.yml', 'public-lifecycle-acceptance.yml']:
            workflow = (WORKFLOW.parent / filename).read_text()
            setups = re.findall(r'uses: actions/setup-dotnet@[^\n]+\n(.*?)(?=\n      -|\Z)',
                                workflow, re.DOTALL)
            self.assertTrue(setups, filename)
            for setup in setups:
                with self.subTest(workflow=filename, setup=setup):
                    self.assertIn("dotnet-version: '10.x'", setup)
                    self.assertIn("dotnet-quality: 'ga'", setup)

    def test_node_selection_pins_verified_patch_before_node_guard(self):
        """A major-only selector must not accept the runner's cached Node 24.21.0."""
        version = (ROOT / '.nvmrc').read_text().strip()
        self.assertEqual(version, '24.22.2')
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

    def run_windows_selection(self, installations):
        """Run the shipped PowerShell step with disposable executable tool-cache entries."""
        script = step_script(job_body(WORKFLOW.read_text(), 'cleanup-safety'),
                             'Select provisioned Windows cleanup tools')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for version, options in installations:
                folder = root / 'node' / version / 'x64'
                folder.mkdir(parents=True)
                if options.get('node', True):
                    node = folder / 'node.exe'
                    node.write_text('#!/bin/sh\n' +
                        f'if [ "$1" = --version ]; then echo "{options.get("actual", "v" + version)}"; '
                        f'else echo "{options.get("arch", "x64")}"; fi\n'
                        f'exit {options.get("exit", 0)}\n')
                    node.chmod(0o755)
                if options.get('npm', True):
                    (folder / 'npm.cmd').write_text('@echo off\n')
            python = root / 'Python/3.12.10/x64/python.exe'
            python.parent.mkdir(parents=True)
            python.write_text('#!/bin/sh\necho 3.12.10\n')
            python.chmod(0o755)
            selected = root / 'selected.txt'
            result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', script],
                                    cwd=ROOT, text=True, capture_output=True,
                                    env=dict(os.environ, RUNNER_TOOL_CACHE=directory,
                                             GITHUB_PATH=str(selected)))
            paths = selected.read_text(encoding='utf-8-sig').splitlines() if selected.exists() else []
            return result, [str(Path(p).relative_to(root)) for p in paths]

    def test_windows_node_selects_highest_eligible_numeric_version(self):
        """The minimum, newer patches/minors and numeric ordering survive shuffled cache order."""
        for versions, expected in [(['24.22.1'], '24.22.1'), (['24.22.2'], '24.22.2'),
                                   (['24.23.0'], '24.23.0'),
                                   (['24.22.9', '24.23.0', '24.22.10'], '24.23.0'),
                                   (['24.22.10', '24.22.2', '24.22.9'], '24.22.10')]:
            with self.subTest(versions=versions):
                result, paths = self.run_windows_selection([(v, {}) for v in versions])
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(paths, [f'node/{expected}/x64', 'Python/3.12.10/x64'])

    def test_windows_node_rejects_ineligible_or_incomplete_caches(self):
        """Names alone cannot admit unsafe, missing, mismatched or wrong-architecture tools."""
        cases = [[], [('24.21.9', {}), ('24.22.0', {}), ('22.23.4', {}),
                      ('25.0.0', {}), ('24.23.0-rc.1', {})]]
        cases += [[('24.22.2', options)] for options in [
            {'node': False}, {'npm': False}, {'actual': 'v24.22.1'},
            {'actual': 'v24.22.2-rc.1'}, {'arch': 'arm64'}, {'exit': 1}]]
        for installations in cases:
            with self.subTest(installations=installations):
                result, paths = self.run_windows_selection(installations)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('No eligible provisioned Node', result.stderr)
                self.assertEqual(paths, [])
        # Ignore invalid higher candidates and still choose the highest verified tool.
        result, paths = self.run_windows_selection([
            ('24.24.0', {'npm': False}), ('24.23.0', {'actual': 'v25.0.0'}),
            ('24.22.2', {}), ('24.22.1', {}), ('24.25.0-rc.1', {})])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(paths[0], 'node/24.22.2/x64')
