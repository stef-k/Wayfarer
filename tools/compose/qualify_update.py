"""One real update boundary and bounded fault observations in the established recovery fixture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def qualify_update(journey):
    """Candidate-only operator build, exact source boundary, lost acknowledgement, private failure and restore handoff."""
    journey.ctl('backup', '--quiesced')
    journey.ctl('release', 'adopt', str(journey.release_bundle))
    original = configuration(journey)
    # Child-process fault injection reuses the fixture's existing Docker boundary.
    wrapper = journey.host('cat', str(journey.directory / 'docker-test')).stdout
    injection = '''case "$point:$*" in
  update-failure:*" database migrate")
    remaining=$#
    while test "$remaining" -gt 0; do
      value=$1; shift
      case "$value" in --env=Database__PasswordFile=*) value=--env=Database__PasswordFile=/missing-credential ;; esac
      set -- "$@" "$value"
      remaining=$((remaining-1))
    done ;;
  update-ack:*" start "*)
    for helper; do :; done
    operation=$(/usr/bin/docker inspect --format '{{index .Config.Labels "wayfarer.update"}}' "$helper" 2>/dev/null)
    if test -n "$operation"; then
      /usr/bin/docker "$@" || exit $?
      /usr/bin/docker wait "$helper" >/dev/null
      kill -KILL "$PPID"
      exit 137
    fi ;;
esac
'''
    journey.host('tee', str(journey.directory / 'docker-test'), data=wrapper.replace('exec /usr/bin/docker "$@"', injection + 'exec /usr/bin/docker "$@"'))
    migration_failure(journey)
    plan, digest = plan_update(journey)
    journey.host('tee', str(journey.directory / 'failure'), data='update-ack')
    failure = journey.ctl('update', '--accept-plan', digest, check=False)
    assert failure.returncode != 0, failure.stderr
    receipt = update_receipt(journey)
    assert receipt['Phase'] == 3 and receipt['RecoveryArchive'] and receipt['RecoverySha256'], failure.stderr
    helper = receipt['MigrationContainer']
    started = journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', helper).stdout
    assert journey.ctl('start', check=False).returncode != 0
    assert journey.ctl('update', '--abort', plan['Operation'], check=False).returncode != 0
    journey.host('tee', str(journey.directory / 'failure'), data='doctor')
    assert journey.ctl('update', '--resume', plan['Operation'], check=False).returncode == 1
    assert update_receipt(journey)['Phase'] == 7
    assert journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', helper).stdout == started
    ports = json.loads(journey.host('docker', 'inspect', '--format', '{{json .HostConfig.PortBindings}}',
                                   journey.project + '-wayfarer-1').stdout)
    assert not ports
    journey.host('tee', str(journey.directory / 'failure'), data='')
    foreign_consumer_refusal(journey, plan)
    journey.ctl('update', '--restore', plan['Operation'])
    restored = configuration(journey)
    assert restored['Release'] == original['Release']
    assert restored['StorageGeneration'] != original.get('StorageGeneration')
    assert update_receipt(journey)['RestoreAccepted']
    journey.probe_command('verify')
    print('PASS real migration lost acknowledgement without rerun, private postflight failure, external ingress fence and durable old-release restore handoff', flush=True)
    # Forward completion retains this active generation; subsequent backup restores the target release through #695.
    plan, digest = plan_update(journey)
    journey.ctl('update', '--accept-plan', digest)
    accepted = update_receipt(journey)
    assert accepted['Phase'] == 9
    target = configuration(journey)
    assert target['StorageGeneration'] == restored['StorageGeneration']
    assert target['Release'] != original['Release']
    assert target['Backup']['Source']['ApplicationImage'].endswith(target['AppDigest'])
    journey.probe_command('verify')
    journey.ctl('backup', '--quiesced')
    restore = journey.ctl('restore', '--plan', '--without-emergency-backup').stdout
    digest = restore.split('Plan SHA-256: ')[1].splitlines()[0]
    journey.ctl('restore', '--accept-plan', digest, '--trust-controlled-backup')
    journey.probe_command('verify')
    assert configuration(journey)['Release'] == target['Release']
    assert journey.compose('exec', '-T', 'wayfarer', 'cat',
        '/var/lib/wayfarer/uploads/imports/recovery-qualification').strip() == 'durable'
    print('PASS same-generation forward activation, secure data continuity, target backup binding and post-update managed restore', flush=True)


def configuration(journey):
    """Read protected installation evidence through the fixture's existing host adapter."""
    return json.loads(journey.host('cat', str(journey.install / 'installation.json')).stdout)


def update_receipt(journey):
    """Observe product-owned phase and held evidence, never fabricate authorization."""
    return json.loads(journey.host('cat', str(journey.install / 'recovery-control/update.json')).stdout)


