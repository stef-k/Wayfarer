"""Unit tests for the Wayfarer release version helper."""

from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path

import pytest


MODULE_PATH = Path(__file__).resolve().parents[1] / "version.py"
SPEC = importlib.util.spec_from_file_location("release_version", MODULE_PATH)
version = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules["release_version"] = version
SPEC.loader.exec_module(version)


def write_repo(tmp_path: Path, release_version: str = "1.4.0") -> None:
    """Create the minimal release metadata files used by the helper."""

    (tmp_path / "Version.props").write_text(
        "\n".join(
            [
                "<Project>",
                "  <PropertyGroup>",
                f"    <WayfarerVersion>{release_version}</WayfarerVersion>",
                "    <Version>$(WayfarerVersion)</Version>",
                "    <PackageVersion>$(WayfarerVersion)</PackageVersion>",
                "    <AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>",
                "  </PropertyGroup>",
                "</Project>",
                "",
            ]
        ),
        encoding="utf-8",
    )
    (tmp_path / "CHANGELOG.md").write_text(
        "\n".join(
            [
                "# CHANGELOG",
                "",
                f"## [{release_version}] - 2026-05-20",
                "",
                "### Changed",
                "- Existing release note.",
                "",
            ]
        ),
        encoding="utf-8",
    )


