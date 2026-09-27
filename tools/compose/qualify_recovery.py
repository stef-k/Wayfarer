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
from qualify_ctl import Journey, run, HOST
from qualify import Stack


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

    def recovery(self):
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
        print('PASS completed source application setup', flush=True)
        result = self.ctl('backup', 'configure', '--destination', str(self.directory / 'destination'),
                         '--payload', str(self.payload / 'wayfarer-recovery'))
        print(result.stdout, flush=True)
        # Wait on the committed scheduler receipt, not an assumed capture duration.
        deadline = time.monotonic() + 90
        while True:
            receipt = self.host('cat', str(self.install / 'recovery-control/scheduler.json'), check=False)
            if receipt.returncode == 0 and json.loads(receipt.stdout)['Succeeded']:
                break
            if time.monotonic() > deadline:
                raise RuntimeError('scheduled capture did not commit its bounded receipt')
            time.sleep(0.1)
        print('PASS scheduled due capture receipt', flush=True)
        generation = json.loads(self.host('cat', str(self.install / 'installation.json')).stdout)['Backup']['Generation']
        scheduler_args = ['docker', 'compose', '--project-name', self.project, '--project-directory', str(self.bundle),
                          '--env-file', str(self.install / 'deployment.env'), '-f', str(self.bundle / 'compose.yaml'),
                          '-f', str(self.bundle / 'external.yaml'), '-f', str(self.install / 'recovery-generations' / generation / 'compose.json'), '--profile', 'backup']
        before = self.host('cat', str(self.install / 'recovery-control/scheduler.json')).stdout
        self.host(*scheduler_args, 'up', '-d', '--no-deps', '--force-recreate', 'backup-scheduler')
        assert self.host('cat', str(self.install / 'recovery-control/scheduler.json')).stdout == before
        # Host lock owner is a self-contained process with no database or Docker access.
        owner = self.project + '-lock-owner'
        run('docker', 'run', '-di', '--name', owner, '--network', 'none', '--label', 'com.docker.compose.project=' + self.project,
            '-v', str(self.install / 'recovery-control') + ':/control', '-v', str(self.lock_probe) + ':/lock-probe:ro',
            HOST, '/lock-probe', '/control/recovery.lock')
        deadline = time.monotonic() + 10
        while 'acquired' not in run('docker', 'logs', owner).stdout:
            if time.monotonic() > deadline: raise RuntimeError('lock probe did not acquire')
            time.sleep(0.1)
        assert self.ctl('backup', check=False).returncode == 1
        assert self.ctl('stop', check=False).returncode == 1
        assert self.host(*scheduler_args, 'run', '--rm', '--no-deps', '-T', 'backup-worker', 'backups', check=False).returncode == 1
        run('docker', 'kill', owner)
        print('PASS host lock contention and owner-death release; scheduler recreation retained receipt', flush=True)
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
        assert self.snapshot() == source_before
        print('PASS complete clean Compose reconstruction; source state unchanged', flush=True)
        self.mounted_destination()

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

    def restore(self):
        # Extract only our just-verified owned fixture archive; this is deliberately not shipped restore UX.
        with tempfile.TemporaryDirectory(prefix='wayfarer-533-restore-') as directory:
            target = Stack(directory, 'ghcr.io/stef-k/wayfarer@' + self.digest,
                           'ghcr.io/stef-k/wayfarer-db@' + __import__('qualify_ctl').DB)
            try:
                target.prepare()
                run('docker', 'run', '--rm', '--network', 'none', '-v', str(self.directory) + ':/source:ro',
                    '-v', directory + ':/target', HOST, 'sh', '-ec',
                    'archive=$(ls /source/destination/*.tar | sort | tail -1); mkdir /target/archive; '
                    'tar -xf "$archive" -C /target/archive; cp /source/probe/auth-token /target/auth-token; '
                    'chmod 755 /target/archive; chmod 644 /target/archive/* /target/auth-token')
                target.compose('up', '-d', '--wait', 'db')
                target.compose('run', '--rm', '--no-deps', '--user', '0', '--entrypoint', 'sh', '-v', directory + '/archive:/archive:ro',
                               'wayfarer', '-ec', 'mkdir -p /var/lib/wayfarer/data-protection /var/lib/wayfarer/uploads; '
                               'tar -xzf /archive/data-protection.tar.gz -C /var/lib/wayfarer/data-protection; '
                               'tar -xzf /archive/uploads.tar.gz -C /var/lib/wayfarer/uploads; '
                               'chown -R 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer')
                db = target.container('db')
                run('docker', 'cp', directory + '/archive/database.dump', db + ':/tmp/recovery.dump')
                target.compose('exec', '-T', 'db', 'pg_restore', '--exit-on-error', '-U', 'postgres', '-d', 'wayfarer', '/tmp/recovery.dump')
                target.database_semantics()
                assert target.sql('SELECT id, name, ST_AsText(position), octet_length(payload) FROM recovery_qualification') == '1|Αθήνα|POINT(23.7 37.9)|320000'
                assert target.compose('run', '--rm', '--no-deps', '-T', '--entrypoint', 'cat', 'wayfarer',
                                      '/var/lib/wayfarer/uploads/imports/recovery-qualification').stdout.strip() == 'durable'
                inspection = target.compose('run', '--rm', '--no-deps', '-T', '--volume',
                    str(self.payload / 'WayfarerRecoverySource.dll') + ':/inspection/WayfarerRecoverySource.dll:ro',
                    '--entrypoint', 'dotnet', 'wayfarer', 'exec', '--runtimeconfig', '/app/Wayfarer.runtimeconfig.json',
                    '--depsfile', '/app/Wayfarer.deps.json', '/inspection/WayfarerRecoverySource.dll').stdout
                manifest = json.loads(Path(directory, 'archive/manifest.json').read_text())
                assert json.loads(inspection)['ExpectedMigrations'] == manifest['Database']['Migrations']
                assert manifest['Mode'] == 'quiesced'
                assert Path(directory, 'archive/database.dump').stat().st_size > 262144
                print(target.compose('run', '--rm', '--no-deps', '-T', '--volume', str(self.probe) + ':/probe-bin/RecoveryProbe.dll:ro',
                                     '--volume', directory + '/auth-token:/probe/auth-token:ro', '--entrypoint', 'dotnet', 'wayfarer', 'exec',
                                     '--runtimeconfig', '/app/Wayfarer.runtimeconfig.json', '--depsfile', '/app/Wayfarer.deps.json',
                                     '/probe-bin/RecoveryProbe.dll', 'verify').stdout, flush=True)
            finally:
                target.cleanup()
                run('docker', 'run', '--rm', '--network', 'none', '-v', directory + ':/target', HOST,
                    'chown', '-R', str(__import__('os').getuid()) + ':' + str(__import__('os').getgid()), '/target')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', required=True)
    parser.add_argument('--worker', required=True)
    parser.add_argument('--inspector', required=True)
    parser.add_argument('--probe', required=True)
    parser.add_argument('--app-digest', required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='wayfarer-533-') as directory:
        journey = RecoveryJourney(directory, args.executable, args.app_digest, args.worker, args.inspector, args.probe)
        try:
            journey.prepare()
            journey.recovery()
        finally:
            journey.cleanup()


if __name__ == '__main__':
    main()
