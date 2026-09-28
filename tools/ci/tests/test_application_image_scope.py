"""Focused scope contract tests, runnable with the runner's standard-library Python."""

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / 'application_image_scope.py'
SPEC = importlib.util.spec_from_file_location('scope', SCRIPT)
scope = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(scope)


class ApplicationImageScopeTests(unittest.TestCase):
    """Keep unrelated product work cheap while preserving lifecycle qualification."""

    def test_sensitive_paths(self):
        """Represent each independent delivery and application authority boundary."""
        paths = [
            'Dockerfile', '.dockerignore', 'Wayfarer.csproj', 'Version.props',
            '.github/workflows/application-release.yml', '.github/workflows/tests.yml',
            '.github/workflows/database-image.yml', 'tools/release/image.py',
            'tools/release/tests/fixture.json', 'deploy/compose/compose.yaml',
            'tools/WayfarerCtl/Setup.cs', 'tools/WayfarerRecovery/Program.cs',
            'tools/WayfarerRecoverySource/Program.cs', 'tools/compose/qualify.py',
            'CommandLine/RecoverySourceCli.cs', 'Util/QuartzSchemaInstaller.cs',
            'Util/QuartzSnapshot.cs', 'Services/ApplicationReadiness.cs',
            'Services/LocationProviders/DataProtectionAuthority.cs',
            'Services/StoragePaths.cs', 'Services/DatabaseSecret.cs',
            'Migrations/Example.cs', 'Scripts/tables_postgres.sql',
            'Models/ApplicationDbContext.LocationProviders.cs',
            'Models/LocationProviders/PersonalLocationProviderProfile.cs',
            'tools/ci/application_image_scope.py', 'Directory.Build.targets',
            'package-lock.json', 'appsettings.Production.json',
        ]
        for path in paths:
            with self.subTest(path=path):
                self.assertTrue(scope.sensitive(path))

    def test_unrelated_paths(self):
        """Classifier tests alone, docs, MVC, UI and ordinary tests remain cheap."""
        for path in [
            'Controllers/HomeController.cs', 'Services/GroupService.cs',
            'Views/Home/Index.cshtml', 'wwwroot/css/site.css', 'wwwroot/js/site.js',
            'ClientApps/trip-editor/Editor.vue', 'tests/Wayfarer.Tests/GroupTests.cs',
            'docs/22-Testing.md', 'README.md', 'tools/maintenance.py',
            'tools/ci/tests/test_application_image_scope.py',
        ]:
            with self.subTest(path=path):
                self.assertFalse(scope.sensitive(path))

    def test_mixed_diff_and_unavailable_evidence(self):
        """One sensitive path or an unreadable diff must select the full path."""
        with patch.object(scope, 'changed_paths', return_value=['README.md', 'Dockerfile']):
            self.assertTrue(scope.decision('', '')[0])
        with patch.object(scope, 'changed_paths', side_effect=OSError):
            self.assertTrue(scope.decision('', '')[0])
        self.assertTrue(scope.decision('invalid', 'invalid')[0])

    def test_real_git_rename_delete_and_pr_merge_base(self):
        """Exercise actual NUL parsing and PR ancestry without a GitHub API or Docker."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args):
                """Run isolated commits without depending on developer Git identity."""
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
            # Advance the base independently: its sensitive-only change is not the PR diff.
            git('checkout', '-qb', 'base-advance', base)
            (root / 'Version.props').write_text('base-only')
            git('add', '.')
            git('commit', '-qm', 'advance')
            advanced = git('rev-parse', 'HEAD')
            # subprocess cwd is explicit so the test never changes process-global cwd.
            original = scope.subprocess.run
            with patch.object(scope.subprocess, 'run', side_effect=lambda *a, **k: original(*a, cwd=directory, **k)):
                paths = scope.changed_paths(advanced, renamed)
                self.assertEqual(set(paths), {'Dockerfile', 'ordinary file\nname.txt'})
                self.assertTrue(scope.decision(advanced, renamed)[0])
                git('checkout', '-q', base)
                git('rm', 'Dockerfile')
                git('commit', '-qm', 'delete')
                self.assertTrue(scope.decision(base, git('rev-parse', 'HEAD'))[0])
                git('checkout', '-q', base)
                (root / 'README.md').write_text('docs only')
                git('add', '.')
                git('commit', '-qm', 'docs')
                self.assertFalse(scope.decision(advanced, git('rev-parse', 'HEAD'))[0])


if __name__ == '__main__':
    unittest.main()
