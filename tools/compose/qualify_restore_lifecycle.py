"""Real restore finalization, bounded staging/capacity and emergency publication regressions."""
import json
import uuid
from qualify_ctl import HOST, run


def assert_single_staging(journey, plan):
    """Reverification retains one component tree and never duplicates archive-sized staging."""
    directory = journey.install / 'restore-plans' / plan['Operation'].replace('-', '')
    names = journey.host('find', str(directory), '-maxdepth', '1', '-name', 'verified*', '-printf', '%f\n').stdout.splitlines()
    assert names == ['verified']
    return int(journey.host('du', '-sk', str(directory / 'verified')).stdout.split()[0])


def qualify_restore_lifecycle(journey):
    """Extend the maintained real journey; each regression uses the shipped operator/engine."""
    qualify_emergency_publication(journey)
    qualify_capacity(journey)
    qualify_finalization(journey)


def qualify_emergency_publication(journey):
    """Kill the real engine before/after pair publication and reconcile under the product lock."""
    journey.ctl('stop')
    journey.compose('up', '-d', '--wait', 'db')
    config = json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout)
    overlay = journey.install / 'recovery-generations' / config['Backup']['Generation'] / 'compose.json'
    reservation = journey.install / 'recovery-control/host-operation.json'
    destination = journey.directory / 'destination'
    for point in ['emergency-pending-death', 'emergency-fail', 'emergency-hold-death', 'emergency-archive-death', 'emergency-committed-death']:
        before = set(journey.host('ls', str(destination)).stdout.splitlines())
        token = uuid.uuid4().hex
        journey.host('tee', str(reservation), data=json.dumps(dict(
            Schema=1, Token=token, Container=journey.project + '-hold-probe', Quiesced=True, RestoreHold=True)))
        journey.host('chown', '0:1654', str(reservation))
        journey.host('chmod', '640', str(reservation))
        try:
            # Compose creates only the fixture worker; engine owns archive bytes, hold and atomic publication.
            output = journey.compose('-f', str(overlay), '--profile', 'backup', 'run', '--rm', '--no-deps', '-T',
                '--volume', str(journey.lock_probe) + ':/lock-probe:ro', '--entrypoint', 'sh', 'backup-worker',
                '-c', '/lock-probe "$@"; result=$?; printf "probe-exit=%s\n" "$result"', 'sh', point, token)
            assert 'probe-exit=' + ('1' if point == 'emergency-fail' else '137') in output
        finally:
            journey.host('rm', '-f', str(reservation))
        created = set(journey.host('ls', str(destination)).stdout.splitlines()) - before
        holds = {name for name in created if '.restore-hold' in name}
        assert len(holds) == (0 if point == 'emergency-fail' else 1)
        if point == 'emergency-archive-death':
            # Model the old worker's possible truncated final hold without changing ownership/mode.
            journey.host('truncate', '-s', '0', str(destination / next(iter(holds))))
        journey.ctl('backup', '--quiesced')
        after = set(journey.host('ls', str(destination)).stdout.splitlines())
        if point == 'emergency-committed-death':
            hold = next(iter(holds))
            assert {hold, hold.removesuffix('.restore-hold'), hold.removesuffix('.restore-hold') + '.sha256'} <= after
        else:
            assert not holds & after
    print('PASS emergency failure and actual process deaths reclaim uncommitted holds; committed held pair survives retention', flush=True)


def qualify_capacity(journey):
    """A real 64-MiB filesystem at the Docker capacity observation seam refuses before fencing."""
    planned = journey.ctl('restore', '--without-emergency-backup', '--plan').stdout
    plan = json.loads(planned.splitlines()[0])
    accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
    baseline = journey.host('cat', str(journey.install / 'installation.json')).stdout
    small = journey.directory / 'capacity-small'
    failure = journey.directory / 'failure'
    journey.host('mkdir', str(small))
    def mount_command(*args):
        return run('docker', 'run', '--rm', '--privileged', '--pid=host', '--network', 'none', HOST,
                   'nsenter', '-t', '1', '-m', '--', *args)
    try:
        mount_command('mount', '-t', 'tmpfs', '-o', 'size=64m,mode=0700', 'tmpfs', str(small))
        journey.host('tee', str(failure), data='restore-capacity')
        result = journey.ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup', check=False)
        assert result.returncode == 1 and 'Insufficient restore capacity' in result.stderr
        receipt = json.loads(journey.host('cat', str(journey.install / 'recovery-control/restore.json')).stdout)
        assert receipt['Phase'] == 0 and not receipt['Volumes'] and not receipt['Containers']
        assert journey.host('cat', str(journey.install / 'installation.json')).stdout == baseline
        assert_single_staging(journey, plan)
    finally:
        journey.host('rm', '-f', str(failure))
        mount_command('umount', str(small))
    journey.ctl('restore', '--abort', plan['Operation'])
    print('PASS measured capacity shortage refused before source fencing/candidate creation', flush=True)


def qualify_finalization(journey):
    """Actual process death during restart restoration and completion-write failure remain unresolved."""
    completion = journey.install / 'restore-complete'
    failure = journey.directory / 'failure'
    for point in ['restore-restart-death', 'restore-completion-failure']:
        planned = journey.ctl('restore', '--without-emergency-backup', '--plan').stdout
        plan = json.loads(planned.splitlines()[0])
        accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
        size = assert_single_staging(journey, plan)
        if point == 'restore-restart-death':
            journey.host('tee', str(failure), data=point)
        else:
            journey.host('rm', '-f', str(completion))
            journey.host('mkdir', str(completion))
        result = journey.ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup', check=False)
        assert result.returncode != 0
        journey.host('rm', '-f', str(failure))
        receipt = json.loads(journey.host('cat', str(journey.install / 'recovery-control/restore.json')).stdout)
        assert receipt['Phase'] == 7 and receipt['WritesPossible'], f"phase={receipt['Phase']}; {result.stderr}"
        journey.host('test', '-f', str(journey.install / 'recovery-control/restore-in-progress'))
        assert journey.ctl('start', check=False).returncode == 2
        assert journey.ctl('restore', '--abort', plan['Operation'], check=False).returncode == 2
        if point == 'restore-completion-failure':
            journey.host('rmdir', str(completion))
        journey.ctl('restore', '--resume', plan['Operation'])
        assert assert_single_staging(journey, plan) == size
        final = json.loads(journey.host('cat', str(journey.install / 'recovery-control/restore.json')).stdout)
        assert final['Phase'] == 8 and journey.host('cat', str(completion)).stdout == plan['Operation']
        assert journey.host('test', '-e', str(journey.install / 'recovery-control/restore-in-progress'), check=False).returncode == 1
        for identifier in journey.compose('ps', '-q').split():
            state = json.loads(run('docker', 'inspect', identifier).stdout)[0]
            assert state['HostConfig']['RestartPolicy']['Name'] == 'unless-stopped'
        journey.ctl('doctor')
    print('PASS finalization death/failure stays WritesPossible; forward resume commits completion and restart policies before Accepted', flush=True)
