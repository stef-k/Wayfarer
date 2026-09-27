"""Disposable recovery qualification extending the established operator/Compose fixture.

Only random labelled projects and fixture-owned paths are mutated. Python is test
orchestration; the published C# operator and worker own every backup operation.
"""
import argparse
import json
import hashlib
import time
import subprocess
from pathlib import Path
import shutil
import tempfile
import uuid
from datetime import datetime, timedelta
from qualify_ctl import Journey, run, HOST
from qualify import Stack
from qualify_restore_cancellation import qualify_sql_cancellation


class RecoveryJourney(Journey):
    """Exercise actual self-contained commands against a complete application database."""
    def __init__(self, directory, executable, digest, worker, inspector, probe):
        super().__init__(directory, executable, digest)
        self.payload = self.directory / 'payload'
        self.payload.mkdir()
        shutil.copy2(worker, self.payload / 'wayfarer-recovery')
        shutil.copy2(inspector, self.payload / 'WayfarerRecoverySource.dll')
        (self.payload / 'wayfarer-recovery').chmod(0o555)
        (self.payload / 'WayfarerRecoverySource.dll').chmod(0o444)
        (self.directory / 'destination').mkdir()
        self.probe = Path(probe).resolve()
        shutil.copy2(self.probe, self.directory / 'RecoveryProbe.dll')
        (self.directory / 'probe').mkdir()
        self.lock_probe = self.directory / 'lock-probe'
        shutil.copy2(Path(worker).parent / 'lock-probe', self.lock_probe)

    def recovery(self, restore_only=False):
        """Setup uses existing authority; source inspection must work without changing the app image."""
        self.ctl('setup', '--bundle', str(self.bundle), '--hostname', 'wayfarer.example.org',
                 '--app-digest', self.digest, '--project', self.project, '--edge-prefix', '172.30.69',
                 '--mode', 'external', '--loopback-port', str(self.loopback),
                 '--password-stdin', data=self.password + '\n')
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                     'mkdir -p /var/lib/wayfarer/uploads/imports; printf durable > /var/lib/wayfarer/uploads/imports/recovery-qualification')
        self.host('chown', '1654:1654', str(self.directory / 'probe'))
        self.probe_command('seed')
        self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-v', 'ON_ERROR_STOP=1', '-c',
                     "CREATE TABLE recovery_qualification (id integer PRIMARY KEY, name citext, position geometry(Point,4326), payload bytea); "
                     "ALTER TABLE recovery_qualification OWNER TO wayfarer; "
                     "INSERT INTO recovery_qualification VALUES (1, 'Αθήνα', ST_SetSRID(ST_Point(23.7,37.9),4326), (SELECT decode(string_agg(md5(i::text),''),'hex') FROM generate_series(1,20000) i));")
        self.host('mkdir', '-m', '700', str(self.install / 'recovery-control'))
        print('PASS completed source application setup', flush=True)
        result = self.ctl('backup', 'configure', '--destination', str(self.directory / 'destination'),
                         '--payload', str(self.payload / 'wayfarer-recovery'), '--retention', '1')
        print(result.stdout, flush=True)
        # Wait on the committed scheduler receipt, not an assumed capture duration.
        deadline = time.monotonic() + 90
        while True:
            receipt = self.host('cat', str(self.install / 'recovery-control/state/scheduler.json'), check=False)
            if receipt.returncode == 0 and json.loads(receipt.stdout)['Succeeded']:
                break
            if time.monotonic() > deadline:
                raise RuntimeError('scheduled capture did not commit its bounded receipt')
            time.sleep(0.1)
        print('PASS scheduled due capture receipt', flush=True)
        if restore_only:
            self.ctl('backup', '--quiesced')
            self.restore_refusals()
            qualify_sql_cancellation(self)
            self.restore_boundaries()
            self.restore()
            return
        generation = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)['Backup']['Generation']
        scheduler_args = ['docker', 'compose', '--project-name', self.project, '--project-directory', str(self.bundle),
                          '--env-file', str(self.install / 'deployment.env'), '-f', str(self.bundle / 'compose.yaml'),
                          '-f', str(self.bundle / 'external.yaml'), '-f', str(self.install / 'recovery-generations' / generation / 'compose.json'), '--profile', 'backup']
        before = self.host('cat', str(self.install / 'recovery-control/state/scheduler.json')).stdout
        self.host(*scheduler_args, 'up', '-d', '--no-deps', '--force-recreate', 'backup-scheduler')
        assert self.host('cat', str(self.install / 'recovery-control/state/scheduler.json')).stdout == before
        self.host(*scheduler_args, 'stop', 'backup-scheduler')
        self.scheduler_reconciliation(scheduler_args, json.loads(before))
        self.restricted_authority(scheduler_args)
        # Host lock owner is a self-contained process with no database or Docker access.
        owner = self.project + '-lock-owner'
        run('docker', 'run', '-di', '--name', owner, '--network', 'none', '--label', 'com.docker.compose.project=' + self.project,
            '-v', str(self.install / 'recovery-control') + ':/control', '-v', str(self.lock_probe) + ':/lock-probe:ro',
            HOST, '/lock-probe', '/control/recovery.lock')
        deadline = time.monotonic() + 10
        while 'acquired' not in run('docker', 'logs', owner).stdout:
            if time.monotonic() > deadline: raise RuntimeError('lock probe did not acquire')
            time.sleep(0.1)
        self.lock_ownership()
        assert self.ctl('backup', check=False).returncode == 1
        assert self.ctl('stop', check=False).returncode == 1
        assert self.host(*scheduler_args, 'run', '--rm', '--no-deps', '-T', 'backup-reader', 'backups', check=False).returncode == 1
        duplicate = self.project + '-duplicate-scheduler'
        self.host(*scheduler_args, 'run', '-d', '--no-deps', '--name', duplicate, 'backup-scheduler', 'schedule')
        deadline = time.monotonic() + 10
        while 'deferred' not in (run('docker', 'logs', duplicate).stdout + run('docker', 'logs', duplicate).stderr):
            if time.monotonic() > deadline: raise RuntimeError('duplicate scheduler did not defer to recovery exclusion')
            time.sleep(0.1)
        run('docker', 'stop', '--time', '5', duplicate)
        run('docker', 'rm', duplicate)
        run('docker', 'kill', owner)
        run('docker', 'rm', owner)
        print('PASS host lock contention and owner-death release; scheduler recreation retained receipt', flush=True)
        self.stale_residue()
        print(self.ctl('backup').stdout, flush=True)
        print(self.ctl('backups').stdout, flush=True)
        print(self.ctl('verify-backup').stdout, flush=True)
        self.cancellation()
        self.configuration_interruption()
        # Corruption and missing commit marker must fail without consuming or repairing archive bytes.
        listing = self.host('sh', '-ec', 'ls ' + str(self.directory / 'destination') + '/*.tar | sort | tail -1').stdout.strip()
        self.host('mv', listing + '.sha256', listing + '.sha256.saved')
        assert self.ctl('verify-backup', Path(listing).name, check=False).returncode == 1
        self.host('mv', listing + '.sha256.saved', listing + '.sha256')
        self.host('sh', '-ec', 'cp ' + listing + '.sha256 ' + listing + '.sha256.saved; printf corrupt > ' + listing + '.sha256')
        assert self.ctl('verify-backup', Path(listing).name, check=False).returncode == 1
        self.host('mv', listing + '.sha256.saved', listing + '.sha256')
        self.compose('stop', '--timeout', '70', 'wayfarer')
        source_before = self.snapshot()
        self.ctl('backup', '--quiesced')
        assert self.snapshot() == source_before
        self.restore()
        print('PASS product restore qualification', flush=True)
        self.mounted_destination()

    def restricted_authority(self, compose):
        """Inspect actual offline services and exercise their negative filesystem authority as UID1654."""
        for service in ['backup-reader', 'backup-destination-check']:
            name = self.project + '-' + service
            script = 'test ! -e /source; test ! -e /run/secrets/app-password; '
            if service == 'backup-reader':
                script += ('test ! -w /control; test ! -w /control/state; '
                           'test -w /control/recovery.lock; test ! -w /destination/slot; '
                           '! touch /destination/slot/forbidden; ! touch /control/host-operation.json')
            else:
                script += 'test ! -e /control; test -w /destination/slot'
            self.host(*compose, 'run', '-d', '--no-deps', '--name', name, '--entrypoint', 'sh', service, '-ec', script)
            envelope = json.loads(run('docker', 'inspect', name).stdout)[0]
            assert envelope['Config']['User'] == '1654:1654'
            assert envelope['HostConfig']['NetworkMode'] == 'none'
            assert not any(m['Destination'] in ['/source', '/run/secrets/app-password'] for m in envelope['Mounts'])
            destination = next(m for m in envelope['Mounts'] if m['Destination'] == '/destination/slot')
            assert destination['RW'] == (service == 'backup-destination-check')
            assert run('docker', 'wait', name).stdout.strip() == '0'
            run('docker', 'rm', name)
        print('PASS actual offline verification/probe envelopes have no DB network, secret or source authority', flush=True)

    def lock_ownership(self):
        """Even a writable control bind cannot give the participant directory-entry or reservation authority."""
        control = self.install / 'recovery-control'
        before = self.host('stat', '-c', '%d:%i', str(control / 'recovery.lock')).stdout
        self.host('tee', str(control / 'host-operation.json'), data='root-owned reservation')
        self.host('chown', '0:1654', str(control / 'host-operation.json'))
        self.host('chmod', '640', str(control / 'host-operation.json'))
        run('docker', 'run', '--rm', '--network', 'none', '--user', '1654:1654', '--cap-drop', 'ALL',
            '-v', str(control) + ':/control', HOST, 'sh', '-ec',
            'test -r /control/host-operation.json; test -w /control/recovery.lock; '
            '! rm /control/recovery.lock; ! mv /control/recovery.lock /control/replaced; '
            '! chmod 600 /control/recovery.lock; ! touch /control/new-lock; '
            '! sh -c "echo forged > /control/host-operation.json"; ! rm /control/host-operation.json; '
            'touch /control/state/receipt-probe; rm /control/state/receipt-probe')
        assert self.host('stat', '-c', '%d:%i', str(control / 'recovery.lock')).stdout == before
        assert self.host('cat', str(control / 'host-operation.json')).stdout == 'root-owned reservation'
        self.host('rm', str(control / 'host-operation.json'))
        print('PASS participant cannot replace/chmod lock or forge/unlink host reservation; state remains writable', flush=True)

    def scheduler_reconciliation(self, compose, previous):
        """Use real publications and process death at the pre-retention boundary; never infer this window from completed capture."""
        receipt = self.install / 'recovery-control/state/scheduler.json'
        slot = datetime.fromisoformat(previous['Slot']) + timedelta(days=1)
        now = (slot + timedelta(hours=1)).isoformat()
        probe = ['run', '--rm', '--no-deps', '-T', '--volume', str(self.lock_probe) + ':/lock-probe:ro',
                 '--entrypoint', '/lock-probe', 'backup-scheduler']
        self.host(*compose, *probe, 'capture-slot', (slot + timedelta(days=1)).isoformat())
        interrupted = dict(previous, Slot=slot.isoformat(), LastAttempt=slot.isoformat(), Attempts=3,
                           Succeeded=False, Failure='interrupted', NextRetry=None)
        self.host('tee', str(receipt), data=json.dumps(interrupted))
        self.host(*compose, *probe, 'scheduler-tick', now)
        assert json.loads(self.host('cat', str(receipt)).stdout) == interrupted
        # Permit exactly the final attempt, then kill its process after sidecar publication.
        interrupted['Attempts'] = 2
        self.host('tee', str(receipt), data=json.dumps(interrupted))
        crashed = self.host(*compose, *probe, 'scheduler-crash', now, check=False)
        assert crashed.returncode == 137
        after_crash = json.loads(self.host('cat', str(receipt)).stdout)
        assert after_crash['Attempts'] == 3 and not after_crash['Succeeded'] and after_crash['Failure'] == 'interrupted'
        destination = self.directory / 'destination'
        committed = self.host('sh', '-ec', 'ls ' + str(destination) + '/*.tar.sha256').stdout.split()
        assert len(committed) == 2  # Retention=1 has not run, unlike the previous lost-receipt simulation.
        # Read-only destination permits verification but forces reconciliation retention to fail visibly.
        readonly_probe = probe[:4] + ['--volume', str(destination) + ':/destination/slot:ro'] + probe[4:]
        self.host(*compose, *readonly_probe, 'scheduler-tick', now)
        failed = json.loads(self.host('cat', str(receipt)).stdout)
        assert failed['Succeeded'] and failed['Failure'] == 'retention-failed' and failed['Slot'] == after_crash['Slot']
        assert self.host('sh', '-ec', 'ls ' + str(destination) + '/*.tar.sha256').stdout.split() == committed
        # Replay the same crash receipt with writable storage: exact-slot reconciliation must finish retention.
        self.host('tee', str(receipt), data=json.dumps(after_crash))
        self.host(*compose, *probe, 'scheduler-tick', now)
        reconciled = json.loads(self.host('cat', str(receipt)).stdout)
        assert reconciled['Succeeded'] and reconciled['Failure'] == 'none' and reconciled['Attempts'] == 3
        assert reconciled['Slot'] == after_crash['Slot'] and reconciled['LastArchive'] == failed['LastArchive']
        assert len(self.host('sh', '-ec', 'ls ' + str(destination) + '/*.tar.sha256').stdout.split()) == 1
        print('PASS exact-slot reconciliation rejects later slot; actual post-publication death and retention failure reconcile without recapture', flush=True)

    def stale_residue(self):
        """Only stale installation-owned private partials and validated orphan archives may be reclaimed."""
        destination = self.directory / 'destination'
        archive = self.host('sh', '-ec', 'ls ' + str(destination) + '/*.tar').stdout.strip()
        base = Path(archive).name
        installation = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)['Installation']
        partial = base + '.partial-' + uuid.uuid4().hex
        side_partial = base + '.sha256.partial-' + uuid.uuid4().hex
        foreign = partial.replace(installation, str(uuid.uuid4()))
        fresh = base + '.partial-' + uuid.uuid4().hex
        linked = base + '.partial-' + uuid.uuid4().hex
        root_owned = base + '.partial-' + uuid.uuid4().hex
        hardlinked = base + '.partial-' + uuid.uuid4().hex
        invalid = base.replace(base[-40:-4], str(uuid.uuid4()))
        for name in [partial, side_partial, foreign, fresh, root_owned, invalid]:
            self.host('tee', str(destination / name), data='incomplete')
            self.host('chmod', '600', str(destination / name))
            if name != root_owned: self.host('chown', '1654:1654', str(destination / name))
            if name != fresh: self.host('touch', '-d', '2 days ago', str(destination / name))
        self.host('ln', '-s', str(destination / foreign), str(destination / linked))
        self.host('ln', str(destination / fresh), str(destination / hardlinked))
        self.host('mv', archive + '.sha256', archive + '.sha256.saved')
        self.host('touch', '-d', '2 days ago', archive)
        # Read-only listing must not perform cleanup as a side effect.
        self.ctl('backups')
        self.host('test', '-e', str(destination / partial))
        self.ctl('backup')
        for name in [base, partial, side_partial]:
            assert self.host('test', '-e', str(destination / name), check=False).returncode == 1
        for name in [foreign, fresh, linked, root_owned, hardlinked, invalid, base + '.sha256.saved']:
            self.host('test', '-e', str(destination / name))
        # Fixture-only removal lets subsequent restore qualification choose a known valid archive.
        self.host('rm', *[str(destination / name) for name in [foreign, fresh, linked, root_owned, hardlinked, invalid, base + '.sha256.saved']])
        print('PASS stale partial/orphan cleanup preserves fresh, foreign, invalid, linked and wrong-owner files', flush=True)

    def configuration_interruption(self):
        """A receipted pre-commit crash retains the previous installation and blocks ordinary mutation."""
        current = self.host('cat', str(self.install / 'installation.json')).stdout
        proposed = json.loads(current)
        proposed['Backup']['Enabled'] = False
        next_bytes = json.dumps(proposed)
        for name, value in [('backup-transition.json', json.dumps({'Previous': current, 'Next': next_bytes})),
                            ('installation.backup-next', next_bytes)]:
            self.host('tee', str(self.install / name), data=value)
            self.host('chmod', '600', str(self.install / name))
        assert self.ctl('backup', check=False).returncode == 2
        self.ctl('backup', 'configure', '--recover')
        assert self.host('cat', str(self.install / 'installation.json')).stdout == current
        print('PASS interrupted configuration preserves exact previous installation bytes', flush=True)

    def cancellation(self):
        """Cancel while real pg_dump waits for a fixture table lock; actual worker must terminate and disappear."""
        database = self.compose('ps', '-q', 'db').strip()
        blocker = subprocess.Popen(['docker', 'exec', '-i', database, 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At'],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        controller = None
        try:
            blocker.stdin.write("BEGIN; LOCK TABLE recovery_qualification IN ACCESS EXCLUSIVE MODE; SELECT 'locked';\n")
            blocker.stdin.flush()
            while blocker.stdout.readline().strip() != 'locked':
                if blocker.poll() is not None: raise RuntimeError('fixture table lock unavailable')
            name = self.project + '-cancel-controller'
            controller = subprocess.Popen(['docker', 'run', '--rm', '-i', '--name', name, '--network', 'host',
                '--label', 'com.docker.compose.project=' + self.project,
                '-v', str(self.directory) + ':' + str(self.directory), '-v', str(self.executable) + ':/ctl:ro',
                '-v', '/usr/bin/docker:/usr/bin/docker:ro', '-v', str(self.plugin) + ':/usr/libexec/docker/cli-plugins:ro',
                '-v', self.socket + ':/var/run/docker.sock', HOST, '/ctl', '--deployment-root', str(self.install), 'backup'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            deadline = time.monotonic() + 15
            while True:
                worker = run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + self.project,
                             '--filter', 'label=com.docker.compose.service=backup-worker').stdout.strip()
                waiting = self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-c',
                                       "SELECT count(*) FROM pg_stat_activity WHERE application_name='pg_dump' AND wait_event_type='Lock'")
                if worker and int(waiting.strip()) > 0: break
                if controller.poll() is not None:
                    output, error = controller.communicate(timeout=2)
                    raise RuntimeError('controller failed before cancellation gate: ' + output[-1000:] + error[-1000:])
                if time.monotonic() > deadline:
                    observation = run('docker', 'logs', worker, check=False).stdout if worker else 'no worker container'
                    raise RuntimeError('actual dump did not reach the cancellation gate: ' + observation[-1000:])
                time.sleep(0.1)
            envelope = json.loads(run('docker', 'inspect', worker).stdout)[0]
            assert envelope['Config']['User'] == '1654:1654'
            assert envelope['HostConfig']['ReadonlyRootfs'] and not envelope['HostConfig']['Privileged']
            assert 'ALL' in envelope['HostConfig']['CapDrop']
            assert set(envelope['NetworkSettings']['Networks']) == {self.project + '_backend'}
            assert any(m['Destination'] == '/source' and m['Name'] == self.project + '_app-data' and not m['RW']
                       for m in envelope['Mounts'])
            assert not any('docker.sock' in m['Destination'] or m.get('Name') == self.project + '_db-data' for m in envelope['Mounts'])
            run('docker', 'kill', '--signal=INT', name)
            controller.communicate(timeout=45)
            assert controller.returncode == 1
            assert not run('docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=' + self.project,
                           '--filter', 'label=com.docker.compose.service=backup-worker').stdout.strip()
            assert self.host('test', '-e', str(self.install / 'recovery-control/host-operation.json'), check=False).returncode == 1
            print('PASS Ctrl-C stopped and reaped actual pg_dump/maintenance container', flush=True)
        finally:
            blocker.stdin.write('ROLLBACK;\n\\q\n')
            blocker.stdin.flush()
            blocker.communicate(timeout=10)
            if controller is not None and controller.poll() is None:
                run('docker', 'stop', '--time', '5', self.project + '-cancel-controller', check=False)
                controller.communicate(timeout=15)

    def mounted_destination(self):
        """Host-only privileged fixture provisions mounts; the actual worker remains UID1654/capability-free."""
        parent = self.directory / 'remote'
        slot = parent / 'slot'
        filesystem = self.directory / 'fixture-filesystem'
        self.host('mkdir', '-p', str(slot), str(filesystem))
        def mount_command(*args):
            return run('docker', 'run', '--rm', '--privileged', '--pid=host', '--network', 'none', HOST,
                       'nsenter', '-t', '1', '-m', '--', *args)
        try:
            mount_command('mount', '--bind', str(parent), str(parent))
            mount_command('mount', '--make-shared', str(parent))
            mount_command('mount', '-t', 'tmpfs', '-o', 'size=64m,mode=0700', 'tmpfs', str(filesystem))
            mount_command('mount', '--bind', str(filesystem), str(slot))
            self.ctl('backup', 'configure', '--destination', str(slot), '--kind', 'mounted',
                     '--payload', str(self.payload / 'wayfarer-recovery'))
            config = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)
            scheduler = run('docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=' + self.project,
                            '--filter', 'label=com.docker.compose.service=backup-scheduler').stdout.strip()
            assert scheduler
            # The same running container must see disappearance and substitution, without re-creation.
            mount_command('umount', str(slot))
            assert run('docker', 'exec', scheduler, '/worker/wayfarer-recovery', 'backups', check=False).returncode == 1
            assert not self.host('ls', '-A', str(slot)).stdout.strip()
            mount_command('mount', '-t', 'tmpfs', '-o', 'size=64m,mode=0700', 'tmpfs', str(slot))
            assert run('docker', 'exec', scheduler, '/worker/wayfarer-recovery', 'backups', check=False).returncode == 1
            assert not self.host('ls', '-A', str(slot)).stdout.strip()
            mount_command('umount', str(slot))
            mount_command('mount', '--bind', str(filesystem), str(slot))
            mount_command('mount', '-o', 'remount,bind,ro', str(slot))
            assert self.ctl('backup', check=False).returncode == 1
            mount_command('mount', '-o', 'remount,bind,rw', str(slot))
            self.ctl('backup')
            archives_before = self.host('sh', '-ec', 'ls ' + str(slot) + '/*.tar').stdout
            assert self.host('sh', '-ec', 'dd if=/dev/zero of=' + str(slot / 'foreign-full') + ' bs=1M', check=False).returncode != 0
            assert self.ctl('backup', check=False).returncode == 1
            assert self.host('sh', '-ec', 'ls ' + str(slot) + '/*.tar').stdout == archives_before
            self.host('rm', str(slot / 'foreign-full'))
            self.host('touch', str(slot / 'foreign-sentinel'), str(slot / 'incomplete.tar'))
            self.ctl('backup', 'configure', '--destination', str(slot), '--kind', 'mounted', '--retention', '1',
                     '--payload', str(self.payload / 'wayfarer-recovery'))
            self.ctl('backup')
            self.ctl('backup')
            names = self.host('ls', '-A', str(slot)).stdout.split()
            assert 'foreign-sentinel' in names and 'incomplete.tar' in names
            assert len([name for name in names if name.startswith('wayfarer-recovery-v1_') and name.endswith('.tar')]) == 1
            self.ctl('backup', 'configure', '--disable')
            print('PASS full destination preserves prior archive; retention preserves foreign/incomplete files', flush=True)
            print('PASS unchanged unprivileged container observes unmount/substitution/read-only destination; no underlay fallback', flush=True)
        finally:
            self.ctl('backup', 'configure', '--disable', check=False)
            for path in [slot, filesystem, parent]:
                run('docker', 'run', '--rm', '--privileged', '--pid=host', '--network', 'none', HOST,
                    'nsenter', '-t', '1', '-m', '--', 'umount', str(path), check=False)

    def probe_command(self, operation):
        return self.compose('run', '--rm', '--no-deps', '-T', '--volume',
                            str(self.directory / 'RecoveryProbe.dll') + ':/probe-bin/RecoveryProbe.dll:ro', '--volume',
                            str(self.directory / 'probe') + ':/probe', '--entrypoint', 'dotnet', 'wayfarer', 'exec',
                            '--runtimeconfig', '/app/Wayfarer.runtimeconfig.json', '--depsfile', '/app/Wayfarer.deps.json',
                            '/probe-bin/RecoveryProbe.dll', operation)

    def snapshot(self):
        files = self.compose('run', '--rm', '--no-deps', '-T', '--volume', self.project + '_app-data:/var/lib/wayfarer:ro',
                             '--entrypoint', 'sh', 'wayfarer', '-ec',
                             r'find /var/lib/wayfarer -type f -exec sha256sum {} \; | sort')
        database = self.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-c',
            "SELECT tablename, md5(query_to_xml(format('SELECT row_to_json(t)::text FROM %I.%I t ORDER BY row_to_json(t)::text', "
            "schemaname, tablename), false, true, '')::text) FROM pg_tables WHERE schemaname='public' ORDER BY tablename")
        return hashlib.sha256((files + database).encode()).hexdigest()

    def restore_refusals(self):
        """Prove pre-mutation refusals and a real pg_restore missing-role failure against fresh candidates."""
        baseline = self.host('cat', str(self.install / 'installation.json')).stdout
        config = json.loads(baseline)
        name = self.ctl('backups').stdout.split(' ')[0]
        archive = self.directory / 'destination' / name
        sidecar = str(archive) + '.sha256'
        checksum = self.host('cat', sidecar).stdout
        options = ['--restore-payload', str(self.payload / 'wayfarer-recovery')]
        self.host('tee', sidecar, data='corrupt')
        assert self.ctl('restore', name, *options, '--plan', check=False).returncode == 1
        self.host('tee', sidecar, data=checksum)
        assert self.ctl('restore', '--archive', str(archive), *options, '--plan', check=False).returncode == 2
        evidence = self.directory / 'incompatible-source.json'
        source = dict(config['Backup']['Source'])
        source['ApplicationVersion'] = '0.0.0'
        self.host('tee', str(evidence), data=json.dumps(source))
        self.host('chmod', '600', str(evidence))
        assert self.ctl('restore', name, *options, '--target-evidence', str(evidence), '--plan', check=False).returncode == 1
        assert self.ctl('restore', '--accept-plan', 'f' * 64, '--trust-controlled-backup', check=False).returncode == 2
        assert self.host('cat', str(self.install / 'installation.json')).stdout == baseline
        print('PASS corrupt/incompatible archive, missing foreign acknowledgement and plan mismatch before mutation', flush=True)
        sql = ['exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-v', 'ON_ERROR_STOP=1', '-c']
        self.compose(*sql, 'CREATE ROLE restore_missing; CREATE TABLE restore_owner_probe(id integer); '
            'ALTER TABLE restore_owner_probe OWNER TO restore_missing; GRANT SELECT ON restore_owner_probe TO wayfarer;')
        self.ctl('backup', '--quiesced')
        self.compose(*sql, 'DROP TABLE restore_owner_probe; DROP ROLE restore_missing;')
        planned = self.ctl('restore', *options, '--without-emergency-backup', '--plan').stdout
        plan = json.loads(planned.splitlines()[0])
        accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
        failure = self.ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup', check=False)
        assert failure.returncode == 1 and 'phase=Staging' in failure.stderr
        assert self.host('cat', str(self.install / 'installation.json')).stdout == baseline
        receipt_path = str(self.install / 'recovery-control/restore.json')
        first = json.loads(self.host('cat', receipt_path).stdout)
        assert not first['WritesPossible'] and first['EmergencyArchive'] is None
        assert self.ctl('start', check=False).returncode == 2
        assert self.ctl('backup', 'configure', '--recover', check=False).returncode == 2
        assert self.ctl('restore', '--resume', plan['Operation'], check=False).returncode == 1
        retry = json.loads(self.host('cat', receipt_path).stdout)
        assert retry['CandidateAttempt'] == 1 and len(set(retry['Volumes'])) == 6
        self.ctl('restore', '--abort', plan['Operation'])
        assert self.host('cat', str(self.install / 'installation.json')).stdout == baseline
        self.compose('up', '-d', '--wait', 'db')
        self.ctl('backup', '--quiesced')
        print('PASS actual SQL failure, old authority intact, waiver, blocked lifecycle, fresh retry and pre-writer abort', flush=True)

    def restore_boundaries(self):
        """Lose acknowledgements at the pointer and real writer boundaries, then exercise product recovery."""
        options = ['restore', '--restore-payload', str(self.payload / 'wayfarer-recovery'), '--without-emergency-backup']
        original = self.host('cat', str(self.install / 'installation.json')).stdout
        for point in ['restore-files', 'restore-validation', 'restore-pointer', 'restore-writer']:
            planned = self.ctl(*options, '--plan').stdout
            plan = json.loads(planned.splitlines()[0])
            accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
            self.host('tee', str(self.directory / 'failure'), data=point)
            result = self.ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup', check=False)
            assert result.returncode == 1
            self.host('rm', str(self.directory / 'failure'))
            receipt = json.loads(self.host('cat', str(self.install / 'recovery-control/restore.json')).stdout)
            assert self.ctl('status', check=False).returncode == 1
            assert self.ctl('start', check=False).returncode == 2
            for service in ['db', 'wayfarer']:
                identifier = self.compose('ps', '-aq', service).strip()
                state = json.loads(run('docker', 'inspect', identifier).stdout)[0]
                assert not state['State']['Running'] and state['HostConfig']['RestartPolicy']['Name'] == 'no'
            if point != 'restore-writer':
                assert receipt['Phase'] == (5 if point == 'restore-pointer' else 3) and not receipt['WritesPossible']
                self.ctl('restore', '--abort', plan['Operation'])
                assert self.host('cat', str(self.install / 'installation.json')).stdout == original
                self.ctl('start')
                for role in ['db-data', 'app-data']:
                    assert self.project + '_' + role in self.compose('config')
                self.ctl('stop')
                self.compose('up', '-d', '--wait', 'db')
            else:
                assert receipt['Phase'] == 7 and receipt['WritesPossible']
                assert self.ctl('restore', '--abort', plan['Operation'], check=False).returncode == 2
                self.ctl('restore', '--resume', plan['Operation'])
                assert self.ctl('doctor').returncode == 0
                self.ctl('backup', '--quiesced')
        print('PASS filesystem/validation failures, pointer abort mounts, real writer acknowledgement loss, fencing and forward-only resume', flush=True)

    def restore(self):
        """The shipped operator owns destructive restore; fixture code observes product contracts only."""
        before = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)
        options = ['restore', '--restore-payload', str(self.payload / 'wayfarer-recovery')]
        planned = self.ctl(*options, '--plan').stdout
        plan = json.loads(planned.splitlines()[0])
        plan_hash = planned.split('Plan SHA-256: ')[1].splitlines()[0]
        result = self.ctl('restore', '--accept-plan', plan_hash, '--trust-controlled-backup')
        print(result.stdout, flush=True)
        after = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)
        assert after['Schema'] == 3 and after['Installation'] == before['Installation']
        assert after['Backup'] == before['Backup']
        for role in ['db-data', 'app-data', 'app-cache']:
            run('docker', 'volume', 'inspect', self.project + '_' + role)
            run('docker', 'volume', 'inspect', self.project + '_' + role + '_' + after['StorageGeneration'])
        assert 'durable' == self.compose('exec', '-T', 'wayfarer', 'cat',
            '/var/lib/wayfarer/uploads/imports/recovery-qualification').strip()
        self.probe_command('verify')
        print('PASS product in-place restore, Identity/provider continuity, uploads and retained old volumes', flush=True)
        self.clean_root_restore(plan)

    def clean_root_restore(self, plan):
        """Disaster restore uses a new local UUID/secrets without any setup stages or backup policy."""
        from qualify_ctl import DB
        target = self.directory / 'disaster-installation'
        project = self.project + '-disaster'
        operation = plan['Operation'].replace('-', '')
        frozen = self.install / 'restore-plans' / operation / 'frozen'
        archive = self.host('find', str(frozen), '-maxdepth', '1', '-name', '*.tar').stdout.strip()
        evidence = self.install / 'restore-plans' / operation / 'source.json'
        options = ['restore', '--new-install', '--archive', archive, '--source-installation', plan['SourceInstallation'],
            '--bundle', str(self.bundle), '--hostname', 'wayfarer.example.org', '--app-digest', self.digest,
            '--db-digest', DB, '--mode', 'external', '--project', project, '--edge-prefix', '172.30.70',
            '--loopback-port', str(self.free_port()), '--restore-payload', str(self.payload / 'wayfarer-recovery'),
            '--capture-payload', str(self.payload / 'wayfarer-recovery'), '--target-evidence', str(evidence)]
        def ctl(*args):
            return self.host('/ctl/wayfarerctl', '--deployment-root', str(target), *args)
        try:
            planned = ctl(*options, '--plan').stdout
            accepted = planned.split('Plan SHA-256: ')[1].splitlines()[0]
            print(ctl('restore', '--accept-plan', accepted, '--trust-controlled-backup').stdout, flush=True)
            restored = json.loads(self.host('cat', str(target / 'installation.json')).stdout)
            assert restored['Installation'] != plan['SourceInstallation']
            assert 'Backup' not in restored
            assert self.host('test', '-e', str(target / 'setup-progress.json'), check=False).returncode == 1
            ctl('doctor')
            print('PASS product clean-root restore, new UUID, no setup stages, backup unconfigured', flush=True)
        finally:
            self.cleanup_project(project)

    def cleanup_project(self, project):
        """Reap only this fixture's labelled restore helpers before releasing their retained volumes."""
        for kind, listing, removal in [('container', ['ps', '-aq'], ['rm', '-f']),
            ('network', ['network', 'ls', '-q'], ['network', 'rm']),
            ('volume', ['volume', 'ls', '-q'], ['volume', 'rm'])]:
            ids = set()
            for label in ['com.docker.compose.project=', 'wayfarer.restore-helper=']:
                ids.update(run('docker', *listing, '--filter', 'label=' + label + project).stdout.split())
            if ids:
                run('docker', *removal, *sorted(ids))

    def cleanup(self):
        self.cleanup_project(self.project)
        super().cleanup()

    def compose(self, *args):
        """Observe the same installation-owned generation as the operator after activation."""
        current = self.host('cat', str(self.install / 'installation.json'), check=False)
        overlay = []
        if current.returncode == 0:
            generation = json.loads(current.stdout).get('StorageGeneration')
            if generation:
                overlay = ['-f', str(self.install / 'storage-generations' / generation / 'compose.json')]
        return self.host('docker', 'compose', '--project-name', self.project, '--env-file',
            str(self.install / 'deployment.env'), '-f', str(self.bundle / 'compose.yaml'),
            '-f', str(self.bundle / 'external.yaml'), *overlay, *args).stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', required=True)
    parser.add_argument('--worker', required=True)
    parser.add_argument('--inspector', required=True)
    parser.add_argument('--probe', required=True)
    parser.add_argument('--app-digest', required=True)
    parser.add_argument('--restore-only', action='store_true', help='Run the managed restore journey without repeating backup regression matrices.')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='wayfarer-533-') as directory:
        journey = RecoveryJourney(directory, args.executable, args.app_digest, args.worker, args.inspector, args.probe)
        try:
            journey.prepare()
            journey.recovery(args.restore_only)
        except Exception:
            # Bounded non-secret ownership evidence before fixture cleanup, never raw container environment.
            ids = run('docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=' + journey.project).stdout.split()
            for identifier in ids:
                print(run('docker', 'inspect', '--format', '{{.Name}} {{json .Config.Labels}} {{json .Mounts}}', identifier).stdout, flush=True)
            raise
        finally:
            journey.cleanup()


if __name__ == '__main__':
    main()
