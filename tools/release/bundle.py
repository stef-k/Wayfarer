"""Assemble a deterministic offline candidate bundle; never publish, pull or activate."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile

sys.dont_write_bytecode = True
import image
import version

# This mirrors the fixed C# inventory; the shipped validator is the final authority.
PAYLOADS = ('compose.yaml', 'external.yaml', 'caddy/Caddyfile', 'db/20-wayfarer.sh',
            'config/deployment.env.example', 'compose.sh', 'INSTALL.md', 'wayfarerctl',
            'wayfarer-recovery', 'WayfarerRecoverySource.dll')
CAPTURE_PAYLOADS = ('capture/wayfarer-recovery', 'capture/WayfarerRecoverySource.dll')
DB = 'sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61'
CADDY = 'sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b'


def mode(name: str) -> int:
    """Fixed executable roles, independent of checkout umask."""
    if Path(name).name in ('wayfarerctl', 'wayfarer-recovery'):
        return 0o555
    if Path(name).name == 'WayfarerRecoverySource.dll':
        return 0o444
    return 0o755 if name in ('compose.sh', 'db/20-wayfarer.sh') else 0o644


def digest(path: Path) -> str:
    """Hash actual retained file bytes without loading executable payloads into memory."""
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def probe(ref: str, entry: str, *args: str, mount: Path | None = None) -> str:
    """No network, daemon socket, installation mounts or writable root in payload probes."""
    command = ['docker', 'run', '--rm', '--pull=never', '--platform=linux/amd64', '--network=none',
               '--user=1654:1654', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
               '--memory=512m', '--pids-limit=64', '--tmpfs=/tmp:uid=1654,gid=1654,mode=0700,size=67108864']
    if mount:
        command += ['--volume', f'{mount}:/payload.dll:ro' if mount.suffix == '.dll' else f'{mount}:/payload:ro']
    return image.run(*command, '--entrypoint=' + entry, ref, *args)


def archive(bundle: Path, output: Path) -> Path:
    """Reproducible USTAR/gzip bytes; checksum lives beside the archive, never inside it."""
    manifest = json.loads((bundle / 'release.json').read_text())
    name = f"wayfarer-candidate-v{manifest['Version']}-{manifest['SourceRevision']}-linux-amd64.tar.gz"
    target = output / name
    with target.open('xb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w', format=tarfile.USTAR_FORMAT) as tar:
            for name in sorted([entry['Path'] for entry in manifest['Files']] + ['release.json']):
                info = tarfile.TarInfo(name)
                info.size = (bundle / name).stat().st_size
                info.mode = mode(name)
                with (bundle / name).open('rb') as source:
                    tar.addfile(info, source)
    with (output / 'SHA256SUMS').open('x') as sums:
        sums.write(f'{digest(target)}  {target.name}\n')
    return target


def assemble(output: Path, app_digest: str, capture_directory: Path | None = None,
             capture_evidence: Path | None = None) -> Path:
    """Reuse version/image authorities and publish only three bounded lifecycle payloads."""
    if not image.DIGEST.fullmatch(app_digest):
        raise version.ValidationError('an actual immutable local application digest is required')
    if image.run('git', 'status', '--porcelain', '--untracked-files=normal'):
        raise version.ValidationError('assembly requires a clean committed source tree')
    release = image.identity(None, None)
    evidence = None
    if capture_directory or capture_evidence:
        if not (capture_directory and capture_evidence) or capture_evidence.stat().st_size > 131072:
            raise version.ValidationError('historical capture requires a pair directory and bounded independent source evidence')
        evidence = json.loads(capture_evidence.read_text())
        if not re.fullmatch('[a-f0-9]{40}', evidence['SourceRevision']):
            raise version.ValidationError('invalid historical application source')
        release['sourceRevision'] = evidence['SourceRevision']
    payloads = (*PAYLOADS, *CAPTURE_PAYLOADS) if evidence else PAYLOADS
    ref = f'{image.IMAGE}@{app_digest}'
    actual = image.inspect_image(release, ref)
    if ref not in actual.get('RepoDigests', []):
        raise version.ValidationError('digest is not present in local repository identity')
    output.mkdir(parents=True, exist_ok=False)
    bundle = output / f"candidate-v{release['version']}-{release['sourceRevision']}"
    bundle.mkdir(mode=0o755)
    with tempfile.TemporaryDirectory(prefix='wayfarer-bundle-publish-') as temporary:
        temp = Path(temporary)
        for project, destination in [('WayfarerCtl', 'operator'), ('WayfarerRecovery', 'worker')]:
            subprocess.run(['dotnet', 'publish', f'tools/{project}', '-c', 'Release', '-r', 'linux-x64',
                            '--self-contained', 'true', '-o', str(temp / destination)], check=True)
        subprocess.run(['dotnet', 'build', 'tools/WayfarerRecoverySource', '-c', 'Release'], check=True)
        for name in PAYLOADS[:6]:
            target = bundle / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(version.REPO_ROOT / 'deploy/compose' / name, target)
        shutil.copyfile(version.REPO_ROOT / 'tools/release/INSTALL.md', bundle / 'INSTALL.md')
        shutil.copyfile(temp / 'operator/wayfarerctl', bundle / 'wayfarerctl')
        shutil.copyfile(temp / 'worker/wayfarer-recovery', bundle / 'wayfarer-recovery')
        shutil.copyfile(version.REPO_ROOT / 'tools/WayfarerRecoverySource/bin/Release/net10.0/WayfarerRecoverySource.dll',
                        bundle / 'WayfarerRecoverySource.dll')
    if evidence:
        (bundle / 'capture').mkdir()
        for name in CAPTURE_PAYLOADS:
            shutil.copyfile(capture_directory / Path(name).name, bundle / name)
    for name in payloads:
        (bundle / name).chmod(mode(name))
    application = json.loads(probe(ref, 'dotnet', 'exec', '--runtimeconfig', '/app/Wayfarer.runtimeconfig.json',
        '--depsfile', '/app/Wayfarer.deps.json', '/payload.dll', 'release-contract', mount=bundle / 'WayfarerRecoverySource.dll'))
    worker = json.loads(probe('ghcr.io/stef-k/wayfarer-db@' + DB, '/payload', 'runtime-check', mount=bundle / 'wayfarer-recovery'))
    application['WorkerVersion'] = worker['Version']
    operator = json.loads(probe('ghcr.io/stef-k/wayfarer-db@' + DB, '/payload', 'release', 'protocol', mount=bundle / 'wayfarerctl'))
    manifest = {'Schema': 1, 'BundleContract': 1, 'ConfigurationSchema': 1, 'Status': 'candidate',
        'Version': release['version'], 'Tag': None, 'Repository': image.SOURCE,
        'SourceRevision': release['sourceRevision'], 'Platform': image.PLATFORM,
        'Images': {'ApplicationRepository': image.IMAGE, 'ApplicationDigest': app_digest, 'PlatformDigest': app_digest, 'OciVersion': release['version'],
                   'DatabaseDigest': DB, 'CaddyDigest': CADDY, 'PostgreSqlMajor': 17, 'Postgis': '3.6.4',
                   'Citext': '1.6', 'Encoding': 'UTF8', 'Collation': 'C.UTF-8', 'CharacterType': 'C.UTF-8', 'LocaleProvider': 'c'},
        'Application': application, 'Operator': operator, 'Sources': [],
        'LegacyCapture': {'WorkerVersion': evidence['WorkerVersion'], 'ReleaseStatus': evidence['ReleaseStatus']} if evidence else None,
        'Files': [{'Path': name, 'Sha256': digest(bundle / name), 'Type': 'file', 'Mode': mode(name)} for name in sorted(payloads)]}
    (bundle / 'release.json').write_text(json.dumps(manifest, sort_keys=True, separators=(',', ':')) + '\n')
    (bundle / 'release.json').chmod(0o644)
    # Offline inspection uses the same shipped C# validator as import/dispatch.
    subprocess.run([str(bundle / 'wayfarerctl'), 'release', 'inspect', str(bundle)], check=True)
    if evidence:
        subprocess.run([str(bundle / 'wayfarerctl'), 'release', 'corroborate', str(bundle), str(capture_evidence)], check=True)
    archive(bundle, output)
    return bundle


def main() -> int:
    """Candidate-only authoring; stable publication remains a separate acceptance gate."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app-digest', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--capture-directory', type=Path, help='Explicit historical worker/inspection pair to retain unchanged')
    parser.add_argument('--capture-evidence', type=Path, help='Independent configured SourceIdentity; never an archive manifest')
    args = parser.parse_args()
    try:
        print(assemble(args.output.resolve(), args.app_digest, args.capture_directory, args.capture_evidence))
        return 0
    except (version.ValidationError, OSError, subprocess.CalledProcessError) as error:
        print(f'Candidate assembly failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
