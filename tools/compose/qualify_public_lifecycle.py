"""One read-only-publication Linux AMD64 #748 lifecycle using actual stable assets.

Only disposable random projects and owned temporary paths are mutated. No candidate
assembly, release publication, migration injection or broad failure matrix runs here.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import tempfile
import uuid

from qualify_ctl import Journey
import public_lifecycle_evidence as evidence

UPLOAD = '/var/lib/wayfarer/uploads/imports/recovery-qualification'
VALUE = 'wayfarer-748-continuity'


class PublicJourney(Journey):
    """Reuse the established host/TLS/login/probe/cleanup seams for one state lineage."""
    def __init__(self, directory, executable, probe, log):
        super().__init__(directory, executable, '')
        self.project = 'wayfarer-748-' + uuid.uuid4().hex[:10]
        self.proxy = self.project + '-proxy'
        self.projects = [self.project]
        self.log = log
        self.stage = 'source-setup'
        self.command_count = 0
        self.passwords = [self.password]
        shutil.copy2(probe, self.directory / 'RecoveryProbe.dll')
        (self.directory / 'probe').mkdir()
        (self.directory / 'destination').mkdir()

    def ctl(self, *args, data=None, check=True, executable=None):
        """Ordinary commands use the fixed source bootstrap; direct repair shares sanitized result logging."""
        command = args[0]
        public_words = {'dispatch', 'release', 'user', 'backup', 'configure', 'inspect', 'target',
                        'import', 'adopt', 'find', 'reset-password', 'doctor', 'start', 'version', 'update', 'verify-backup',
                        'repair-backup-source-v1.9.21'}
        if command in public_words:
            command = ' '.join(arg for arg in args[:3] if arg in public_words)
        if executable is None:
            result = super().ctl(*args, data=data, check=False)
        else:
            evidence.require(args == ('release', 'repair-backup-source-v1.9.21'))
            result = self.host(str(executable), '--deployment-root', str(self.install), *args, data=data, check=False)
        evidence.require(all(password not in result.stdout + result.stderr for password in self.passwords))
        self.command_count += 1
        evidence.require(self.command_count <= 128)
        with self.log.open('a') as log:
            log.write(json.dumps({'stage': self.stage, 'command': command, 'exitCode': result.returncode,
                                  'protectedStdin': data is not None}) + '\n')
        if check:
            evidence.require(result.returncode == 0)
        return result

    def read_json(self, path):
        """Read protected product observations in memory; raw JSON never becomes an artifact."""
        value = self.host('cat', str(path)).stdout
        evidence.require(len(value.encode()) <= 1024 * 1024)
        return json.loads(value)

    def config(self):
        """The installed product pointer supplies every active bundle/storage observation."""
        return self.read_json(self.install / 'installation.json')

    def file_hash(self, path):
        """Retain non-secret hashes only, read through the existing root host adapter."""
        return evidence.checked(self.host('sha256sum', str(path)).stdout.split()[0], evidence.HASH)

    def installed_facts(self, tag):
        """Product inspect revalidates exact retained bytes before projecting public identity."""
        config = self.config()
        manifest = self.read_json(Path(config['Bundle']) / 'release.json')
        inspected = json.loads(self.ctl('release', 'inspect', config['Bundle']).stdout)
        evidence.require(inspected['Fingerprint'] == config['Release']['Fingerprint'])
        evidence.require(inspected['Integrity'] == 'validated')
        return evidence.installation(config, manifest, tag)

    def prepare_public(self):
        """Reuse unchanged public preparation, then give the existing probe its private output."""
        super().prepare_public()
        self.host('chown', '1654:1654', str(self.directory / 'probe'))

    def start_proxy(self, digest):
        """Wait on real HTTPS readiness before the first routed authentication attempt."""
        super().start_proxy(digest)
        evidence.require(self.curl('/health/ready', '--retry', '5', '--retry-connrefused', '--retry-max-time', '30') == 'ready')

    def seed(self):
        """Reuse the synthetic production credential/Identity probe and one durable DB/upload fact."""
        self.probe_command('seed')
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                     'mkdir -p /var/lib/wayfarer/uploads/imports; printf %s "$1" > "$2"', 'upload', VALUE, UPLOAD)
        self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-v', 'ON_ERROR_STOP=1', '-c',
                     'CREATE TABLE recovery_qualification (id integer PRIMARY KEY, value text); '
                     'ALTER TABLE recovery_qualification OWNER TO wayfarer; '
                     f"INSERT INTO recovery_qualification VALUES (1, '{VALUE}');")

    def ring_hashes(self, ring):
        """Hash every complete ring file without reading key material into evidence."""
        ring_path = '/var/lib/wayfarer/' + evidence.checked(ring, r'[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*')
        lines = self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                             r'find "$1" -type f -exec sha256sum {} +', 'ring', ring_path).splitlines()
        hashes = {}
        for line in lines:
            sha, path = line.split(maxsplit=1)
            name = evidence.checked(Path(path).name, r'key-[a-f0-9-]{36}\.xml')
            evidence.require(name not in hashes)
            hashes[name] = evidence.checked(sha, evidence.HASH)
        evidence.require(bool(hashes))
        return dict(sorted(hashes.items()))

    def observe(self, ring, expected_ring):
        """Run the representative functional and HTTPS observations on the currently selected destination."""
        self.ctl('dispatch', 'doctor')
        evidence.require(self.curl('/health/ready') == 'ready')
        value = self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At',
                             '-v', 'ON_ERROR_STOP=1', '-c', 'SELECT value FROM recovery_qualification WHERE id=1').strip()
        evidence.require(value == VALUE)
        evidence.require(self.compose('exec', '-T', 'wayfarer', 'cat', UPLOAD) == VALUE)
        evidence.require(self.ring_hashes(ring) == expected_ring)
        self.probe_command('verify')
        with tempfile.NamedTemporaryFile() as cookie:
            self.authenticate(cookie.name)
        return {name: True for name in evidence.CHECKS}

    def repair_source(self, public_target, target_bootstrap):
        """Direct public-target repair alone crosses operator versions before selecting the witness archive."""
        self.stage = 'backup-source-repair'
        before = self.config()
        evidence.require(before['Backup']['Source']['ReleaseStatus'] == 'candidate')
        operator_sha = self.file_hash(target_bootstrap)
        evidence.repair_operator(public_target, operator_sha)
        self.ctl('release', 'repair-backup-source-v1.9.21', executable=target_bootstrap)
        return evidence.repair_facts(before, self.config(), public_target, operator_sha)

    def capture_source(self, cookie, public_source, public_target, target_bootstrap):
        """Exact public source setup, normal backup configuration, supported repair and released capture."""
        self.prepare_public()
        self.ctl('setup', '--version', evidence.SOURCE[1:], '--hostname', 'wayfarer.example.org',
                 '--project', self.project, '--edge-prefix', '172.30.69', '--mode', 'external',
                 '--loopback-port', str(self.loopback), '--password-stdin', data=self.password + '\n')
        config = self.config()
        source_bundle = Path(config['Bundle'])
        self.start_proxy(self.read_json(source_bundle / 'release.json')['Images']['CaddyDigest'])
        self.authenticate(cookie)
        self.ctl('dispatch', 'doctor')
        self.seed()
        self.ctl('backup', 'configure', '--destination', str(self.directory / 'destination'),
                 '--payload', str(source_bundle / 'wayfarer-recovery'), '--retention', '7')
        source = self.installed_facts(evidence.SOURCE)
        evidence.require(self.file_hash(self.executable) == source['release']['operatorSha256'] ==
                         public_source['executedBootstrapSha256'])
        repair = self.repair_source(public_target, target_bootstrap)
        evidence.require(self.installed_facts(evidence.SOURCE) == source)
        ring = self.config()['Backup']['Ring']
        ring_hashes = self.ring_hashes(ring)
        self.stage = 'selected-capture'
        captured = self.ctl('dispatch', 'backup', '--quiesced').stdout
        name, archive = evidence.selected_name(captured, self.host('ls', str(self.directory / 'destination')).stdout)
        selected_path = self.directory / 'destination' / name
        verified = self.ctl('dispatch', 'verify-backup', name).stdout
        evidence.require('Archive ' + archive in verified and 'Integrity: True; compatible: True' in verified)
        sha = self.file_hash(selected_path)
        sidecar = self.host('cat', str(selected_path) + '.sha256').stdout
        manifest = json.loads(self.host('tar', '-xOf', str(selected_path), 'manifest.json').stdout)
        selected = evidence.archive_facts(name, archive, sha, sidecar, manifest, source)
        self.ctl('dispatch', 'start')
        source['checks'] = self.observe(ring, ring_hashes)
        source['ringHashes'] = ring_hashes
        source['uploadSha256'] = hashlib.sha256(VALUE.encode()).hexdigest()
        return source, selected, source_bundle, ring, repair

    def forward_update(self, tag, source, selected, ring, cookie, target_bootstrap):
        """The fixed v1.9.21 bootstrap plans/accepts a real public update and then dispatches target bytes."""
        self.stage = 'public-update'
        plan, digest = evidence.parse_plan(self.ctl('dispatch', 'update', tag[1:], '--plan').stdout)
        target_manifest = self.read_json(Path(plan['Target']['Bundle']) / 'release.json')
        evidence.require(target_manifest['Status'] == 'stable' and target_manifest['Tag'] == tag)
        boundaries = [item for item in target_manifest['Sources'] if item['Version'] == evidence.SOURCE[1:]
                      and item['Fingerprint'] == source['release']['fingerprint']]
        evidence.require(len(boundaries) == 1 and boundaries[0] == plan['Boundary'])
        target_sha = self.file_hash(target_bootstrap)
        evidence.require(target_sha == plan['Target']['Release']['OperatorSha256'])
        self.ctl('dispatch', 'update', '--accept-plan', digest)
        receipt = self.read_json(self.install / 'recovery-control/update.json')
        target = self.installed_facts(tag)
        version = self.ctl('dispatch', 'version').stdout.splitlines()[0]
        evidence.require(version == 'wayfarerctl ' + target['release']['operatorVersion'])
        self.host('test', '-f', str(self.install / 'update-plans' / plan['Operation'].replace('-', '') / 'completed'))
        checks = self.observe(ring, source['ringHashes'])
        evidence.require(self.curl('/Admin/Users', '-b', cookie, '-o', '/dev/null', '-w', '%{http_code}') == '200')
        selected_path = self.directory / 'destination' / selected['basename']
        evidence.unchanged(selected, self.file_hash(selected_path), self.host('cat', str(selected_path) + '.sha256').stdout)
        held = self.directory / 'destination' / receipt['RecoveryName']
        evidence.require(self.file_hash(held) == receipt['RecoverySha256'])
        evidence.require(self.host('cat', str(held) + '.restore-hold').stdout == receipt['RecoveryArchive'] + '\n')
        evidence.require(receipt['RecoveryArchive'] != selected['archive'])
        update = evidence.update_facts(plan, digest, receipt, source, target, checks)
        update['preUpdateCookieAccepted'] = True
        update['selectedArchiveRetained'] = True
        update['heldRecoveryBasename'] = evidence.checked(receipt['RecoveryName'],
            r'wayfarer-recovery-v1_[a-f0-9-]{36}_[0-9]{8}T[0-9]{13}Z_[a-f0-9-]{36}\.tar')
        return update

    def clean_restore(self, source, selected, source_bundle, ring):
        """Reconstruct the selected archive with exact v1.9.21 authority in an empty independent project."""
        self.stage = 'clean-source-restore'
        self.install = self.directory / 'clean-installation'
        self.project = source['project'] + '-clean'
        self.proxy = self.project + '-proxy'
        self.projects.append(self.project)
        self.port, self.loopback = self.free_port(), self.free_port()
        evidence.require(self.host('test', '-e', str(self.install), check=False).returncode == 1)
        evidence.require(not self.host('docker', 'volume', 'ls', '-q', '--filter',
                                       'label=com.docker.compose.project=' + self.project).stdout.strip())
        self.host('mkdir', '-m', '700', str(self.install))
        # v1.9.21 clean restore consumes explicit trusted choices; adoption is its existing metadata-only bridge.
        imported = json.loads(self.ctl('release', 'import', str(source_bundle)).stdout)
        evidence.require(imported['Fingerprint'] == source['release']['fingerprint'])
        bundle = self.install / 'releases' / evidence.SOURCE
        evidence.require(self.file_hash(self.executable) == source['release']['operatorSha256'])
        target_evidence = self.directory / 'clean-target-evidence.json'
        target = self.ctl('release', 'target', str(bundle), self.project).stdout
        self.host('tee', str(target_evidence), data=target)
        self.host('chmod', '600', str(target_evidence))
        archive = self.directory / 'destination' / selected['basename']
        evidence.unchanged(selected, self.file_hash(archive), self.host('cat', str(archive) + '.sha256').stdout)
        plan, digest = evidence.parse_plan(self.ctl('restore', '--new-install', '--archive', str(archive),
            '--source-installation', source['installation'], '--bundle', str(bundle),
            '--hostname', 'wayfarer.example.org', '--project', self.project, '--edge-prefix', '172.30.70',
            '--app-digest', source['appDigest'], '--db-digest', source['dbDigest'], '--mode', 'external',
            '--loopback-port', str(self.loopback), '--target-evidence', str(target_evidence),
            '--capture-payload', str(bundle / 'wayfarer-recovery'), '--restore-payload', str(bundle / 'wayfarer-recovery'),
            '--plan').stdout)
        evidence.require(plan['Archive'] == selected['archive'] and plan['ArchiveSha256'] == selected['sha256'])
        self.ctl('restore', '--accept-plan', digest, '--trust-controlled-backup')
        receipt = self.read_json(self.install / 'recovery-control/restore.json')
        self.ctl('release', 'adopt', str(bundle))
        destination = self.installed_facts(evidence.SOURCE)
        evidence.require(self.config().get('Backup') is None)
        for marker in ('setup-progress.json', 'setup-complete'):
            evidence.require(self.host('test', '-e', str(self.install / marker), check=False).returncode == 1)
        self.start_proxy(source['caddyDigest'])
        checks = self.observe(ring, source['ringHashes'])
        self.stage = 'clean-headless-recovery'
        evidence.require('admin' in self.ctl('dispatch', 'user', 'find', 'admin').stdout)
        checks['lookup'] = True
        self.password = secrets.token_hex(32) + '!aA9'
        self.passwords.append(self.password)
        self.ctl('dispatch', 'user', 'reset-password', 'admin', '--password-stdin', data=self.password + '\n')
        checks['reset'] = True
        with tempfile.NamedTemporaryFile() as cookie:
            self.authenticate(cookie.name)
        checks['newLogin'] = True
        return evidence.restore_facts(plan, digest, receipt, source, selected, destination, checks)

    def cleanup(self):
        """Reuse labelled cleanup for both owned projects; never select production resources."""
        for project in self.projects:
            if project != self.project:
                self.cleanup_project(project)
        super().cleanup()


def main():
    """A PASS ledger is published only after the complete witness and owned-resource cleanup succeed."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', required=True, help='Already published supported later stable tag.')
    parser.add_argument('--probe', required=True, type=Path, help='Existing test-only RecoveryProbe.dll.')
    parser.add_argument('--output', required=True, type=Path, help='New sanitized artifact directory.')
    args = parser.parse_args()
    log = None
    stage = 'public-provenance'
    try:
        evidence.target_version(args.target)
        evidence.require(platform.system() == 'Linux' and platform.machine() == 'x86_64')
        args.output.mkdir(mode=0o700)
        log = args.output / 'commands.jsonl'
        source_public = evidence.public_release(evidence.SOURCE)
        target_public = evidence.public_release(args.target)
        with tempfile.TemporaryDirectory(prefix='wayfarer-748-') as directory:
            root = Path(directory)
            source_bootstrap = evidence.bootstrap(source_public, root / 'source-bootstrap')
            (root / 'journey').mkdir(mode=0o700)
            # The root host adapter mounts only this owned directory; prepare_public protects both public operators.
            target_bootstrap = evidence.bootstrap(target_public, root / 'journey' / 'target-bootstrap')
            journey = PublicJourney(root / 'journey', source_bootstrap, args.probe, log)
            primary_failure = False
            try:
                with tempfile.NamedTemporaryFile() as cookie:
                    source, selected, bundle, ring, repair = journey.capture_source(
                        cookie.name, source_public, target_public, target_bootstrap)
                    update = journey.forward_update(args.target, source, selected, ring, cookie.name, target_bootstrap)
                    restored = journey.clean_restore(source, selected, bundle, ring)
                ledger = {'schema': 1, 'result': 'PASS', 'platform': 'linux/amd64',
                          'httpsScope': 'fixture-controlled-external-TLS', 'sourcePublic': source_public,
                          'targetPublic': target_public, 'source': source, 'backupSourceRepair': repair,
                          'selectedArchive': selected,
                          'update': update, 'cleanRestore': restored}
                for variable, field in (('GITHUB_RUN_ID', 'workflowRun'), ('GITHUB_RUN_ATTEMPT', 'workflowAttempt')):
                    if variable in os.environ:
                        ledger[field] = evidence.checked(os.environ[variable], r'[1-9][0-9]{0,20}')
            except Exception:
                stage = journey.stage
                primary_failure = True
                raise
            finally:
                try:
                    journey.cleanup()
                except Exception:
                    with log.open('a') as output:
                        output.write(json.dumps({'stage': 'cleanup', 'result': 'FAIL'}) + '\n')
                    if not primary_failure:
                        stage = 'cleanup'
                        raise
        (args.output / 'lifecycle.json').write_text(json.dumps(ledger, indent=2) + '\n')
        print('PASS public stable lifecycle; sanitized ledger and command results retained.')
        return 0
    except Exception:
        if log is not None:
            with log.open('a') as output:
                output.write(json.dumps({'stage': stage, 'result': 'FAIL'}) + '\n')
        print('FAIL public stable lifecycle at ' + stage + '; no PASS ledger emitted.')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
