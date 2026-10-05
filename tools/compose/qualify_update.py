"""Joined update, restore and removal acceptance in the established recovery fixture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid
from qualify_ctl import run


def qualify_update(journey):
    """Candidate-only operator build, exact source boundary, lost acknowledgement, private failure and restore handoff."""
    journey.ctl('backup', '--quiesced')
    journey.ctl('release', 'adopt', str(journey.release_bundle))
    original = configuration(journey)
    # Child-process fault injection reuses the fixture's existing Docker boundary.
    wrapper = journey.host('cat', str(journey.directory / 'docker-test')).stdout
    injection = '''case "$point:$*" in
  update-failure:*" database migrate")
    remaining=$#
    while test "$remaining" -gt 0; do
      value=$1; shift
      case "$value" in --env=Database__PasswordFile=*) value=--env=Database__PasswordFile=/missing-credential ;; esac
      set -- "$@" "$value"
      remaining=$((remaining-1))
    done ;;
  update-ack:*" start "*)
    for helper; do :; done
    operation=$(/usr/bin/docker inspect --format '{{index .Config.Labels "wayfarer.update"}}' "$helper" 2>/dev/null)
    if test -n "$operation"; then
      /usr/bin/docker "$@" || exit $?
      /usr/bin/docker wait "$helper" >/dev/null
      kill -KILL "$PPID"
      exit 137
    fi ;;
esac
'''
    journey.host('tee', str(journey.directory / 'docker-test'), data=wrapper.replace('exec /usr/bin/docker "$@"', injection + 'exec /usr/bin/docker "$@"'))
    migration_failure(journey)
    plan, digest = plan_update(journey)
    journey.host('tee', str(journey.directory / 'failure'), data='update-ack')
    failure = journey.ctl('update', '--accept-plan', digest, check=False)
    assert failure.returncode != 0, failure.stderr
    receipt = update_receipt(journey)
    assert receipt['Phase'] == 3 and receipt['RecoveryArchive'] and receipt['RecoverySha256'], failure.stderr
    helper = receipt['MigrationContainer']
    started = journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', helper).stdout
    assert journey.ctl('start', check=False).returncode != 0
    assert journey.ctl('update', '--abort', plan['Operation'], check=False).returncode != 0
    journey.host('tee', str(journey.directory / 'failure'), data='doctor')
    assert journey.ctl('update', '--resume', plan['Operation'], check=False).returncode == 1
    assert update_receipt(journey)['Phase'] == 7
    assert journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', helper).stdout == started
    ports = json.loads(journey.host('docker', 'inspect', '--format', '{{json .HostConfig.PortBindings}}',
                                   journey.project + '-wayfarer-1').stdout)
    assert not ports
    journey.host('tee', str(journey.directory / 'failure'), data='')
    foreign_consumer_refusal(journey, plan)
    journey.ctl('update', '--restore', plan['Operation'])
    restored = configuration(journey)
    assert restored['Release'] == original['Release']
    assert restored['StorageGeneration'] != original.get('StorageGeneration')
    assert update_receipt(journey)['RestoreAccepted']
    journey.probe_command('verify')
    print('PASS real migration lost acknowledgement without rerun, private postflight failure, external ingress fence and durable old-release restore handoff', flush=True)
    # Forward completion retains this active generation; subsequent backup restores the target release through #695.
    plan, digest = plan_update(journey)
    journey.ctl('update', '--accept-plan', digest)
    accepted = update_receipt(journey)
    assert accepted['Phase'] == 9
    target = configuration(journey)
    assert target['StorageGeneration'] == restored['StorageGeneration']
    assert target['Release'] != original['Release']
    assert target['Backup']['Source']['ApplicationImage'].endswith(target['AppDigest'])
    journey.probe_command('verify')
    journey.ctl('backup', '--quiesced')
    restore = journey.ctl('restore', '--plan', '--without-emergency-backup').stdout
    digest = restore.split('Plan SHA-256: ')[1].splitlines()[0]
    journey.ctl('restore', '--accept-plan', digest, '--trust-controlled-backup')
    journey.probe_command('verify')
    assert configuration(journey)['Release'] == target['Release']
    assert journey.compose('exec', '-T', 'wayfarer', 'cat',
        '/var/lib/wayfarer/uploads/imports/recovery-qualification').strip() == 'durable'
    print('PASS same-generation forward activation, secure data continuity, target backup binding and post-update managed restore', flush=True)


def configuration(journey):
    """Read protected installation evidence through the fixture's existing host adapter."""
    return json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout)