def plan_update(journey):
    """Authorize only the exact canonical hash printed by the published operator."""
    output = journey.ctl('update', '--bundle', str(journey.update_bundle), '--plan').stdout
    return json.loads(output.splitlines()[0]), output.split('Plan SHA-256: ')[1].splitlines()[0]


def migration_failure(journey):
    """A real nonzero target helper leaves source history unchanged and cannot be retried or accepted."""
    plan, digest = plan_update(journey)
    journey.host('tee', str(journey.directory / 'failure'), data='update-failure')
    failure = journey.ctl('update', '--accept-plan', digest, check=False)
    assert failure.returncode == 1, failure.stderr
    receipt = update_receipt(journey)
    assert receipt['Phase'] == 3 and receipt['MigrationExit'] != 0, (receipt['Phase'], failure.stderr)
    history = journey.compose('exec', '-T', 'db', 'psql', '-U', 'postgres', '-d', 'wayfarer', '-At', '-c',
        'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";').splitlines()
    assert history == plan['Current']['Backup']['Source']['ExpectedMigrations']
    started = journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', receipt['MigrationContainer']).stdout
    journey.host('tee', str(journey.directory / 'failure'), data='')
    # A stopped DB represents daemon-loss resource state; resume must reconcile that exact DB, without relaunching migration.
    journey.host('docker', 'stop', journey.project + '-db-1')
    assert journey.ctl('update', '--resume', plan['Operation'], check=False).returncode == 1
    assert update_receipt(journey)['Phase'] == 3
    assert journey.host('docker', 'inspect', '--format', '{{.State.StartedAt}}', receipt['MigrationContainer']).stdout == started
    journey.ctl('update', '--restore', plan['Operation'])
    print('PASS real nonzero migration, unchanged source history, exact stopped-DB reconciliation and no blind retry', flush=True)


def foreign_consumer_refusal(journey, plan):
    """A consumer introduced after the update fence cannot inherit restore ownership."""
    mounts = json.loads(journey.host('docker', 'inspect', '--format', '{{json .Mounts}}',
                                   journey.project + '-wayfarer-1').stdout)
    volume = next(mount['Name'] for mount in mounts if mount['Destination'] == '/var/lib/wayfarer')
    name = journey.project + '-foreign-update-consumer'
    before = update_receipt(journey)
    restore_path = str(journey.install / 'recovery-control/restore.json')
    previous = journey.host('cat', restore_path, check=False)
    journey.host('docker', 'run', '-d', '--name', name, '--pull=never', '--restart=no',
                 '--network', journey.project + '_backend', '--mount', 'type=volume,src=' + volume + ',dst=/state',
                 '--entrypoint', 'sleep', 'ghcr.io/stef-k/wayfarer@' + plan['Target']['AppDigest'], 'infinity')
    try:
        refused = journey.ctl('update', '--restore', plan['Operation'], check=False)
        assert refused.returncode == 1 and 'Foreign durable-state consumer' in refused.stderr, refused.stderr
        after = update_receipt(journey)
        assert after['RestoreOperation'] == before['RestoreOperation'] and after['Phase'] == before['Phase']
        current = journey.host('cat', restore_path, check=False)
        assert (current.returncode, current.stdout) == (previous.returncode, previous.stdout)
        assert journey.host('docker', 'inspect', '--format', '{{.State.Running}}', name).stdout.strip() == 'true'
    finally:
        journey.host('docker', 'rm', '-f', name)
    print('PASS foreign durable-state consumer refuses restore ownership transfer until removed', flush=True)


