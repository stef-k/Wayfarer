"""Conservative, offline PR scope for the required application-image check."""

import argparse
from fnmatch import fnmatchcase
import os
from pathlib import Path
import re
import subprocess


# Entire delivery/qualification boundaries include their fixed fixtures and future files.
BOUNDARIES = (
    '.github/workflows/', '.github/actions/', 'tools/release/', 'deploy/compose/',
    'tools/WayfarerCtl/', 'tools/WayfarerRecovery/', 'tools/WayfarerRecoverySource/',
    'tools/compose/', 'CommandLine/', 'Migrations/',
)
# Derived from LifecycleCli/RecoverySourceCli, readiness, and recovery-probe imports.
OWNERS = {
    'Program.cs', 'Util/QuartzSchemaInstaller.cs', 'Util/QuartzSnapshot.cs',
    'Scripts/tables_postgres.sql', 'Services/ApplicationConfiguration.cs',
    'Services/ApplicationReadiness.cs', 'Services/DatabaseSecret.cs',
    'Services/StoragePaths.cs', 'Models/Options/StorageOptions.cs',
    'Services/LocationProviders/DataProtectionAuthority.cs',
    'Services/LocationProviders/StableIdentityReadiness.cs',
    'Services/LocationProviders/StableIdentityPreparation.cs',
    'Services/LocationProviders/LegacyCredentialPreparationCodec.cs',
    'Services/LocationProviders/PersonalProviderCredentialService.cs',
    'Services/AppVersionProvider.cs', 'Services/QuartzHostedService.cs',
    'Services/BrowserRuntime.cs', 'Services/BrowserWorkflow.cs',
    'Services/TrustedProxyConfiguration.cs', 'Models/ApplicationUser.cs',
    'Models/ApplicationSettings.cs', 'Models/ActivityType.cs',
    'tools/ci/application_image_scope.py',
}
# Build/runtime metadata may be introduced at nested MSBuild or NuGet boundaries.
METADATA = (
    '*.csproj', '*.props', '*.targets', '*.pubxml', '*packages.lock.json',
    '*nuget.config', '*NuGet.Config', '*NuGet.config', '*global.json',
    'Dockerfile*', '**/Dockerfile*', '.dockerignore', '**/.dockerignore',
    '.config/dotnet-tools.json', 'appsettings*.json', '.nvmrc', '.npmrc',
    'package.json', 'package-lock.json', 'frontend.config.yaml', 'vite.config.*',
    'tsconfig*.json', 'tools/build-*.mjs',
    'Models/ApplicationDbContext*.cs', 'Models/Configuration/*.cs',
    'Models/LocationProviders/*.cs',
)


def sensitive(path):
    """Match repository-relative paths without consulting their current existence."""
    return (path in OWNERS or path.startswith(BOUNDARIES)
            or any(fnmatchcase(path, pattern) for pattern in METADATA))


def changed_paths(base, head):
    """Compare the PR merge base to its head; expose both sides of every rename."""
    if not all(re.fullmatch(r'[0-9a-fA-F]{40}', sha) for sha in (base, head)):
        raise ValueError('expected full base and head commit SHAs')
    result = subprocess.run(
        ['git', 'diff', '--no-ext-diff', '--no-renames', '--name-only', '-z',
         f'{base}...{head}', '--'], check=True, capture_output=True,
    )
    return result.stdout.decode('utf-8', errors='surrogateescape').split('\0')[:-1]


def decision(base, head):
    """Fail conservatively to heavy qualification when diff evidence is unavailable."""
    try:
        paths = changed_paths(base, head)
    except (ValueError, OSError, subprocess.CalledProcessError):
        return True, ['diff unavailable or invalid; full qualification required']
    matches = sorted(set(path for path in paths if sensitive(path)))
    return bool(matches), [f'sensitive path: {path!r}' for path in matches]


def main():
    """Emit a bounded GitHub output and human-readable scope evidence."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', required=True)
    args = parser.parse_args()
    run, reasons = decision(args.base, args.head)
    output = f'run_application_image={str(run).lower()}'
    print(output)
    if os.environ.get('GITHUB_OUTPUT'):
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
            stream.write(output + '\n')
    if run:
        print('application-image scope: full qualification required.')
        print('\n'.join(reasons))
    else:
        print('PASS application-image scope: no container/lifecycle-sensitive changes; heavy qualification skipped.')


if __name__ == '__main__':
    main()