def update_receipt(journey):
    """Observe product-owned phase and held evidence, never fabricate authorization."""
    return json.loads(journey.host('cat', str(journey.install / 'recovery-control/update.json')).stdout)


def plan_update(journey):
    """Authorize only the exact canonical hash printed by the published operator."""
    output = journey.ctl('update', '--bundle', str(journey.update_bundle), '--plan').stdout
    return json.loads(output.splitlines()[0]), output.split('Plan SHA-256: ')[1].splitlines()[0]


def migration_failure(journey):
    """A real nonzero target helper leaves source history unchanged and cannot be retried or accepted."""
    plan, digest = plan_update(journey)
    journey.host('tee', str(journey.directory / 'failure'), data='update-failure')
    failure = journey.ctl('update', '--accept-plan', digest, check=False)
    assert failure.returncode == 1, failure.stderr
    receipt = update_receipt(journey)
    assert receipt['Phase'] == 3 and receipt['MigrationExit'] != 0, (receipt['Phase'], failure.stderr)
    history = journey.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-c',
        'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";').splitlines()
    assert history == plan['Current']['Backup']['Source']['ExpectedMigrations']
    started = journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', receipt['MigrationContainer']).stdout
    journey.host('tee', str(journey.directory / 'failure'), data='')
    # A stopped DB represents daemon-loss resource state; resume must reconcile that exact DB, without relaunching migration.
    journey.host('docker', 'stop', journey.project + '-db-1')
    assert journey.ctl('update', '--resume', plan['Operation'], check=False).returncode == 1
    assert update_receipt(journey)['Phase'] == 3
    assert journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', receipt['MigrationContainer']).stdout == started
    journey.ctl('update', '--restore', plan['Operation'])
    print('PASS real nonzero migration, unchanged source history, exact stopped-DB reconciliation and no blind retry', flush=True)


def foreign_consumer_refusal(journey, plan):
    """A consumer introduced after the update fence cannot inherit restore ownership."""
    mounts = json.loads(journey.host('docker', 'inspect', '--format', '{{json .Mounts}}',
                                   journey.project + '-wayfarer-1').stdout)
    volume = next(mount['Name'] for mount in mounts if mount['Destination'] == '/var/lib/wayfarer')
    name = journey.project + '-foreign-update-consumer'
    before = update_receipt(journey)
    restore_path = str(journey.install / 'recovery-control/restore.json')
    previous = journey.host('cat', restore_path, check=False)
    journey.host('docker', 'run', '-d', '--name', name, '--pull=never', '--restart=no',
                 '--network', journey.project + '_backend', '--mount', 'type=volume,src=' + volume + ',dst=/state',
                 '--entrypoint', 'sleep', 'ghcr.io/stef-k/wayfarer@' + plan['Target']['AppDigest'], 'infinity')
    try:
        refused = journey.ctl('update', '--restore', plan['Operation'], check=False)
        assert refused.returncode == 1 and 'Foreign durable-state consumer' in refused.stderr, refused.stderr
        after = update_receipt(journey)
        assert after['RestoreOperation'] == before['RestoreOperation'] and after['Phase'] == before['Phase']
        current = journey.host('cat', restore_path, check=False)
        assert (current.returncode, current.stdout) == (previous.returncode, previous.stdout)
        assert journey.host('docker', 'inspect', '--format', '{{.State.Running}}', name).stdout.strip() == 'true'
    finally:
        journey.host('docker', 'rm', '-f', name)
    print('PASS foreign durable-state consumer refuses restore ownership transfer until removed', flush=True)


