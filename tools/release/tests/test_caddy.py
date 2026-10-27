"""Missing proxy-transition proof: exact admission and refusal before publication writes."""
import copy
import json
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import caddy
import image
import public_bundle
import version


def pair(platform='linux/amd64'):
    """Use the checked policy with independent source/target manifest objects."""
    policy = caddy.POLICY
    source = dict(Status='stable', Version='1.9.22', SourceRevision=policy['sourceRevision'],
                  BundleContract=1, ConfigurationSchema=1, Platform=platform, Sources=[],
                  Images=dict(DatabaseDigest='sha256:' + 'b' * 64, CaddyDigest=policy['sourceCaddy']),
                  Application=dict(Migrations=['20260101000000_Initial'], TerminalMigration='20260101000000_Initial'),
                  Files=[dict(Path=path, Sha256=digest) for path, digest in
                         {**policy['unchanged'], 'compose.yaml': policy['sourceCompose']}.items()])
    target = copy.deepcopy(source)
    target.update(Version='9999.0.0', Operator=dict(UpdateReceiptSchemas=[1, 2], RestoreReceiptSchemas=[1, 2]))
    target['Images']['CaddyDigest'] = policy['targetCaddy']
    next(item for item in target['Files'] if item['Path'] == 'compose.yaml')['Sha256'] = policy['targetCompose']
    return source, policy['sources'][platform], target


@pytest.mark.parametrize('platform', image.PLATFORMS)
def test_only_the_exact_forward_template_pair_is_admitted(platform):
    """The image/comment change passes; every unrelated hash and reverse/source substitution fails."""
    source, fingerprint, target = pair(platform)
    assert public_bundle.boundary(source, fingerprint, target)['Fingerprint'] == fingerprint
    for file in target['Files']:
        changed = copy.deepcopy(target)
        next(item for item in changed['Files'] if item['Path'] == file['Path'])['Sha256'] = 'f' * 64
        with pytest.raises(version.ValidationError):
            public_bundle.boundary(source, fingerprint, changed)
    for before, authority, after in [(target, fingerprint, source), (source, 'f' * 64, target)]:
        with pytest.raises(version.ValidationError):
            public_bundle.boundary(before, authority, after)


@pytest.mark.parametrize('command', ['publish', 'index'])
@pytest.mark.parametrize('failure', ['unsupported predecessor', 'unqualified transition'])
def test_compatibility_failure_precedes_every_publication_write(monkeypatch, tmp_path, command, failure):
    """Call the real publication entry points and reject before build/tag/push/index creation."""
    def refuse(release):
        raise version.ValidationError(failure)
    monkeypatch.setattr(caddy, 'preflight', refuse)
    monkeypatch.setattr(image, 'run', lambda *args: pytest.fail('publication operation before compatibility'))
    monkeypatch.setattr(image, 'require_absent', lambda *args: pytest.fail('publication preparation before compatibility'))
    monkeypatch.setattr(image, 'build', lambda *args: pytest.fail('build before compatibility'))
    with pytest.raises(version.ValidationError, match=failure):
        if command == 'publish':
            image.publish(dict(tag='v9999.0.0'), tmp_path / 'receipt')
        else:
            image.publish_index(dict(tag='v9999.0.0'), tmp_path, tmp_path / 'receipt')


def test_missing_native_qualification_and_mismatched_owner_refuse(tmp_path):
    """A generic green job or single architecture cannot qualify replacement publication."""
    with pytest.raises(version.ValidationError, match='native AMD64 and ARM64'):
        caddy.require_qualification(tmp_path, 'scope', 'head')
    for platform in image.PLATFORMS:
        (tmp_path / f'caddy-replacement-{platform.split("/")[1]}.json').write_text(json.dumps(dict(
            result='passed', platform=platform, sourceRevision='head', scopeSha256='scope',
            sourceFingerprint=caddy.POLICY['sources'][platform], sourceCaddy=caddy.POLICY['sourceCaddy'],
            targetCaddy=caddy.POLICY['targetCaddy'], targetOwner='new-owner', recoveredOwner='old-owner',
            tlsStateReadable=['2.11.4', '2.11.7'])))
    with pytest.raises(version.ValidationError, match='receipt authority'):
        caddy.require_qualification(tmp_path, 'scope', 'head')


@pytest.mark.parametrize('supported', [False, True])
def test_real_preflight_refuses_unsupported_or_unqualified_bridge_before_writes(monkeypatch, tmp_path, supported):
    """Exercise actual compatibility composition through publish, including genuine-template checks before qualification."""
    import bundle
    source, fingerprint, _ = pair()
    migrations = sorted(path.stem for path in (version.REPO_ROOT / 'Migrations').glob('*.cs')
                        if path.name[0].isdigit() and not path.name.endswith('.Designer.cs'))
    source['Application'].update(Migrations=migrations[:1], TerminalMigration=migrations[0])
    if not supported:
        source['Version'] = '1.9.21'
    monkeypatch.setattr(public_bundle, 'previous', lambda tag: 'v' + source['Version'])
    monkeypatch.setattr(caddy, 'source_bundle', lambda *args: (source, fingerprint, None))
    monkeypatch.setattr(bundle, 'accepted_database', lambda: dict(platforms=[
        dict(platform=platform, platformDigest=source['Images']['DatabaseDigest']) for platform in image.PLATFORMS]))
    def unqualified():
        raise version.ValidationError('unqualified transition')
    monkeypatch.setattr(caddy, 'qualification', unqualified)
    monkeypatch.setattr(image, 'build', lambda *args: pytest.fail('preflight reached image build'))
    monkeypatch.setattr(image, 'run', lambda *args: pytest.fail('preflight reached publication'))
    with pytest.raises(version.ValidationError, match='unqualified transition' if supported else 'unsupported Caddy replacement'):
        image.publish(dict(tag='v9999.0.0', version='9999.0.0'), tmp_path / 'receipt')
