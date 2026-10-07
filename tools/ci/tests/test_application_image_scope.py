"""Offline evidence-owner contracts, runnable with standard-library Python."""

import importlib.util
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / 'application_image_scope.py'
SPEC = importlib.util.spec_from_file_location('scope', SCRIPT)
scope = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(scope)

# These are execution prerequisites, not reverse trigger relationships.
SUBSTRATE = {'app_image', 'db_compose'}
OPERATOR = SUBSTRATE | {'operator'}
RECOVERY = OPERATOR | {'recovery'}
UPDATE = RECOVERY | {'update'}


class ApplicationImageScopeTests(unittest.TestCase):
    """Prove cheap unrelated diffs and the existing qualification owners."""

    def assert_domains(self, paths, expected):
        """Compare the complete selection so unexpected expensive lanes fail too."""
        result = scope.classify(paths)
        self.assertEqual({domain for domain, reasons in result.items() if reasons}, expected)

    def test_release_helper_only(self):
        """The #725 parser correction has no image, runtime or native ownership."""
        self.assert_domains([
            'tools/release/version.py', 'tools/release/tests/test_version.py',
            'docs/maintainer/versioning.md',
        ], {'release_tooling'})

    def test_docs_and_classifier_tests_only(self):
        """Ordinary documentation and classifier tests retain required cheap checks."""
        self.assert_domains([
            'README.md', 'docs/development/testing.md',
            'tools/ci/tests/test_application_image_scope.py',
        ], set())
        self.assert_domains([], set())

    def test_ordinary_778_style_application_change(self):
        """Ordinary API/service/model/test work selects only .NET product evidence."""
        self.assert_domains([
            'Areas/Api/Controllers/TripsController.cs',
            'Services/TripAuthorizationService.cs',
            'Models/TripSummary.cs',
            'tests/Wayfarer.Tests/Services/TripAuthorizationServiceTests.cs',
        ], {'dotnet'})

    def test_ordinary_backend_only(self):
        """Compilation owns unrelated C#, Razor and embedded-resource changes."""
        for path in ['Controllers/HomeController.cs', 'Services/GroupService.cs',
                     'Views/Home/Index.cshtml', 'tests/Wayfarer.Tests/GroupTests.cs',
                     'Resources/Example.resx']:
            with self.subTest(path=path):
                self.assert_domains([path], {'dotnet'})

    def test_ordinary_frontend_only(self):
        """Client behavior has no automatic .NET, browser-image or lifecycle lane."""
        for path in ['ClientApps/trip-editor/Editor.vue', 'wwwroot/js/site.js',
                     'wwwroot/css/site.css', 'tests/client/tripImportClient.test.mjs']:
            with self.subTest(path=path):
                self.assert_domains([path], {'frontend'})

    def test_cleanup_only(self):
        """Dedicated filesystem-safety paths bypass the generic client-test owner."""
        for path in ['tools/test-cleanup.mjs', 'tools/test-artifact-paths.mjs',
                     'tools/test-artifact-paths.ps1', 'tests/client/testCleanup.test.mjs',
                     'tools/coverage-report-paths.ps1', 'tools/coverage-report.ps1',
                     'tools/coverage-report.safety.tests.ps1',
                     'tools/test_artifact_paths.py', 'tools/browser_processes.py',
                     'tools/tests/test_test_artifact_paths.py', 'tools/tests/test_browser_processes.py',
                     'tools/tests/test_browser_e2e.py']:
            with self.subTest(path=path):
                self.assert_domains([path], {'cleanup_safety'})

    def test_browser_workflow(self):
        """Explicit ARM64 launch behavior owns both browser and native image proof."""
        self.assert_domains(['Services/BrowserWorkflow.cs'],
                            SUBSTRATE | {'dotnet', 'playwright', 'arm64'})

    def test_shared_frontend_and_cleanup_wiring(self):
        """Shared smoke/host wiring retains both frontend and filesystem-safety evidence."""
        for path in ['tools/trip-editor-asset-smoke.mjs', 'tools/browser_e2e.py', 'tools/browser-e2e.mjs']:
            with self.subTest(path=path):
                self.assert_domains([path], {'frontend', 'cleanup_safety'})
        self.assert_domains(['package.json'], {'frontend', 'cleanup_safety', 'app_image', 'arm64'})
        self.assert_domains(['vite.config.ts'], {'frontend', 'app_image'})

    def test_browser_migration_and_deleted_replacement_owners(self):
        """Migration unions remain sufficient; canonical ownership never depends on file existence."""
        self.assert_domains(['tools/start-shared-layout-e2e-host.ps1', 'tools/shared-layout-lifecycle.ps1',
                             'tools/shared-layout.safety.tests.ps1', 'tools/run-407-waypoint-browser.ps1',
                             'tools/browser_e2e.py', 'tools/tests/test_browser_e2e.py'],
                            {'frontend', 'cleanup_safety'})
        self.assert_domains(['tools/browser_e2e.py'], {'frontend', 'cleanup_safety'})
        self.assert_domains(['tools/test_artifact_paths.py'], {'cleanup_safety'})
        self.assert_domains(['tests/e2e/shared-layout/sharedLayoutConfig.ts'], {'frontend'})

    def test_playwright_owners(self):
        """Real rendering fixtures and their imported production owners select Chromium."""
        for path in ['Services/BrowserCapturePolicy.cs', 'Services/TripExportService.Pdf.cs',
                     'Services/TripMapThumbnailGenerator.cs', 'Util/RichNotes.cs',
                     'Util/HtmlHelpers.cs', 'Util/TileProviderAttribution.cs',
                     'Views/Trip/Print.cshtml', 'Views/Shared/_Layout.cshtml',
                     'tests/Wayfarer.Tests/Services/BrowserCaptureBoundaryTests.cs',
                     'tests/Wayfarer.Tests/Services/TripMapThumbnailGeneratorTests.cs',
                     'tests/Wayfarer.Tests/Services/PublishedReadOnlyRuntimeTests.cs',
                     'tests/Wayfarer.Tests/Views/TileAttributionLayoutRenderingTests.cs',
                     'tests/Wayfarer.Tests/Util/RichNotesTests.cs',
                     'tests/Wayfarer.Tests/Infrastructure/PlaywrightEnvironmentTestCollection.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], {'dotnet', 'playwright'})
        for path in ['wwwroot/js/embeddedMap.js', 'wwwroot/js/Trip/tripPopupBuilder.js',
                     'wwwroot/js/util/feature-metadata.js', 'wwwroot/js/Areas/User/Groups/Index.js']:
            with self.subTest(path=path):
                self.assert_domains([path], {'dotnet', 'playwright', 'frontend'})

    def test_docker_owners(self):
        """Application composition alone must not imply the lifecycle stack."""
        for path in ['Dockerfile', '.dockerignore']:
            with self.subTest(path=path):
                self.assert_domains([path], {'app_image', 'arm64'})
        self.assert_domains(['tools/release/image.py'], {'release_tooling', 'app_image', 'arm64'})

    def test_database_owners(self):
        """Native DB recipes need an application prerequisite, not recovery/update."""
        for path in ['deploy/compose/db/Dockerfile', 'deploy/compose/db/20-wayfarer.sh']:
            with self.subTest(path=path):
                self.assert_domains([path], SUBSTRATE | {'arm64'})
        for path in ['tools/release/db_image.py', 'tools/release/database-release.json']:
            with self.subTest(path=path):
                self.assert_domains([path], SUBSTRATE | {'release_tooling', 'arm64'})
        self.assert_domains(['deploy/compose/caddy/Caddyfile'], SUBSTRATE)

    def test_operator_only(self):
        """Ordinary operator work keeps exact-head substrate prerequisites, not lifecycle."""
        path = 'tools/WayfarerCtl/Diagnostics.cs'
        self.assert_domains([path], OPERATOR | {'dotnet'})
        reasons = scope.classify([path])
        self.assertIn('execution prerequisite for operator', reasons['db_compose'])
        self.assertIn('execution prerequisite for db_compose', reasons['app_image'])
        self.assertFalse(reasons['recovery'])
        self.assertFalse(reasons['update'])

    def test_uninstall_shared_lifecycle_owners(self):
        """Purge-tombstone consumption and Preserved reactivation own the joined journey."""
        for path in ['tools/WayfarerCtl/Setup.cs', 'tools/WayfarerCtl/DeploymentLifecycle.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], UPDATE | {'dotnet'})

    def test_recovery_owner(self):
        """An architecture-neutral recovery worker selects only its prerequisite chain."""
        self.assert_domains(['tools/WayfarerRecovery/RecoveryEngine.cs'], RECOVERY | {'dotnet'})
        self.assert_domains(['tools/WayfarerCtl/RestoreReceipt.cs'], RECOVERY | {'dotnet'})
        for path in ['tools/WayfarerCtl/BackupCommands.cs', 'tools/WayfarerCtl/RestoreCommands.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], RECOVERY | {'dotnet'})

    def test_managed_ingress_driver(self):
        """The mounted browser driver runs in AMD64 DB/Compose qualification, not .NET tests."""
        self.assert_domains(['tools/compose/managed_ingress.mjs'], SUBSTRATE)

    def test_browser_fixture_projects(self):
        """Architecture-neutral seed helpers cannot start an empty native qualification job."""
        for fixture in ['Wayfarer.LifecycleBrowserFixture', 'Wayfarer.WaypointBrowserFixture']:
            with self.subTest(fixture=fixture):
                self.assert_domains([f'tools/{fixture}/{fixture}.csproj'], {'dotnet'})

    def test_native_projects_have_concrete_qualification(self):
        """Shipped operator/recovery project inputs also select the native image prerequisites."""
        for project, expected in [('WayfarerCtl', OPERATOR), ('WayfarerRecovery', RECOVERY),
                                  ('WayfarerRecoverySource', RECOVERY)]:
            with self.subTest(project=project):
                self.assert_domains([f'tools/{project}/{project}.csproj'], expected | {'dotnet', 'arm64'})

    def test_update_and_migration_owners(self):
        """Forward migrations and update-to-restore handoff require both journeys."""
        for path in ['tools/WayfarerCtl/UpdateRuntime.cs', 'Migrations/Example.cs',
                     'Util/QuartzSnapshot.cs', 'CommandLine/RecoverySourceCli.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], UPDATE | {'dotnet'})
        self.assert_domains(['tools/compose/qualify_update.py'], UPDATE)

    def test_uninstall_owns_complete_lifecycle(self):
        """Every current or future uninstall source directly selects all three lifecycle owners."""
        for path in ['tools/WayfarerCtl/UninstallCommands.cs', 'tools/WayfarerCtl/UninstallPlan.cs',
                     'tools/WayfarerCtl/UninstallFuture.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], UPDATE | {'dotnet'})
                reasons = scope.classify([path])
                for domain in ['operator', 'recovery', 'update']:
                    self.assertIn(f'owner: {path!r}', reasons[domain])
        self.assert_domains(['docs/self-hosting/operations.md',
                             'docs/self-hosting/wayfarerctl.md'], set())

    def test_bundle_and_shared_release_owners(self):
        """Shared manifest and platform-qualified bundles reach their lifecycle consumers."""
        for path in ['tools/release/bundle.py', 'tools/release/public_bundle.py']:
            with self.subTest(path=path):
                self.assert_domains([path], UPDATE | {'release_tooling', 'arm64'})
        self.assert_domains(['tools/WayfarerCtl/ReleaseManifest.cs'],
                            UPDATE | {'dotnet', 'release_tooling', 'arm64'})

    def test_native_owners(self):
        """Platform selection and native filesystem ABI changes receive ARM evidence."""
        for path in ['tools/WayfarerRecovery/NativePlatform.cs',
                     'tools/WayfarerRecovery/SafeDirectory.cs',
                     'tools/WayfarerRecovery/RecoveryLock.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], RECOVERY | {'dotnet', 'arm64'})
        self.assert_domains(['tools/WayfarerCtl/Preflight.cs'], UPDATE | {'dotnet', 'arm64'})

    def test_release_version_identity(self):
        """A future release bump proves only its tooling, compiled version and image identity."""
        self.assert_domains(['Version.props', 'CHANGELOG.md', 'docs/maintainer/versioning.md',
                             'tests/Wayfarer.Tests/Services/AppVersionProviderTests.cs'],
                            {'release_tooling', 'dotnet', 'app_image'})

    def test_gate_self_change(self):
        """Workflow/classifier changes must exercise every expensive domain once."""
        for path in ['.github/workflows/tests.yml', 'tools/ci/application_image_scope.py',
                     '.github/workflows/application-release.yml', '.github/actions/example/action.yml']:
            with self.subTest(path=path):
                self.assert_domains([path], set(scope.DOMAINS))

    def test_mixed_diff_and_deterministic_reasons(self):
        """Mixed ownership is a union; ordering and duplicate paths cannot change evidence."""
        paths = ['tools/release/version.py', 'Services/GroupService.cs', 'Dockerfile',
                 'tools/test-cleanup.mjs', 'wwwroot/js/site.js']
        self.assert_domains(paths, {'release_tooling', 'dotnet', 'app_image',
                                    'arm64', 'cleanup_safety', 'frontend'})
        self.assertEqual(scope.classify(paths), scope.classify([*reversed(paths), paths[0]]))

    def test_unavailable_or_invalid_diff(self):
        """Malformed inputs, missing Git objects and failed subprocesses fail broad."""
        expected = {domain: ['diff unavailable or invalid; broad qualification required']
                    for domain in scope.DOMAINS}
        for failure in [OSError(), subprocess.CalledProcessError(128, 'git'), ValueError()]:
            with self.subTest(failure=type(failure).__name__):
                with patch.object(scope, 'changed_paths', side_effect=failure):
                    self.assertEqual(scope.decision('', ''), expected)
        self.assertEqual(scope.decision('invalid', 'invalid'), expected)
        self.assertEqual(scope.decision('0' * 40, '0' * 40), expected)
        with patch.object(scope.subprocess, 'run', return_value=subprocess.CompletedProcess(
                'git', 0, stdout=b'Dockerfile')):
            self.assertEqual(scope.decision('a' * 40, 'b' * 40), expected)

    def test_invalid_path_streams_fail_broad(self):
        """Reject aliases and malformed entries before ownership matching can miss a contract."""
        for data in [b'\0', b'.\0', b'..\0', b'./Dockerfile\0', b'../Dockerfile\0',
                     b'deploy/../Dockerfile\0', b'deploy/./compose.yml\0', b'/Dockerfile\0',
                     b'//server/Dockerfile\0', b'deploy//compose.yml\0', b'deploy/compose/\0',
                     b'C:/Dockerfile\0', b'C:Dockerfile\0', b'C:\\Dockerfile\0',
                     b'deploy\\compose.yml\0', b'Dockerfile\0\0', b'README.md\0./Dockerfile\0']:
            with self.subTest(data=data), patch.object(scope.subprocess, 'run',
                    return_value=subprocess.CompletedProcess('git', 0, stdout=data)):
                with self.assertRaises(ValueError):
                    scope.changed_paths('a' * 40, 'b' * 40)
                result = scope.decision('a' * 40, 'b' * 40)
                self.assertEqual({domain for domain, reasons in result.items() if reasons}, set(scope.DOMAINS))

    def test_canonical_path_streams(self):
        """Empty diffs, hidden directories and literal Git filenames keep their exact identity."""
        for paths in [[], ['Dockerfile'], ['.github/workflows/tests.yml'],
                      ['ordinary file\nname.txt', 'docs/\u03b1.md', 'tabs\there.txt', '\udcff.cs']]:
            data = ''.join(path + '\0' for path in paths).encode('utf-8', errors='surrogateescape')
            with self.subTest(paths=paths), patch.object(scope.subprocess, 'run',
                    return_value=subprocess.CompletedProcess('git', 0, stdout=data)):
                self.assertEqual(scope.changed_paths('a' * 40, 'b' * 40), paths)
                self.assertEqual(scope.decision('a' * 40, 'b' * 40), scope.classify(paths))

    def test_cli_boolean_outputs_and_skip_reasons(self):
        """Workflow outputs are bounded booleans; logs explain every run and skip."""
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'output'
            with patch.object(scope, 'decision', return_value=scope.classify(['tools/release/version.py'])), \
                    patch('sys.argv', [str(SCRIPT), '--base', 'a' * 40, '--head', 'b' * 40]), \
                    patch.dict(os.environ, {'GITHUB_OUTPUT': str(output)}), \
                    patch('builtins.print') as printed:
                scope.main()
            self.assertEqual(output.read_text().splitlines(), [
                f'run_{domain}={str(domain == "release_tooling").lower()}' for domain in scope.DOMAINS])
            lines = '\n'.join(str(call.args[0]) for call in printed.call_args_list)
            self.assertIn('RUN release_tooling:', lines)
            self.assertIn('SKIP app_image:', lines)

    def test_real_git_rename_delete_and_pr_merge_base(self):
        """NUL-safe old/new paths survive renames, deletions and independent base advancement."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args):
                """Run isolated commits without relying on the developer's Git identity."""
                return subprocess.check_output(
                    ['git', '-C', directory, '-c', 'user.name=Scope Test',
                     '-c', 'user.email=scope@example.invalid', *args], text=True).strip()

            git('init', '-q')
            (root / 'Dockerfile').write_text('original')
            git('add', '.')
            git('commit', '-qm', 'base')
            base = git('rev-parse', 'HEAD')
            git('mv', 'Dockerfile', 'ordinary file\nname.txt')
            git('commit', '-qm', 'rename')
            renamed = git('rev-parse', 'HEAD')
            git('checkout', '-qb', 'base-advance', base)
            (root / 'Version.props').write_text('base-only')
            git('add', '.')
            git('commit', '-qm', 'advance')
            advanced = git('rev-parse', 'HEAD')
            original = scope.subprocess.run
            with patch.object(scope.subprocess, 'run', side_effect=lambda *a, **k: original(*a, cwd=directory, **k)):
                paths = scope.changed_paths(advanced, renamed)
                self.assertEqual(set(paths), {'Dockerfile', 'ordinary file\nname.txt'})
                self.assertEqual(scope.decision(advanced, renamed), scope.classify(paths))
                git('checkout', '-q', base)
                git('rm', 'Dockerfile')
                git('commit', '-qm', 'delete')
                self.assertEqual(scope.decision(base, git('rev-parse', 'HEAD')),
                                 scope.classify(['Dockerfile']))
                git('checkout', '-q', base)
                (root / 'README.md').write_text('docs only')
                git('add', '.')
                git('commit', '-qm', 'docs')
                self.assertFalse(any(scope.decision(advanced, git('rev-parse', 'HEAD')).values()))


class CiGateTests(unittest.TestCase):
    """Execute the workflow's real Bash gate with bounded synthetic classifier outputs."""

    def setUp(self):
        """Extract the verification step and its selector bindings without a YAML dependency."""
        workflow = (SCRIPT.parents[2] / '.github/workflows/tests.yml').read_text()
        step = workflow.split('      - name: Verify selected exact-head evidence\n', 1)[1]
        step = step.split('\n      - name:', 1)[0]
        bindings = dict(re.findall(r'^          (\w+): \$\{\{ needs\.classify\.outputs\.(run_\w+) }}$', step, re.M))
        self.assertEqual(set(bindings.values()), {f'run_{domain}' for domain in scope.DOMAINS})
        self.selectors = tuple(bindings)
        self.script = textwrap.dedent(step.split('        run: |\n', 1)[1])
        self.env = dict(os.environ, EXPECTED_HEAD='a' * 40, CLASSIFIED_HEAD='a' * 40,
                        CLASSIFY_RESULT='success', TEST_RESULT='success', CLEANUP_RESULT='skipped',
                        IMAGE_RESULT='skipped', ARM_RESULT='skipped')
        self.env.update({selector: 'false' for selector in self.selectors})

    def test_complete_boolean_outputs(self):
        """Both the cheap path and fully selected successful evidence must pass."""
        for selected, result in [('false', 'skipped'), ('true', 'success')]:
            values = {**self.env, **{selector: selected for selector in self.selectors},
                      'CLEANUP_RESULT': result, 'IMAGE_RESULT': result, 'ARM_RESULT': result}
            with self.subTest(selected=selected):
                gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
                self.assertEqual(gate.returncode, 0, gate.stderr)

    def test_missing_blank_or_malformed_outputs(self):
        """Every expected selector must be present and exactly lowercase true or false."""
        for selector in self.selectors:
            for value in [None, '', 'TRUE', '0', 'false ', 'true\nfalse']:
                values = dict(self.env)
                if value is None:
                    values.pop(selector)
                else:
                    values[selector] = value
                with self.subTest(selector=selector, value=value):
                    gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
                    self.assertNotEqual(gate.returncode, 0)

    def test_contradictory_amd64_prerequisites_fail(self):
        """Contradictions fail before job results, even when the owning runner succeeded."""
        cases = [
            (('DB_COMPOSE_SELECTED',), 'DB Compose'),
            (('OPERATOR_SELECTED',), 'Operator'),
            (('RECOVERY_SELECTED',), 'Recovery'),
            (('UPDATE_SELECTED',), 'Update'),
            (('OPERATOR_SELECTED', 'IMAGE_SELECTED'), 'Operator'),
            (('RECOVERY_SELECTED', 'DB_COMPOSE_SELECTED', 'IMAGE_SELECTED'), 'Recovery'),
            (('UPDATE_SELECTED', 'OPERATOR_SELECTED', 'DB_COMPOSE_SELECTED', 'IMAGE_SELECTED'), 'Update'),
        ]
        for selected, consumer in cases:
            for result in ['skipped', 'success']:
                values = {**self.env, **{selector: 'true' for selector in selected}, 'IMAGE_RESULT': result}
                with self.subTest(selected=selected, result=result):
                    gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
                    self.assertNotEqual(gate.returncode, 0)
                    self.assertIn(f'{consumer} selection requires', gate.stderr)

    def test_valid_amd64_chains_require_successful_owning_runner(self):
        """Every valid image/lifecycle chain passes only with successful AMD64 evidence."""
        chain = ('IMAGE_SELECTED', 'DB_COMPOSE_SELECTED', 'OPERATOR_SELECTED', 'RECOVERY_SELECTED', 'UPDATE_SELECTED')
        for length in range(1, len(chain) + 1):
            selected = chain[:length]
            for result in ['success', 'skipped', 'failure', 'cancelled']:
                values = {**self.env, **{selector: 'true' for selector in selected}, 'IMAGE_RESULT': result}
                with self.subTest(selected=selected, result=result):
                    gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
                    if result == 'success':
                        self.assertEqual(gate.returncode, 0, gate.stderr)
                    else:
                        self.assertNotEqual(gate.returncode, 0)
                        self.assertIn("Selected evidence 'application-image'", gate.stderr)

    def test_release_tooling_only_requires_successful_owning_runner(self):
        """Release tooling keeps its AMD64 runner without selecting image/lifecycle prerequisites."""
        for result in ['success', 'skipped', 'failure', 'cancelled']:
            values = {**self.env, 'RELEASE_SELECTED': 'true', 'IMAGE_RESULT': result}
            with self.subTest(result=result):
                gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
                self.assertEqual(gate.returncode == 0, result == 'success', gate.stderr)

    def test_ordinary_dotnet_path_skips_image_and_lifecycle(self):
        """An ordinary product change passes with only .NET selected and the AMD64 runner skipped."""
        values = {**self.env, 'DOTNET_SELECTED': 'true'}
        gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
        self.assertEqual(gate.returncode, 0, gate.stderr)

    def test_hollow_arm64_selection_fails(self):
        """An ARM job cannot certify native evidence when its qualification steps were unselected."""
        values = {**self.env, 'ARM_SELECTED': 'true', 'ARM_RESULT': 'success'}
        gate = subprocess.run(['bash', '-c', self.script], env=values, capture_output=True, text=True)
        self.assertNotEqual(gate.returncode, 0)


if __name__ == '__main__':
    unittest.main()