def qualify_removal(journey):
    """Continue this same updated/restored installation through both uninstall endpoints and fresh setup."""
    observe_removal(journey)
    journey.start_proxy()
    old = configuration(journey)
    authority = authority_snapshot(journey)
    secrets = journey.host('sh', '-ec', 'sha256sum "$1"/secrets/*', 'secrets', str(journey.install)).stdout
    images = run('docker', 'image', 'ls', '-aq', '--no-trunc').stdout.splitlines()
    before_backup = destination_snapshot(journey, old)
    with tempfile.NamedTemporaryFile() as cookie:
        journey.authenticate(cookie.name)
        durable = durable_evidence(journey)
        normal, normal_hash = removal_plan(journey, '--backup')
        retained = volume_facts(journey, normal)
        volumes = run('docker', 'volume', 'ls', '-q').stdout.splitlines()
        assert all(resource['Action'] == 1 for resource in normal['Resources'] if resource['Kind'] == 2)
        accepted = journey.ctl('uninstall', '--accept-plan', normal_hash)
        receipt = json.loads(journey.host('cat', str(journey.install / 'uninstall.json')).stdout)
        assert receipt['Phase'] == 3 and receipt['RemovedVolumes'] == []
        final_backup = receipt['FinalBackup']
        assert final_backup['IntegrityValid'] and final_backup['CompatibilitySupported']
        after_backup = destination_snapshot(journey, old)
        assert before_backup['identity'] == after_backup['identity'] and before_backup['marker'] == after_backup['marker']
        assert before_backup['files'].items() <= after_backup['files'].items()
        new_archives = set(after_backup['files']) - set(before_backup['files'])
        assert new_archives == {final_backup['Basename'], final_backup['Basename'] + '.sha256'}
        assert after_backup['files'][final_backup['Basename']] == final_backup['Sha256']
        commands = command_trace(journey)
        assert any('verify' in command and final_backup['Basename'] in command for command in commands)
        assert 'Integrity: True; compatible: True' in accepted.stdout
        assert_absent(normal, (0, 1))
        assert sorted(volumes) == sorted(run('docker', 'volume', 'ls', '-q').stdout.splitlines())
        assert retained == volume_facts(journey, normal) and authority == authority_snapshot(journey)
        for command in ['status', 'doctor']:
            assert 'Preserved' in journey.ctl(command).stdout
        before_replay = command_trace(journey)
        assert 'Already intentionally uninstalled' in journey.ctl('uninstall', '--accept-plan', normal_hash).stdout
        assert command_trace(journey) == before_replay
        assert destination_snapshot(journey, old) == after_backup and volume_facts(journey, normal) == retained
        print('PASS normal uninstall: exact fresh verified quiesced archive, zero volume deletion, protected authority and Preserved no-Docker replay', flush=True)
        reactivate(journey, normal, retained, durable, cookie.name)
    waiver, waiver_hash = removal_plan(journey, '--without-backup')
    assert waiver_hash != normal_hash and waiver['Operation'] != normal['Operation']
    journey.ctl('uninstall', '--accept-plan', waiver_hash)
    previous = journey.host('cat', str(journey.install / 'uninstall.json')).stdout
    assert json.loads(previous)['Phase'] == 3 and json.loads(previous)['FinalBackup'] is None
    assert destination_snapshot(journey, old) == after_backup
    assert_absent(waiver, (0, 1))
    assert volume_facts(journey, waiver) == retained
    print('PASS fresh explicit-waiver normal uninstall reached Preserved without another recovery set', flush=True)
    purge, purge_hash = removal_plan(journey, '--purge', '--without-backup')
    assert purge['StartingState'] == 1 and purge['Mode'] == 1 and purge['Backup'] == 1
    assert purge_hash not in (normal_hash, waiver_hash)
    historical = [resource['Name'] for resource in purge['Resources'] if resource['Kind'] == 2
                  and resource['DockerId'] is not None and resource['LifecycleOperation'] is not None]
    # This real update/restore journey retains old generations; no fabricated history or debris.
    assert historical, 'update/restore no longer retains generations; reassess the documented evidence boundary'
    sentinel = 'qualification-uninstall-sentinel-' + uuid.uuid4().hex
    run('docker', 'volume', 'create', '--label', 'wayfarer.qualification-uninstall=' + journey.project, sentinel)
    try:
        journey.host('tee', str(journey.directory / 'preserved-receipt.json'), data=previous)
        journey.host('tee', str(journey.directory / 'failure.purge-observe'), data=waiver['Operation'].replace('-', ''))
        before_purge = destination_snapshot(journey, old)
        purge_volumes = run('docker', 'volume', 'ls', '-q').stdout.splitlines()
        journey.ctl('uninstall', '--accept-plan', purge_hash)
        transfer = json.loads(journey.host('cat', str(journey.directory / 'purge-transfer.json')).stdout)
        assert transfer['Phase'] == 0 and transfer['PlanHash'] == purge_hash and transfer['Plan'] == purge
        assert journey.host('cat', str(journey.directory / 'purge-history.json')).stdout == previous
        assert_absent(purge, (0, 1, 2))
        removed = {resource['Name'] for resource in purge['Resources'] if resource['Kind'] == 2 and resource['DockerId'] is not None}
        assert set(run('docker', 'volume', 'ls', '-q').stdout.splitlines()) == set(purge_volumes) - removed
        run('docker', 'volume', 'inspect', sentinel)
        assert destination_snapshot(journey, old) == before_purge
        root_names = journey.host('ls', '-A', str(journey.install)).stdout.splitlines()
        assert sorted(root_names) == ['operation.lock', 'releases', 'uninstall-purged.json']
        tombstone = json.loads(journey.host('cat', str(journey.install / 'uninstall-purged.json')).stdout)
        assert set(tombstone) == {'Schema', 'Operation', 'PlanHash', 'OperatorOwner', 'Result'}
        assert tombstone['PlanHash'] == purge_hash and tombstone['Result'] == 'purged'
        before_replay = command_trace(journey)
        assert 'Already intentionally purged' in journey.ctl('uninstall', '--accept-plan', purge_hash).stdout
        for command in ['status', 'doctor']:
            assert 'Purged' in journey.ctl(command).stdout
        assert command_trace(journey) == before_replay
        print('PASS Preserved-to-purge archived exact normal receipt before Authorized; removed current and historical volumes: ' + ', '.join(sorted(removed)), flush=True)
        print('PASS minimal Purged root, tombstone-only no-Docker replay/status/doctor, unrelated sentinel and byte-identical backup custody', flush=True)
        fresh_setup(journey, secrets, old, before_purge)
        assert sorted(images) == sorted(run('docker', 'image', 'ls', '-aq', '--no-trunc').stdout.splitlines())
        commands = command_trace(journey)
        assert journey.host('test', '-e', str(journey.directory / 'failure.forbidden'), check=False).returncode != 0
        evidence = dict(project=journey.project, normalPlan=normal_hash, waiverPlan=waiver_hash, purgePlan=purge_hash,
                        finalBackup=final_backup, retainedVolumes=retained, historicalVolumes=historical,
                        purgedVolumes=sorted(removed), preservedReceiptSha256=hashlib.sha256(previous.encode()).hexdigest(),
                        transferPhase=transfer['Phase'], sentinel=sentinel, backupBefore=before_purge,
                        backupAfterFreshSetup=destination_snapshot(journey, old), purgedRoot=root_names,
                        tombstone=tombstone, continuitySha256=hashlib.sha256(durable.encode()).hexdigest(),
                        dockerCommandCount=len(commands), dockerCommandsSha256=hashlib.sha256(json.dumps(commands).encode()).hexdigest(),
                        freshInstallation=configuration(journey).get('Installation', str(uuid.UUID(int=0))), result='passed')
        if getattr(journey, 'lifecycle_evidence', None):
            journey.lifecycle_evidence.write_text(json.dumps(evidence, indent=2) + '\n')
        print('PASS joined lifecycle evidence ' + json.dumps(evidence, sort_keys=True), flush=True)
    finally:
        run('docker', 'volume', 'rm', sentinel)
    print('PASS complete Wayfarer Compose lifecycle including uninstall, reactivation, purge and fresh reinstall', flush=True)


