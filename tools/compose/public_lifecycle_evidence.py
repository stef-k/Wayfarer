"""Fail-closed public provenance and sanitized observations for the #748 witness.

This ledger is evidence, never release, update or restore authorization. Product
operators validate bundles and own all lifecycle plans and receipts.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'release'))
import public_bundle

SOURCE = 'v1.9.21'
HASH = r'[a-f0-9]{64}'
VERSION = r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
CHECKS = ('httpsHealth', 'doctor', 'database', 'uploads', 'ring', 'provider', 'token', 'originalLogin')


def require(condition):
    """Errors never echo untrusted product output, credentials or input values."""
    if not condition:
        raise ValueError('Required public lifecycle evidence is missing or inconsistent.')


def checked(value, pattern):
    """Allow only bounded identity syntax onto an uploaded artifact."""
    require(isinstance(value, str) and re.fullmatch(pattern, value) is not None)
    return value


def identity(value):
    """Only canonical nonempty UUIDs identify installations, archives and operations."""
    require(isinstance(value, str))
    require(str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0)
    return value


def target_version(tag):
    """No latest, candidate, prerelease, historical source or assumed v1.9.22 target."""
    checked(tag, 'v' + VERSION)
    require(tuple(map(int, tag[1:].split('.'))) > (1, 9, 21))
    return tag[1:]


def publication(release, tag):
    """Reuse the production publication validator for all four native public assets."""
    checked(tag, 'v' + VERSION)
    assets = {name: public_bundle.asset(release, tag, name) for name in public_bundle.asset_names(tag)}
    require(type(release['id']) is int and release['id'] > 0)
    return {'tag': tag, 'releaseId': release['id'],
            'publishedAt': checked(release['published_at'], r'[0-9]{4}(-[0-9]{2}){2}T[0-9]{2}(:[0-9]{2}){2}Z'),
            'assets': {name: {'sha256': item['digest'], 'size': item['size']} for name, item in assets.items()}}


def gh_read(url, output=None):
    """GitHub reads use gh with an explicit non-authenticating header and no usable token.

    Metadata and asset downloads therefore cannot fall back to runner/user credentials.
    No raw stderr enters workflow logs, even if gh or an upstream response fails.
    """
    result = subprocess.run(['gh', 'api', url, '--method', 'GET', '-H', 'Authorization: none'],
                            env={**os.environ, 'GH_TOKEN': 'public-lifecycle-anonymous', 'GH_HOST': 'github.com'},
                            stdout=output or subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
    require(result.returncode == 0)
    if output is None:
        require(len(result.stdout) <= public_bundle.METADATA_LIMIT)
        return json.loads(result.stdout)


def public_release(tag):
    """Fixed repository/tag authority; callers cannot select an arbitrary download URL."""
    checked(tag, 'v' + VERSION)
    return publication(gh_read(f'repos/{public_bundle.REPOSITORY}/releases/tags/{tag}'), tag)


def bootstrap(facts, directory):
    """Verify the public bootstrap and sidecar bytes before exposing any executable."""
    directory.mkdir(mode=0o700)
    name = 'wayfarerctl-linux-amd64.tar.gz'
    for asset in (name, name + '.sha256'):
        path = directory / asset
        with path.open('xb') as output:
            gh_read(f'https://github.com/{public_bundle.REPOSITORY}/releases/download/{facts["tag"]}/{asset}', output)
        with path.open('rb') as downloaded:
            digest = hashlib.file_digest(downloaded, 'sha256').hexdigest()
        require(path.stat().st_size == facts['assets'][asset]['size'])
        require('sha256:' + digest == facts['assets'][asset]['sha256'])
    expected = facts['assets'][name]['sha256'].removeprefix('sha256:')
    require((directory / (name + '.sha256')).read_text() == expected + '  ' + name + '\n')
    with tarfile.open(directory / name, 'r:gz') as archive:
        members = archive.getmembers()
        require(len(members) == 1 and members[0].name == 'wayfarerctl' and members[0].isfile())
        require(0 < members[0].size <= public_bundle.LIMIT and members[0].mode & 0o111)
        executable = directory / 'wayfarerctl'
        with archive.extractfile(members[0]) as input_file, executable.open('xb') as output:
            shutil.copyfileobj(input_file, output)
    executable.chmod(0o555)
    return executable


def parse_plan(output):
    """Bind the exact printed JSON bytes to the canonical product plan hash."""
    lines = [line for line in output.splitlines() if line.startswith('{')]
    hashes = re.findall(r'^Plan SHA-256: (' + HASH + r')$', output, re.MULTILINE)
    require(len(lines) == len(hashes) == 1)
    require(hashlib.sha256(lines[0].encode()).hexdigest() == hashes[0])
    plan = json.loads(lines[0])
    identity(plan['Operation'])
    return plan, hashes[0]


def selected_name(output, listing):
    """Select the UUID returned by this explicit capture, never the newest archive."""
    found = re.findall(r'^Archive ([a-f0-9-]{36})$', output, re.MULTILINE)
    require(len(found) == 1)
    archive = identity(found[0])
    names = [name for name in listing.splitlines() if name.endswith('_' + archive + '.tar')]
    require(len(names) == 1)
    checked(names[0], r'wayfarer-recovery-v1_[a-f0-9-]{36}_[0-9]{8}T[0-9]{13}Z_[a-f0-9-]{36}\.tar')
    return names[0], archive


def authority(value):
    """Project exactly the four product-owned retained-release fields."""
    return {'name': checked(value['Name'], 'v' + VERSION), 'fingerprint': checked(value['Fingerprint'], HASH),
            'operatorVersion': checked(value['OperatorVersion'], VERSION),
            'operatorSha256': checked(value['OperatorSha256'], HASH)}


def installation(config, manifest, tag):
    """Corroborate public manifest/image/operator identity without copying configuration secrets."""
    release = authority(config['Release'])
    require(manifest['Status'] == 'stable' and manifest['Tag'] == tag and manifest['Version'] == tag[1:])
    require(manifest['Platform'] == 'linux/amd64' and release['name'] == tag)
    require(release['operatorVersion'] == manifest['Operator']['Version'])
    require(release['operatorSha256'] == next(item['Sha256'] for item in manifest['Files'] if item['Path'] == 'wayfarerctl'))
    require(config['AppDigest'] == manifest['Images']['PlatformDigest'])
    require(config['DbDigest'] == manifest['Images']['DatabaseDigest'])
    generation = config.get('StorageGeneration')
    return {'installation': identity(config['Installation']), 'project': checked(config['Project'], r'wayfarer-748-[a-f0-9]{10}(-clean)?'),
            'release': release, 'manifestVersion': checked(manifest['Version'], VERSION),
            'sourceRevision': checked(manifest['SourceRevision'], r'[a-f0-9]{40}'),
            'appDigest': checked(config['AppDigest'], 'sha256:' + HASH),
            'applicationIndexDigest': checked(manifest['Images']['ApplicationDigest'], 'sha256:' + HASH),
            'dbDigest': checked(config['DbDigest'], 'sha256:' + HASH),
            'caddyDigest': checked(manifest['Images']['CaddyDigest'], 'sha256:' + HASH),
            'storageGeneration': 'canonical' if generation is None else checked(generation, r'[a-f0-9]{32}')}


def archive_facts(name, archive, sha, sidecar, manifest, source):
    """Retain capture identity and sidecar bytes/hash, excluding all user and secret payloads."""
    checked(sha, HASH)
    checked(name, r'wayfarer-recovery-v1_[a-f0-9-]{36}_[0-9]{8}T[0-9]{13}Z_[a-f0-9-]{36}\.tar')
    require(sidecar == sha + '  ' + name + '\n')
    require(manifest['Archive'] == archive and manifest['Installation'] == source['installation'])
    require(manifest['Mode'] == 'quiesced')
    captured = manifest['Source']
    require(captured['ApplicationVersion'] == source['manifestVersion'] and captured['SourceRevision'] == source['sourceRevision'])
    require(captured['ApplicationImage'] == 'ghcr.io/stef-k/wayfarer@' + source['appDigest'])
    require(captured['DatabaseImage'] == 'ghcr.io/stef-k/wayfarer-db@' + source['dbDigest'])
    require(captured['Project'] == source['project'] and captured['Platform'] == 'linux/amd64')
    summary = {'applicationVersion': captured['ApplicationVersion'], 'sourceRevision': captured['SourceRevision'],
               'applicationImage': captured['ApplicationImage'], 'databaseImage': captured['DatabaseImage'],
               'project': captured['Project'], 'platform': 'linux/amd64',
               'releaseStatus': checked(captured['ReleaseStatus'], r'stable|candidate'),
               'workerVersion': checked(captured['WorkerVersion'], r'[0-9]{1,5}(\.[0-9]{1,5}){2,3}'),
               'quartzCompatibilityContract': checked(captured['QuartzCompatibilityContract'], r'[A-Za-z0-9.-]{1,128}'),
               'quartzSnapshotFingerprint': checked(captured['QuartzSnapshotFingerprint'], r'[a-f0-9]{32}'),
               'bundleFingerprint': checked(captured['BundleFingerprint'], HASH),
               'payloadFingerprint': checked(captured['PayloadFingerprint'], HASH)}
    require(type(manifest['Database']['Major']) is int and manifest['Database']['Major'] > 0)
    return {'basename': name, 'archive': identity(archive), 'sha256': sha,
            'sidecar': sidecar, 'sidecarSha256': hashlib.sha256(sidecar.encode()).hexdigest(),
            'sourceInstallation': source['installation'], 'sourceIdentity': summary,
            'sourceIdentitySha256': hashlib.sha256(json.dumps(summary, sort_keys=True).encode()).hexdigest(),
            'databaseMajor': manifest['Database']['Major'], 'captureMode': 'quiesced', 'explicitVerify': True}


def unchanged(selected, sha, sidecar):
    """A lost/replaced pre-update pair invalidates the entire continuous witness."""
    require(sha == selected['sha256'] and sidecar == selected['sidecar'])


def continuity(checks, reset=False):
    """Every observation must be established; missing, null and truthy values cannot pass."""
    names = CHECKS + (('lookup', 'reset', 'newLogin') if reset else ())
    require(all(checks.get(name) is True for name in names))
    return {name: True for name in names}


def update_facts(plan, plan_hash, receipt, source, target, checks):
    """Join the real accepted update to the source installation and public target authority."""
    require(receipt['Phase'] == 9 and receipt['Plan'] == plan and receipt['PlanHash'] == plan_hash)
    require(plan['Current']['Installation'] == plan['Target']['Installation'] == source['installation'] == target['installation'])
    require(plan['Current']['Project'] == plan['Target']['Project'] == source['project'] == target['project'])
    require(authority(plan['Current']['Release']) == authority(plan['OperatorOwner']) == source['release'])
    require(authority(plan['Target']['Release']) == target['release'])
    require(source['storageGeneration'] == target['storageGeneration'])
    require(plan['Current'].get('StorageGeneration') == plan['Target'].get('StorageGeneration'))
    require((plan['Target'].get('StorageGeneration') or 'canonical') == target['storageGeneration'])
    require(source['dbDigest'] == target['dbDigest'] and source['caddyDigest'] == target['caddyDigest'])
    boundary = plan['Boundary']
    require(boundary['Version'] == SOURCE[1:] and boundary['Fingerprint'] == source['release']['fingerprint'])
    require(boundary['ExactOrderedPrefix'] is True and boundary['ReferenceSeeding'] is False)
    delta = [checked(item, r'[0-9]{14}_[A-Za-z0-9_]{1,200}') for item in plan['MigrationDelta']]
    return {'operation': identity(plan['Operation']), 'planHash': checked(plan_hash, HASH),
            'source': source, 'target': target, 'migrationDelta': delta, 'zeroEfDelta': len(delta) == 0,
            'heldRecoveryArchive': identity(receipt['RecoveryArchive']),
            'heldRecoverySha256': checked(receipt['RecoverySha256'], HASH),
            'phase': 'Accepted', 'completedMarker': True, 'checks': continuity(checks)}


def restore_facts(plan, plan_hash, receipt, source, selected, destination, checks):
    """Join clean-target recovery to the exact pre-update archive and retained source operator."""
    require(receipt['Phase'] == 8 and receipt['Plan'] == plan and receipt['PlanHash'] == plan_hash)
    require(plan['NewInstall'] is True and plan['SourceInstallation'] == source['installation'])
    require(plan['Archive'] == selected['archive'] and plan['ArchiveSha256'] == selected['sha256'])
    require(plan['Target']['Installation'] == destination['installation'] != source['installation'])
    require(plan['Target']['Project'] == destination['project'] != source['project'])
    require(plan['CandidateGeneration'] == destination['storageGeneration'] != source['storageGeneration'])
    require(destination['release'] == source['release'])
    require(destination['appDigest'] == source['appDigest'] and destination['dbDigest'] == source['dbDigest'])
    require(plan['CaptureMode'] == 'quiesced')
    require(plan['BundleFingerprint'] == selected['sourceIdentity']['bundleFingerprint'])
    require(plan['CapturePayloadFingerprint'] == selected['sourceIdentity']['payloadFingerprint'])
    return {'operation': identity(plan['Operation']), 'planHash': checked(plan_hash, HASH),
            'archive': selected['archive'], 'archiveSha256': selected['sha256'], 'destination': destination,
            'executedOperator': source['release'], 'phase': 'Accepted',
            'backupUnconfigured': True, 'setupMarkersAbsent': True, 'checks': continuity(checks, reset=True)}
