"""Offline evidence-owner contracts, runnable with standard-library Python."""

import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
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
            'docs/23-Versioning.md',
        ], {'release_tooling'})

    def test_docs_and_classifier_tests_only(self):
        """Ordinary documentation and classifier tests retain required cheap checks."""
        self.assert_domains([
            'README.md', 'docs/22-Testing.md',
            'tools/ci/tests/test_application_image_scope.py',
        ], set())
        self.assert_domains([], set())

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
                     'tools/shared-layout-lifecycle.ps1', 'tools/shared-layout.safety.tests.ps1']:
            with self.subTest(path=path):
                self.assert_domains([path], {'cleanup_safety'})

    def test_browser_workflow(self):
        """Explicit ARM64 launch behavior owns both browser and native image proof."""
        self.assert_domains(['Services/BrowserWorkflow.cs'],
                            {'dotnet', 'playwright', 'app_image', 'arm64'})

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
        """Setup selects the operator journey without implying backup or update."""
        self.assert_domains(['tools/WayfarerCtl/Setup.cs'], OPERATOR | {'dotnet'})

    def test_recovery_owner(self):
        """An architecture-neutral recovery worker selects only its prerequisite chain."""
        self.assert_domains(['tools/WayfarerRecovery/RecoveryEngine.cs'], RECOVERY | {'dotnet'})
        self.assert_domains(['tools/WayfarerCtl/RestoreReceipt.cs'], RECOVERY | {'dotnet'})

    def test_update_and_migration_owners(self):
        """Forward migrations and update-to-restore handoff require both journeys."""
        for path in ['tools/WayfarerCtl/UpdateRuntime.cs', 'Migrations/Example.cs',
                     'Util/QuartzSnapshot.cs', 'CommandLine/RecoverySourceCli.cs']:
            with self.subTest(path=path):
                self.assert_domains([path], UPDATE | {'dotnet'})
        self.assert_domains(['tools/compose/qualify_update.py'], UPDATE)

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
        self.assert_domains(['Version.props', 'CHANGELOG.md', 'docs/23-Versioning.md',
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
        for failure in [OSError(), subprocess.CalledProcessError(128, 'git'), ValueError()]:
            with self.subTest(failure=type(failure).__name__):
                with patch.object(scope, 'changed_paths', side_effect=failure):
                    self.assertTrue(all(scope.decision('', '').values()))
        self.assertTrue(all(scope.decision('invalid', 'invalid').values()))
        self.assertTrue(all(scope.decision('0' * 40, '0' * 40).values()))

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


if __name__ == '__main__':
    unittest.main()
