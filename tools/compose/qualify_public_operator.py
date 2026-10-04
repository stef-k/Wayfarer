"""One disposable Linux AMD64 #764 witness using only public stable v1.9.22 bytes.

PR tests qualify this evidence contract. Public runtime acceptance is a separate
post-merge dispatch; this runner cannot publish, update or restore a release.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import tempfile
import time
import uuid

from qualify_ctl import Journey, QUALIFICATION_PROXY_LABEL, run
import public_lifecycle_evidence as evidence

TAG = 'v1.9.22'
UPLOAD = '/var/lib/wayfarer/uploads/imports/operator-qualification'
VALUE = 'wayfarer-764-continuity'
CHECKS = {
    'acquisition': ('exact', 'latest', 'sameRetainedRelease', 'noActivation'),
    'setupResume': ('webAcknowledgementLost', 'originalInputsAndVolumes', 'completedCheckpoints',
                    'mutationsSkipped', 'setupComplete', 'doctor', 'httpsLogin'),
    'operations': ('status', 'restart', 'doctor', 'wayfarerLogs', 'dbLogs',
                   'releaseSecretsVolumesAndFile', 'originalCookie', 'httpsHealth'),
    'backups': ('listed', 'exactVerify', 'defaultVerify', 'schedulerStopped', 'disabledRefusal',
                'disabledListedAndVerified', 'selectedPairPreserved', 'newGenerations',
                'schedulerReenabled', 'releasedCapture', 'installationContinuity', 'originalCookie'),
}
COMMAND_WORDS = {'setup', '--resume', 'release', 'acquire', TAG[1:], 'latest', 'inspect', 'dispatch',
                 'status', 'restart', 'doctor', 'logs', 'wayfarer', 'db', 'backup', 'configure',
                 '--disable', '--quiesced', 'backups', 'verify-backup', 'start'}


def required_checks(observed, names):
    """Only complete true observations enter a sanitized scenario record."""
    evidence.require(all(observed.get(name) is True for name in names))
    return {name: True for name in names}


def acquired_facts(output, retained):
    """Require the standalone handler's public execution-ready response without exporting paths."""
    evidence.require(len(output.encode()) <= 32768)
    value = json.loads(output)
    evidence.require(value['Path'] == str(retained) and value['Version'] == TAG[1:])
    evidence.require(value['Images'] == 'execution-ready' and value['Publisher'] == 'public-stable-GitHub')
    evidence.require(value['Integrity'] == 'GitHub-asset-SHA256')
    return {'fingerprint': evidence.checked(value['Fingerprint'], evidence.HASH), 'version': TAG[1:]}


def verified_archive(output, archive):
    """Exact and default verification must both identify the explicitly selected complete archive."""
    found = re.findall(r'^Archive ([a-f0-9-]{36})$', output, re.MULTILINE)
    evidence.require(found == [evidence.identity(archive)])
    evidence.require(output.splitlines().count('Integrity: True; compatible: True') == 1)


def policy_transition(before, after, enabled):
    """Supported policy toggles preserve all installation and source fields in a new generation."""
    previous = evidence.checked(before['Backup']['Generation'], evidence.HASH)
    current = evidence.checked(after['Backup']['Generation'], evidence.HASH)
    evidence.require(previous != current and after['Backup']['Enabled'] is enabled)
    expected = copy.deepcopy(before)
    expected['Backup'].update(Enabled=enabled, Generation=current)
    evidence.require(after == expected)


