"""Deterministic offline PR evidence owners for the existing required CI checks."""

import argparse
from fnmatch import fnmatchcase
import os
from pathlib import Path
import re
import subprocess


# The gate must prove itself, including publication/runner wiring and local actions.
GATE_OWNERS = ('.github/workflows/*', '.github/actions/*', 'tools/ci/application_image_scope.py')
# Dedicated safety tests are also client files, but require only the cleanup matrix.
CLEANUP_OWNERS = (
    'tools/test-artifact-paths.*', 'tools/test-cleanup.mjs', 'tests/client/testCleanup.test.mjs',
    'tools/coverage-report*.ps1', 'tools/coverage_report.py', 'tools/tests/test_coverage_report.py',
    'tools/test_artifact_paths.py', 'tools/browser_processes.py',
    'tools/tests/test_test_artifact_paths.py', 'tools/tests/test_browser_processes.py',
    'tools/tests/test_browser_e2e.py',
)
# These files are imported by setup/readiness, recovery inspection and managed update.
# Their shared responsibilities cannot safely be separated by changed paths alone.
LIFECYCLE_SHARED = (
    'Program.cs', 'CommandLine/LifecycleCli.cs', 'CommandLine/RecoverySourceCli.cs',
    'CommandLine/DataProtectionCli.cs', 'Util/QuartzSchemaInstaller.cs',
    'Util/QuartzSnapshot.cs', 'Scripts/tables_postgres.sql',
    'Services/ApplicationConfiguration.cs', 'Services/ApplicationReadiness.cs',
    'Services/DatabaseSecret.cs', 'Services/StoragePaths.cs', 'Models/Options/StorageOptions.cs',
    'Services/LocationProviders/DataProtectionAuthority.cs',
    'Services/LocationProviders/StableIdentityReadiness.cs',
    'Services/LocationProviders/StableIdentityPreparation.cs',
    'Services/LocationProviders/LegacyCredentialPreparationCodec.cs',
    'Services/LocationProviders/PersonalProviderCredentialService.cs',
    'Services/QuartzHostedService.cs', 'Services/TrustedProxyConfiguration.cs',
    'Models/ApplicationDbContext*.cs', 'Models/Configuration/*', 'Models/LocationProviders/*',
    'Models/ApplicationUser.cs', 'Models/ApplicationSettings.cs', 'Models/ActivityType.cs',
    'appsettings*.json',
    'tools/WayfarerCtl/Program.cs', 'tools/WayfarerCtl/Cli.cs',
    'tools/WayfarerCtl/Deployment.cs', 'tools/WayfarerCtl/ActiveStorage.cs',
    # Fresh setup consumes purge tombstones; retained start reactivates Preserved state.
    'tools/WayfarerCtl/Setup.cs', 'tools/WayfarerCtl/DeploymentLifecycle.cs',
    'tools/WayfarerCtl/ProcessRunner.cs', 'tools/WayfarerCtl/ProtectedFiles.cs',
    'tools/WayfarerCtl/Preflight.cs', 'tools/WayfarerCtl/Release*.cs',
    'tools/WayfarerCtl/PublicRelease.cs',
    'tools/release/bundle.py', 'tools/release/public_bundle.py',
    'tools/compose/qualify.py', 'tools/compose/qualify_ctl.py',
)
# Global/package inputs can alter native payloads and schema/browser dependencies.
# Version.props is deliberately separate: changing only version identity is neutral.
SHARED_BUILD = (
    'Wayfarer.csproj', 'Directory.*.props', 'Directory.*.targets',
    '*/Directory.*.props', '*/Directory.*.targets', '*packages.lock.json',
    '*nuget.config', '*NuGet.Config', '*NuGet.config', '*global.json',
)
# Explicit ABI, platform/RID, OCI-selection and native qualification owners.
NATIVE_OWNERS = (
    'Dockerfile', '.dockerignore', 'Services/BrowserWorkflow.cs',
    'tools/release/image.py', 'tools/release/image-smoke.sh',
    'tools/release/db_image.py', 'tools/release/database-release.json',
    'tools/release/bundle.py', 'tools/release/public_bundle.py', 'deploy/compose/db/*',
    # Shipped native payload projects; local browser seed fixtures have no native contract.
    'tools/WayfarerCtl/WayfarerCtl.csproj', 'tools/WayfarerRecovery/WayfarerRecovery.csproj',
    'tools/WayfarerRecoverySource/WayfarerRecoverySource.csproj',
    'tools/WayfarerRecovery/NativePlatform.cs',
    'tools/WayfarerRecovery/SafeDirectory.cs', 'tools/WayfarerRecovery/RecoveryLock.cs',
    'tools/WayfarerRecovery/WorkerCli.cs', 'tools/WayfarerRecovery/WorkerConfiguration.cs',
    'tools/WayfarerRecovery/RestoreWorker.cs',
    'tools/WayfarerCtl/Preflight.cs', 'tools/WayfarerCtl/Deployment.cs',
    'tools/WayfarerCtl/ProtectedFiles.cs', 'tools/WayfarerCtl/BackupConfiguration.cs',
    'tools/WayfarerCtl/RestorePreparation.cs', 'tools/WayfarerCtl/ReleaseManifest.cs',
    'tools/WayfarerCtl/ReleaseImages.cs', 'tools/WayfarerCtl/PublicRelease.cs',
    'tools/compose/qualify.py', 'tools/compose/qualify_ctl.py',
    'tools/compose/qualify_recovery.py', 'tools/compose/lock-probe/*',
    'package.json', 'package-lock.json', '.nvmrc', '.npmrc',
)
# Plain path patterns map current product contracts; '*' includes nested paths.
OWNERS = {
    'dotnet': (
        '*.cs', '*.cshtml', '*.razor', '*.csproj', '*.props', '*.targets', '*.pubxml',
        '*.sln', '*.slnx', '*.resx', '*.sql', '*packages.lock.json',
        '*nuget.config', '*NuGet.Config', '*NuGet.config', '*global.json',
        '.config/dotnet-tools.json', 'appsettings*.json',
    ),
    # Derived from every RequiresPlaywright test's production calls and file imports.
    'playwright': (
        'Services/Browser*.cs', 'Services/TripMapThumbnailGenerator*.cs',
        'Services/ITripMapThumbnailGenerator.cs', 'Services/TripExportService.Pdf.cs',
        'Services/MapSnapshotService.cs', 'Util/RichNotes.cs', 'Util/HtmlHelpers.cs',
        'Util/TileProviderAttribution.cs', 'Services/ApplicationSettingsService.cs',
        'Views/Shared/_Layout.cshtml*', 'Views/Shared/_EmbedLayout.cshtml*',
        'Views/_ViewImports.cshtml', 'Views/_ViewStart.cshtml',
        'Views/Trip/Viewer.cshtml', 'Views/Trip/Print.cshtml', 'Views/Trip/Partials/*',
        'wwwroot/js/embeddedMap.js', 'wwwroot/js/Trip/tripPopupBuilder.js',
        'wwwroot/js/util/feature-metadata.js', 'wwwroot/js/Areas/User/Groups/Index.js',
        'wwwroot/lib/leaflet/*',
        'tests/Wayfarer.Tests/Services/BrowserCaptureBoundaryTests.cs',
        'tests/Wayfarer.Tests/Services/TripMapThumbnailGeneratorTests.cs',
        'tests/Wayfarer.Tests/Services/PublishedReadOnlyRuntimeTests.cs',
        'tests/Wayfarer.Tests/Views/TileAttributionLayoutRenderingTests.cs',
        'tests/Wayfarer.Tests/Util/RichNotesTests.cs',
        'tests/Wayfarer.Tests/Infrastructure/PlaywrightEnvironmentTestCollection.cs',
        'CommandLine/PlaywrightCli.cs', 'tests/Wayfarer.Tests/Tools/PlaywrightCliTests.cs',
        'tools/playwright_metadata.py', 'tools/tests/test_playwright_metadata.py',
        'tests/Wayfarer.Tests/Wayfarer.Tests.csproj', 'Program.cs',
    ) + SHARED_BUILD,
    'frontend': (
        '.nvmrc', '.npmrc', 'package.json', 'package-lock.json', 'frontend.config.yaml',
        'ClientApps/*', 'wwwroot/css/*', 'wwwroot/js/*', 'tests/client/*',
        'vite.config.*', 'tsconfig*.json', 'playwright*.config.*',
        'tools/build-*.mjs', 'tools/trip-editor-asset-smoke.mjs',
        'tools/browser_e2e.py', 'tools/browser-e2e.mjs', 'tests/e2e/shared-layout/*',
    ),
    'cleanup_safety': CLEANUP_OWNERS + (
        'package.json', 'package-lock.json', '.nvmrc', '.npmrc',
        'tools/trip-editor-asset-smoke.mjs', 'tools/browser_e2e.py', 'tools/browser-e2e.mjs',
        'tools/playwright_metadata.py', 'tools/tests/test_playwright_metadata.py',
    ),
    'release_tooling': (
        'tools/release/*.py', 'tools/release/tests/*', 'tools/release/database-release.json',
        'tools/WayfarerCtl/ReleaseManifest.cs', 'Version.props', 'CHANGELOG.md',
    ),
    'app_image': (
        'Dockerfile', '.dockerignore', 'Version.props', 'Services/AppVersionProvider.cs',
        'CommandLine/AppVersionCli.cs', 'Services/BrowserRuntime.cs', 'Services/BrowserWorkflow.cs',
        'tools/release/image.py', 'tools/release/image-smoke.sh',
        'package.json', 'package-lock.json', '.nvmrc', '.npmrc', 'frontend.config.yaml',
        'vite.config.*', 'tsconfig*.json', 'tools/build-*.mjs',
    ) + SHARED_BUILD,
    # Native C# browser launch is exercised by qualify.py's thumbnail/PDF routes;
    # image-smoke.sh launches JS Chromium and cannot prove BrowserWorkflow's ARM branch.
    # The ingress driver uses the production image's browser in AMD64 Compose qualification.
    'db_compose': ('deploy/compose/*', 'tools/release/db_image.py',
                   'tools/release/database-release.json', 'Services/BrowserWorkflow.cs',
                   'tools/compose/managed_ingress.mjs',
                   'tools/compose/qualify_sse.py', 'tools/compose/sse-probe/*'),
    'operator': ('tools/WayfarerCtl/*', 'tools/release/bundle.py',
                 'tools/release/public_bundle.py', 'tools/release/INSTALL.md'),
    'recovery': (
        'tools/WayfarerRecovery/*', 'tools/WayfarerRecoverySource/*',
        'tools/WayfarerCtl/Backup*.cs', 'tools/WayfarerCtl/Restore*.cs',
        'tools/WayfarerCtl/Uninstall*.cs',
        'tools/compose/qualify_recovery.py', 'tools/compose/qualify_restore*.py',
        'tools/compose/qualify_release.py', 'tools/compose/recovery-probe/*',
        'tools/compose/lock-probe/*',
    ),
    # Uninstall owns the joined setup/backup/update/restore/removal/reinstall journey.
    'update': ('tools/WayfarerCtl/Update*.cs', 'tools/WayfarerCtl/Uninstall*.cs',
               'tools/compose/qualify_update.py',
               'Migrations/*') + LIFECYCLE_SHARED + SHARED_BUILD,
    'arm64': NATIVE_OWNERS + SHARED_BUILD,
}
DOMAINS = tuple(OWNERS)


