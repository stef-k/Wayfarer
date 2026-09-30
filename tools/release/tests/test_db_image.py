"""DB publication authority, exact-artifact qualification and recoverable evidence contracts."""

import json
import os
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import db_image as db
import image
import version


@pytest.fixture
def release():
    """Describe the fixed DB recipe at one full source revision."""
    return {'tag': db.DB_VERSION + '-' + 'a' * 40, 'version': db.DB_VERSION,
            'sourceRevision': 'a' * 40}


def test_source_requires_exact_clean_checkout(monkeypatch):
    """No moving ref, dirty input or mismatched package version can authorize a build."""
    monkeypatch.setattr(image, 'run', lambda *a: 'a' * 40 if a[1] == 'rev-parse' else '')
    assert db.identity('a' * 40, db.DB_VERSION)['tag'].endswith('a' * 40)
    for source, db_version in [('main', db.DB_VERSION), ('b' * 40, db.DB_VERSION),
                               ('a' * 40, 'pg18')]:
        with pytest.raises(version.ValidationError):
            db.identity(source, db_version)
    monkeypatch.setattr(image, 'run', lambda *a: 'a' * 40 if a[1] == 'rev-parse' else ' M Dockerfile')
    with pytest.raises(version.ValidationError, match='clean'):
        db.identity('a' * 40, db.DB_VERSION)


def test_publication_preserves_digest_before_anonymous_gate(monkeypatch, tmp_path, release):
    """Registry verification failure after push leaves recoverable exact manifest evidence."""
    monkeypatch.setenv('GITHUB_EVENT_NAME', 'workflow_dispatch')
    monkeypatch.setenv('GITHUB_REPOSITORY', 'stef-k/Wayfarer')
    monkeypatch.setenv('GITHUB_SHA', release['sourceRevision'])
    calls = []
    monkeypatch.setattr(image, 'require_absent', lambda ref: calls.append('absence'))
    monkeypatch.setattr(db, 'build', lambda r: calls.append('build') or 'built')
    monkeypatch.setattr(db, 'qualify', lambda *a: calls.append('qualify') or {'Id': 'sha256:' + 'b' * 64})
    monkeypatch.setattr(db, 'identity', lambda *a: calls.append('identity'))
    digest = 'sha256:' + 'c' * 64
    monkeypatch.setattr(image, 'run', lambda *a: calls.append(a[1]) or f'tag: digest: {digest} size: 42')
    def fail_manifest(*args):
        raise version.ValidationError('registry unavailable after push')
    monkeypatch.setattr(image, 'manifest_matches', fail_manifest)
    output = tmp_path / 'publication.json'
    with pytest.raises(version.ValidationError, match='registry unavailable'):
        db.publish(release, 'app', output)
    assert calls == ['absence', 'absence', 'build', 'qualify', 'identity', 'absence', 'absence', 'tag', 'push']
    assert json.loads(output.read_text())['manifestDigest'] == digest
    assert json.loads(output.read_text())['qualification'] == 'pushed; anonymous qualification pending'


def test_pr_cannot_publish(monkeypatch, tmp_path, release):
    """A PR event fails before any registry operation, including builds or probes."""
    monkeypatch.setenv('GITHUB_EVENT_NAME', 'pull_request')
    with pytest.raises(version.ValidationError, match='manual workflow'):
        db.publish(release, 'app', tmp_path / 'publication.json')


