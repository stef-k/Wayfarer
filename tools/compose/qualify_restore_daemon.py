"""Qualify restore's restart fence in a disposable, isolated Docker daemon.

Requires local Docker Engine and its installed Linux binaries/libraries. The outer
container is privileged for nested cgroups, has no network or host Docker socket,
and mounts only executable/library directories read-only. No host daemon is
restarted. A local pinned Ubuntu root filesystem is imported into the nested daemon;
no image is pulled, published or substituted for an application release.
"""
import json
import subprocess
import time
import uuid

IMAGE = 'ubuntu@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3'


def run(*args, check=True):
    """Capture bounded-purpose fixture commands without inheriting a production daemon socket inside the fixture."""
    return subprocess.run(args, text=True, capture_output=True, check=check)


def qualify():
    """A positive restart control distinguishes a real daemon restart from merely stopping every container."""
    name = 'wayfarer-695-daemon-' + uuid.uuid4().hex[:10]

    def nested(*args, check=True):
        return run('docker', 'exec', name, 'docker', '--host=unix:///run/restore-docker.sock', *args, check=check)

    def start():
        run('docker', 'exec', '-d', name, 'dockerd', '--host=unix:///run/restore-docker.sock',
            '--data-root=/state', '--exec-root=/run/restore', '--pidfile=/run/restore.pid',
            '--iptables=false', '--ip6tables=false', '--bridge=none', '--ip-forward=false',
            '--ip-masq=false', '--storage-driver=vfs', '--exec-opt=native.cgroupdriver=cgroupfs', '--shutdown-timeout=5')
        for _ in range(60):
            if nested('info', check=False).returncode == 0:
                return
            time.sleep(0.5)
        raise RuntimeError('isolated daemon failed readiness')

    try:
        mounts = []
        for path in ['/usr/bin', '/usr/sbin', '/usr/lib', '/usr/libexec']:
            mounts.extend(['--volume', path + ':' + path + ':ro'])
        run('docker', 'run', '-d', '--name', name, '--label', 'wayfarer.qualification=695',
            '--privileged', '--network=none', '--tmpfs=/run', *mounts, IMAGE, 'sleep', '600')
        start()
        source = name + '-image'
        run('docker', 'create', '--name', source, IMAGE, 'sleep', '1')
        # Export/import avoids depending on the outer daemon's OCI/containerd image-store representation.
        export = subprocess.Popen(['docker', 'export', source], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        loaded = subprocess.run(['docker', 'exec', '-i', name, 'docker', '--host=unix:///run/restore-docker.sock',
            'import', '-', 'wayfarer-restart-probe:local'], stdin=export.stdout, capture_output=True)
        export.stdout.close()
        if export.wait() != 0 or loaded.returncode != 0:
            raise RuntimeError('isolated fixture image import failed')
        run('docker', 'rm', source)
        for role, policy in [('old', 'no'), ('candidate', 'no'), ('control', 'unless-stopped')]:
            nested('run', '-d', '--name', role, '--restart=' + policy, '--network=none',
                '--security-opt=apparmor=unconfined', 'wayfarer-restart-probe:local', 'sleep', '300')
        # Exercise daemon restart after Docker's successful-start window, with all three processes still running.
        time.sleep(11)
        run('docker', 'exec', name, 'sh', '-ec', 'kill -TERM "$(cat /run/restore.pid)"')
        for _ in range(40):
            if run('docker', 'exec', name, 'test', '-e', '/run/restore.pid', check=False).returncode:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError('isolated daemon did not terminate')
        start()
        states = {role: json.loads(nested('inspect', role).stdout)[0]['State']['Running']
                  for role in ['old', 'candidate', 'control']}
        assert states == {'old': False, 'candidate': False, 'control': True}, states
        print('PASS real isolated daemon restart: old/candidate stayed stopped; restart control restarted', flush=True)
    finally:
        run('docker', 'rm', '-f', name + '-image', check=False)
        removed = run('docker', 'rm', '-f', '-v', name, check=False)
        if removed.returncode:
            raise RuntimeError('isolated daemon fixture cleanup failed')


if __name__ == '__main__':
    qualify()