def matches(path, patterns):
    """Match repository-relative paths without consulting their current existence."""
    return any(fnmatchcase(path, pattern) for pattern in patterns)


def classify(paths):
    """Union exact path owners, then add only the existing execution prerequisites."""
    reasons = {domain: set() for domain in DOMAINS}
    for path in sorted(set(paths)):
        if matches(path, GATE_OWNERS):
            for domain in DOMAINS:
                reasons[domain].add(f'gate self-test: {path!r}')
            continue
        # Only these Markdown files are executable release inputs/payloads.
        if path.endswith('.md') and path not in ('CHANGELOG.md', 'tools/release/INSTALL.md'):
            continue
        if matches(path, CLEANUP_OWNERS):
            reasons['cleanup_safety'].add(f'owner: {path!r}')
            continue
        for domain, patterns in OWNERS.items():
            if matches(path, patterns):
                reasons[domain].add(f'owner: {path!r}')
    # Ordered explicitly: no generic dependency framework or reverse inference.
    # Operator qualification currently assembles an exact-head candidate bundle and
    # exercises setup against freshly qualified application and database artifacts.
    # Until CI has a separately proven immutable-substrate reuse contract, removing
    # operator -> db_compose -> app_image would under-classify operator evidence.
    for consumer, prerequisite in (
        ('playwright', 'dotnet'), ('update', 'recovery'), ('recovery', 'operator'),
        ('operator', 'db_compose'), ('db_compose', 'app_image'),
    ):
        if reasons[consumer]:
            reasons[prerequisite].add(f'execution prerequisite for {consumer}')
    return {domain: sorted(evidence) for domain, evidence in reasons.items()}