def observe_removal(journey):
    """Observe product child calls and receipt transfer; reject broad cleanup before it can affect the daemon."""
    wrapper = journey.host('cat', str(journey.directory / 'docker-test')).stdout
    observation = '''if test "$1" = --host && test "$2" = unix:///var/run/docker.sock; then
(
# Observe only operator calls; normalize its local-daemon prefix in a subshell so forwarding keeps it.
shift 2
printf '%s\\t' "$@" >> "$WAYFARER_TEST_FAILURE.commands"
printf '\\n' >> "$WAYFARER_TEST_FAILURE.commands"
case "$*" in
  "image rm "*|"rmi "*|"image prune"*|"system prune"*|"builder prune"*|"buildx prune"*|"volume prune"*|"network prune"*|"container prune"*|compose*" down"*" -v"*|compose*" down"*" --volumes"*)
    printf forbidden > "$WAYFARER_TEST_FAILURE.forbidden"; exit 99 ;;
esac
if test "$1" = info && test -f "$WAYFARER_TEST_FAILURE.purge-observe"; then
  fixture=$(dirname "$WAYFARER_TEST_FAILURE")
  previous=$(cat "$WAYFARER_TEST_FAILURE.purge-observe")
  if test -f "$fixture/installation/uninstall-history/$previous.json" && ! test -f "$fixture/purge-transfer.json"; then
    cmp -s "$fixture/installation/uninstall-history/$previous.json" "$fixture/preserved-receipt.json" || exit 98
    cp "$fixture/installation/uninstall.json" "$fixture/purge-transfer.json"
    cp "$fixture/installation/uninstall-history/$previous.json" "$fixture/purge-history.json"
  fi
fi
)
observed=$?
test "$observed" -eq 0 || exit "$observed"
fi
'''
    journey.host('tee', str(journey.directory / 'docker-test'), data=wrapper.replace('#!/bin/sh\n', '#!/bin/sh\n' + observation))