def test_anonymous_recovery_never_builds_or_pushes(monkeypatch, tmp_path, release):
    """An empty client pulls and fully qualifies the exact digest with no publication path."""
    digest = 'sha256:' + 'c' * 64
    ref = f'{db.IMAGE}@{digest}'
    observed = []
    inspected = {'Id': 'sha256:' + 'b' * 64}
    def run(*args):
        assert list(Path(os.environ['DOCKER_CONFIG']).iterdir()) == []
        observed.append(args)
    monkeypatch.setattr(image, 'run', run)
    monkeypatch.setattr(db, 'inspect_payload', lambda r, actual: inspected if actual == ref else None)
    monkeypatch.setattr(image, 'manifest_matches', lambda *a: observed.append(a))
    monkeypatch.setattr(db, 'qualify', lambda *a: observed.append(a))
    monkeypatch.setattr(db, 'build', lambda *a: pytest.fail('recovery rebuilt DB'))
    monkeypatch.setattr(db, 'publish', lambda *a: pytest.fail('recovery published DB'))
    monkeypatch.setenv('DOCKER_CONFIG', '/unused/publishing-client')
    output = tmp_path / 'qualified.json'
    db.anonymous(release, digest, 'app', output)
    assert observed == [('docker', 'pull', '--platform', 'linux/amd64', ref),
                        (ref, digest, inspected['Id']), (release, ref, 'app')]
    assert os.environ['DOCKER_CONFIG'] == '/unused/publishing-client'
    assert 'full Compose qualification passed' in json.loads(output.read_text())['qualification']


def test_payload_rejects_labels_that_hide_wrong_installed_packages(monkeypatch, release):
    """Package labels alone cannot prove the installed payload version."""
    inspected = {'Os': 'linux', 'Architecture': 'amd64', 'Config': {'Labels': db.labels(release)}}
    monkeypatch.setattr(image, 'run', lambda *a: json.dumps([inspected]) if a[1] == 'image' else '17.5')
    with pytest.raises(version.ValidationError, match='installed package'):
        db.inspect_payload(release, 'db')


@pytest.mark.parametrize('layout', ['official', 'pg17', 'nested'])
def test_payload_requires_exact_pg18_packages_executable_and_layout(monkeypatch, release, layout):
    """A PG18 label cannot authorize old storage or a different executable/package revision."""
    root = '/var/lib/postgresql' if layout != 'pg17' else '/var/lib/postgresql/data'
    pgdata = '/var/lib/postgresql/18/docker' if layout != 'nested' else root
    inspected = {'Os': 'linux', 'Architecture': 'amd64', 'Config': {
        'Labels': db.labels(release), 'Env': ['PGDATA=' + pgdata], 'Volumes': {root: {}}}}
    packages = {'postgresql-18': '18.6-1.pgdg12+2',
                'postgresql-18-postgis-3': '3.6.4+dfsg-2.pgdg12+1',
                'postgresql-18-postgis-3-scripts': '3.6.4+dfsg-2.pgdg12+1'}
    def run(*args):
        if args[1] == 'image':
            return json.dumps([inspected])
        if args[-1] == '--version':
            return 'postgres (PostgreSQL) 18.6 (Debian 18.6-1.pgdg12+2)'
        return packages[args[-1]]
    monkeypatch.setattr(image, 'run', run)
    if layout == 'official':
        assert db.inspect_payload(release, 'db') == inspected
    else:
        with pytest.raises(version.ValidationError, match='layout'):
            db.inspect_payload(release, 'db')


def test_dry_run_cannot_claim_registry_identity(monkeypatch, tmp_path, release):
    """The PR CLI only builds/qualifies locally and emits null registry digests."""
    output = tmp_path / 'dry.json'
    monkeypatch.setattr(sys, 'argv', ['db_image.py', 'dry-run', '--source', 'a' * 40,
        '--db-version', db.DB_VERSION, '--app-image', 'app', '--output', str(output)])
    monkeypatch.setattr(db, 'identity', lambda *a: release)
    monkeypatch.setattr(db, 'build', lambda *a: 'local')
    monkeypatch.setattr(db, 'qualify', lambda *a: {'Id': 'sha256:' + 'b' * 64})
    monkeypatch.setattr(db, 'publish', lambda *a: pytest.fail('dry run published'))
    assert db.main() == 0
    assert json.loads(output.read_text())['manifestDigest'] is None
