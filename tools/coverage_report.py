"""Generate ordinary-test coverage with exact GUID artifact ownership and safe retention.

Run from any working directory with Python 3.12+. The CLI accepts no cleanup paths;
only fresh children beneath this repository's fixed coverage roots are owned.
"""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

from test_artifact_paths import ordinary


class CoverageRun:
    """Retain creation identities for this invocation's two generated GUID children."""

    def __init__(self, repository):
        self.repository = ordinary(repository)
        self.run_id = uuid.uuid4().hex
        self.report = self.repository / 'coverage-report' / self.run_id
        self.results = self.repository / 'tests/Wayfarer.Tests/TestResults/coverage-report' / self.run_id
        self._created = {}

    def create(self):
        """Create fresh ordinary children, never adopting a preexisting GUID directory."""
        for directory in (self.report, self.results):
            ordinary(directory.parent)
            directory.parent.mkdir(parents=True, exist_ok=True)
            ordinary(directory.parent)
            directory.mkdir()
            info = directory.stat()
            self._created[directory] = info.st_dev, info.st_ino

    def _verify(self, directory):
        """Require the exact created directory and inspect its full tree before use/removal."""
        ordinary(directory, tree=True)
        info = directory.stat()
        if not directory.is_dir() or self._created.get(directory) != (info.st_dev, info.st_ino):
            raise ValueError(f'Coverage directory creation identity changed: {directory}')

    def _remove(self, directory):
        """Remove only this retained ordinary child; a recreated path fails identity checks."""
        if os.path.lexists(directory):
            self._verify(directory)
            shutil.rmtree(directory)

    def cobertura(self):
        """Consume exactly one nonempty regular Cobertura XML from current-run results."""
        self._verify(self.results)
        files = list(self.results.rglob('coverage.cobertura.xml'))
        if len(files) != 1 or not files[0].is_file() or files[0].stat().st_size == 0:
            raise ValueError(f'Expected one nonempty Cobertura file; found {len(files)}')
        try:
            if ET.parse(files[0]).getroot().tag != 'coverage':
                raise ValueError('Expected a Cobertura coverage document')
        except ET.ParseError as error:
            raise ValueError('Cobertura file is not valid XML') from error
        return files[0]

    def validate_report(self):
        """A report commits only after its ordinary tree contains a nonempty HTML index."""
        self._verify(self.report)
        index = self.report / 'index.html'
        if not index.is_file() or index.stat().st_size == 0:
            raise ValueError(f'ReportGenerator did not create a nonempty HTML index: {index}')
        return index

    def _prune_previous(self):
        """After report validation, prune only ordinary GUID siblings; retain unsafe/unknown files."""
        self.validate_report()
        for child in self.report.parent.iterdir():
            if child == self.report or not re.fullmatch(r'[0-9a-f]{32}', child.name) or not child.is_dir():
                continue
            try:
                ordinary(child, tree=True)
                shutil.rmtree(child)
            except (OSError, ValueError) as error:
                print(f'Retained unsafe/inaccessible previous report: {error}', file=sys.stderr)

    def finish(self, succeeded):
        """Always clean current results; failed reports preserve all earlier report evidence."""
        errors = []
        for directory in (self.results, self.report):
            if directory not in self._created:
                continue
            try:
                if directory == self.report and succeeded:
                    self._prune_previous()
                else:
                    self._remove(directory)
            except (OSError, ValueError) as error:
                errors.append(f'Coverage cleanup failed for {directory}: {error}')
        return errors


def generate_report(repository):
    """Run existing .NET tools, preserving primary exit status when cleanup also fails."""
    run = CoverageRun(repository)
    succeeded = False
    status = 0
    try:
        subprocess.run(['dotnet', 'tool', 'restore'], cwd=repository, check=True)
        run.create()
        project = 'tests/Wayfarer.Tests/Wayfarer.Tests.csproj'
        subprocess.run(['dotnet', 'build', project, '-c', 'Debug'], cwd=repository, check=True)
        subprocess.run([
            'dotnet', 'test', project, '-c', 'Debug', '--no-build',
            '--settings', str(run.repository / 'coverlet.runsettings'),
            '--collect:XPlat Code Coverage', '--results-directory', str(run.results),
            '--filter', 'Category!=RequiresSpatialite&Category!=RequiresPlaywright&Category!=RequiresRoot',
        ], cwd=repository, check=True)
        cobertura = run.cobertura()
        subprocess.run([
            'dotnet', 'reportgenerator', f'-reports:{cobertura}', f'-targetdir:{run.report}',
            '-reporttypes:Html',
            '-assemblyfilters:+Wayfarer;-AspNetCoreGeneratedDocument*;-WayfarerAspNetCoreGeneratedDocument*',
            '-filefilters:-*Migrations*;-*Areas/Identity/Pages/*;-*Models/Dtos/*;-*Models/ViewModels/*;'
            '-*Views/*;-*.cshtml;-*.cshtml.cs;-*.cshtml.g.cs',
        ], cwd=repository, check=True)
        index = run.validate_report()
        succeeded = True
        print(f'Cobertura consumed: {cobertura} ({cobertura.stat().st_size} bytes)')
        print(f'HTML report: {index} ({index.stat().st_size} bytes)')
    except subprocess.CalledProcessError as error:
        print(f'Coverage tool failed: {error}', file=sys.stderr)
        status = error.returncode if error.returncode > 0 else 128 - error.returncode
    except (OSError, ValueError, KeyboardInterrupt) as error:
        print(f'Coverage failed: {error}', file=sys.stderr)
        status = 130 if isinstance(error, KeyboardInterrupt) else 1
    finally:
        errors = run.finish(succeeded)
        for error in errors:
            print(error, file=sys.stderr)
        if errors and status == 0:
            status = 1
    return status


def main():
    """Use fixed repository roots, rejecting caller-selected output or deletion paths."""
    argparse.ArgumentParser(description=__doc__).parse_args()
    return generate_report(Path(__file__).absolute().parents[1])


if __name__ == '__main__':
    sys.exit(main())