def command_trace(journey):
    """Read only operator child calls; Docker observations made by the runner do not enter this trace."""
    return [line.rstrip('\t').split('\t') for line in
            journey.host('cat', str(journey.directory / 'failure.commands'), check=False).stdout.splitlines()]


def removal_plan(journey, *choices):
    """Bind the structured printed plan, exact protected bytes and the printed SHA-256 before acceptance."""
    output = journey.ctl('uninstall', '--plan', *choices).stdout
    serialized = next(line for line in output.splitlines() if line.startswith('{'))
    digest = output.split('Plan SHA-256: ')[1].splitlines()[0]
    assert hashlib.sha256(serialized.encode()).hexdigest() == digest
    assert journey.host('cat', str(journey.install / 'uninstall-plans' / (digest + '.json'))).stdout == serialized
    return json.loads(serialized), digest


def volume_facts(journey, plan):
    """Inspect actual Docker creation/ownership and local inode identities, including retained old generations."""
    facts = {}
    for resource in plan['Resources']:
        if resource['Kind'] != 2 or resource['DockerId'] is None:
            continue
        volume = json.loads(run('docker', 'volume', 'inspect', resource['Name']).stdout)[0]
        inode = journey.host('stat', '-c', '%d:%i:%u:%g:%a:%W', volume['Mountpoint']).stdout.strip()
        planned = json.loads(resource['Evidence'])
        assert volume['CreatedAt'] == planned['CreatedAt'] and volume['Mountpoint'] == planned['Mountpoint']
        assert int(inode.split(':')[1]) == planned['DataDirectory']['Inode']
        facts[resource['Name']] = dict(created=volume['CreatedAt'], labels=volume['Labels'], inode=inode)
    return facts