class OperatorJourney(Journey):
    """Reuse public bootstrap, host, Compose, TLS, login and exact labelled cleanup helpers."""
    def __init__(self, directory, executable, public, log):
        super().__init__(directory, executable, '')
        self.project = 'wayfarer-764-' + uuid.uuid4().hex[:10]
        self.proxy = self.project + '-proxy'
        self.public, self.log = public, log
        self.stage, self.command_count = 'acquisition', 0
        self.destination = self.directory / 'destination'
        self.destination.mkdir(mode=0o700)

    def ctl(self, *args, data=None, expected=0):
        """Record bounded command/expected-result identities; protected input and raw output stay private."""
        result = super().ctl(*args, data=data, check=False)
        evidence.require(self.password not in result.stdout + result.stderr)
        self.command_count += 1
        evidence.require(self.command_count <= 64)
        with self.log.open('a') as output:
            output.write(json.dumps({'stage': self.stage,
                'command': ' '.join(arg for arg in args if arg in COMMAND_WORDS),
                'exitCode': result.returncode, 'expectedExitCode': expected,
                'protectedStdin': data is not None}) + '\n')
        evidence.require(result.returncode == expected)
        return result

    def read_json(self, path):
        """Read bounded product observations in memory, never upload raw configuration or receipts."""
        value = self.host('cat', str(path)).stdout
        evidence.require(len(value.encode()) <= 1024 * 1024)
        return json.loads(value)

    def config(self):
        """The protected installation pointer owns the selected release and backup generation."""
        return self.read_json(self.install / 'installation.json')

    def file_hash(self, path):
        """Read file digests through the existing root host adapter without retaining content."""
        return evidence.checked(self.host('sha256sum', str(path)).stdout.split()[0], evidence.HASH)

    def empty_resources(self):
        """Check every existing cleanup selector for this task's exact random project."""
        labels = ('com.docker.compose.project=', 'wayfarer.restore-helper=', 'wayfarer.update-project=',
                  QUALIFICATION_PROXY_LABEL + '=')
        for listing in (('ps', '-aq'), ('network', 'ls', '-q'), ('volume', 'ls', '-q')):
            for label in labels:
                evidence.require(not run('docker', *listing, '--filter', 'label=' + label + self.project).stdout.strip())

    def acquire(self):
        """Exact and latest standalone prefetch share retained bytes and create no installation or services."""
        retained = self.install / 'releases' / TAG
        facts = []
        for selector in (TAG[1:], 'latest'):
            facts.append(acquired_facts(self.ctl('release', 'acquire', selector).stdout, retained))
            entries = self.host('ls', '-A', str(self.install)).stdout.splitlines()
            evidence.require(set(entries) == {'operation.lock', 'releases'})
            self.empty_resources()
        evidence.require(facts[0] == facts[1])
        self.acquired = facts[0]
        return required_checks(dict.fromkeys(CHECKS['acquisition'], True), CHECKS['acquisition'])

    def snapshot(self):
        """Compare protected inputs, durable volume identities and the one file without exporting private facts."""
        config = {key: value for key, value in self.config().items() if key != 'Backup'}
        credentials = self.host('sh', '-ec', 'sha256sum "$1"/secrets/* "$1"/deployment.env',
                                'inputs', str(self.install)).stdout
        names = sorted(run('docker', 'volume', 'ls', '-q', '--filter',
                           'label=com.docker.compose.project=' + self.project).stdout.split())
        evidence.require(len(names) >= 2)
        volumes = json.loads(run('docker', 'volume', 'inspect', *names).stdout)
        upload = self.compose('exec', '-T', 'wayfarer', 'sha256sum', UPLOAD).split()[0]
        evidence.require(upload == hashlib.sha256(VALUE.encode()).hexdigest())
        return {'config': config, 'credentials': credentials, 'volumes': volumes, 'upload': upload}

    def setup_resume(self, cookie):
        """Lose only real web-start acknowledgement, then resume the same original completed mutations."""
        self.stage = 'setup-resume'
        self.host('tee', str(self.directory / 'failure'), data='web')
        self.ctl('setup', '--version', TAG[1:], '--hostname', 'wayfarer.example.org',
                 '--project', self.project, '--edge-prefix', '172.30.76', '--mode', 'external',
                 '--loopback-port', str(self.loopback), '--password-stdin', data=self.password + '\n', expected=1)
        checkpoint = self.read_json(self.install / 'setup-progress.json')
        evidence.require(checkpoint['Schema'] == 1 and checkpoint['Completed'] == 3 and checkpoint['AdminStarted'] is True)
        evidence.require(self.host('test', '-e', str(self.install / 'setup-complete'), check=False).returncode == 1)
        evidence.require(len(self.running('wayfarer')) == 1)
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                     'mkdir -p /var/lib/wayfarer/uploads/imports; printf %s "$1" > "$2"', 'upload', VALUE, UPLOAD)
        before = self.snapshot()
        self.host('tee', str(self.directory / 'failure'), data='')
        resumed = self.ctl('setup', '--resume').stdout
        evidence.require(all(name not in resumed for name in ('Preparing database structure',
                         'Preparing initial application data', 'Creating the administrator account')))
        evidence.require(self.read_json(self.install / 'setup-progress.json') == checkpoint and self.snapshot() == before)
        evidence.require(self.host('cat', str(self.install / 'setup-complete')).stdout == '1\n')
        self.validate_release()
        self.start_proxy(self.read_json(Path(self.config()['Bundle']) / 'release.json')['Images']['CaddyDigest'])
        evidence.require(self.curl('/health/ready', '--retry', '5', '--retry-connrefused', '--retry-max-time', '30') == 'ready')
        self.ctl('dispatch', 'doctor')
        self.authenticate(cookie)
        return required_checks(dict.fromkeys(CHECKS['setupResume'], True), CHECKS['setupResume'])

    def validate_release(self):
        """Corroborate the retained public manifest, acquisition fingerprint and actual executed operator."""
        config = self.config()
        manifest = self.read_json(Path(config['Bundle']) / 'release.json')
        inspected = json.loads(self.ctl('release', 'inspect', config['Bundle']).stdout)
        release = evidence.authority(config['Release'])
        evidence.require(inspected['Integrity'] == 'validated' and inspected['Status'] == 'stable')
        evidence.require(inspected['Fingerprint'] == release['fingerprint'] == self.acquired['fingerprint'])
        evidence.require(manifest['Status'] == 'stable' and manifest['Tag'] == release['name'] == TAG)
        evidence.require(manifest['Version'] == release['operatorVersion'] == manifest['Operator']['Version'] == TAG[1:])
        evidence.require(manifest['Platform'] == 'linux/amd64')
        evidence.require(self.file_hash(self.executable) == self.file_hash(Path(config['Bundle']) / 'wayfarerctl') ==
                         release['operatorSha256'] == self.public['executedBootstrapSha256'])
        evidence.require(config['AppDigest'] == manifest['Images']['PlatformDigest'])
        evidence.require(config['DbDigest'] == manifest['Images']['DatabaseDigest'])
        return {'release': release, 'manifestVersion': TAG[1:],
                'sourceRevision': evidence.checked(manifest['SourceRevision'], r'[a-f0-9]{40}'),
                'project': evidence.checked(config['Project'], r'wayfarer-764-[a-f0-9]{10}'),
                'appDigest': evidence.checked(config['AppDigest'], 'sha256:' + evidence.HASH),
                'dbDigest': evidence.checked(config['DbDigest'], 'sha256:' + evidence.HASH)}

    def operations(self, cookie):
        """One dispatched status/restart/doctor and bounded service-log routing retain state and login."""
        self.stage = 'ordinary-operations'
        before = self.snapshot()
        self.ctl('dispatch', 'status')
        self.ctl('dispatch', 'restart')
        self.ctl('dispatch', 'doctor')
        for service in ('wayfarer', 'db'):
            logs = self.ctl('dispatch', 'logs', service, '--tail', '10').stdout
            evidence.require(0 < len(logs.encode()) <= 1024 * 1024)
        evidence.require(self.snapshot() == before and self.curl('/health/ready') == 'ready')
        evidence.require(self.curl('/Admin/Users', '-b', cookie, '-o', '/dev/null', '-w', '%{http_code}') == '200')
        self.validate_release()
        return required_checks(dict.fromkeys(CHECKS['operations'], True), CHECKS['operations'])

    def running(self, service):
        """Select only running containers with both exact product project and service labels."""
        return run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + self.project,
                   '--filter', 'label=com.docker.compose.service=' + service).stdout.split()

    def scheduler(self, enabled):
        """Observe stop or the actual running scheduler's coherent mounted worker generation."""
        containers = self.running('backup-scheduler')
        evidence.require(len(containers) == (1 if enabled else 0))
        if enabled:
            config = self.config()
            worker = json.loads(run('docker', 'exec', containers[0], 'cat', '/config/worker.json').stdout)
            policy = config['Backup']
            evidence.require(worker['Generation'] == policy['Generation'] and worker['Enabled'] is True)
            evidence.require(worker['Installation'] == config['Installation'] and worker['Source'] == policy['Source'])
            expected = self.read_json(self.install / 'recovery-generations' / policy['Generation'] / 'worker.json')
            evidence.require(worker == expected)

    def wait_scheduled_capture(self):
        """Reuse the existing bounded committed-receipt observation before competing with initial capture."""
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            result = self.host('cat', str(self.install / 'recovery-control/state/scheduler.json'), check=False)
            if result.returncode == 0 and json.loads(result.stdout)['Succeeded'] is True:
                return
            time.sleep(0.25)
        evidence.require(False)

    def capture(self):
        """Select one released capture by its returned UUID and verify that exact archive and default selection."""
        captured = self.ctl('dispatch', 'backup', '--quiesced').stdout
        name, archive = evidence.selected_name(captured, self.host('ls', str(self.destination)).stdout)
        evidence.require(name + ' (listing only; not fully verified)' in self.ctl('dispatch', 'backups').stdout.splitlines())
        verified_archive(self.ctl('dispatch', 'verify-backup', name).stdout, archive)
        verified_archive(self.ctl('dispatch', 'verify-backup').stdout, archive)
        path = self.destination / name
        manifest = self.read_json_archive(path)
        source = self.validate_release()
        source['installation'] = evidence.identity(self.config()['Installation'])
        evidence.require(manifest['Source'] == self.config()['Backup']['Source'])
        return evidence.archive_facts(name, archive, self.file_hash(path),
                    self.host('cat', str(path) + '.sha256').stdout, manifest, source)

    def read_json_archive(self, path):
        """Read only the bounded manifest from a product-created archive, never restore or extract payloads."""
        value = self.host('tar', '-xOf', str(path), 'manifest.json').stdout
        evidence.require(len(value.encode()) <= 1024 * 1024)
        return json.loads(value)

    def pair(self, name):
        """Compare both exact selected archive and sidecar bytes across policy changes."""
        return (self.file_hash(self.destination / name), self.file_hash(self.destination / (name + '.sha256')))

    def backups(self, cookie):
        """One public discovery/disable/refusal/reconfigure cycle preserves the selected pair and installation."""
        self.stage = 'backup-toggle'
        configure = ('dispatch', 'backup', 'configure', '--destination', str(self.destination),
                     '--payload', str(Path(self.config()['Bundle']) / 'wayfarer-recovery'), '--retention', '7')
        self.ctl(*configure)
        self.scheduler(True)
        self.wait_scheduled_capture()
        before = self.config()
        evidence.require(before['Backup']['Source']['ReleaseStatus'] == 'released')
        continuity = self.snapshot()
        selected = self.capture()
        self.ctl('dispatch', 'start')
        pair = self.pair(selected['basename'])
        self.stage = 'backup-disable'
        self.ctl('dispatch', 'backup', 'configure', '--disable')
        disabled = self.config()
        policy_transition(before, disabled, False)
        self.scheduler(False)
        names = self.host('ls', '-A', str(self.destination)).stdout
        refused = self.ctl('dispatch', 'backup', expected=2)
        evidence.require('Backup disabled.' in refused.stderr)
        evidence.require(self.host('ls', '-A', str(self.destination)).stdout == names)
        evidence.require(selected['basename'] + ' (listing only; not fully verified)' in
                         self.ctl('dispatch', 'backups').stdout.splitlines())
        verified_archive(self.ctl('dispatch', 'verify-backup', selected['basename']).stdout, selected['archive'])
        verified_archive(self.ctl('dispatch', 'verify-backup').stdout, selected['archive'])
        evidence.require(self.pair(selected['basename']) == pair)
        self.stage = 'backup-reconfigure'
        self.ctl(*configure)
        enabled = self.config()
        policy_transition(disabled, enabled, True)
        evidence.require(enabled['Backup']['Generation'] != before['Backup']['Generation'])
        self.scheduler(True)
        self.wait_scheduled_capture()
        reenabled = self.capture()
        evidence.require(reenabled['archive'] != selected['archive'] and self.pair(selected['basename']) == pair)
        self.ctl('dispatch', 'start')
        self.scheduler(True)
        self.ctl('dispatch', 'doctor')
        evidence.require(self.snapshot() == continuity)
        evidence.require(self.curl('/Admin/Users', '-b', cookie, '-o', '/dev/null', '-w', '%{http_code}') == '200')
        return required_checks(dict.fromkeys(CHECKS['backups'], True), CHECKS['backups']), [selected, reenabled]

    def cleanup(self):
        """Remove and verify only this exact task's labelled Docker resources and private fixture state."""
        super().cleanup()
        self.empty_resources()


