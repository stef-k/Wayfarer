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
import version


@pytest.fixture
def database_registry(monkeypatch, tmp_path):
    """Use the promoted publication schema with deterministic immutable registry responses."""
    evidence = json.loads(Path(bundle.__file__).with_name('database-release.json').read_text())
    accepted = tmp_path / 'database-release.json'
    accepted.write_text(json.dumps(evidence))
    monkeypatch.setattr(bundle, '__file__', str(tmp_path / 'bundle.py'))
    registry = {evidence['manifestDigest']: {'manifests': [
        {'digest': fact['manifestDigest'], 'platform': dict(zip(('os', 'architecture'), fact['platform'].split('/')))}
        for fact in evidence['platforms']]}}
    registry.update({fact['manifestDigest']: {'config': {'digest': fact['configDigest']}}
                     for fact in evidence['platforms']})
    calls = []
    def inspect(*command):
        calls.append(command)
        assert command[:3] == ('docker', 'manifest', 'inspect')
        assert command[3].startswith(evidence['image'] + '@sha256:')
        return json.dumps(registry[command[3].split('@')[1]])
    monkeypatch.setattr(bundle.image, 'run', inspect)
    return evidence, accepted, registry, calls


@pytest.mark.parametrize('platform', bundle.image.PLATFORMS)
def test_stable_database_uses_promoted_evidence_independently_of_recipe(database_registry, monkeypatch, platform):
    """The index runner's platform and next candidate recipe cannot change stable native selection."""
    import db_image
    evidence, _, _, calls = database_registry
    monkeypatch.setattr(bundle.image, 'PLATFORM', platform)
    monkeypatch.setattr(db_image, 'DB_VERSION', 'next-unpublished-recipe')
    monkeypatch.setattr(db_image, 'PACKAGES', {})
    monkeypatch.setattr(db_image, 'base_image', lambda: pytest.fail('stable must not read the candidate recipe'))
    assert bundle.accepted_database() == evidence
    selected = bundle.stable_database_digest()
    assert selected == next(fact['platformDigest'] for fact in evidence['platforms'] if fact['platform'] == platform)
    assert selected != evidence['manifestDigest']
    assert {command[3].split('@')[1] for command in calls} == {
        evidence['manifestDigest'], *(fact['manifestDigest'] for fact in evidence['platforms'])}


@pytest.mark.parametrize('path,value', [
    (('image',), 'ghcr.io/other/db'), (('source',), 'https://github.com/other/repo'),
    (('sourceRevision',), '1234'), (('tag',), 'latest'), (('version',), 'pg99-invalid'),
    (('manifestDigest',), None), (('manifestDigest',), 'latest'),
    (('qualification',), 'index created; verification pending'),
    (('packages', 'postgresql-18'), '17.11-1.pgdg12+1'),
    (('packages', 'postgresql-18-postgis-3-scripts'), '3.6.3+dfsg-2.pgdg12+1'),
    (('platforms',), []), (('platforms',), {}),
    (('platforms', 0, 'platform'), 'linux/arm64'),
    (('platforms', 0, 'sourceRevision'), 'a' * 40),
    (('platforms', 0, 'packages'), {}),
    (('platforms', 0, 'manifestDigest'), 'sha256:' + 'a' * 64),
    (('platforms', 0, 'platformDigest'), None),
    (('platforms', 0, 'configDigest'), 'sha256:bad'),
    (('platforms', 0, 'baseImage'), 'postgres:18.6-bookworm'),
    (('platforms', 0, 'qualification'), 'pushed; anonymous qualification pending'),
])
def test_malformed_accepted_database_fails_before_registry(database_registry, path, value):
    """Common facts, package consistency and qualified platform shape fail closed locally."""
    evidence, accepted, _, calls = database_registry
    owner = evidence
    for key in path[:-1]:
        owner = owner[key]
    owner[path[-1]] = value
    accepted.write_text(json.dumps(evidence))
    with pytest.raises(version.ValidationError):
        bundle.stable_database_digest()
    assert calls == []


@pytest.mark.parametrize('kind', ['missing', 'extra', 'missing-digest', 'invalid-json', 'missing-file'])
def test_incomplete_accepted_database_fails_closed(database_registry, kind):
    """Only a readable, complete two-platform artifact can authorize registry inspection."""
    evidence, accepted, _, calls = database_registry
    if kind == 'missing':
        evidence['platforms'].pop()
    elif kind == 'extra':
        evidence['platforms'].append(evidence['platforms'][0])
    elif kind == 'missing-digest':
        del evidence['platforms'][0]['platformDigest']
    accepted.write_text('{' if kind == 'invalid-json' else json.dumps(evidence))
    if kind == 'missing-file':
        accepted.unlink()
    with pytest.raises(version.ValidationError):
        bundle.stable_database_digest()
    assert calls == []


