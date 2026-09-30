"""Deterministic distribution bytes and external checksum boundary."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tarfile

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bundle


@pytest.mark.parametrize("platform", bundle.image.PLATFORMS)
def test_stable_database_pin_requires_both_native_platforms(monkeypatch, platform):
    """Stable publication cannot start from the historical single-platform DB identity."""
    import version
    monkeypatch.setattr(bundle.image, 'PLATFORM', platform)
    entries = [{'digest': 'sha256:' + char * 64, 'platform': {'os': 'linux', 'architecture': arch}}
               for char, arch in (('b', 'amd64'), ('c', 'arm64'))]
    monkeypatch.setattr(bundle.image, 'run', lambda *args: json.dumps({'manifests': entries}))
    assert bundle.stable_database_digest('sha256:' + 'a' * 64) == entries[0 if platform == 'linux/amd64' else 1]['digest']
    entries.pop()
    with pytest.raises(version.ValidationError, match='exactly one'):
        bundle.stable_database_digest('sha256:' + 'a' * 64)
    monkeypatch.setattr(bundle.image, 'run', lambda *args: json.dumps({'config': {'digest': 'sha256:' + 'd' * 64}}))
    with pytest.raises(version.ValidationError, match='two-platform DB index pin'):
        bundle.stable_database_digest('sha256:' + 'a' * 64)


def test_candidate_cannot_fall_back_to_published_pg17(tmp_path):
    """Pre-publication qualification must explicitly supply a built native PG18 DB manifest."""
    import version
    with pytest.raises(version.ValidationError, match='explicit native PG18'):
        bundle.assemble(tmp_path / 'bundle', 'sha256:' + 'a' * 64)


def test_archive_is_reproducible_and_checksum_external(tmp_path):
    """Location and mtime do not affect archive identity; inventory has no self-checksum."""
    source = tmp_path / 'source'
    source.mkdir()
    for name in bundle.PAYLOADS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
    (source / 'release.json').write_text(json.dumps({'Version': '1.9.19', 'Platform': bundle.image.PLATFORM, 'SourceRevision': 'a' * 40, 'Files': [{'Path': name} for name in bundle.PAYLOADS]}))
    first, second = tmp_path / 'first', tmp_path / 'second'
    first.mkdir()
    second.mkdir()
    a = bundle.archive(source, first)
    b = bundle.archive(source, second)
    assert a.read_bytes() == b.read_bytes()
    assert (first / 'SHA256SUMS').read_text() == f'{bundle.digest(a)}  {a.name}\n'
    with tarfile.open(a) as archive:
        assert archive.getnames() == sorted((*bundle.PAYLOADS, 'release.json'))
        assert all(item.isfile() and item.mode == bundle.mode(item.name) for item in archive)
    assert a.name.startswith('wayfarer-candidate-')
    assert not (first / 'wayfarerctl-linux-amd64.tar.gz').exists()


@pytest.mark.parametrize("platform", bundle.image.PLATFORMS)
def test_stable_manifest_and_archive_use_exact_public_identity(tmp_path, monkeypatch, platform):
    """Controlled release facts construct v1 stable baseline bytes without creating a GitHub release."""
    source = tmp_path / 'source'
    source.mkdir()
    for name in bundle.PAYLOADS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
    monkeypatch.setattr(bundle.image, 'PLATFORM', platform)
    suffix = platform.replace('/', '-')
    facts = dict(tag='v1.9.20', version='1.9.20', sourceRevision='b' * 40)
    digest = 'sha256:' + 'c' * 64
    manifest = bundle.release_manifest(facts, digest, {}, {}, source, bundle.PAYLOADS, True, None)
    assert manifest['Schema'] == 1 and manifest['Status'] == 'stable'
    assert manifest['Tag'] == facts['tag'] and manifest['SourceRevision'] == facts['sourceRevision']
    assert manifest['Sources'] == [] and manifest['LegacyCapture'] is None
    assert manifest['Images']['ApplicationDigest'] == manifest['Images']['PlatformDigest'] == digest
    assert manifest['Images']['PostgreSqlMajor'] == 18
    assert manifest['Images']['Citext'] == '1.8'
    assert set(manifest) == {'Schema', 'BundleContract', 'ConfigurationSchema', 'Status', 'Version', 'Tag',
                             'Repository', 'SourceRevision', 'Platform', 'Images', 'Application', 'Operator',
                             'Sources', 'LegacyCapture', 'Files'}
    (source / 'release.json').write_text(json.dumps(manifest))
    first, second = tmp_path / 'first', tmp_path / 'second'
    first.mkdir()
    second.mkdir()
    a, b = bundle.archive(source, first), bundle.archive(source, second)
    assert a.name == f'wayfarer-v1.9.20-{suffix}.tar.gz'
    assert a.read_bytes() == b.read_bytes()
    assert (first / (a.name + '.sha256')).read_text() == f'{bundle.digest(a)}  {a.name}\n'
    assert not (first / 'SHA256SUMS').exists()
    bootstrap = first / f'wayfarerctl-{suffix}.tar.gz'
    assert bootstrap.read_bytes() == (second / bootstrap.name).read_bytes()
    with tarfile.open(bootstrap) as operator_archive:
        assert operator_archive.getnames() == ['wayfarerctl']
        entry = operator_archive.getmembers()[0]
        assert entry.isfile() and entry.mode == 0o555
        assert operator_archive.extractfile(entry).read() == (source / 'wayfarerctl').read_bytes()
    assert (first / (bootstrap.name + '.sha256')).read_text() == f'{bundle.digest(bootstrap)}  {bootstrap.name}\n'


def test_stable_source_boundary_is_exact_and_incompatibility_blocks():
    """The immediate public source binds exact fingerprint/prefix and cannot hide a breaking boundary."""
    import copy
    import pytest
    import public_bundle
    import version
    source = dict(Version='1.9.20', BundleContract=1, ConfigurationSchema=1, Platform='linux/amd64', Sources=[],
                  Images=dict(DatabaseDigest=bundle.DB, CaddyDigest=bundle.CADDY),
                  Application=dict(Migrations=['20260101000000_Initial'], TerminalMigration='20260101000000_Initial'),
                  Files=[dict(Path=name, Sha256='d' * 64) for name in bundle.PAYLOADS])
    target = copy.deepcopy(source)
    target['Version'] = '1.9.21'
    target['Application']['Migrations'].append('20260102000000_Forward')
    result = public_bundle.boundary(source, 'e' * 64, target)
    assert result['Version'] == '1.9.20' and result['Fingerprint'] == 'e' * 64
    assert result['TerminalMigration'] == source['Application']['TerminalMigration']
    assert result['ExactOrderedPrefix'] and not result['ReferenceSeeding']
    for kind in ('db', 'topology', 'prefix', 'seeding'):
        bad = copy.deepcopy(source)
        if kind == 'db':
            bad['Images']['DatabaseDigest'] = 'sha256:' + 'f' * 64
        elif kind == 'topology':
            bad['Files'][0]['Sha256'] = 'f' * 64
        elif kind == 'prefix':
            bad['Application']['Migrations'] = ['20260103000000_Unknown']
        else:
            bad['Sources'] = [dict(ReferenceSeeding=True)]
        with pytest.raises(version.ValidationError):
            public_bundle.boundary(bad, 'e' * 64, target)


def test_prior_discovery_preserves_baseline_and_chooses_deployable_source(monkeypatch):
    """Source-only historical versions never become an invented Compose source."""
    import public_bundle
    releases = [dict(tag_name='v1.9.19', draft=False, prerelease=False, assets=[])]
    monkeypatch.setattr(bundle.image, 'run', lambda *args: json.dumps(releases))
    assert public_bundle.previous('v1.9.20') is None
    releases.extend([dict(tag_name='v1.9.20', draft=False, prerelease=False,
                          assets=[dict(name='wayfarer-v1.9.20-linux-amd64.tar.gz')]),
                     dict(tag_name='v1.9.21', draft=False, prerelease=False, assets=[])])
    assert public_bundle.previous('v1.9.22') == 'v1.9.20'


def test_publication_does_not_clobber_or_rebuild_occupied_identity(monkeypatch, tmp_path):
    """An interrupted upload with even only a sidecar present stops before any upload/rebuild."""
    import pytest
    import public_bundle
    import version
    releases = dict(assets=[])
    monkeypatch.setattr(public_bundle, 'metadata', lambda tag: releases)
    monkeypatch.setattr(public_bundle.subprocess, 'run', lambda *args, **kwargs: pytest.fail('must not upload'))
    for name in public_bundle.asset_names('v1.9.20'):
        releases['assets'] = [dict(name=name)]
        with pytest.raises(version.ValidationError, match='occupied'):
            public_bundle.publish(tmp_path, 'v1.9.20')
        monkeypatch.setenv('GITHUB_EVENT_NAME', 'release')
        monkeypatch.setenv('GITHUB_REPOSITORY', public_bundle.REPOSITORY)
        monkeypatch.setattr(sys, 'argv', ['bundle.py', '--stable', '--publish', '--tag', 'v1.9.20',
                                        '--source', 'a' * 40, '--app-digest', 'sha256:' + 'b' * 64, '--output', str(tmp_path)])
        monkeypatch.setattr(bundle, 'assemble', lambda *args, **kwargs: pytest.fail('must not rebuild'))
        assert bundle.main() == 1


def test_stable_authoring_requires_exact_published_application_descriptor(monkeypatch):
    """A locally correct image cannot substitute for a different stable tag descriptor."""
    import pytest
    import version
    monkeypatch.setattr(bundle.image, 'run', lambda *args: json.dumps(dict(Descriptor=dict(digest='sha256:' + 'a' * 64))))
    with pytest.raises(version.ValidationError, match='exact supplied digest'):
        bundle.stable_image(dict(tag='v1.9.20'), 'sha256:' + 'b' * 64)


def test_publication_verifies_bootstrap_and_complete_asset_set(monkeypatch, tmp_path):
    """Create-only upload checks every public digest, including the new bootstrap, against intended bytes."""
    import pytest
    import public_bundle
    import version
    tag = 'v1.9.20'
    names = public_bundle.asset_names(tag)
    release = dict(tag_name=tag, name=tag, draft=False, prerelease=False,
                   html_url=f'{bundle.image.SOURCE}/releases/tag/{tag}',
                   url='https://api.github.com/repos/stef-k/Wayfarer/releases/1', assets=[])
    assets = []
    for name in names:
        path = tmp_path / name
        path.write_text(name)
        assets.append(dict(name=name, state='uploaded', size=path.stat().st_size,
                           digest='sha256:' + bundle.digest(path)))
    calls = []
    monkeypatch.setattr(public_bundle, 'metadata', lambda _: release)
    def upload(command, **kwargs):
        calls.append(command)
        release['assets'] = assets
    monkeypatch.setattr(public_bundle.subprocess, 'run', upload)
    facts = public_bundle.publish(tmp_path, tag)
    assert set(facts) == set(names)
    assert calls == [['gh', 'release', 'upload', tag, '--repo', public_bundle.REPOSITORY,
                      *[str(tmp_path / name) for name in names]]]
    release['assets'] = []
    next(item for item in assets if item['name'] == 'wayfarerctl-linux-amd64.tar.gz')['digest'] = 'sha256:' + 'f' * 64
    with pytest.raises(version.ValidationError, match='digest differs'):
        public_bundle.publish(tmp_path, tag)
