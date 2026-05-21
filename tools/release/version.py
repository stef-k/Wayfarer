"""Prepare and validate Wayfarer release version metadata."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Sequence


REPO_ROOT = Path(__file__).resolve().parents[2]
VERSION_PROPS = "Version.props"
CHANGELOG = "CHANGELOG.md"
WAYFARER_VERSION = "WayfarerVersion"
DERIVED_PROPERTIES = (
    "Version",
    "PackageVersion",
    "AssemblyInformationalVersion",
)
DERIVED_VALUE = "$(WayfarerVersion)"
SEMVER_CORE_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
CHANGELOG_HEADING_PATTERN = re.compile(
    r"^## \[(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\] - \d{4}-\d{2}-\d{2}$"
)


class ValidationError(Exception):
    """Raised when release metadata does not satisfy the command contract."""


@dataclass(frozen=True)
class TextFile:
    """A UTF-8 text file and its detected newline style."""

    path: Path
    text: str
    newline: str


@dataclass(frozen=True)
class VersionState:
    """Validated release metadata read from Version.props."""

    file: TextFile
    version: str


@dataclass(frozen=True)
class ChangelogState:
    """Validated changelog text and its top release heading."""

    file: TextFile
    top_heading: str


def main(argv: Sequence[str] | None = None) -> int:
    """Run the release helper command line interface."""

    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        if args.command == "prepare":
            prepare(args.version)
        elif args.command == "check":
            check(args.require_tag, args.require_github_release)
        else:
            parser.error("unknown command")
    except ValidationError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    return 0


def build_parser() -> argparse.ArgumentParser:
    """Build the command parser with the required subcommands."""

    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare_parser = subparsers.add_parser("prepare")
    prepare_parser.add_argument("version")

    check_parser = subparsers.add_parser("check")
    check_parser.add_argument("--require-tag", action="store_true")
    check_parser.add_argument("--require-github-release", action="store_true")
    return parser


def prepare(target_version: str) -> None:
    """Prepare Version.props and CHANGELOG.md for the target release."""

    target_tuple = parse_semver(target_version)
    version_state = read_version_state()
    current_tuple = parse_semver(version_state.version)
    if target_tuple <= current_tuple:
        raise ValidationError(
            f"{target_version} must be greater than current version {version_state.version}"
        )

    changelog_state = read_changelog_state()
    ensure_changelog_section_missing(changelog_state.file.text, target_version)

    updated_version_props = replace_wayfarer_version(
        version_state.file.text, target_version
    )
    updated_changelog = insert_changelog_skeleton(
        changelog_state.file.text,
        changelog_state.file.newline,
        target_version,
        date.today().isoformat(),
    )

    write_text(version_state.file.path, updated_version_props, version_state.file.newline)
    write_text(changelog_state.file.path, updated_changelog, changelog_state.file.newline)
    print(f"Prepared Wayfarer {target_version}")


def check(require_tag: bool, require_github_release: bool) -> None:
    """Validate the current release metadata and optional external release state."""

    version_state = read_version_state()
    changelog_state = read_changelog_state()
    changelog_version = parse_top_changelog_version(changelog_state.top_heading)
    if changelog_version != version_state.version:
        raise ValidationError(
            "top changelog release version "
            f"{changelog_version} does not match {version_state.version}"
        )

    expected_tag = f"v{version_state.version}"
    if require_tag:
        validate_local_tag(expected_tag)
    if require_github_release:
        validate_github_release(expected_tag)
    print(f"Wayfarer release metadata is valid for {expected_tag}")


def read_version_state() -> VersionState:
    """Read and validate root Version.props release metadata."""

    text_file = read_text(REPO_ROOT / VERSION_PROPS)
    values = find_msbuild_values(text_file.text, WAYFARER_VERSION)
    if len(values) != 1:
        raise ValidationError("Version.props must contain exactly one WayfarerVersion")

    version = values[0]
    parse_semver(version)
    for property_name in DERIVED_PROPERTIES:
        property_values = find_msbuild_values(text_file.text, property_name)
        if not property_values:
            raise ValidationError(f"Version.props is missing {property_name}")
        if len(property_values) != 1 or property_values[0] != DERIVED_VALUE:
            raise ValidationError(
                f"Version.props {property_name} must be exactly {DERIVED_VALUE}"
            )
    return VersionState(text_file, version)


def read_changelog_state() -> ChangelogState:
    """Read CHANGELOG.md and validate the top release heading."""

    text_file = read_text(REPO_ROOT / CHANGELOG)
    if not text_file.text.startswith("# CHANGELOG"):
        raise ValidationError("CHANGELOG.md must start with # CHANGELOG")

    top_heading = find_top_changelog_heading(text_file.text)
    if not CHANGELOG_HEADING_PATTERN.match(top_heading):
        raise ValidationError(
            "top changelog release heading must match ## [X.Y.Z] - YYYY-MM-DD"
        )
    return ChangelogState(text_file, top_heading)


def read_text(path: Path) -> TextFile:
    """Read a UTF-8 text file and preserve its dominant newline style."""

    if not path.exists():
        raise ValidationError(f"{path.name} is missing")
    text = path.read_text(encoding="utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    return TextFile(path, text, newline)


def write_text(path: Path, text: str, newline: str) -> None:
    """Write UTF-8 text while preserving the detected newline style."""

    normalized = text.replace("\r\n", "\n").replace("\n", newline)
    path.write_text(normalized, encoding="utf-8", newline="")


def parse_semver(version: str) -> tuple[int, int, int]:
    """Parse strict SemVer core syntax into a numeric tuple."""

    match = SEMVER_CORE_PATTERN.match(version)
    if not match:
        raise ValidationError(f"{version} is not a strict SemVer core version")
    return tuple(int(part) for part in match.groups())


def find_msbuild_values(text: str, property_name: str) -> list[str]:
    """Find simple MSBuild property element values by name."""

    pattern = re.compile(
        rf"<{re.escape(property_name)}>(.*?)</{re.escape(property_name)}>",
        re.DOTALL,
    )
    return [match.group(1) for match in pattern.finditer(text)]


def replace_wayfarer_version(text: str, target_version: str) -> str:
    """Replace only the single WayfarerVersion element value."""

    pattern = re.compile(r"(<WayfarerVersion>)(.*?)(</WayfarerVersion>)", re.DOTALL)
    updated, count = pattern.subn(rf"\g<1>{target_version}\g<3>", text)
    if count != 1:
        raise ValidationError("Version.props must contain exactly one WayfarerVersion")
    return updated


def find_top_changelog_heading(text: str) -> str:
    """Return the first release heading after the changelog title."""

    for line in text.splitlines():
        if line.startswith("## "):
            return line
    raise ValidationError("CHANGELOG.md is missing a release heading")


def parse_top_changelog_version(heading: str) -> str:
    """Extract the release version from an already validated heading."""

    match = CHANGELOG_HEADING_PATTERN.match(heading)
    if not match:
        raise ValidationError(
            "top changelog release heading must match ## [X.Y.Z] - YYYY-MM-DD"
        )
    return ".".join(match.groups())


def ensure_changelog_section_missing(text: str, target_version: str) -> None:
    """Fail when the changelog already has a section for the target release."""

    section_pattern = re.compile(
        rf"^## \[{re.escape(target_version)}\] - \d{{4}}-\d{{2}}-\d{{2}}$",
        re.MULTILINE,
    )
    if section_pattern.search(text):
        raise ValidationError(f"CHANGELOG.md already contains {target_version}")


def insert_changelog_skeleton(
    text: str, newline: str, target_version: str, release_date: str
) -> str:
    """Insert the required release skeleton immediately after the changelog title."""

    lines = text.splitlines(keepends=True)
    if not lines or lines[0].rstrip("\r\n") != "# CHANGELOG":
        raise ValidationError("CHANGELOG.md must start with # CHANGELOG")

    skeleton = (
        f"{newline}## [{target_version}] - {release_date}{newline}"
        f"{newline}### Changed{newline}"
        f"- TODO: Add release notes before publishing.{newline}"
    )
    return "".join([lines[0], skeleton, *lines[1:]])


def validate_local_tag(expected_tag: str) -> None:
    """Require the exact local Git tag for the current release version."""

    result = run_command(["git", "tag", "--list", expected_tag])
    tags = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    if tags != [expected_tag]:
        raise ValidationError(f"local Git tag {expected_tag} is missing")


def validate_github_release(expected_tag: str) -> None:
    """Require an exact non-draft, non-prerelease GitHub release."""

    result = run_command(
        [
            "gh",
            "release",
            "view",
            expected_tag,
            "--json",
            "tagName,name,isDraft,isPrerelease",
        ]
    )
    try:
        release = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise ValidationError("gh release view returned invalid JSON") from exc

    expected = {
        "tagName": expected_tag,
        "name": expected_tag,
        "isDraft": False,
        "isPrerelease": False,
    }
    for key, expected_value in expected.items():
        if release.get(key) != expected_value:
            raise ValidationError(f"GitHub release {key} must be {expected_value!r}")


def run_command(command: Sequence[str]) -> subprocess.CompletedProcess[str]:
    """Run a validation command without mutating release state."""

    result = subprocess.run(
        command,
        cwd=REPO_ROOT,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        suffix = f": {detail}" if detail else ""
        raise ValidationError(f"{' '.join(command)} failed{suffix}")
    return result


if __name__ == "__main__":
    sys.exit(main())