def prepare_candidates(source_bundle, output):
    """Build a disposable real migration from this exact head; no product migration or stable tag."""
    repo = Path(__file__).resolve().parents[2]
    source_bundle, output = source_bundle.resolve(), output.resolve()
    def run(*args, cwd=repo, env=None):
        """Keep recipe subprocess failures visible and retain their exact output."""
        return subprocess.run(args, cwd=cwd, env=env, check=True, text=True, stdout=subprocess.PIPE).stdout.strip()
    if run('git', 'status', '--porcelain', '--untracked-files=normal'):
        raise RuntimeError('candidate recipe requires a clean committed exact head')
    head = run('git', 'rev-parse', 'HEAD')
    source_manifest = json.loads((source_bundle / 'release.json').read_text())
    if source_manifest['SourceRevision'] != head or source_manifest['Status'] != 'candidate':
        raise RuntimeError('source must be a candidate assembled from this exact head')
    output.mkdir(parents=True, exist_ok=False)
    source = output / 'source'
    shutil.copytree(source_bundle, source)
    with tempfile.TemporaryDirectory(prefix='wayfarer-update-target-') as temporary:
        checkout = Path(temporary) / 'checkout'
        run('git', 'clone', '--shared', '--no-checkout', str(repo), str(checkout))
        run('git', 'checkout', '--detach', head, cwd=checkout)
        props = checkout / 'Version.props'
        major, minor, patch = map(int, source_manifest['Version'].split('.'))
        target_version = f'{major}.{minor}.{patch + 1}'
        props.write_text(props.read_text().replace(f'>{source_manifest["Version"]}<', f'>{target_version}<'))
        migration = '20990101000000_UpdateQualification'
        (checkout / 'Migrations' / (migration + '.cs')).write_text('''using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wayfarer.Models;

namespace Wayfarer.Migrations;

/// <summary>Disposable qualification only: exercise real transactional DDL across the update boundary.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20990101000000_UpdateQualification")]
public sealed class UpdateQualification : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE update_qualification (id integer PRIMARY KEY);");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE update_qualification;");
}
''')
        run('git', 'add', 'Version.props', 'Migrations/' + migration + '.cs', cwd=checkout)
        # Fixed author/committer identity and parent timestamp make the fixture commit reproducible.
        stamp = run('git', 'show', '-s', '--format=%cI', head)
        env = dict(os.environ, GIT_AUTHOR_DATE=stamp, GIT_COMMITTER_DATE=stamp,
                   GIT_AUTHOR_NAME='Wayfarer qualification', GIT_COMMITTER_NAME='Wayfarer qualification',
                   GIT_AUTHOR_EMAIL='qualification@example.invalid', GIT_COMMITTER_EMAIL='qualification@example.invalid')
        run('git', '-c', 'commit.gpgsign=false', 'commit', '-m', 'Disposable update migration qualification', cwd=checkout, env=env)
        target_head = run('git', 'rev-parse', 'HEAD', cwd=checkout)
        # Reuse existing image and bundle owners, with a candidate-only local image tag.
        build = "import sys; sys.path.insert(0, 'tools/release'); import image; r=image.identity(None,None); r['tag']='candidate-update-'+r['sourceRevision']; ref=image.build(r); print(image.inspect_image(r,ref)['RepoDigests'][0].split('@')[1])"
        digest = run('python3', '-B', '-c', build, cwd=checkout).splitlines()[-1]
        run('python3', '-B', 'tools/release/bundle.py', '--app-digest', digest, '--output', str(output / 'target'), cwd=checkout)
        target = next((output / 'target').glob('candidate-*'))
        operator = Path(temporary) / 'operator'
        run('dotnet', 'publish', 'tools/WayfarerCtl', '-c', 'Release', '-r', 'linux-x64',
            '--self-contained', 'true', '-p:DefineConstants=UPDATE_QUALIFICATION', '-o', str(operator))
        for bundle in (source, target):
            executable = bundle / 'wayfarerctl'
            executable.chmod(0o755)
            shutil.copyfile(operator / 'wayfarerctl', executable)
            executable.chmod(0o555)
            manifest = json.loads((bundle / 'release.json').read_text())
            next(entry for entry in manifest['Files'] if entry['Path'] == 'wayfarerctl')['Sha256'] = hashlib.sha256(executable.read_bytes()).hexdigest()
            if bundle == target:
                inspected = json.loads(run(str(source / 'wayfarerctl'), 'release', 'inspect', str(source)))
                manifest['Sources'] = [dict(Version=source_manifest['Version'], Fingerprint=inspected['Fingerprint'],
                    TerminalMigration=source_manifest['Application']['TerminalMigration'], ExactOrderedPrefix=True,
                    ReferenceSeeding=False, RetryRestriction='manual-recovery', Warning='Disposable qualification only')]
                assert manifest['Application']['Migrations'] == source_manifest['Application']['Migrations'] + [migration]
            (bundle / 'release.json').write_text(json.dumps(manifest, sort_keys=True, separators=(',', ':')) + '\n')
            run(str(executable), 'release', 'inspect', str(bundle))
        # Replace the assembly archive after candidate-only operator/source-boundary changes.
        for archive in (output / 'target').glob('*.tar.gz'):
            archive.unlink()
        (output / 'target' / 'SHA256SUMS').unlink()
        run('python3', '-B', '-c', "import sys; from pathlib import Path; sys.path.insert(0,'tools/release'); import bundle; bundle.archive(Path(sys.argv[1]),Path(sys.argv[2]))", str(target), str(output / 'target'))
        evidence = dict(sourceHead=head, targetHead=target_head, migration=migration, applicationDigest=digest,
                        sourceBundle=str(source), targetBundle=str(target))
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
        print(json.dumps(evidence), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Prepare disposable update candidates for qualify_recovery.py --update-bundle.')
    parser.add_argument('--source-bundle', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    prepare_candidates(args.source_bundle, args.output)
