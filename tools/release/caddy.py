"""Exact proxy bridge and read-only pre-publication compatibility qualification."""
import hashlib
import json
import re
from pathlib import Path
import subprocess
import tarfile
import tempfile

import image
import version

POLICY = json.loads(Path(__file__).with_name('caddy-replacement.json').read_text())


def require(source, fingerprint, target):
    """Only the genuine native v1.9.22 authority and reviewed image/comment delta may cross."""
    def file_hash(manifest, path):
        return next(item['Sha256'] for item in manifest['Files'] if item['Path'] == path)
    if (source['Status'] != 'stable' or source['Version'] != POLICY['sourceVersion'] or
            source['SourceRevision'] != POLICY['sourceRevision'] or
            fingerprint != POLICY['sources'].get(source['Platform']) or
            source['Images']['CaddyDigest'] != POLICY['sourceCaddy'] or
            target['Images']['CaddyDigest'] != POLICY['targetCaddy'] or
            file_hash(source, 'compose.yaml') != POLICY['sourceCompose'] or
            file_hash(target, 'compose.yaml') != POLICY['targetCompose']):
        raise version.ValidationError('unsupported Caddy replacement release/template authority')
    for path, digest in POLICY['unchanged'].items():
        if file_hash(source, path) != digest or file_hash(target, path) != digest:
            raise version.ValidationError('Caddy replacement changes unrelated template authority')
    if any(2 not in target['Operator'][key] for key in ('UpdateReceiptSchemas', 'RestoreReceiptSchemas')):
        raise version.ValidationError('target lacks replacement recovery protocols')


def source_bundle(tag, platform, directory):
    """Hash public bytes and bounded regular payloads without executing a historical operator."""
    import public_bundle
    name = f'wayfarer-{tag}-{platform.replace("/", "-")}.tar.gz'
    release = public_bundle.metadata(tag)
    matches = [entry for entry in release['assets'] if entry['name'] == name]
    if (release['tag_name'] != tag or release['draft'] or release['prerelease'] or len(matches) != 1):
        raise version.ValidationError('unsupported public predecessor metadata')
    asset = matches[0]
    if asset['state'] != 'uploaded' or not 0 < asset['size'] <= public_bundle.LIMIT:
        raise version.ValidationError('invalid predecessor asset')
    subprocess.run(['gh', 'release', 'download', tag, '--repo', public_bundle.REPOSITORY,
                    '--pattern', name, '--dir', str(directory)], check=True)
    archive = directory / name
    with archive.open('rb') as stream:
        archive_digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if archive.stat().st_size != asset['size'] or 'sha256:' + archive_digest != asset['digest']:
        raise version.ValidationError('predecessor archive differs from public authority')
    with tarfile.open(archive) as tar:
        members = []
        for member in tar:
            members.append(member)
            if len(members) > 16 or not member.isfile() or member.size > 256 * 1024 * 1024:
                raise version.ValidationError('invalid predecessor payload inventory')
        by_name = {item.name: item for item in members}
        if len(by_name) != len(members) or 'release.json' not in by_name or by_name['release.json'].size > 1024 * 1024:
            raise version.ValidationError('ambiguous predecessor inventory')
        raw = tar.extractfile('release.json').read()
        manifest = json.loads(raw)
        import bundle
        expected = set(bundle.PAYLOADS) | (set(bundle.CAPTURE_PAYLOADS) if manifest.get('LegacyCapture') else set())
        if (manifest.get('Schema') != 1 or manifest.get('BundleContract') != 1 or manifest.get('ConfigurationSchema') != 1 or
                manifest.get('Version') != tag[1:] or manifest.get('Repository') != image.SOURCE or
                {item['Path'] for item in manifest['Files']} != expected or len(manifest['Files']) != len(expected)):
            raise version.ValidationError('unsupported predecessor release contract')
        if (manifest['Platform'] != platform or manifest['Status'] != 'stable' or manifest['Tag'] != tag or
                set(by_name) != {'release.json', *(item['Path'] for item in manifest['Files'])}):
            raise version.ValidationError('predecessor manifest identity differs')
        fingerprint = hashlib.sha256(b'wayfarer-release:1:1\n' + hashlib.sha256(raw).digest())
        for item in sorted(manifest['Files'], key=lambda item: item['Path']):
            member = by_name[item['Path']]
            digest = hashlib.file_digest(tar.extractfile(member), 'sha256').digest()
            if digest.hex() != item['Sha256'] or member.mode != item['Mode'] or item['Mode'] != bundle.mode(item['Path']) or item['Type'] != 'file':
                raise version.ValidationError('predecessor payload differs')
            fingerprint.update(item['Path'].encode() + b'\n' + digest)
    return manifest, fingerprint.hexdigest(), archive