@pytest.mark.parametrize('kind', ['native-index', 'missing', 'duplicate', 'extra', 'digest', 'config'])
def test_registry_contradictions_fail_closed(database_registry, kind):
    """Live index selection and both tested native config identities must match recorded evidence."""
    evidence, _, registry, _ = database_registry
    entries = registry[evidence['manifestDigest']]['manifests']
    if kind == 'native-index':
        registry[evidence['manifestDigest']] = {'config': {'digest': evidence['platforms'][0]['configDigest']}}
    elif kind == 'missing':
        entries.pop()
    elif kind == 'duplicate':
        entries[1] = entries[0]
    elif kind == 'extra':
        entries.append(entries[0])
    elif kind == 'digest':
        entries[1]['digest'] = 'sha256:' + 'a' * 64
    else:
        registry[evidence['platforms'][1]['manifestDigest']]['config']['digest'] = 'sha256:' + 'a' * 64
    with pytest.raises(version.ValidationError):
        bundle.stable_database_digest()


def test_candidate_requires_explicit_native_database(tmp_path, monkeypatch):
    """Pre-publication qualification must supply a built native manifest before consulting any authority."""
    monkeypatch.setattr(bundle, 'stable_database_digest', lambda: pytest.fail('candidate must not use accepted DB'))
    with pytest.raises(version.ValidationError, match='explicit native'):
        bundle.assemble(tmp_path / 'bundle', 'sha256:' + 'a' * 64)


def test_stable_cli_rejects_database_override(tmp_path, monkeypatch, capsys):
    """Stable CLI input cannot override committed DB authority."""
    monkeypatch.setattr(bundle.image, 'run', lambda *args: pytest.fail('invalid input must fail before work'))
    monkeypatch.setattr(sys, 'argv', ['bundle.py', '--stable', '--tag', 'v1.9.20', '--source', 'a' * 40,
        '--app-digest', 'sha256:' + 'b' * 64, '--db-digest', 'sha256:' + 'c' * 64, '--output', str(tmp_path)])
    assert bundle.main() == 1
    assert 'unsupported' in capsys.readouterr().err


def test_candidate_assembly_checks_explicit_digest_against_current_recipe(tmp_path, monkeypatch):
    """Candidate assembly reaches the existing recipe validator with exactly the supplied native manifest."""
    import db_image
    supplied = 'sha256:' + 'c' * 64
    ref = db_image.IMAGE + '@' + supplied
    monkeypatch.setattr(bundle, 'stable_database_digest', lambda: pytest.fail('candidate must not use accepted DB'))
    monkeypatch.setattr(bundle.image, 'identity', lambda *args: {})
    monkeypatch.setattr(bundle.image, 'inspect_image', lambda *args: {'RepoDigests': [bundle.image.IMAGE + '@' + supplied]})
    def inspect(*command):
        if command[0] == 'git':
            return ''
        assert command == ('docker', 'image', 'inspect', ref)
        return json.dumps([{'Config': {'Labels': {'org.opencontainers.image.revision': 'a' * 40}}}])
    monkeypatch.setattr(bundle.image, 'run', inspect)
    def check_recipe(release, reference):
        assert release == {'sourceRevision': 'a' * 40, 'version': db_image.DB_VERSION}
        assert reference == ref
        raise version.ValidationError('candidate recipe checked')
    monkeypatch.setattr(db_image, 'inspect_payload', check_recipe)
    with pytest.raises(version.ValidationError, match='candidate recipe checked'):
        bundle.assemble(tmp_path / 'bundle', supplied, db_digest=supplied)


def test_stable_assembly_skips_candidate_recipe(database_registry, tmp_path, monkeypatch):
    """Stable assembly validates accepted evidence then reaches output creation without inspecting the candidate recipe."""
    import db_image
    inspect_registry = bundle.image.run
    monkeypatch.setattr(bundle.image, 'run', lambda *args: '' if args[0] == 'git' else inspect_registry(*args))
    supplied = 'sha256:' + 'c' * 64
    monkeypatch.setattr(bundle, 'stable_image', lambda *args: supplied)
    monkeypatch.setattr(bundle.image, 'identity', lambda *args: {})
    monkeypatch.setattr(bundle.image, 'inspect_image', lambda *args: {'RepoDigests': [bundle.image.IMAGE + '@' + supplied]})
    monkeypatch.setattr(db_image, 'inspect_payload', lambda *args: pytest.fail('stable must not inspect the candidate recipe'))
    # An existing output proves validation completed without building lifecycle payloads.
    with pytest.raises(FileExistsError):
        bundle.assemble(tmp_path, supplied, stable=True, tag='v1.9.20', source='a' * 40)


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
    database = json.loads(Path(bundle.__file__).with_name('database-release.json').read_text())
    native = next(fact['manifestDigest'] for fact in database['platforms'] if fact['platform'] == platform)
    manifest = bundle.release_manifest(facts, digest, {}, {}, source, bundle.PAYLOADS, True, None, db_digest=native)
    assert manifest['Schema'] == 1 and manifest['Status'] == 'stable'
    assert manifest['Tag'] == facts['tag'] and manifest['SourceRevision'] == facts['sourceRevision']
    assert manifest['Sources'] == [] and manifest['LegacyCapture'] is None
    assert manifest['Images']['ApplicationDigest'] == manifest['Images']['PlatformDigest'] == digest
    assert manifest['Images']['PostgreSqlMajor'] == 18
    assert manifest['Images']['Citext'] == '1.8'
    assert manifest['Images']['DatabaseDigest'] == native != database['manifestDigest']
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
                  Images=dict(DatabaseDigest='sha256:' + 'b' * 64, CaddyDigest=bundle.CADDY),
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