def assert_absent(plan, kinds):
    """List real daemon resources rather than treating a failed inspect or command exit as absence."""
    for kind in kinds:
        arguments = {0: ['ps', '-a', '--format', '{{.Names}}'],
                     1: ['network', 'ls', '--format', '{{.Name}}'], 2: ['volume', 'ls', '-q']}[kind]
        present = set(run('docker', *arguments).stdout.splitlines())
        assert not present.intersection(resource['Name'] for resource in plan['Resources'] if resource['Kind'] == kind)


def authority_snapshot(journey):
    """Hash protected retained authority, excluding evolving scheduler state and uninstall's own new receipts."""
    return journey.host('sh', '-ec',
        r'find "$1" -path "$1/uninstall*" -prune -o -path "$1/recovery-control/state" -prune -o -type f -exec sha256sum {} \; | sort',
        'authority', str(journey.install)).stdout


def destination_snapshot(journey, config):
    """Administrator storage identity, exact marker bytes and every archive/sidecar/hold hash survive purge and setup."""
    destination = config['Backup']['Destination']
    identity = journey.host('stat', '-c', '%d:%i:%u:%g:%a', destination).stdout.strip()
    marker = journey.host('cat', destination + '/.wayfarer-recovery').stdout
    hashes = journey.host('sh', '-ec', r'cd "$1"; find . -maxdepth 1 -type f -exec sha256sum {} \; | sort',
                          'destination', destination).stdout.splitlines()
    return dict(destination=destination, identity=identity, marker=marker,
                files={line.split('  ./', 1)[1]: line.split('  ./', 1)[0] for line in hashes})


def durable_evidence(journey):
    """Reuse the existing credential/token probe and hash the real SQL witness, migration history, Uploads and complete key ring."""
    assert 'PASS protected credential and production Identity token continuity' in journey.probe_command('verify')
    database = journey.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-v', 'ON_ERROR_STOP=1', '-c',
        'SELECT id, name::text, ST_AsEWKT(position), md5(payload) FROM recovery_qualification ORDER BY id; '
        'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"; '
        "SELECT 'update_qualification'::regclass;")
    files = journey.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                            r'find /var/lib/wayfarer -type f -exec sha256sum {} \; | sort')
    assert 'Αθήνα' in database and '20990101000000_UpdateQualification' in database
    assert '/uploads/imports/recovery-qualification' in files and '/data-protection/key-' in files
    return database + files


def reactivate(journey, plan, retained, durable, cookie):
    """Prove storage preflight precedes creation, retained --pull never activation and the same login/data evidence."""
    start = len(command_trace(journey))
    assert 'Reactivation complete' in journey.ctl('start').stdout
    commands = command_trace(journey)[start:]
    creation = next(index for index, command in enumerate(commands) if command[0] == 'compose' and 'up' in command)
    for name in retained:
        assert ['volume', 'inspect', name] in commands[:creation]
    for command in commands:
        if command[0] == 'compose' and 'up' in command:
            assert command[command.index('--pull') + 1] == 'never'
        assert command[0] != 'pull' and not ('database' in command and 'migrate' in command)
    assert volume_facts(journey, plan) == retained and durable_evidence(journey) == durable
    assert journey.curl('/Admin/Users', '-b', cookie, '-o', '/dev/null', '-w', '%{http_code}') == '200'
    for service, target, role in [('wayfarer', '/var/lib/wayfarer', 'app-data'),
                                  ('db', '/var/lib/postgresql', 'db-data')]:
        mounts = json.loads(run('docker', 'inspect', '--format', '{{json .Mounts}}', journey.project + '-' + service + '-1').stdout)
        expected = next(resource['Name'] for resource in plan['Resources'] if resource['Kind'] == 2
                        and resource['Role'] == role and resource['LifecycleOperation'] is None)
        assert next(mount['Name'] for mount in mounts if mount['Destination'] == target) == expected
    scheduler = json.loads(run('docker', 'inspect', journey.project + '-backup-scheduler-1').stdout)[0]
    assert scheduler['State']['Running'] and scheduler['Config']['Cmd'] == ['schedule']
    assert any('backup-scheduler' in command and 'up' in command for command in commands)
    journey.ctl('doctor')
    archived = json.loads(journey.host('cat', str(journey.install / 'uninstall-history' / (plan['Operation'].replace('-', '') + '.json'))).stdout)
    assert archived['Plan'] == plan and archived['Phase'] == 3
    print('PASS reactivation: all exact volume inodes inspected before Compose, --pull never, enabled scheduler, same SQL/Identity login/Uploads/Data Protection evidence', flush=True)