def scope_hash():
    """Bind qualification to actual product/tooling bytes; release numbering and prose are separate authority."""
    paths = image.run('git', 'ls-files').splitlines()
    selected = [path for path in paths if path.endswith(('.cs', '.csproj', '.py', '.json', '.yaml', '.yml', '.sh', '.props', '.targets'))
                and not path.startswith(('docs/', 'tests/', 'wwwroot/')) and path != 'Version.props']
    digest = hashlib.sha256()
    for path in sorted(selected + ['Dockerfile']):
        digest.update(path.encode() + b'\n' + hashlib.sha256((version.REPO_ROOT / path).read_bytes()).digest())
    return digest.hexdigest()


def require_qualification(directory, expected_scope, source_revision):
    """Both native receipts must come from the same successful source workflow and exact compatibility inputs."""
    receipts = [json.loads(path.read_text()) for path in directory.rglob('caddy-replacement-*.json')]
    if len(receipts) != 2 or {item.get('platform') for item in receipts} != set(image.PLATFORMS):
        raise version.ValidationError('Caddy replacement needs native AMD64 and ARM64 qualification receipts')
    for receipt in receipts:
        owner = receipt.get('targetOwner')
        if not isinstance(owner, dict) or any(not re.fullmatch(r'[a-f0-9]{64}', owner.get(key, '')) for key in ('Fingerprint', 'OperatorSha256')):
            raise version.ValidationError('unqualified Caddy replacement receipt authority')
        if (receipt.get('result') != 'passed' or receipt.get('sourceRevision') != source_revision or
                receipt.get('scopeSha256') != expected_scope or
                receipt.get('sourceFingerprint') != POLICY['sources'][receipt['platform']] or
                receipt.get('sourceCaddy') != POLICY['sourceCaddy'] or receipt.get('targetCaddy') != POLICY['targetCaddy'] or
                receipt.get('recoveredOwner') != receipt.get('targetOwner') or not receipt.get('targetOwner') or
                receipt.get('tlsStateReadable') != ['2.11.4', '2.11.7']):
            raise version.ValidationError('unqualified Caddy replacement receipt authority')


def qualification():
    """Reuse only successful hosted receipts matching the current complete compatibility scope."""
    runs = json.loads(image.run('gh', 'run', 'list', '--repo', 'stef-k/Wayfarer', '--workflow', 'tests.yml',
                                '--status', 'success', '--limit', '30', '--json', 'databaseId,headSha'))
    expected = scope_hash()
    for run in runs:
        with tempfile.TemporaryDirectory(prefix='wayfarer-caddy-evidence-') as temporary:
            directory = Path(temporary)
            result = subprocess.run(['gh', 'run', 'download', str(run['databaseId']), '--repo', 'stef-k/Wayfarer',
                                     '--pattern', 'caddy-replacement-*', '--dir', temporary], capture_output=True)
            if result.returncode:
                continue
            try:
                require_qualification(directory, expected, run['headSha'])
                return
            except version.ValidationError:
                continue
    raise version.ValidationError('no successful hosted Caddy replacement qualification matches current inputs; publication refused')


def preflight(release):
    """Resolve compatibility before any create-only native-image or index write, with no publication side effects."""
    import bundle
    import public_bundle
    prior = public_bundle.previous(release['tag'])
    if prior is None:
        return
    database = bundle.accepted_database()
    migrations = sorted(path.stem for path in (version.REPO_ROOT / 'Migrations').glob('*.cs')
                        if not path.name.endswith('.Designer.cs') and path.name[0].isdigit())
    replacement = False
    for platform in image.PLATFORMS:
        with tempfile.TemporaryDirectory(prefix='wayfarer-compatibility-') as temporary:
            source, fingerprint, _ = source_bundle(prior, platform, Path(temporary))
        target = dict(BundleContract=1, ConfigurationSchema=1, Platform=platform, Version=release['version'],
                      Images=dict(DatabaseDigest=next(item['platformDigest'] for item in database['platforms'] if item['platform'] == platform),
                                  CaddyDigest=bundle.CADDY),
                      Files=[dict(Path=path, Sha256=bundle.digest(version.REPO_ROOT / 'deploy/compose' / path))
                             for path in ('compose.yaml', *POLICY['unchanged'])],
                      Application=dict(Migrations=migrations), Operator=dict(UpdateReceiptSchemas=[1, 2], RestoreReceiptSchemas=[1, 2]))
        public_bundle.boundary(source, fingerprint, target)
        replacement |= source['Images']['CaddyDigest'] != target['Images']['CaddyDigest']
    if replacement:
        qualification()
