"""Offline proof of the public stable gate, explicit archive join and sanitized ledger.

These tests validate the implementation; they cannot qualify an unpublished target.
"""
import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import Mock, call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import public_lifecycle_evidence as evidence
from qualify_public_lifecycle import PublicJourney

INSTALLATION = '11111111-1111-4111-8111-111111111111'
ARCHIVE = '22222222-2222-4222-8222-222222222222'
OPERATION = '33333333-3333-4333-8333-333333333333'
CLEAN = '44444444-4444-4444-8444-444444444444'
PROJECT = 'wayfarer-748-0123456789'
NAME = f'wayfarer-recovery-v1_{INSTALLATION}_20261003T1200000000000Z_{ARCHIVE}.tar'
SECRET = 'password-cookie-provider-token-MUST-NOT-APPEAR'


def release(tag):
    """Representative public API metadata, with the same fixed identities as the product validator."""
    return {'id': 1, 'tag_name': tag, 'name': tag, 'draft': False, 'prerelease': False,
            'html_url': f'https://github.com/stef-k/Wayfarer/releases/tag/{tag}',
            'url': 'https://api.github.com/repos/stef-k/Wayfarer/releases/1',
            'published_at': '2026-10-03T12:00:00Z',
            'assets': [{'name': name, 'state': 'uploaded', 'size': 100, 'digest': 'sha256:' + 'a' * 64}
                       for name in evidence.public_bundle.asset_names(tag)]}


def deployment(tag=evidence.SOURCE, installation=INSTALLATION, project=PROJECT, generation=None):
    """Product-shaped observations intentionally contain secret and unrelated keys to test projection."""
    manifest = {'Status': 'stable', 'Tag': tag, 'Version': tag[1:], 'Platform': 'linux/amd64',
                'SourceRevision': 'b' * 40, 'Operator': {'Version': tag[1:]},
                'Images': {'PlatformDigest': 'sha256:' + 'c' * 64, 'ApplicationDigest': 'sha256:' + 'c' * 64,
                           'DatabaseDigest': 'sha256:' + 'd' * 64, 'CaddyDigest': 'sha256:' + 'e' * 64},
                'Files': [{'Path': 'wayfarerctl', 'Sha256': 'f' * 64}]}
    config = {'Installation': installation, 'Project': project, 'StorageGeneration': generation,
              'AppDigest': manifest['Images']['PlatformDigest'], 'DbDigest': manifest['Images']['DatabaseDigest'],
              'Release': {'Name': tag, 'Fingerprint': 'a' * 64, 'OperatorVersion': tag[1:], 'OperatorSha256': 'f' * 64},
              'Backup': {'Secret': SECRET}, 'Password': SECRET}
    return config, manifest


def capture():
    """One pre-update manifest/sidecar bound to the representative source identity."""
    config, manifest = deployment()
    source = evidence.installation(config, manifest, evidence.SOURCE)
    captured = {'Archive': ARCHIVE, 'Installation': INSTALLATION, 'Mode': 'quiesced',
                'Source': {'ApplicationVersion': '1.9.21', 'SourceRevision': 'b' * 40,
                           'ApplicationImage': 'ghcr.io/stef-k/wayfarer@' + source['appDigest'],
                           'DatabaseImage': 'ghcr.io/stef-k/wayfarer-db@' + source['dbDigest'],
                           'Project': PROJECT, 'Platform': 'linux/amd64', 'ReleaseStatus': 'released',
                           'ConfigurationSchema': 3,
                           'WorkerVersion': '1.9.21.0', 'QuartzCompatibilityContract': 'wayfarer-quartz-v1',
                           'QuartzSnapshotFingerprint': 'a' * 32,
                           'BundleFingerprint': 'b' * 64, 'PayloadFingerprint': 'c' * 64, 'Token': SECRET},
                'Database': {'Major': 18, 'Password': SECRET}}
    sidecar = 'd' * 64 + '  ' + NAME + '\n'
    return source, captured, sidecar


def repair_state():
    """The public source's known defect changes only capture status and backup generation."""
    before, _ = deployment()
    before['Bundle'] = '/owned/releases/v1.9.21'
    before['Backup'] = {'Source': capture()[1]['Source'], 'Generation': 'a' * 64,
                        'Retention': 7, 'Ring': 'data-protection', 'Destination': '/owned/' + SECRET}
    before['Backup']['Source']['ReleaseStatus'] = 'candidate'
    after = copy.deepcopy(before)
    after['Backup']['Source']['ReleaseStatus'] = 'released'
    after['Backup']['Generation'] = 'b' * 64
    public_target = evidence.publication(release('v1.9.22'), 'v1.9.22')
    public_target['executedBootstrapSha256'] = 'e' * 64
    return before, after, public_target