def changed_paths(base, head):
    """Compare the PR merge base to its head; expose both sides of every rename."""
    if not all(re.fullmatch(r'[0-9a-fA-F]{40}', sha) for sha in (base, head)):
        raise ValueError('expected full base and head commit SHAs')
    result = subprocess.run(
        ['git', 'diff', '--no-ext-diff', '--no-renames', '--name-only', '-z',
         f'{base}...{head}', '--'], check=True, capture_output=True,
    )
    if result.stdout and not result.stdout.endswith(b'\0'):
        raise ValueError('expected NUL-delimited Git paths')
    paths = result.stdout.decode('utf-8', errors='surrogateescape').split('\0')[:-1]
    # Match canonical portable Git paths, without normalizing aliases or consulting existence.
    for path in paths:
        if '\\' in path or re.match(r'^[A-Za-z]:', path) or any(
                part in ('', '.', '..') for part in path.split('/')):
            raise ValueError('expected canonical repository-relative Git paths')
    return paths


def decision(base, head):
    """Fail broad when exact diff evidence is invalid or unavailable."""
    try:
        return classify(changed_paths(base, head))
    except (ValueError, OSError, subprocess.CalledProcessError):
        return {domain: ['diff unavailable or invalid; broad qualification required']
                for domain in DOMAINS}


def main():
    """Emit bounded Boolean workflow outputs and deterministic run/skip explanations."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', required=True)
    args = parser.parse_args()
    reasons = decision(args.base, args.head)
    outputs = [f'run_{domain}={str(bool(reasons[domain])).lower()}' for domain in DOMAINS]
    if os.environ.get('GITHUB_OUTPUT'):
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
            stream.write('\n'.join(outputs) + '\n')
    for domain, output in zip(DOMAINS, outputs):
        print(output)
        if reasons[domain]:
            print(f'RUN {domain}: ' + '; '.join(reasons[domain]))
        else:
            print(f'PASS SKIP {domain}: no owning changed path or execution prerequisite.')


if __name__ == '__main__':
    main()
