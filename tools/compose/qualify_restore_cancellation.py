"""One actual pg_restore cancellation proof using the existing Compose journey and process seam."""
import json
import subprocess
import time
import tempfile
from qualify_ctl import HOST, run


def qualify_sql_cancellation(journey):
    """Gate SQL helper creation, hold a candidate-only catalog lock, then cancel after pg_restore actually waits."""
    baseline = journey.host('cat', str(journey.install / 'installation.json')).stdout
    planned = journey.ctl('restore', '--without-emergency-backup', '--plan').stdout
    plan = json.loads(planned.splitlines()[0])
    accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
    control = journey.project + '-restore-cancel-controller'
    candidate = journey.project + '-restore-' + plan['CandidateGeneration'] + '-db'
    worker = journey.project + '-restore-' + plan['CandidateGeneration'] + '-sql'
    failure = str(journey.directory / 'failure')
    journey.host('tee', failure, data='restore-cancel')
    controller = None
    blocker = None
    output_file = tempfile.TemporaryFile(mode="w+t")
    error_file = tempfile.TemporaryFile(mode="w+t")
    try:
        controller = subprocess.Popen(['docker', 'run', '--rm', '-i', '--name', control, '--network', 'host',
            '-v', str(journey.directory) + ':' + str(journey.directory), '-v', str(journey.executable) + ':/ctl:ro',
            '-v', '/usr/bin/docker:/usr/bin/docker:ro', '-v', str(journey.directory / 'docker-test') + ':/usr/local/bin/docker:ro',
            '-e', 'WAYFARER_TEST_FAILURE=' + failure, '-v', str(journey.plugin) + ':/usr/libexec/docker/cli-plugins:ro',
            '-v', journey.socket + ':/var/run/docker.sock', HOST, '/ctl', '--deployment-root', str(journey.install),
            'restore', '--accept-plan', accepted, '--trust-controlled-backup'],
            stdin=subprocess.PIPE, stdout=output_file, stderr=error_file, text=True)
        deadline = time.monotonic() + 120
        while journey.host('test', '-e', failure + '.ready', check=False).returncode:
            if controller.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError('restore controller did not reach the SQL creation gate')
            time.sleep(0.1)
        blocker = subprocess.Popen(['docker', 'exec', '-i', candidate, 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        blocker.stdin.write("BEGIN; LOCK TABLE pg_catalog.pg_extension IN ACCESS EXCLUSIVE MODE; SELECT 'locked';\n")
        blocker.stdin.flush()
        while blocker.stdout.readline().strip() != 'locked':
            if blocker.poll() is not None:
                raise RuntimeError('candidate catalog lock unavailable')
        journey.host('touch', failure + '.go')
        deadline = time.monotonic() + 30
        while True:
            waiting = run('docker', 'exec', candidate, 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-c',
                "SELECT count(*) FROM pg_stat_activity WHERE application_name='pg_restore' AND wait_event_type='Lock'").stdout
            if int(waiting.strip()) > 0:
                break
            if controller.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError('actual pg_restore did not reach the candidate-only lock')
            time.sleep(0.1)
        run('docker', 'kill', '--signal=INT', control)
        controller.communicate(timeout=90)
        error_file.seek(0)
        error = error_file.read()
        assert controller.returncode == 1 and 'phase=Staging' in error
        state = json.loads(run('docker', 'inspect', worker).stdout)[0]
        assert not state['State']['Running'] and state['HostConfig']['RestartPolicy']['Name'] == 'no'
        assert journey.host('cat', str(journey.install / 'installation.json')).stdout == baseline
        print('PASS cancellation stopped/reaped actual pg_restore helper while candidate SQL waited; old authority intact', flush=True)
    finally:
        if controller is not None and controller.poll() is None:
            run('docker', 'kill', '--signal=INT', control, check=False)
            controller.communicate(timeout=90)
        if blocker is not None:
            if blocker.poll() is None:
                blocker.terminate()
            blocker.communicate(timeout=10)
        output_file.close()
        error_file.close()
        journey.host('rm', '-f', failure, failure + '.ready', failure + '.go')
    journey.ctl('restore', '--abort', plan['Operation'])
    journey.compose('up', '-d', '--wait', 'db')
