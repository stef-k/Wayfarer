"""Public stable asset provenance, immediate prior-source binding and create-only publication."""
from __future__ import annotations

import json
from pathlib import Path
import re
import subprocess

import image
import version

REPOSITORY = 'stef-k/Wayfarer'
LIMIT = 512 * 1024 * 1024
METADATA_LIMIT = 1024 * 1024


def asset_name(tag: str) -> str:
    """One versioned AMD64 identity, with no mutable aliases."""
    version.parse_semver(tag.removeprefix('v'))
    if not tag.startswith('v'):
        raise version.ValidationError('stable tag required')
    return f'wayfarer-{tag}-linux-amd64.tar.gz'


def metadata(tag: str) -> dict:
    """Use gh for project metadata; no repository/admin permission or arbitrary URL selection."""
    text = image.run('gh', 'api', f'repos/{REPOSITORY}/releases/tags/{tag}')
    if len(text.encode()) > METADATA_LIMIT:
        raise version.ValidationError('public metadata exceeds bound')
    return json.loads(text)


def asset(release: dict, tag: str) -> dict:
    """Require the same stable release/asset digest boundary as anonymous operator acquisition."""
    expected = asset_name(tag)
    if (release.get('tag_name') != tag or release.get('name') != tag or
            release.get('draft') is not False or release.get('prerelease') is not False or
            release.get('html_url') != f'{image.SOURCE}/releases/tag/{tag}' or
            not re.fullmatch(r'https://api.github.com/repos/stef-k/Wayfarer/releases/[1-9][0-9]*', release.get('url', ''))):
        raise version.ValidationError('invalid public stable release identity')
    matches = [item for item in release['assets'] if item['name'] == expected]
    if len(matches) != 1:
        raise version.ValidationError('exactly one public deployment asset required')
    result = matches[0]
    if (result.get('state') != 'uploaded' or type(result.get('size')) is not int or
            not 0 < result['size'] <= LIMIT or not re.fullmatch('sha256:[a-f0-9]{64}', result.get('digest', ''))):
        raise version.ValidationError('invalid public asset size/state/digest')
    return result


def previous(tag: str) -> str | None:
    """Locate the highest earlier deployable stable release; invalid advertised assets fail closed."""
    target = version.parse_semver(tag[1:])
    found = []
    # Bound discovery to 1000 releases. Exhaustion is uncertainty, never evidence of a baseline.
    for page in range(1, 11):
        text = image.run('gh', 'api', f'repos/{REPOSITORY}/releases?per_page=100&page={page}')
        if len(text.encode()) > 8 * METADATA_LIMIT:
            raise version.ValidationError('release discovery exceeds bound')
        releases = json.loads(text)
        for release in releases:
            candidate = release.get('tag_name', '')
            if not re.fullmatch(r'v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', candidate):
                continue
            if release.get('draft') or release.get('prerelease') or version.parse_semver(candidate[1:]) >= target:
                continue
            if any(item.get('name') == asset_name(candidate) for item in release['assets']):
                found.append(candidate)
        if len(releases) < 100:
            return max(found, key=lambda value: version.parse_semver(value[1:])) if found else None
    raise version.ValidationError('release discovery bound exhausted')


def acquire_source(tag: str, directory: Path, operator: Path) -> tuple[dict, str]:
    """Validate public source bytes before extraction/execution; only the current trusted operator is run."""
    # The same anonymous product owner validates metadata/digest and safely stages the public source.
    # Never execute the previous bundle's operator: only this freshly built trusted operator is invoked.
    stage = directory / 'stage'
    stage.mkdir(mode=0o700)
    subprocess.run([str(operator), 'release', 'unpack', tag[1:], str(stage)], check=True)
    source = stage / 'bundle'
    inspected = json.loads(subprocess.run([str(operator), 'release', 'inspect', str(source)],
                           check=True, text=True, capture_output=True).stdout)
    manifest = json.loads((source / 'release.json').read_text())
    if manifest['Status'] != 'stable' or manifest['Tag'] != tag or manifest['Version'] != tag[1:]:
        raise version.ValidationError('previous stable bundle identity mismatch')
    return manifest, inspected['Fingerprint']


def boundary(source: dict, fingerprint: str, target: dict) -> dict:
    """Bind one exact source only after the accepted DB/proxy/layout/migration compatibility boundary."""
    for key in ('BundleContract', 'ConfigurationSchema', 'Platform'):
        if source[key] != target[key]:
            raise version.ValidationError('incompatible prior stable topology contract')
    if source['Images']['DatabaseDigest'] != target['Images']['DatabaseDigest'] or source['Images']['CaddyDigest'] != target['Images']['CaddyDigest']:
        raise version.ValidationError('incompatible prior stable DB/proxy authority')
    # Conservative exact templates are stronger than #704 normalized topology comparison.
    for path in ('compose.yaml', 'external.yaml', 'caddy/Caddyfile', 'db/20-wayfarer.sh'):
        before = next(item['Sha256'] for item in source['Files'] if item['Path'] == path)
        after = next(item['Sha256'] for item in target['Files'] if item['Path'] == path)
        if before != after:
            raise version.ValidationError('incompatible prior stable topology payload')
    migrations = source['Application']['Migrations']
    if target['Application']['Migrations'][:len(migrations)] != migrations:
        raise version.ValidationError('prior stable migrations are not an exact ordered prefix')
    # The accepted release-contract has no reference-seeding input; never infer support from metadata.
    if any(item.get('ReferenceSeeding') for item in source['Sources']):
        raise version.ValidationError('unsupported prior reference seeding')
    return dict(Version=source['Version'], Fingerprint=fingerprint,
                TerminalMigration=source['Application']['TerminalMigration'], ExactOrderedPrefix=True,
                ReferenceSeeding=False, RetryRestriction='manual-recovery', Warning='Migration is forward-only; retain held recovery.')


def publish(directory: Path, tag: str) -> dict:
    """Never clobber/rebuild occupied identities; a rerun must reconcile using retained intended bytes."""
    import bundle
    release = metadata(tag)
    archive = directory / asset_name(tag)
    checksum = directory / (archive.name + '.sha256')
    names = {archive.name, checksum.name}
    if any(item['name'] in names for item in release['assets']):
        raise version.ValidationError('stable asset identity already occupied; reconcile retained publication evidence, never rebuild/clobber')
    subprocess.run(['gh', 'release', 'upload', tag, '--repo', REPOSITORY, str(archive), str(checksum)], check=True)
    facts = asset(metadata(tag), tag)
    if facts['size'] != archive.stat().st_size or facts['digest'] != 'sha256:' + bundle.digest(archive):
        raise version.ValidationError('uploaded asset digest differs; stop and reconcile, never overwrite')
    return facts