def main():
    """Publish PASS only after all selected scenarios, expected results and private-resource cleanup succeed."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', default=TAG, help='Accepted already-published baseline; currently v1.9.22 only.')
    parser.add_argument('--output', required=True, type=Path, help='New sanitized artifact directory.')
    args = parser.parse_args()
    stage, log = 'public-provenance', None
    try:
        args.output.mkdir(mode=0o700)
        log = args.output / 'commands.jsonl'
        evidence.require(args.tag == TAG and platform.system() == 'Linux' and platform.machine() == 'x86_64')
        public = evidence.public_release(TAG)
        latest = evidence.publication(evidence.gh_read('repos/stef-k/Wayfarer/releases/latest'), TAG)
        evidence.require(latest == public)
        with tempfile.TemporaryDirectory(prefix='wayfarer-764-') as directory:
            root = Path(directory)
            executable = evidence.bootstrap(public, root / 'bootstrap')
            (root / 'journey').mkdir(mode=0o700)
            journey = OperatorJourney(root / 'journey', executable, public, log)
            primary_failure = False
            try:
                journey.prepare_public(faults=True)
                scenarios = {'acquisition': journey.acquire()}
                with tempfile.NamedTemporaryFile() as cookie:
                    scenarios['setupResume'] = journey.setup_resume(cookie.name)
                    scenarios['operations'] = journey.operations(cookie.name)
                    scenarios['backups'], captures = journey.backups(cookie.name)
                release = journey.validate_release()
                evidence.require(set(scenarios) == set(CHECKS))
                scenarios = {name: required_checks(scenarios[name], checks) for name, checks in CHECKS.items()}
            except Exception:
                stage, primary_failure = journey.stage, True
                raise
            finally:
                try:
                    journey.cleanup()
                    with log.open('a') as output:
                        output.write(json.dumps({'stage': 'cleanup', 'result': 'PASS'}) + '\n')
                except Exception:
                    with log.open('a') as output:
                        output.write(json.dumps({'stage': 'cleanup', 'result': 'FAIL'}) + '\n')
                    if not primary_failure:
                        stage = 'cleanup'
                        raise
        evidence.require(not root.exists())
        record = {'schema': 1, 'result': 'PASS', 'platform': 'linux/amd64', 'public': public,
                  'httpsScope': 'fixture-controlled-external-TLS', 'release': release, 'scenarios': scenarios,
                  'captures': captures, 'cleanup': {'dockerResourcesRemoved': True, 'privateStateRemoved': True}}
        for variable, field in (('GITHUB_RUN_ID', 'workflowRun'), ('GITHUB_RUN_ATTEMPT', 'workflowAttempt')):
            if variable in os.environ:
                record[field] = evidence.checked(os.environ[variable], r'[1-9][0-9]{0,20}')
        (args.output / 'operator.json').write_text(json.dumps(record, indent=2) + '\n')
        print('PASS public stable operator witness; sanitized observations and command results retained.')
        return 0
    except Exception:
        if log is not None:
            with log.open('a') as output:
                output.write(json.dumps({'stage': stage, 'result': 'FAIL'}) + '\n')
        print('FAIL public stable operator witness at ' + stage + '; no PASS record emitted.')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