def use_repo(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    """Point the release helper at a temporary repo root."""

    monkeypatch.setattr(version, "REPO_ROOT", tmp_path)


def test_prepare_success_updates_version_and_inserts_skeleton(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """prepare updates only WayfarerVersion and adds the exact changelog skeleton."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    version.prepare("1.4.1")

    version_props = (tmp_path / "Version.props").read_text(encoding="utf-8")
    assert "<WayfarerVersion>1.4.1</WayfarerVersion>" in version_props
    assert "<Version>$(WayfarerVersion)</Version>" in version_props
    assert "<PackageVersion>$(WayfarerVersion)</PackageVersion>" in version_props
    assert (
        "<AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>"
        in version_props
    )

    changelog = (tmp_path / "CHANGELOG.md").read_text(encoding="utf-8")
    assert changelog.startswith(
        "# CHANGELOG\n\n"
        "## [1.4.1] - "
        f"{version.date.today().isoformat()}\n\n"
        "### Changed\n"
        "- TODO: Add release notes before publishing.\n"
    )


@pytest.mark.parametrize(
    "invalid_version",
    ["v1.4.1", "1.4", "1.4.1-beta.1", "1.4.1+build.1", "-1.4.1", "1.x.1"],
)
def test_invalid_version_format_rejection(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, invalid_version: str
) -> None:
    """prepare rejects anything outside strict SemVer core syntax."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.prepare(invalid_version)


@pytest.mark.parametrize("target_version", ["1.4.0", "1.3.9"])
def test_lower_or_repeated_version_rejection(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, target_version: str
) -> None:
    """prepare rejects target versions that are not greater numerically."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.prepare(target_version)
    assert "<WayfarerVersion>1.4.0</WayfarerVersion>" in (
        tmp_path / "Version.props"
    ).read_text(encoding="utf-8")


def test_changelog_skeleton_insertion(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """prepare inserts the skeleton immediately after the changelog title."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    version.prepare("1.4.2")

    lines = (tmp_path / "CHANGELOG.md").read_text(encoding="utf-8").splitlines()
    assert lines[0] == "# CHANGELOG"
    assert lines[1] == ""
    assert lines[2] == f"## [1.4.2] - {version.date.today().isoformat()}"
    assert lines[3] == ""
    assert lines[4] == "### Changed"
    assert lines[5] == "- TODO: Add release notes before publishing."


def test_existing_changelog_section_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """prepare fails without rewriting when the target section already exists."""

    write_repo(tmp_path)
    original_version_props = (tmp_path / "Version.props").read_text(encoding="utf-8")
    original_changelog = (tmp_path / "CHANGELOG.md").read_text(encoding="utf-8")
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.prepare("1.4.0")

    assert (tmp_path / "Version.props").read_text(encoding="utf-8") == original_version_props
    assert (tmp_path / "CHANGELOG.md").read_text(encoding="utf-8") == original_changelog


def test_malformed_changelog_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """prepare fails when CHANGELOG.md does not start with the required title."""

    write_repo(tmp_path)
    (tmp_path / "CHANGELOG.md").write_text("# Changes\n", encoding="utf-8")
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.prepare("1.4.1")


def test_missing_changelog_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """prepare fails when CHANGELOG.md is absent."""

    write_repo(tmp_path)
    (tmp_path / "CHANGELOG.md").unlink()
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.prepare("1.4.1")


def test_offline_check_detects_changelog_version_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """default check fails when the top changelog version drifts from Version.props."""

    write_repo(tmp_path, release_version="1.4.0")
    changelog = (tmp_path / "CHANGELOG.md").read_text(encoding="utf-8")
    (tmp_path / "CHANGELOG.md").write_text(
        changelog.replace("## [1.4.0]", "## [1.4.1]"),
        encoding="utf-8",
    )
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.check(require_tag=False, require_github_release=False)


def test_derived_msbuild_property_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """default check fails when derived MSBuild properties stop using WayfarerVersion."""

    write_repo(tmp_path)
    version_props = (tmp_path / "Version.props").read_text(encoding="utf-8")
    (tmp_path / "Version.props").write_text(
        version_props.replace(
            "<Version>$(WayfarerVersion)</Version>",
            "<Version>1.4.0</Version>",
        ),
        encoding="utf-8",
    )
    use_repo(monkeypatch, tmp_path)

    with pytest.raises(version.ValidationError):
        version.check(require_tag=False, require_github_release=False)


def test_explicit_local_tag_validation_success(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """check --require-tag accepts an exact local tag match."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    def fake_run(command, **kwargs):
        assert command == ["git", "tag", "--list", "v1.4.0"]
        return subprocess.CompletedProcess(command, 0, stdout="v1.4.0\n", stderr="")

    monkeypatch.setattr(version.subprocess, "run", fake_run)

    version.check(require_tag=True, require_github_release=False)


def test_explicit_local_tag_validation_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """check --require-tag rejects a missing local tag."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    def fake_run(command, **kwargs):
        assert command == ["git", "tag", "--list", "v1.4.0"]
        return subprocess.CompletedProcess(command, 0, stdout="", stderr="")

    monkeypatch.setattr(version.subprocess, "run", fake_run)

    with pytest.raises(version.ValidationError):
        version.check(require_tag=True, require_github_release=False)


def test_mocked_github_release_success(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """check --require-github-release accepts exact release metadata."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    def fake_run(command, **kwargs):
        assert command == [
            "gh",
            "release",
            "view",
            "v1.4.0",
            "--json",
            "tagName,name,isDraft,isPrerelease",
        ]
        payload = (
            '{"tagName":"v1.4.0","name":"v1.4.0",'
            '"isDraft":false,"isPrerelease":false}'
        )
        return subprocess.CompletedProcess(command, 0, stdout=payload, stderr="")

    monkeypatch.setattr(version.subprocess, "run", fake_run)

    version.check(require_tag=False, require_github_release=True)


def test_mocked_github_release_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """check --require-github-release rejects draft or mismatched releases."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    def fake_run(command, **kwargs):
        payload = (
            '{"tagName":"v1.4.0","name":"v1.4.0",'
            '"isDraft":true,"isPrerelease":false}'
        )
        return subprocess.CompletedProcess(command, 0, stdout=payload, stderr="")

    monkeypatch.setattr(version.subprocess, "run", fake_run)

    with pytest.raises(version.ValidationError):
        version.check(require_tag=False, require_github_release=True)


def test_default_check_does_not_call_gh_or_git(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """default check validates only offline files and never calls subprocess."""

    write_repo(tmp_path)
    use_repo(monkeypatch, tmp_path)

    def fail_run(command, **kwargs):
        raise AssertionError(f"default check called subprocess: {command}")

    monkeypatch.setattr(version.subprocess, "run", fail_run)

    version.check(require_tag=False, require_github_release=False)


def test_usage_error_exit_code() -> None:
    """invalid command-line arguments use argparse's required usage exit code."""

    with pytest.raises(SystemExit) as exc_info:
        version.main(["prepare"])

    assert exc_info.value.code == 2