def fresh_setup(journey, secrets, old, backup):
    """Use ordinary release-backed setup on the same root; the product consumes its tombstone without adopting old backups."""
    journey.ctl('setup', '--bundle', str(journey.release_bundle), '--hostname', 'wayfarer.example.org',
                '--project', journey.project, '--edge-prefix', '172.30.69', '--mode', 'external',
                '--loopback-port', str(journey.loopback), '--password-stdin', data=journey.password + '\n')
    fresh = configuration(journey)
    # Deployment intentionally omits its default Guid.Empty identity until backup is configured.
    identity = fresh.get('Installation', str(uuid.UUID(int=0)))
    assert identity == str(uuid.UUID(int=0)) and identity != old['Installation']
    assert 'Backup' not in fresh
    current = journey.host('sh', '-ec', 'sha256sum "$1"/secrets/*', 'secrets', str(journey.install)).stdout
    assert all(left.split()[0] != right.split()[0] for left, right in zip(secrets.splitlines(), current.splitlines(), strict=True))
    for name in ['uninstall-purged.json', 'uninstall.json', 'backup-identity', 'recovery-generations']:
        assert journey.host('test', '-e', str(journey.install / name), check=False).returncode == 1
    assert destination_snapshot(journey, old) == backup
    journey.ctl('doctor')
    assert journey.curl('/health/ready', '-o', '/dev/null', '-w', '%{http_code}') == '200'
    print('PASS fresh setup consumed tombstone: healthy, every secret fresh, Guid.Empty no-backup identity, no inherited policy/destination or relabelling', flush=True)


