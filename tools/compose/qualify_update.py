"""One real update boundary and bounded fault observations in the established recovery fixture."""
import json


def qualify_update(journey):
    """Candidate-only operator build, exact source boundary, lost acknowledgement, private failure and restore handoff."""
    journey.ctl('backup', '--quiesced')
    journey.ctl('release', 'adopt', str(journey.release_bundle))
    original = configuration(journey)
    # Child-process fault injection reuses the fixture's existing Docker boundary.
    wrapper = journey.host('cat', str(journey.directory / 'docker-test')).stdout
    injection = '''case "$point:$*" in
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
    plan, digest = plan_update(journey)
    journey.host('tee', str(journey.directory / 'failure'), data='update-ack')
    assert journey.ctl('update', '--accept-plan', digest, check=False).returncode != 0
    receipt = update_receipt(journey)
    assert receipt['Phase'] == 3 and receipt['RecoveryArchive'] and receipt['RecoverySha256']
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
