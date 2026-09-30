"""Publish the reviewed DB recipe once, or qualify its exact public registry artifact."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
import image
import version

IMAGE = 'ghcr.io/stef-k/wayfarer-db'
RECIPE = 'deploy/compose/db'
DB_VERSION = 'pg18.6-postgis3.6.4-bookworm'
PACKAGES = {'postgresql-18': '18.6-1.pgdg12+2',
            'postgresql-18-postgis-3': '3.6.4+dfsg-2.pgdg12+1',
            'postgresql-18-postgis-3-scripts': '3.6.4+dfsg-2.pgdg12+1'}


def identity(source, db_version):
    """Bind every operation to clean exact source and the supported package contract."""
    if not re.fullmatch(r'[0-9a-f]{40}', source) or image.run('git', 'rev-parse', 'HEAD') != source:
        raise version.ValidationError('source must be the full checked-out commit SHA')
    if image.run('git', 'status', '--porcelain', '--untracked-files=all'):
        raise version.ValidationError('DB operations require a clean source checkout')
    if db_version != DB_VERSION:
        raise version.ValidationError('unsupported DB version; update pins through review')
    return {'image': IMAGE, 'source': image.SOURCE, 'sourceRevision': source,
            'version': db_version, 'tag': f'{db_version}-{source}', 'platform': image.PLATFORM,
            'packages': PACKAGES}


def labels(release):
    """Bind the source, base and exact installed package expectations into OCI metadata."""
    return {'org.opencontainers.image.source': image.SOURCE,
            'org.opencontainers.image.revision': release['sourceRevision'],
            'org.opencontainers.image.version': release['version'],
            'org.opencontainers.image.base.name': base_image(),
            **{f'io.wayfarer.package.{key}': value for key, value in PACKAGES.items()}}


def base_image():
    """Read the accepted pinned base directly from the unchanged recipe."""
    return next(line.split()[1] for line in
                (version.REPO_ROOT / RECIPE / 'Dockerfile').read_text().splitlines()
                if line.startswith('FROM '))


def build(release):
    """Load one native artifact from the accepted shared recipe without a registry exporter."""
    ref = f"{IMAGE}:{release['tag']}"
    command = ['docker', 'buildx', 'build', '--platform', image.PLATFORM, '--load',
               '--provenance=false', '--sbom=false', '--tag', ref]
    for key, value in labels(release).items():
        command.extend(['--label', f'{key}={value}'])
    subprocess.run([*command, RECIPE], cwd=version.REPO_ROOT, check=True)
    return ref


def inspect_payload(release, ref):
    """Check platform, source/package labels and installed PostgreSQL executable/packages."""
    inspected = json.loads(image.run('docker', 'image', 'inspect', ref))[0]
    if f"{inspected['Os']}/{inspected['Architecture']}" != image.PLATFORM:
        raise version.ValidationError('DB platform must match the native release platform')
    actual = inspected['Config'].get('Labels') or {}
    expected_labels = labels(release)
    if any(actual.get(key) != value for key, value in expected_labels.items()):
        raise version.ValidationError('DB OCI identity mismatch')
    for package, expected in PACKAGES.items():
        installed = image.run('docker', 'run', '--rm', '--network', 'none', '--read-only',
                              '--tmpfs=/var/lib/postgresql:ro,mode=000,size=65536',
                              '--entrypoint', 'dpkg-query', ref, '-W', '-f=${Version}', package)
        if installed != expected:
            raise version.ValidationError(f'unexpected installed package: {package}')
    actual_version = image.run('docker', 'run', '--rm', '--network', 'none', '--read-only',
                               '--tmpfs=/var/lib/postgresql:ro,mode=000,size=65536',
                               '--entrypoint', 'postgres', ref, '--version')
    if actual_version != 'postgres (PostgreSQL) 18.6 (Debian 18.6-1.pgdg12+2)':
        raise version.ValidationError('unexpected PostgreSQL executable')
    if (inspected['Config'].get('Volumes') != {'/var/lib/postgresql': {}}
            or 'PGDATA=/var/lib/postgresql/18/docker' not in inspected['Config'].get('Env', [])):
        raise version.ValidationError('unexpected PostgreSQL data layout')
    return inspected


def qualify(release, ref, app_image):
    """Run the accepted full Compose gate, including actual PostGIS SQL and dump/restore."""
    inspected = inspect_payload(release, ref)
    subprocess.run([sys.executable, 'tools/compose/qualify.py', '--image', app_image,
                    '--db-image', ref, *(['--native-only'] if image.PLATFORM == 'linux/arm64' else [])], cwd=version.REPO_ROOT, check=True)
    return inspected


def evidence(release, inspected, digest, status):
    """Keep registry manifest, tested config, package/base and workflow evidence distinct."""
    run_id = os.environ.get('GITHUB_RUN_ID')
    return {**release, 'baseImage': base_image(), 'manifestDigest': digest,
            'platformDigest': digest, 'configDigest': image.config_digest(inspected),
            'qualification': status,
            'workflowRun': f'{image.SOURCE}/actions/runs/{run_id}' if run_id else None,
            'workflowAttempt': os.environ.get('GITHUB_RUN_ATTEMPT')}


def publish(release, app_image, output):
    """Qualify before a single create-only-intended push; retain digest before registry checks."""
    if (os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
            or os.environ.get('GITHUB_REPOSITORY') != 'stef-k/Wayfarer'
            or os.environ.get('GITHUB_SHA') != release['sourceRevision']):
        raise version.ValidationError('publication requires exact-source official manual workflow')
    ref = f"{IMAGE}:{release['tag']}"
    published = ref + '-' + image.PLATFORM.split('/')[1]
    image.require_absent(ref)
    image.require_absent(published)
    inspected = qualify(release, build(release), app_image)
    identity(release['sourceRevision'], release['version'])
    image.require_absent(ref)
    image.require_absent(published)
    image.run('docker', 'tag', ref, published)
    pushed = image.run('docker', 'push', '--platform', image.PLATFORM, published)
    matches = re.findall(r'^\S+: digest: (sha256:[0-9a-f]{64}) size: \d+$', pushed, re.MULTILINE)
    if len(matches) != 1:
        raise version.ValidationError('push digest unavailable; inspect registry/run, never republish')
    digest = matches[0]
    image.write_evidence(output, evidence(release, inspected, digest, 'pushed; anonymous qualification pending'))
    image.manifest_matches(f'{IMAGE}@{digest}', digest, image.config_digest(inspected))
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
        stream.write(f'digest={digest}\n')


def anonymous(release, digest, app_image, output):
    """Use an empty Docker credential context and run all probes on the pulled exact digest."""
    if not image.DIGEST.fullmatch(digest):
        raise version.ValidationError('invalid derived registry manifest digest')
    ref = f'{IMAGE}@{digest}'
    with tempfile.TemporaryDirectory(prefix='wayfarer-db-anonymous-') as config:
        previous = os.environ.get('DOCKER_CONFIG')
        os.environ['DOCKER_CONFIG'] = config
        try:
            image.run('docker', 'pull', '--platform', image.PLATFORM, ref)
            inspected = inspect_payload(release, ref)
            image.manifest_matches(ref, digest, image.config_digest(inspected))
            qualify(release, ref, app_image)
            image.write_evidence(output, evidence(release, inspected, digest,
                                                 'anonymous pull and full Compose qualification passed'))
        finally:
            if previous is None:
                os.environ.pop('DOCKER_CONFIG', None)
            else:
                os.environ['DOCKER_CONFIG'] = previous


def main():
    """Keep PR dry runs non-publishing and recovery qualification strictly non-mutating."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['dry-run', 'publish', 'qualify', 'index'])
    parser.add_argument('--inputs', type=Path)
    parser.add_argument('--source', required=True)
    parser.add_argument('--db-version', required=True)
    parser.add_argument('--app-image', help='qualified local application image; unnecessary for index assembly')
    parser.add_argument('--digest', default='')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        release = identity(args.source, args.db_version)
        if args.command == 'index':
            if (os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch' or
                    os.environ.get('GITHUB_REPOSITORY') != 'stef-k/Wayfarer' or
                    os.environ.get('GITHUB_SHA') != release['sourceRevision'] or args.inputs is None):
                raise version.ValidationError('index publication requires exact-source official manual workflow and inputs')
            image.publish_index(release, args.inputs, args.output)
        elif not args.app_image:
            raise version.ValidationError('--app-image required for native payload qualification')
        elif args.command == 'publish':
            publish(release, args.app_image, args.output)
        elif args.command == 'qualify':
            anonymous(release, args.digest, args.app_image, args.output)
        else:
            inspected = qualify(release, build(release), args.app_image)
            image.write_evidence(args.output, evidence(release, inspected, None, 'local dry-run only'))
    except (version.ValidationError, subprocess.CalledProcessError) as exc:
        print(str(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