def prepare_candidates(source_bundle, output):
    """Build a disposable real migration from this exact head; no product migration or stable tag."""
    repo = Path(__file__).resolve().parents[2]
    source_bundle, output = source_bundle.resolve(), output.resolve()
    def run(*args, cwd=repo, env=None):
        """Keep recipe subprocess failures visible and retain their exact output."""
        return subprocess.run(args, cwd=cwd, env=env, check=True, text=True, stdout=subprocess.PIPE).stdout.strip()
    if run('git', 'status', '--porcelain', '--untracked-files=normal'):
        raise RuntimeError('candidate recipe requires a clean committed exact head')
    head = run('git', 'rev-parse', 'HEAD')
    source_manifest = json.loads((source_bundle / 'release.json').read_text())
    if source_manifest['SourceRevision'] != head or source_manifest['Status'] != 'candidate':
        raise RuntimeError('source must be a candidate assembled from this exact head')
    output.mkdir(parents=True, exist_ok=False)
    source = output / 'source'
    shutil.copytree(source_bundle, source)
    with tempfile.TemporaryDirectory(prefix='wayfarer-update-target-') as temporary:
        checkout = Path(temporary) / 'checkout'
        run('git', 'clone', '--shared', '--no-checkout', str(repo), str(checkout))
        run('git', 'checkout', '--detach', head, cwd=checkout)
        props = checkout / 'Version.props'
        major, minor, patch = map(int, source_manifest['Version'].split('.'))
        target_version = f'{major}.{minor}.{patch + 1}'
        props.write_text(props.read_text().replace(f'>{source_manifest["Version"]}<', f'>{target_version}<'))
        migration = '20990101000000_UpdateQualification'
        (checkout / 'Migrations' / (migration + '.cs')).write_text('''using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wayfarer.Models;

namespace Wayfarer.Migrations;

/// <summary>Disposable qualification only: exercise real transactional DDL across the update boundary.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20990101000000_UpdateQualification")]
public sealed class UpdateQualification : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE update_qualification (id integer PRIMARY KEY);");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE update_qualification;");
}
''')
        run('git', 'add', 'Version.props', 'Migrations/' + migration + '.cs', cwd=checkout)
        # Fixed author/committer identity and parent timestamp make the fixture commit reproducible.
        stamp = run('git', 'show', '-s', '--format=%cI', head)
        env = dict(os.environ, GIT_AUTHOR_DATE=stamp, GIT_COMMITTER_DATE=stamp,
                   GIT_AUTHOR_NAME='Wayfarer qualification', GIT_COMMITTER_NAME='Wayfarer qualification',
                   GIT_AUTHOR_EMAIL='qualification@example.invalid', GIT_COMMITTER_EMAIL='qualification@example.invalid')
        run('git', '-c', 'commit.gpgsign=false', 'commit', '-m', 'Disposable update migration qualification', cwd=checkout, env=env)
        target_head = run('git', 'rev-parse', 'HEAD', cwd=checkout)
        # Reuse existing image and bundle owners, with a candidate-only local image tag.
        build = "import sys; sys.path.insert(0, 'tools/release'); import image; r=image.identity(None,None); r['tag']='candidate-update-'+r['sourceRevision']; ref=image.build(r); print(image.inspect_image(r,ref)['RepoDigests'][0].split('@')[1])"
        digest = run('python3', '-B', '-c', build, cwd=checkout).splitlines()[-1]
        # Forward updates retain the source candidate's exact database image authority.
        run('python3', '-B', 'tools/release/bundle.py', '--app-digest', digest,
            '--db-digest', source_manifest['Images']['DatabaseDigest'],
            '--output', str(output / 'target'), cwd=checkout)
        target = next((output / 'target').glob('candidate-*'))
        operator = Path(temporary) / 'operator'
        run('dotnet', 'publish', 'tools/WayfarerCtl', '-c', 'Release', '-r', 'linux-x64',
            '--self-contained', 'true', '-p:DefineConstants=UPDATE_QUALIFICATION', '-o', str(operator))
        for bundle in (source, target):
            executable = bundle / 'wayfarerctl'
            executable.chmod(0o755)
            shutil.copyfile(operator / 'wayfarerctl', executable)
            executable.chmod(0o555)
            manifest = json.loads((bundle / 'release.json').read_text())
            # The shared source operator owns both candidates; retain its actual protocol/version.
            manifest['Operator'] = json.loads(run(str(executable), 'release', 'protocol'))
            next(entry for entry in manifest['Files'] if entry['Path'] == 'wayfarerctl')['Sha256'] = hashlib.sha256(executable.read_bytes()).hexdigest()
            if bundle == target:
                inspected = json.loads(run(str(source / 'wayfarerctl'), 'release', 'inspect', str(source)))
                manifest['Sources'] = [dict(Version=source_manifest['Version'], Fingerprint=inspected['Fingerprint'],
                    TerminalMigration=source_manifest['Application']['TerminalMigration'], ExactOrderedPrefix=True,
                    ReferenceSeeding=False, RetryRestriction='manual-recovery', Warning='Disposable qualification only')]
                assert manifest['Application']['Migrations'] == source_manifest['Application']['Migrations'] + [migration]
            (bundle / 'release.json').write_text(json.dumps(manifest, sort_keys=True, separators=(',', ':')) + '\n')
            run(str(executable), 'release', 'inspect', str(bundle))
        # Replace the assembly archive after candidate-only operator/source-boundary changes.
        for archive in (output / 'target').glob('*.tar.gz'):
            archive.unlink()
        (output / 'target' / 'SHA256SUMS').unlink()
        run('python3', '-B', '-c', "import sys; from pathlib import Path; sys.path.insert(0,'tools/release'); import bundle; bundle.archive(Path(sys.argv[1]),Path(sys.argv[2]))", str(target), str(output / 'target'))
        evidence = dict(sourceHead=head, targetHead=target_head, migration=migration, applicationDigest=digest,
                        sourceBundle=str(source), targetBundle=str(target))
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
        print(json.dumps(evidence), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Prepare disposable update candidates for qualify_recovery.py --update-bundle.')
    parser.add_argument('--source-bundle', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    prepare_candidates(args.source_bundle, args.output)
