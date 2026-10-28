"""One genuine managed predecessor, interrupted target ingress recovery, then forward acceptance.

Reuse the plain-host operator and managed TLS fixtures. The ACME endpoint is fixture
DNS/trust only; release payloads, command, mounts and named TLS stores stay intact.
No browser matrix, migration fault matrix, publication or production host is used.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import time

from qualify_ctl import Journey, ROOT, QUALIFICATION_PROXY_LABEL, run
from qualify import Stack, assert_tls_transition

sys.path.insert(0, str(ROOT / 'tools/release'))
import caddy
import image
import public_bundle


def prepare(output):
    """Author disposable candidate bytes from this head; 9999.0.0 is a fixture, never a release selection."""
    if run('git', 'status', '--porcelain').stdout.strip():
        raise RuntimeError('qualification preparation requires a clean committed head')
    output.mkdir(parents=True, exist_ok=False)
    source, fingerprint, archive = caddy.source_bundle('v1.9.22', image.PLATFORM, output)
    assert fingerprint == caddy.POLICY['sources'][image.PLATFORM]
    old = output / 'old'
    old.mkdir()
    import tarfile
    with tarfile.open(archive) as tar:
        tar.extractall(old, filter='data')
    # Python's data filter adds owner write permission; restore the authenticated release modes.
    for entry in source['Files']:
        (old / entry['Path']).chmod(entry['Mode'])
    for reference in ('ghcr.io/stef-k/wayfarer@' + source['Images']['PlatformDigest'],
                      'ghcr.io/stef-k/wayfarer-db@' + source['Images']['DatabaseDigest'],
                      'caddy@' + caddy.POLICY['sourceCaddy'], 'caddy@' + caddy.POLICY['targetCaddy']):
        run('docker', 'pull', reference)
    head = run('git', 'rev-parse', 'HEAD').stdout.strip()
    scope = caddy.scope_hash()
    with tempfile.TemporaryDirectory(prefix='wayfarer-caddy-candidate-') as temporary:
        checkout = Path(temporary) / 'source'
        run('git', 'clone', '--shared', '--no-checkout', str(ROOT), str(checkout))
        subprocess.run(['git', 'checkout', '--detach', head], cwd=checkout, check=True)
        props = checkout / 'Version.props'
        props.write_text(props.read_text().replace('>' + caddy.version.read_version_state().version + '<', '>9999.0.0<'))
        project = checkout / 'tools/WayfarerCtl/WayfarerCtl.csproj'
        project.write_text(project.read_text().replace('<OutputType>', '<!-- Disposable candidate admission only. -->\n    <DefineConstants>UPDATE_QUALIFICATION</DefineConstants>\n    <OutputType>', 1))
        subprocess.run(['git', 'add', 'Version.props', str(project)], cwd=checkout, check=True)
        stamp = run('git', 'show', '-s', '--format=%cI', head).stdout.strip()
        subprocess.run(['git', '-c', 'commit.gpgsign=false', 'commit', '-m', 'Disposable Caddy qualification identity'], cwd=checkout, check=True,
                       env=dict(os.environ, GIT_AUTHOR_DATE=stamp, GIT_COMMITTER_DATE=stamp, GIT_AUTHOR_NAME='Wayfarer qualification', GIT_COMMITTER_NAME='Wayfarer qualification',
                                GIT_AUTHOR_EMAIL='qualification@example.invalid', GIT_COMMITTER_EMAIL='qualification@example.invalid'))
        subprocess.run(['git', 'archive', '--format=tar.gz', '--output', str(output / 'candidate-source.tar.gz'), 'HEAD'], cwd=checkout, check=True)
        commit = subprocess.run(['git', 'show', '-s', '--format=raw', 'HEAD'], cwd=checkout, check=True, capture_output=True, text=True)
        (output / 'candidate-source-commit.txt').write_text(commit.stdout)
        recipe = "import sys;sys.path.insert(0,'tools/release');import image;r=image.identity(None,None);r['tag']='candidate-caddy-'+r['sourceRevision'];ref=image.build(r);print(image.inspect_image(r,ref)['RepoDigests'][0].split('@')[1])"
        result = subprocess.run(['python3', '-B', '-c', recipe], cwd=checkout, check=True, text=True, stdout=subprocess.PIPE)
        digest = result.stdout.splitlines()[-1]
        subprocess.run(['python3', '-B', 'tools/release/bundle.py', '--app-digest', digest,
                        '--db-digest', source['Images']['DatabaseDigest'], '--output', str(output / 'candidate')], cwd=checkout, check=True)
    target = next((output / 'candidate').glob('candidate-*'))
    manifest_path = target / 'release.json'
    target_manifest = json.loads(manifest_path.read_text())
    target_manifest['Sources'] = [public_bundle.boundary(source, fingerprint, target_manifest)]
    manifest_path.write_text(json.dumps(target_manifest, sort_keys=True, separators=(',', ':')) + '\n')
    subprocess.run([str(target / 'wayfarerctl'), 'release', 'inspect', str(target)], check=True)
    import bundle
    final_archive = output / 'bound-archive'
    final_archive.mkdir()
    bundle.archive(target, final_archive)
    evidence = dict(sourceRevision=head, scopeSha256=scope, sourceFingerprint=fingerprint, oldBundle=str(old), targetBundle=str(target))
    (output / 'inputs.json').write_text(json.dumps(evidence, indent=2) + '\n')
    return evidence


class ManagedJourney(Journey):
    """The established plain root-host fixture with a real managed proxy and a private fixture ACME issuer."""
    def prepare_managed(self, old, target, client):
        self.old = self.directory / 'old-input'
        self.target = self.directory / 'target-input'
        shutil.copytree(old, self.old)
        shutil.copytree(target, self.target)
        self.proxy_address = '172.30.69.3'
        self.prepare_public(faults=True)
        self.port = 443
        self.ca = 'qualification-acme-' + self.project
        # Prepare the issuer outside the installation namespace; old setup must still prove a fresh target.
        self.issuer_network = self.ca + '-edge'
        run('docker', 'network', 'create', '--internal', '--label',
            QUALIFICATION_PROXY_LABEL + '=' + self.project, self.issuer_network)
        authority = self.directory / 'acme.Caddyfile'
        self.host('tee', str(authority), data='''{
 skip_install_trust
}
acme-v02.api.letsencrypt.org {
 log
 tls internal
 @directory path /directory
 rewrite @directory /acme/local/directory
 acme_server {
  lifetime 24h
  challenges tls-alpn-01
 }
}
''')
        stores = []
        for role in ('data', 'config'):
            name = self.ca + '-' + role
            run('docker', 'volume', 'create', '--label', QUALIFICATION_PROXY_LABEL + '=' + self.project, name)
            stores.extend(['-v', name + ':/' + role])
        run('docker', 'run', '-d', '--name', self.ca, '--network', self.issuer_network,
            '--network-alias', 'acme-v02.api.letsencrypt.org',
            '--label', QUALIFICATION_PROXY_LABEL + '=' + self.project,
            '-v', str(authority) + ':/etc/caddy/Caddyfile:ro', *stores, 'caddy@' + caddy.POLICY['targetCaddy'])
        for _ in range(60):
            if run('docker', 'cp', self.ca + ':/data/caddy/pki/authorities/local/root.crt', str(client / 'ca.crt'), check=False).returncode == 0:
                break
            time.sleep(0.5)
        root = (client / 'ca.crt').read_text()
        self.trusted_ca = self.directory / 'ca.crt'
        self.host('tee', str(self.trusted_ca), data=root)
        for kind in ('source', 'target'):
            digest = caddy.POLICY[kind + 'Caddy']
            run('docker', 'pull', 'caddy@' + digest)
            roots = run('docker', 'run', '--rm', '--entrypoint', 'cat', 'caddy@' + digest, '/etc/ssl/certs/ca-certificates.crt').stdout
            self.host('tee', str(self.directory / (kind + '-trust.pem')), data=roots + root)
        # Validate routing, directory URLs and trust before any release proxy can execute.
        directory = json.loads(run('docker', 'run', '--rm', '--network', self.issuer_network,
            '-v', str(self.directory / 'source-trust.pem') + ':/etc/ssl/certs/ca-certificates.crt:ro',
            '--entrypoint', 'wget', 'caddy@' + caddy.POLICY['sourceCaddy'], '-qO-',
            'https://acme-v02.api.letsencrypt.org/directory').stdout)
        for endpoint in ('newNonce', 'newAccount', 'newOrder'):
            assert directory[endpoint].startswith('https://acme-v02.api.letsencrypt.org/acme/local/')
        self.ingress = Stack(client, '', '', acme=True)
        self.ingress.port = 443
        self.ingress.address = self.proxy_address
        # Only adapt the existing fixture's Compose return type; TLS evidence remains its established owner.
        self.ingress.compose = lambda *args: subprocess.CompletedProcess(args, 0, self.compose(*args))
        self.install_adapter()

    def install_adapter(self):
        """Inject fixture DNS/CA before Caddy starts and lose exactly one target-start acknowledgement."""
        directory, project, ca = map(shlex.quote, (str(self.directory), self.project, self.ca))
        wrapper = f'''#!/bin/sh
set -eu
fixture={directory}
project={project}
ca={ca}
# Retain only proxy commands (no credential-bearing stdin) and the adapter's failing stage.
case " $* " in
 *" create caddy "|*" create "*" caddy "|*" up "*" caddy "|*" start "*)
  printf '<%s> ' "$@" >> "$fixture/proxy-command.log"
  printf '\\n' >> "$fixture/proxy-command.log"
  stage=docker-command
  trap 'result=$?; printf "exitCode=%s stage=%s\\n" "$result" "$stage" >> "$fixture/proxy-command.log"' 0
  exec 2>>"$fixture/proxy-command.stderr" ;;
esac
wait_proxy() {{
 stage=https-readiness
 count=0
 until /usr/bin/docker exec "$1" wget -O /dev/null https://wayfarer.example.org/health/ready >/dev/null 2>"$fixture/proxy-readiness.stderr"; do
  count=$((count+1)); test "$count" -lt 60; sleep 0.5
 done
}}
# The old operator uses up, which would execute Caddy before injection. Create it stopped instead.
initial_up=false
case " $* " in
 *" up "*" caddy ")
  initial_up=true
  remaining=$#
  while test "$remaining" -gt 0; do
   argument=$1; shift; remaining=$((remaining-1))
   case "$argument" in
    up) argument=create ;;
    -d|--wait) continue ;;
    --wait-timeout) shift; remaining=$((remaining-1)); continue ;;
   esac
   set -- "$@" "$argument"
  done ;;
esac
# Installation mutations follow genuine fresh-target admission. Isolate its edge before Compose can create it.
case " $* " in
 *" compose "*" up "*|*" compose "*" create "*|*" compose "*" run "*)
  stage=fixture-edge
  network="${{project}}_edge"
  if ! /usr/bin/docker network inspect "$network" >/dev/null 2>&1; then
   /usr/bin/docker network create --internal --subnet 172.30.69.0/24 --gateway 172.30.69.1 \
    --label "com.docker.compose.project=$project" --label com.docker.compose.network=edge "$network" >/dev/null
   /usr/bin/docker network connect --ip 172.30.69.4 --alias acme-v02.api.letsencrypt.org "$network" "$ca"
  fi
  test "$(/usr/bin/docker network inspect --format '{{{{.Internal}}}}' "$network")" = true
  stage=docker-command ;;
esac
# This adapter changes fixture trust and DNS, never release bytes or observed mounts.
/usr/bin/docker "$@"
case " $* " in
 *" create caddy "|*" create "*" caddy ")
  proxy=$(/usr/bin/docker ps -aq --filter "label=com.docker.compose.project=$project" --filter label=com.docker.compose.service=caddy)
  test -n "$proxy"
  if ! test -e "$fixture/primed-$proxy"; then
   stage=fixture-dns
   network="${{project}}_edge"
   /usr/bin/docker network disconnect "$network" "$proxy"
   /usr/bin/docker network connect --ip 172.30.69.3 --alias wayfarer.example.org "$network" "$proxy"
   image=$(/usr/bin/docker inspect --format '{{{{.Config.Image}}}}' "$proxy")
   trust=source
   test "$image" != "caddy@{caddy.POLICY['targetCaddy']}" || trust=target
   stage=fixture-trust
   /usr/bin/docker cp "$fixture/$trust-trust.pem" "$proxy:/etc/ssl/certs/ca-certificates.crt"
   touch "$fixture/primed-$proxy"
  fi
  if "$initial_up"; then
   stage=initial-proxy-start
   /usr/bin/docker start "$proxy" >/dev/null
   wait_proxy "$proxy"
  fi ;;
esac
case " $* " in
 *" start "*)
  for proxy; do :; done
  image=$(/usr/bin/docker inspect --format '{{{{.Config.Image}}}}' "$proxy")
  case "$image" in caddy@*) wait_proxy "$proxy" ;; esac
  if test "$(cat "$fixture/failure" 2>/dev/null || true)" = target-proxy &&
     test "$image" = "caddy@{caddy.POLICY['targetCaddy']}"; then
   touch "$fixture/failure.ready"
   stage=target-acknowledgement-fault
   while ! test -e "$fixture/failure.go"; do sleep 0.1; done
   exit 137
  fi ;;
esac
'''
        self.host('tee', str(self.directory / 'docker-test'), data=wrapper)

    def curl(self, path, *args):
        """Use the managed fixture CA and the existing real-login helper."""
        return self.ingress.curl(path, *args).stdout

    def receipt(self, kind, operation=None):
        """Read product intent or its terminal archive; no test writes receipt authority."""
        path = self.install / 'recovery-control' / (kind + '.json')
        if operation:
            path = self.install / 'recovery-control' / (kind + '-history') / (operation.replace('-', '') + '.json')
        return json.loads(self.host('cat', str(path)).stdout)

    def replacement(self):
        """Prepare exact owner-bound plans with the new executable, leaving the old installed operator untouched."""
        output = self.ctl('update', '--replace-release', '--bundle', str(self.target), '--plan').stdout
        return json.loads(output.splitlines()[0]), output.split('Plan SHA-256: ')[1].splitlines()[0]

    def proxy_facts(self):
        """Retain actual native payload, configuration and mounts without exposing TLS keys or credentials."""
        proxy = json.loads(self.host('docker', 'inspect', self.project + '-caddy-1').stdout)[0]
        image_facts = json.loads(self.host('docker', 'image', 'inspect', proxy['Config']['Image']).stdout)[0]
        assert proxy['Image'] == image_facts['Id']
        return dict(reference=proxy['Config']['Image'], imageId=proxy['Image'],
                    platform=image_facts['Os'] + '/' + image_facts['Architecture'],
                    version=self.host('docker', 'exec', self.project + '-caddy-1', 'caddy', 'version').stdout.strip(),
                    mounts=proxy['Mounts'], labels=proxy['Config']['Labels'], command=proxy['Config']['Cmd'],
                    restart=proxy['HostConfig']['RestartPolicy'],
                    autosaveSha256=self.host('docker', 'exec', self.project + '-caddy-1', 'sha256sum', '/config/caddy/autosave.json').stdout.split()[0])

    def retain_diagnostics(self, evidence):
        """Capture missing issuer, network, command and native resource facts before existing cleanup; never dump keys or environment."""
        # These commands are read-only and failures are evidence, not replacement exceptions.
        for name in ('proxy-command.log', 'proxy-command.stderr', 'proxy-readiness.stderr', 'acme.Caddyfile'):
            observed = self.host('cat', str(self.directory / name), check=False)
            (evidence / name).write_text(observed.stdout + observed.stderr)
        setup = getattr(self, 'setup_result', None)
        if setup is not None:
            (evidence / 'setup-operator.log').write_text(
                f'exitCode={setup.returncode}\n' + setup.stdout + setup.stderr)
        for command in ('status', 'doctor'):
            observed = self.ctl(command, check=False)
            (evidence / ('operator-' + command + '.log')).write_text(observed.stdout + observed.stderr)
        for name in (self.project + '-caddy-1', getattr(self, 'ca', 'qualification-acme-' + self.project)):
            observed = run('docker', 'logs', '--tail', '200', name, check=False)
            (evidence / (name + '.log')).write_text(observed.stdout + observed.stderr)
            observed = run('docker', 'inspect', name, check=False)
            if observed.returncode == 0:
                container = json.loads(observed.stdout)[0]
                facts = {key: container[key] for key in ('Id', 'Name', 'Image', 'State', 'Mounts', 'NetworkSettings')}
                facts['config'] = {key: container['Config'][key] for key in ('Image', 'Entrypoint', 'Cmd', 'Labels')}
                (evidence / (name + '.json')).write_text(json.dumps(facts, indent=2) + '\n')
                observed = run('docker', 'image', 'inspect', '--format',
                               '{{json .Id}} {{json .Os}} {{json .Architecture}} {{json .RepoDigests}}', container['Image'], check=False)
                (evidence / (name + '-image.log')).write_text(observed.stdout + observed.stderr)
        # The existing client validates TLS here; wget also records the resolved issuer address.
        observed = run('docker', 'exec', self.project + '-caddy-1', 'sh', '-c',
                       'cat /etc/resolv.conf /etc/hosts; wget -S -O - https://acme-v02.api.letsencrypt.org/directory', check=False)
        (evidence / 'issuer-readiness.log').write_text(observed.stdout + observed.stderr)
        observed = run('docker', 'network', 'inspect', self.project + '_edge', check=False)
        (evidence / 'edge-network.json').write_text(observed.stdout + observed.stderr)
        observed = run('docker', 'info', '--format', 'os={{.OSType}} arch={{.Architecture}} cpus={{.NCPU}} memory={{.MemTotal}}', check=False)
        (evidence / 'resources.log').write_text(observed.stdout + observed.stderr)
        observed = self.host('df', '-h', str(self.directory), self.docker_volumes, check=False)
        with (evidence / 'resources.log').open('a') as stream:
            stream.write(observed.stdout + observed.stderr)

    def transition(self):
        """Exercise one lost target-start acknowledgement, exact-owner restore, and one accepted replacement."""
        self.setup_result = self.ctl('setup', '--bundle', str(self.old), '--hostname', 'wayfarer.example.org', '--project', self.project,
                                     '--edge-prefix', '172.30.69', '--mode', 'managed', '--password-stdin', data=self.password + '\n', check=False)
        if self.setup_result.returncode:
            raise RuntimeError('Initial genuine v1.9.22 setup failed; see retained operator, proxy and fixture diagnostics.')
        # Reuse the recovery fixture's tiny uploads witness before inspecting its durable layout.
        self.compose('exec', '-T', 'wayfarer', 'sh', '-ec',
                     'mkdir -p /var/lib/wayfarer/uploads/imports; printf durable > /var/lib/wayfarer/uploads/imports/caddy-qualification')
        self.ctl('backup', 'configure', '--destination', str(self.directory / 'destination'),
                 '--payload', str(self.old / 'wayfarer-recovery'), '--retention', '4')
        before = self.ingress.tls_facts()
        source_proxy = self.proxy_facts()
        registration = json.loads(self.host('cat', next(path for path in before['account'] if path.endswith('.json'))).stdout)
        assert registration['location'].startswith('https://acme-v02.api.letsencrypt.org/acme/local/account/')
        self.executable = self.target / 'wayfarerctl'
        plan, digest = self.replacement()
        assert plan['UpdateProtocol'] == 2 and plan['OperatorOwner'] == plan['Target']['Release']
        self.host('tee', str(self.directory / 'failure'), data='target-proxy')
        with ThreadPoolExecutor(max_workers=1) as executor:
            pending = executor.submit(self.ctl, 'update', '--accept-plan', digest, check=False)
            try:
                self.host('timeout', '600', 'sh', '-ec', 'while ! test -e "$1"; do sleep 0.1; done',
                          'ready', str(self.directory / 'failure.ready'))
                after_target = self.ingress.tls_facts()
                target_proxy = self.proxy_facts()
            finally:
                self.host('touch', str(self.directory / 'failure.go'))
            failed = pending.result(timeout=180)
        assert failed.returncode != 0
        interrupted = self.receipt('update')
        assert interrupted['Phase'] == 8 and interrupted['Plan']['OperatorOwner'] == plan['OperatorOwner']
        stopped = json.loads(self.host('docker', 'inspect', self.project + '-caddy-1').stdout)[0]
        assert not stopped['State']['Running'] and stopped['HostConfig']['RestartPolicy']['Name'] == 'no'
        assert_tls_transition(before, after_target)
        assert target_proxy['version'].startswith('v2.11.7')
        self.host('tee', str(self.directory / 'failure'), data='')
        self.executable = self.old / 'wayfarerctl'
        assert self.ctl('update', '--restore', plan['Operation'], check=False).returncode != 0
        assert self.ctl('start', check=False).returncode != 0
        self.executable = self.target / 'wayfarerctl'
        self.ctl('dispatch', 'update', '--restore', plan['Operation'])
        restored = self.receipt('update', plan['Operation'])
        assert restored['RestoreAccepted'] and restored['Plan']['OperatorOwner'] == plan['OperatorOwner']
        joined = self.receipt('restore', restored['RestoreOperation'])
        assert joined['Schema'] == 2 and joined['Plan']['OperatorOwner'] == plan['OperatorOwner']
        assert_tls_transition(after_target, self.ingress.tls_facts())
        recovered_proxy = self.proxy_facts()
        assert recovered_proxy['restart'] == source_proxy['restart']
        assert self.host('docker', 'exec', self.project + '-caddy-1', 'caddy', 'version').stdout.startswith('v2.11.4')
        self.ctl('dispatch', 'restore', '--resume', joined['Plan']['Operation'])
        self.ctl('dispatch', 'start')
        with tempfile.NamedTemporaryFile() as cookie:
            self.authenticate(cookie.name)
            next_plan, next_hash = self.replacement()
            self.ctl('dispatch', 'update', '--accept-plan', next_hash)
            accepted = self.receipt('update', next_plan['Operation'])
            assert accepted['Phase'] == 9
            assert_tls_transition(before, self.ingress.tls_facts())
            assert self.curl('/Admin/Users', '-b', cookie.name, '-o', '/dev/null', '-w', '%{http_code}') == '200'
            self.authenticate(cookie.name)
        accepted_proxy = self.proxy_facts()
        assert accepted_proxy['restart'] == source_proxy['restart']
        return dict(sourceFingerprint=caddy.POLICY['sources'][image.PLATFORM], sourceCaddy=caddy.POLICY['sourceCaddy'],
                    targetCaddy=caddy.POLICY['targetCaddy'], targetOwner=plan['OperatorOwner'], recoveredOwner=joined['Plan']['OperatorOwner'],
                    interruptedOperation=plan['Operation'], acceptedOperation=next_plan['Operation'],
                    localAccount=registration['location'], privateEndpoint=self.proxy_address,
                    interruptedReceipt=interrupted, restoredReceipt=restored, joinedRestoreReceipt=joined, acceptedReceipt=accepted,
                    tlsStateReadable=['2.11.4', '2.11.7'], tls=before, sourceProxy=source_proxy,
                    targetProxy=target_proxy, recoveredProxy=recovered_proxy, acceptedProxy=accepted_proxy, result='passed')


def qualify(inputs, output):
    """Keep receipts outside disposable resources and clean only this random project's labelled objects."""
    with tempfile.TemporaryDirectory(prefix='wayfarer-533-caddy-') as directory, tempfile.TemporaryDirectory(prefix='wayfarer-caddy-client-') as client:
        old, target = Path(inputs['oldBundle']), Path(inputs['targetBundle'])
        journey = ManagedJourney(directory, old / 'wayfarerctl', '')
        (Path(directory) / 'destination').mkdir()
        try:
            journey.prepare_managed(old, target, Path(client))
            receipt = journey.transition()
            receipt.update(platform=image.PLATFORM, sourceRevision=inputs['sourceRevision'], scopeSha256=inputs['scopeSha256'])
            output.write_text(json.dumps(receipt, indent=2) + '\n')
            print('PASS genuine managed Caddy replacement, lost acknowledgement, target-owned old-release recovery and TLS/login continuity', flush=True)
        finally:
            failed = sys.exc_info()[0] is not None
            # Preserve non-secret durable authority even when qualification fails.
            evidence = output.parent / (output.stem + '-operations')
            evidence.mkdir(exist_ok=True)
            for service in ('caddy', 'wayfarer', 'db'):
                observed = journey.host('docker', 'logs', '--tail', '100', journey.project + '-' + service + '-1', check=False)
                (evidence / (service + '.log')).write_text(observed.stdout + observed.stderr)
            for name in ('update.json', 'restore.json'):
                observed = journey.host('cat', str(journey.install / 'recovery-control' / name), check=False)
                if observed.returncode == 0:
                    (evidence / name).write_text(observed.stdout)
            try:
                # Capture while failed containers and issuer state still exist, without resuming setup.
                if failed:
                    journey.retain_diagnostics(evidence)
            except Exception as error:
                (evidence / 'diagnostics-error.log').write_text(type(error).__name__ + '\n')
            finally:
                try:
                    journey.cleanup()
                except Exception as error:
                    (evidence / 'cleanup-error.log').write_text(type(error).__name__ + '\n')
                    if not failed:
                        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('prepare', 'qualify'))
    parser.add_argument('--inputs', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'prepare':
        prepare(args.output.resolve())
    else:
        qualify(json.loads(args.inputs.read_text()), args.output.resolve())