class PublicLifecycleTests(unittest.TestCase):
    """Only public-authority and continuity boundaries need deterministic negative cases here."""
    def test_complete_ring_uses_product_relative_path(self):
        """Recovery inspection returns data-root-relative paths; whole-ring proof resolves that authority."""
        journey = PublicJourney.__new__(PublicJourney)
        filename = f'key-{INSTALLATION}.xml'
        journey.compose = Mock(return_value='a' * 64 + '  /var/lib/wayfarer/data-protection/' + filename)
        self.assertEqual(journey.ring_hashes('data-protection'), {filename: 'a' * 64})
        self.assertEqual(journey.compose.call_args.args[-1], '/var/lib/wayfarer/data-protection')
        journey.compose.return_value = ''
        with self.assertRaises(ValueError):
            journey.ring_hashes('data-protection')

    def test_target_must_be_later_exact_stable(self):
        """The expected patch is accepted without hard-coding it as the only future target."""
        self.assertEqual(evidence.target_version('v1.9.22'), '1.9.22')
        self.assertEqual(evidence.target_version('v1.10.0'), '1.10.0')
        for tag in ('latest', 'candidate-v1.9.22', 'v1.9.22-rc.1', 'v1.9.21', 'v1.9.20', 'v01.9.22', 'v1.9.22\n'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                evidence.target_version(tag)

    def test_public_gate_requires_all_native_assets_and_real_stable(self):
        """Draft/prerelease/absent/duplicate/malformed assets cannot supply target authority."""
        actual = release('v1.9.22')
        self.assertEqual(evidence.publication(actual, 'v1.9.22')['releaseId'], 1)
        variants = []
        for key in ('draft', 'prerelease'):
            changed = copy.deepcopy(actual)
            changed[key] = True
            variants.append(changed)
        for index in range(4):
            changed = copy.deepcopy(actual)
            changed['assets'].pop(index)
            variants.append(changed)
        changed = copy.deepcopy(actual)
        changed['assets'][0]['digest'] = None
        variants.append(changed)
        changed = copy.deepcopy(actual)
        changed['assets'].append(changed['assets'][0])
        variants.append(changed)
        for changed in variants:
            with self.subTest(metadata=changed), self.assertRaises(Exception):
                evidence.publication(changed, 'v1.9.22')

    def test_product_plan_hash_is_exact_and_required(self):
        """Truncated, modified or ambiguous printed plans cannot be accepted by the runner."""
        text = json.dumps({'Operation': OPERATION, 'NewInstall': True}, separators=(',', ':'))
        digest = hashlib.sha256(text.encode()).hexdigest()
        output = text + '\nPlan SHA-256: ' + digest + '\n'
        self.assertEqual(evidence.parse_plan(output)[1], digest)
        for changed in (text, output.replace('true', 'false'), output + output):
            with self.subTest(output=changed), self.assertRaises(ValueError):
                evidence.parse_plan(changed)

    def test_explicit_capture_uuid_wins_over_newest_order(self):
        """Scheduler/update archives can coexist without becoming the selected witness."""
        later = NAME.replace(ARCHIVE, CLEAN).replace('120000', '130000')
        self.assertEqual(evidence.selected_name('Archive ' + ARCHIVE + '\n', NAME + '\n' + later), (NAME, ARCHIVE))
        with self.assertRaises(ValueError):
            evidence.selected_name('Archive ' + ARCHIVE, later)

    def test_capture_is_quiesced_and_pair_stays_byte_identical(self):
        """An online capture, changed installation, archive or sidecar breaks the same-archive join."""
        source, manifest, sidecar = capture()
        selected = evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, manifest, source)
        evidence.unchanged(selected, 'd' * 64, sidecar)
        for sha, checksum in (('e' * 64, sidecar), ('d' * 64, sidecar + '\n')):
            with self.assertRaises(ValueError):
                evidence.unchanged(selected, sha, checksum)
        for field, value in (('Mode', 'online'), ('Installation', CLEAN)):
            changed = dict(manifest, **{field: value})
            with self.assertRaises(ValueError):
                evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, changed, source)
        self.assertNotIn(SECRET, json.dumps({'source': source, 'archive': selected}))

    def test_selected_archive_requires_released_schema_three_source(self):
        """Candidate diagnostic captures and the invalid stable token cannot enter the PASS witness."""
        source, manifest, sidecar = capture()
        self.assertEqual(evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, manifest, source)
                         ['sourceIdentity']['releaseStatus'], 'released')
        for status in ('candidate', 'stable'):
            changed = copy.deepcopy(manifest)
            changed['Source']['ReleaseStatus'] = status
            with self.subTest(status=status), self.assertRaises(ValueError):
                evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, changed, source)
        manifest['Source']['ConfigurationSchema'] = 2
        with self.assertRaises(ValueError):
            evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, manifest, source)

    def test_repair_projection_retains_only_public_join_and_observed_success(self):
        """Protected configuration, private policy paths and unexpected metadata never leave memory."""
        before, after, public_target = repair_state()
        public_target['Secret'] = SECRET
        observed = evidence.repair_facts(before, after, public_target, 'e' * 64)
        self.assertEqual(observed, {'publicTargetTag': 'v1.9.22', 'bootstrapSha256': 'e' * 64,
                                    'beforeStatus': 'candidate', 'afterStatus': 'released',
                                    'generationChanged': True, 'sourceIdentityPreserved': True,
                                    'backupPolicyPreserved': True, 'result': 'success'})
        self.assertNotIn(SECRET, json.dumps(observed))

    def test_repair_requires_known_before_and_corrected_after_status(self):
        """A no-op or wrong status cannot stand in for observing the pristine public defect."""
        for state, status in (('before', 'released'), ('before', 'stable'),
                              ('after', 'candidate'), ('after', 'stable')):
            before, after, public_target = repair_state()
            (before if state == 'before' else after)['Backup']['Source']['ReleaseStatus'] = status
            with self.subTest(state=state, status=status), self.assertRaises(ValueError):
                evidence.repair_facts(before, after, public_target, 'e' * 64)

    def test_repair_requires_changed_generation(self):
        """Corrected labels alone do not prove that the supported generation transaction ran."""
        before, after, public_target = repair_state()
        after['Backup']['Generation'] = before['Backup']['Generation']
        with self.assertRaises(ValueError):
            evidence.repair_facts(before, after, public_target, 'e' * 64)

    def test_repair_requires_preserved_source_identity_and_policy(self):
        """Only status and backup generation may differ across the protected readbacks."""
        for section, field, value in ((None, 'Installation', CLEAN), (None, 'Project', PROJECT + '-clean'),
                                      (None, 'StorageGeneration', 'e' * 32), ('Release', 'Fingerprint', 'e' * 64),
                                      ('Backup', 'Retention', 8)):
            before, after, public_target = repair_state()
            (after if section is None else after[section])[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                evidence.repair_facts(before, after, public_target, 'e' * 64)

    def test_repair_operator_requires_verified_public_target_identity(self):
        """A local executable hash, unverified bootstrap or candidate tag is not public repair evidence."""
        _, _, public_target = repair_state()
        variants = [(public_target, 'f' * 64), (dict(public_target, tag='candidate-v1.9.22'), 'e' * 64),
                    (dict(public_target, tag=evidence.SOURCE), 'e' * 64)]
        unverified = dict(public_target)
        unverified.pop('executedBootstrapSha256')
        variants.append((unverified, 'e' * 64))
        for facts, digest in variants:
            with self.subTest(tag=facts['tag']), self.assertRaises(ValueError):
                evidence.repair_operator(facts, digest)

    def test_verified_bootstrap_records_the_extracted_operator_hash(self):
        """Repair provenance binds executable bytes extracted only after public asset and sidecar checks."""
        payload = b'public target operator'
        compressed = io.BytesIO()
        with tarfile.open(fileobj=compressed, mode='w:gz') as archive:
            member = tarfile.TarInfo('wayfarerctl')
            member.size, member.mode = len(payload), 0o555
            archive.addfile(member, io.BytesIO(payload))
        name = 'wayfarerctl-linux-amd64.tar.gz'
        digest = hashlib.sha256(compressed.getvalue()).hexdigest()
        downloads = {name: compressed.getvalue(), name + '.sha256': (digest + '  ' + name + '\n').encode()}
        facts = evidence.publication(release('v1.9.22'), 'v1.9.22')
        for asset, value in downloads.items():
            facts['assets'][asset] = {'size': len(value), 'sha256': 'sha256:' + hashlib.sha256(value).hexdigest()}
        with tempfile.TemporaryDirectory() as directory, patch.object(evidence, 'gh_read') as download:
            download.side_effect = lambda url, output: output.write(downloads[url.rsplit('/', 1)[1]])
            executable = evidence.bootstrap(facts, Path(directory) / 'bootstrap')
            actual = hashlib.sha256(executable.read_bytes()).hexdigest()
            self.assertEqual(facts['executedBootstrapSha256'], actual)
            self.assertEqual(evidence.repair_operator(facts, actual)['bootstrapSha256'], actual)

    def test_repair_invokes_public_target_directly_and_keeps_ordinary_operator(self):
        """Direct target execution is logged without protected state, argv values or command output."""
        before, after, public_target = repair_state()
        journey = PublicJourney.__new__(PublicJourney)
        journey.install = Path('/owned/installation')
        journey.executable = Path('/owned/source-wayfarerctl')
        journey.passwords, journey.command_count = [SECRET], 0
        journey.config = Mock(side_effect=[before, after])
        journey.file_hash = Mock(return_value='e' * 64)
        journey.host = Mock(return_value=subprocess.CompletedProcess([], 0, 'repair succeeded', ''))
        operator = Path('/owned/target-bootstrap/wayfarerctl')
        with tempfile.TemporaryDirectory() as directory:
            journey.log = Path(directory) / 'commands.jsonl'
            observed = journey.repair_source(public_target, operator)
            journey.host.assert_called_once_with(str(operator), '--deployment-root', str(journey.install),
                                                 'release', 'repair-backup-source-v1.9.21', data=None, check=False)
            self.assertEqual(journey.executable, Path('/owned/source-wayfarerctl'))
            self.assertEqual(observed['result'], 'success')
            self.assertEqual(json.loads(journey.log.read_text())['command'], 'release repair-backup-source-v1.9.21')
            self.assertNotIn(SECRET, journey.log.read_text())
            journey.config.side_effect = [before, after]
            journey.host.return_value = subprocess.CompletedProcess([], 1, '', '')
            with self.assertRaises(ValueError):
                journey.repair_source(public_target, operator)
            journey.host.reset_mock()
            journey.config.side_effect = [before, after]
            journey.file_hash.return_value = 'f' * 64
            with self.assertRaises(ValueError):
                journey.repair_source(public_target, operator)
            journey.host.assert_not_called()
            before['Backup']['Source']['ReleaseStatus'] = 'released'
            journey.config.side_effect = [before]
            with self.assertRaises(ValueError):
                journey.repair_source(public_target, operator)
            journey.host.assert_not_called()

    def test_capture_waits_for_successful_repair_observation(self):
        """The real capture sequence aborts before backup when the direct repair fails its join."""
        before, after, public_target = repair_state()
        source, manifest, sidecar = capture()
        public_source = {'executedBootstrapSha256': 'f' * 64}
        journey = PublicJourney.__new__(PublicJourney)
        journey.directory, journey.install = Path('/owned/journey'), Path('/owned/journey/installation')
        journey.project, journey.loopback, journey.password = PROJECT, 8080, SECRET
        journey.executable = journey.directory / 'wayfarerctl'
        operator = journey.directory / 'target-bootstrap/wayfarerctl'
        journey.config = Mock(side_effect=[before, before, after, after])
        journey.prepare_public, journey.authenticate, journey.seed = Mock(), Mock(), Mock()
        journey.start_proxy = Mock()
        journey.read_json = Mock(return_value={'Images': {'CaddyDigest': source['caddyDigest']}})
        journey.installed_facts = Mock(return_value=source)
        hashes = {journey.executable: 'f' * 64, operator: 'e' * 64,
                  journey.directory / 'destination' / NAME: 'd' * 64}
        journey.file_hash = Mock(side_effect=hashes.__getitem__)
        journey.ring_hashes = Mock(return_value={'key.xml': 'a' * 64})
        journey.observe = Mock(return_value={key: True for key in evidence.CHECKS})
        journey.ctl = Mock(return_value=subprocess.CompletedProcess([], 0,
            'Archive ' + ARCHIVE + '\nIntegrity: True; compatible: True\n', ''))
        journey.host = Mock(side_effect=[subprocess.CompletedProcess([], 0, NAME, ''),
                                        subprocess.CompletedProcess([], 0, sidecar, ''),
                                        subprocess.CompletedProcess([], 0, json.dumps(manifest), '')])
        repair_call = call('release', 'repair-backup-source-v1.9.21', executable=operator)
        capture_call = call('dispatch', 'backup', '--quiesced')
        observed = journey.capture_source('/owned/cookie', public_source, public_target, operator)
        self.assertEqual(observed[4]['afterStatus'], 'released')
        self.assertLess(journey.ctl.call_args_list.index(repair_call), journey.ctl.call_args_list.index(capture_call))
        after['Backup']['Generation'] = before['Backup']['Generation']
        journey.config.side_effect = [before, before, after]
        journey.ctl.reset_mock()
        with self.assertRaises(ValueError):
            journey.capture_source('/owned/cookie', public_source, public_target, operator)
        self.assertIn(repair_call, journey.ctl.call_args_list)
        self.assertNotIn(capture_call, journey.ctl.call_args_list)

    def test_update_ledger_joins_authority_and_drops_raw_plan_secrets(self):
        """Only exact-source Accepted same-generation update facts are emitted."""
        raw_source, source_manifest = deployment()
        raw_target, target_manifest = deployment('v1.9.22')
        source = evidence.installation(raw_source, source_manifest, evidence.SOURCE)
        target = evidence.installation(raw_target, target_manifest, 'v1.9.22')
        plan = {'Operation': OPERATION, 'Current': raw_source, 'Target': raw_target,
                'OperatorOwner': raw_source['Release'], 'OldConfiguration': SECRET,
                'Boundary': {'Version': '1.9.21', 'Fingerprint': 'a' * 64,
                             'ExactOrderedPrefix': True, 'ReferenceSeeding': False}, 'MigrationDelta': []}
        receipt = {'Phase': 9, 'Plan': plan, 'PlanHash': 'b' * 64,
                   'RecoveryArchive': CLEAN, 'RecoverySha256': 'c' * 64, 'OldEnvironment': SECRET}
        checks = {key: True for key in evidence.CHECKS}
        result = evidence.update_facts(plan, 'b' * 64, receipt, source, target, checks)
        self.assertTrue(result['zeroEfDelta'])
        self.assertNotIn(SECRET, json.dumps(result))
        for field, value in (('Phase', 8), ('PlanHash', 'f' * 64)):
            with self.assertRaises(ValueError):
                evidence.update_facts(plan, 'b' * 64, dict(receipt, **{field: value}), source, target, checks)
        changed = copy.deepcopy(plan)
        changed['Boundary']['Fingerprint'] = 'f' * 64
        with self.assertRaises(ValueError):
            evidence.update_facts(changed, 'b' * 64, dict(receipt, Plan=changed), source, target, checks)

    def test_clean_restore_requires_selected_archive_new_state_and_all_observations(self):
        """Null/false/missing readbacks and an unrelated recovery set cannot create a PASS ledger."""
        source, captured, sidecar = capture()
        selected = evidence.archive_facts(NAME, ARCHIVE, 'd' * 64, sidecar, captured, source)
        config, manifest = deployment(installation=CLEAN, project=PROJECT + '-clean', generation='e' * 32)
        destination = evidence.installation(config, manifest, evidence.SOURCE)
        plan = {'Operation': OPERATION, 'NewInstall': True, 'Target': config, 'SourceInstallation': INSTALLATION,
                'Archive': ARCHIVE, 'ArchiveSha256': 'd' * 64, 'CandidateGeneration': 'e' * 32,
                'CaptureMode': 'quiesced', 'BundleFingerprint': 'b' * 64, 'CapturePayloadFingerprint': 'c' * 64}
        receipt = {'Plan': plan, 'PlanHash': 'f' * 64, 'Phase': 8}
        checks = {key: True for key in (*evidence.CHECKS, 'lookup', 'reset', 'newLogin')}
        result = evidence.restore_facts(plan, 'f' * 64, receipt, source, selected, destination, checks)
        self.assertNotIn(SECRET, json.dumps(result))
        for key in checks:
            changed = dict(checks)
            changed.pop(key)
            with self.subTest(key=key), self.assertRaises(ValueError):
                evidence.restore_facts(plan, 'f' * 64, receipt, source, selected, destination, changed)
        with self.assertRaises(ValueError):
            evidence.continuity(dict(checks, token=None), reset=True)
        with self.assertRaises(ValueError):
            evidence.restore_facts(plan, 'f' * 64, receipt, source, dict(selected, archive=CLEAN), destination, checks)


if __name__ == '__main__':
    unittest.main()
