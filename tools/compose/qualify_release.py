"""Release-authority observations within the established disposable recovery journey."""
import json
from pathlib import Path
import uuid


def write_json(journey, path, value):
    """Fixture-owned protected JSON only; never rewrite a historical recovery archive."""
    journey.host('tee', str(path), data=json.dumps(value))
    journey.host('chmod', '600' if path.name != 'release.json' else '644', str(path))


def qualify_release(journey):
    """Qualify import/adoption/retention without changing app/DB versions or user data."""
    journey.ctl("backup", "--quiesced")
    source = journey.release_bundle
    original = journey.host('cat', str(journey.install / 'installation.json')).stdout
    before = journey.snapshot()
    inspected = json.loads(journey.ctl('release', 'inspect', str(source)).stdout)
    imported = json.loads(journey.ctl('release', 'import', str(source)).stdout)
    assert imported['Images'] == 'execution-ready' and imported['Fingerprint'] == inspected['Fingerprint']
    assert journey.host('cat', str(journey.install / 'installation.json')).stdout == original
    installed = journey.install / 'releases' / imported['Release']
    assert json.loads(journey.ctl('release', 'import', str(source)).stdout)['Fingerprint'] == imported['Fingerprint']
    manifest_path = source / 'release.json'
    manifest_bytes = journey.host('cat', str(manifest_path)).stdout
    contradictory = json.loads(manifest_bytes)
    contradictory['SourceRevision'] = 'f' * 40
    write_json(journey, manifest_path, contradictory)
    assert journey.ctl('release', 'verify-images', str(source), check=False).returncode != 0
    journey.host('tee', str(manifest_path), data=manifest_bytes)
    # Exact installed payload corruption must fail even though its namespace is trusted.
    journey.host('cp', str(installed / 'compose.yaml'), str(journey.directory / 'compose.saved'))
    journey.host('tee', str(installed / 'compose.yaml'), data='tampered')
    assert journey.ctl('release', 'import', str(source), check=False).returncode != 0
    journey.host('cp', str(journey.directory / 'compose.saved'), str(installed / 'compose.yaml'))
    # A failed adoption cannot change the pointer, deployment inputs, secrets, volumes or services.
    config = json.loads(original)
    wrong = dict(config, AppDigest='sha256:' + 'f' * 64)
    write_json(journey, journey.install / 'installation.json', wrong)
    assert journey.ctl('release', 'adopt', str(source), check=False).returncode != 0
    assert json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout) == wrong
    journey.host('tee', str(journey.install / 'installation.json'), data=original)
    journey.ctl('release', 'adopt', str(source))
    adopted = json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout)
    assert adopted == config and before == journey.snapshot()
    assert journey.ctl('dispatch', 'version').stdout.startswith('wayfarerctl ')
    target = json.loads(journey.ctl('release', 'target', str(installed), journey.project).stdout)
    assert target['QuartzSnapshotFingerprint'] == '0' * 32 and 'QuartzIdentity' not in target
    assert target['SupportedLegacySourceSchemas'] == [2]
    qualify_placement(journey, source, adopted)
    journey.ctl("start")
    print('PASS real candidate image/payload checks, immutable import, metadata-only adoption, retained target and placement recovery', flush=True)


def qualify_placement(journey, source, adopted):
    """Simulate next placement and pointer selection while the previous receipt keeps its exact owner."""
    next_source = journey.directory / 'next-release'
    journey.host('cp', '-a', str(source), str(next_source))
    manifest = json.loads(journey.host('cat', str(next_source / 'release.json')).stdout)
    # A distinct synthetic candidate source avoids colliding with the input's immutable name at any app version.
    manifest['SourceRevision'] = 'f' * 40 if manifest['SourceRevision'] != 'f' * 40 else 'e' * 40
    # Deliberately unavailable next-image identity; it can be retained but cannot be declared execution-ready.
    manifest['Images']['ApplicationDigest'] = manifest['Images']['PlatformDigest'] = 'sha256:' + 'f' * 64
    write_json(journey, next_source / 'release.json', manifest)
    next_info = json.loads(journey.ctl('release', 'inspect', str(next_source)).stdout)
    owner = {'Name': next_info['Release'], 'Fingerprint': next_info['Fingerprint'],
             'OperatorVersion': manifest['Operator']['Version'],
             'OperatorSha256': next(item['Sha256'] for item in manifest['Files'] if item['Path'] == 'wayfarerctl')}
    releases = journey.install / 'releases'
    stage = '.stage-' + uuid.uuid4().hex
    journey.host('mkdir', '-m', '700', str(releases / stage))
    write_json(journey, releases / (stage + '.json'), owner)
    assert journey.ctl('release', 'reconcile', stage, check=False).returncode != 0
    assert json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout) == adopted
    journey.host('cp', '-a', str(next_source) + '/.', str(releases / stage))
    journey.ctl('release', 'reconcile', stage)
    assert json.loads(journey.ctl('release', 'import', str(next_source)).stdout)['Images'] == 'not-qualified'
    # The real plan/receipt owner is generated by the product; no handcrafted plan can waive authorization.
    planned = journey.ctl('restore', '--restore-payload', str(journey.payload / 'wayfarer-recovery'),
                          '--without-emergency-backup', '--plan').stdout
    plan = json.loads(planned.splitlines()[0])
    assert plan['OperatorOwner'] == adopted['Release']
    evidence = json.loads(journey.host('cat', str(journey.install / 'restore-plans' /
        plan['Operation'].replace('-', '') / 'source.json')).stdout)
    assert evidence['QuartzSnapshotFingerprint'] == '0' * 32
    # Fail before candidate writes using the established process seam, leaving a real resumable receipt.
    journey.host('tee', str(journey.directory / 'failure'), data='restore-files')
    accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
    assert journey.ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup', check=False).returncode != 0
    journey.host('tee', str(journey.directory / 'failure'), data='')
    write_json(journey, journey.install / 'installation.json', dict(adopted, Release=owner))
    # Resume reaches the old owner's product guard and refuses the changed installation; dispatch itself succeeds.
    failed = journey.ctl('dispatch', 'restore', '--resume', plan['Operation'], check=False)
    assert failed.returncode == 1 and 'phase=' in failed.stderr
    write_json(journey, journey.install / 'installation.json', adopted)
    journey.ctl('dispatch', 'restore', '--abort', plan['Operation'])
    journey.ctl('dispatch', 'version')
    print('PASS incomplete/complete staged reconciliation, unavailable next release, exact previous receipt dispatch and abort', flush=True)
