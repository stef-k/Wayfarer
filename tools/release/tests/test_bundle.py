"""Deterministic distribution bytes and external checksum boundary."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tarfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bundle


def test_archive_is_reproducible_and_checksum_external(tmp_path):
    """Location and mtime do not affect archive identity; inventory has no self-checksum."""
    source = tmp_path / 'source'
    source.mkdir()
    for name in bundle.PAYLOADS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
    (source / 'release.json').write_text(json.dumps({'Version': '1.9.19', 'SourceRevision': 'a' * 40, 'Files': [{'Path': name} for name in bundle.PAYLOADS]}))
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


def test_stable_manifest_and_archive_use_exact_public_identity(tmp_path):
    """Controlled release facts construct v1 stable baseline bytes without creating a GitHub release."""
    source = tmp_path / 'source'
    source.mkdir()
    for name in bundle.PAYLOADS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
    facts = dict(tag='v1.9.20', version='1.9.20', sourceRevision='b' * 40)
    digest = 'sha256:' + 'c' * 64
    manifest = bundle.release_manifest(facts, digest, {}, {}, source, bundle.PAYLOADS, True, None)
    assert manifest['Schema'] == 1 and manifest['Status'] == 'stable'
    assert manifest['Tag'] == facts['tag'] and manifest['SourceRevision'] == facts['sourceRevision']
    assert manifest['Sources'] == [] and manifest['LegacyCapture'] is None
    assert manifest['Images']['ApplicationDigest'] == manifest['Images']['PlatformDigest'] == digest
    assert set(manifest) == {'Schema', 'BundleContract', 'ConfigurationSchema', 'Status', 'Version', 'Tag',
                             'Repository', 'SourceRevision', 'Platform', 'Images', 'Application', 'Operator',
                             'Sources', 'LegacyCapture', 'Files'}
    (source / 'release.json').write_text(json.dumps(manifest))
    first, second = tmp_path / 'first', tmp_path / 'second'
    first.mkdir()
    second.mkdir()
    a, b = bundle.archive(source, first), bundle.archive(source, second)
    assert a.name == 'wayfarer-v1.9.20-linux-amd64.tar.gz'
    assert a.read_bytes() == b.read_bytes()
    assert (first / (a.name + '.sha256')).read_text() == f'{bundle.digest(a)}  {a.name}\n'
    assert not (first / 'SHA256SUMS').exists()


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
    releases = dict(assets=[dict(name='wayfarer-v1.9.20-linux-amd64.tar.gz.sha256')])
    monkeypatch.setattr(public_bundle, 'metadata', lambda tag: releases)
    monkeypatch.setattr(public_bundle.subprocess, 'run', lambda *args, **kwargs: pytest.fail('must not upload'))
    with pytest.raises(version.ValidationError, match='occupied'):
        public_bundle.publish(tmp_path, 'v1.9.20')


def test_stable_authoring_requires_exact_published_application_descriptor(monkeypatch):
    """A locally correct image cannot substitute for a different stable tag descriptor."""
    import pytest
    import version
    monkeypatch.setattr(bundle.image, 'run', lambda *args: json.dumps(dict(Descriptor=dict(digest='sha256:' + 'a' * 64))))
    with pytest.raises(version.ValidationError, match='exact supplied digest'):
        bundle.stable_image(dict(tag='v1.9.20'), 'sha256:' + 'b' * 64)
