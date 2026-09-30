"""Behavioral tests for immutable release authority and image distribution evidence."""

import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import image
import version
from test_version import write_repo


@pytest.fixture
def release_repo(tmp_path, monkeypatch):
    """Use real local/remote Git refs; only the GitHub Release response is substituted."""

    write_repo(tmp_path)
    monkeypatch.setattr(version, "REPO_ROOT", tmp_path)
    def git(*args):
        return version.run_command(["git", *args]).stdout.strip()
    git("init", "--quiet")
    git("config", "user.email", "fixture@example.invalid")
    git("config", "user.name", "Fixture")
    git("add", ".")
    git("commit", "--quiet", "-m", "Release fixture")
    git("tag", "-a", "v1.4.0", "-m", "Release fixture")
    git("remote", "add", "origin", str(tmp_path))
    monkeypatch.setattr(version, "validate_github_release", lambda tag: None)
    return git, git("rev-parse", "HEAD")


def test_stable_source_binds_annotated_tag_and_clean_source(release_repo):
    """Stable identity accepts the exact commit, including annotated remote tag peeling."""

    _, sha = release_repo
    assert version.stable_source("v1.4.0", sha) == "1.4.0"


@pytest.mark.parametrize("tag", ["v1.4.1", "v1.4.0-rc.1", "main", "v1.4.0\n"])
def test_invalid_stable_identity(release_repo, tag):
    """Mismatch, prerelease, moving ref and trailing input cannot pass stable validation."""

    _, sha = release_repo
    with pytest.raises(version.ValidationError):
        version.stable_source(tag, sha)


def test_moving_head_cannot_masquerade_as_tag(release_repo):
    """Even the correct Version.props cannot authorize a different commit."""

    git, _ = release_repo
    git("commit", "--allow-empty", "--quiet", "-m", "Later source")
    with pytest.raises(version.ValidationError, match="must agree"):
        version.stable_source("v1.4.0", git("rev-parse", "HEAD"))


def test_dirty_source_is_rejected(release_repo):
    """Untracked build inputs are forbidden, as well as tracked source modifications."""

    _, sha = release_repo
    (version.REPO_ROOT / "Extra.cs").write_text("// Unexpected build input\n")
    with pytest.raises(version.ValidationError, match="clean"):
        version.stable_source("v1.4.0", sha)


def test_remote_retarget_is_rejected(release_repo, tmp_path):
    """A divergent remote tag fails even when local tag/HEAD/version still agree."""

    git, sha = release_repo
    remote = tmp_path / "remote.git"
    git("clone", "--bare", ".", str(remote))
    git("remote", "set-url", "origin", str(remote))
    subprocess.run(["git", "--git-dir", str(remote), "update-ref", "-d", "refs/tags/v1.4.0"], check=True)
    with pytest.raises(version.ValidationError, match="remote release tag"):
        version.stable_source("v1.4.0", sha)


@pytest.mark.parametrize("code,error,allowed", [
    (0, "", False),
    (1, "no such manifest: ghcr.io/stef-k/wayfarer:v1.4.0\n", True),
    (1, "manifest unknown\n", True),
    (1, "unauthorized", False), (1, "denied", False), (1, "TLS timeout", False),
])
def test_stable_tag_absence_fails_closed(monkeypatch, code, error, allowed):
    """Only explicit absence permits a push; existing tags and ambiguous failures block."""

    monkeypatch.setattr(image.subprocess, "run", lambda *a, **k:
                        subprocess.CompletedProcess(a, code, stdout="{}", stderr=error))
    if allowed:
        image.require_absent("ghcr.io/stef-k/wayfarer:v1.4.0")
    else:
        with pytest.raises(version.ValidationError):
            image.require_absent("ghcr.io/stef-k/wayfarer:v1.4.0")


def test_image_metadata_and_evidence(monkeypatch, tmp_path):
    """Evidence binds the tested source/platform/config and distinguishes local dry runs."""

    release = {"tag": "v1.4.0", "version": "1.4.0", "sourceRevision": "a" * 40,
               "image": image.IMAGE, "source": image.SOURCE, "platform": image.PLATFORM}
    inspected = {"Id": "sha256:" + "b" * 64, "Os": "linux", "Architecture": "amd64",
                 "Config": {"Labels": image.labels(release)}}
    def inspect_or_probe(*args):
        if args[1:3] == ("image", "inspect"):
            return json.dumps([inspected])
        assert "--pull=never" in args  # Offline qualification cannot acquire a concurrently removed image.
        return "Wayfarer 1.4.0"
    monkeypatch.setattr(image, "run", inspect_or_probe)
    assert image.inspect_image(release, "fixture") == inspected
    result = image.evidence(release, inspected, "sha256:" + "c" * 64, "fixture")
    output = tmp_path / "image.json"
    image.write_evidence(output, result)
    assert json.loads(output.read_text())["platformDigest"] == result["manifestDigest"]
    assert result["sourceRevision"] == "a" * 40
    assert image.evidence(release, inspected, None, "local dry-run only")["manifestDigest"] is None
    inspected["Config"]["Labels"]["org.opencontainers.image.version"] = "1.4.1"
    with pytest.raises(version.ValidationError, match="OCI metadata"):
        image.inspect_image(release, "fixture")


def test_anonymous_qualification_uses_only_exact_digest(monkeypatch, tmp_path):
    """The pull has empty credentials and every probe uses the published digest, never tag."""

    digest = "sha256:" + "c" * 64
    expected = f"{image.IMAGE}@{digest}"
    observed = []
    def run(*args):
        assert list(Path(image.os.environ["DOCKER_CONFIG"]).iterdir()) == []
        observed.append(args)
        return ""
    monkeypatch.setattr(image, "run", run)
    monkeypatch.setattr(image, "qualify", lambda release, ref: {"Id": "sha256:" + "b" * 64} if ref == expected else None)
    monkeypatch.setattr(image, "manifest_matches", lambda ref, dg, config: observed.append((ref, dg)))
    monkeypatch.setattr(image, "evidence", lambda *args: {"qualification": args[-1]})
    monkeypatch.setenv("DOCKER_CONFIG", "/unused/publisher-config")
    image.anonymous({}, digest, tmp_path / "evidence.json")
    assert observed[0] == ("docker", "pull", "--platform", "linux/amd64", expected)
    assert observed[1] == (expected, digest)
    assert image.os.environ["DOCKER_CONFIG"] == "/unused/publisher-config"


def test_manifest_rejects_index_or_wrong_config(monkeypatch):
    """Evidence cannot claim a platform digest for an index or another image config."""

    for manifest in ({"manifests": []}, {"config": {"digest": "wrong"}}):
        monkeypatch.setattr(image, "run", lambda *a: json.dumps(manifest))
        with pytest.raises(version.ValidationError):
            image.manifest_matches("fixture", "sha256:" + "c" * 64, "config")


def test_containerd_and_classic_config_identity():
    """Actual containerd inspect metadata exposes a manifest ID distinct from config."""

    config = "sha256:" + "a" * 64
    manifest = "sha256:" + "b" * 64
    assert image.config_digest({"Id": config}) == config
    assert image.config_digest({"Id": manifest, "Descriptor": {
        "digest": manifest, "annotations": {"config.digest": config}}}) == config


def test_dry_run_never_pushes(monkeypatch, tmp_path):
    """CLI dry run permits only build/qualification/evidence, with no stable operations."""

    monkeypatch.setattr(sys, "argv", ["image.py", "dry-run", "--output", str(tmp_path / "dry.json")])
    monkeypatch.setattr(image, "identity", lambda *a: {})
    monkeypatch.setattr(image, "build", lambda *a: "local")
    monkeypatch.setattr(image, "qualify", lambda *a: {})
    monkeypatch.setattr(image, "evidence", lambda *a: {"manifestDigest": a[2]})
    def forbidden(*args):
        pytest.fail("dry run reached publication")
    monkeypatch.setattr(image, "publish", forbidden)
    monkeypatch.setattr(image, "require_absent", forbidden)
    assert image.main() == 0
    assert json.loads((tmp_path / "dry.json").read_text())["manifestDigest"] is None


def test_existing_tag_blocks_before_build(monkeypatch, tmp_path):
    """A rerun cannot even rebuild a stable image whose identity already exists."""

    monkeypatch.setattr(image.subprocess, "run", lambda *a, **k:
                        subprocess.CompletedProcess(a, 0, stdout="{}", stderr=""))
    def forbidden(*args):
        pytest.fail("existing stable image was rebuilt")
    monkeypatch.setattr(image, "build", forbidden)
    with pytest.raises(version.ValidationError, match="already exists"):
        image.publish({"tag": "v1.4.0"}, tmp_path / "evidence.json")


@pytest.mark.parametrize("architecture,compiled", [
    ("arm64", "Wayfarer 1.4.0"), ("amd64", "Wayfarer 1.4.1"),
])
def test_image_rejects_wrong_platform_or_compiled_version(monkeypatch, architecture, compiled):
    """Correct labels cannot hide the wrong binary version or image architecture."""

    release = {"version": "1.4.0", "sourceRevision": "a" * 40}
    inspected = {"Os": "linux", "Architecture": architecture,
                 "Config": {"Labels": image.labels(release)}}
    monkeypatch.setattr(image, "run", lambda *a: json.dumps([inspected])
                        if a[1:3] == ("image", "inspect") else compiled)
    with pytest.raises(version.ValidationError):
        image.inspect_image(release, "fixture")


@pytest.mark.parametrize("selected", image.PLATFORMS)
def test_index_selection_requires_one_exact_supported_manifest(monkeypatch, selected):
    """One immutable release index resolves only its exact native manifest; duplicates and missing platforms fail."""
    ref = image.IMAGE + "@sha256:" + "a" * 64
    entries = [{"digest": "sha256:" + char * 64, "platform": {"os": "linux", "architecture": arch}}
               for char, arch in (("b", "amd64"), ("c", "arm64"))]
    monkeypatch.setattr(image, "run", lambda *args: json.dumps({"manifests": entries}))
    expected = entries[0 if selected == "linux/amd64" else 1]
    assert image.selected_digest(ref, selected) == expected["digest"]
    entries.append(expected.copy())
    with pytest.raises(version.ValidationError, match="exactly one"):
        image.selected_digest(ref, selected)
    entries.clear()
    with pytest.raises(version.ValidationError, match="exactly one"):
        image.selected_digest(ref, selected)
    with pytest.raises(version.ValidationError):
        image.selected_digest(ref, "linux/arm/v7")


def test_index_publication_rejects_mixed_release_or_duplicate_platform_before_write(monkeypatch, tmp_path):
    """Native evidence cannot combine distinct sources or masquerade as two supported architectures."""
    release = {"image": image.IMAGE, "tag": "v1.4.0", "version": "1.4.0", "sourceRevision": "a" * 40}
    evidence = {**release, "platform": "linux/amd64", "qualification": "anonymous pull passed"}
    (tmp_path / "amd64.json").write_text(json.dumps(evidence))
    (tmp_path / "arm64.json").write_text(json.dumps(evidence))
    monkeypatch.setattr(image, "require_absent", lambda *args: pytest.fail("invalid evidence reached publication"))
    with pytest.raises(version.ValidationError, match="two distinct"):
        image.publish_index(release, tmp_path, tmp_path / "result")
    (tmp_path / "arm64.json").write_text(json.dumps({**evidence, "platform": "linux/arm64", "sourceRevision": "b" * 40}))
    monkeypatch.setattr(image, "manifest_matches", lambda *args: None)
    with pytest.raises(version.ValidationError, match="exact-source"):
        image.publish_index(release, tmp_path, tmp_path / "result")


@pytest.mark.parametrize("contradiction", [False, True])
def test_published_index_binds_both_tested_manifests_and_retains_pending_identity(monkeypatch, tmp_path, contradiction):
    """The index selects the tested manifests; a contradictory registry result retains the already-created digest."""
    release = {"image": image.IMAGE, "tag": "v1.4.0", "version": "1.4.0", "sourceRevision": "a" * 40}
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    facts = [{**release, "platform": platform, "platformDigest": "sha256:" + char * 64,
              "configDigest": "sha256:" + "d" * 64, "qualification": "anonymous pull passed"}
             for platform, char in zip(image.PLATFORMS, "bc")]
    for fact in facts:
        (inputs / (fact["platform"].split("/")[1] + ".json")).write_text(json.dumps(fact))
    index = "sha256:" + "e" * 64
    created = []
    def registry(*args):
        if args[1:4] == ("buildx", "imagetools", "create"):
            created.append(args)
            return ""
        if args[1:4] == ("buildx", "imagetools", "inspect"):
            return "Digest: " + index
        if args[-1].endswith(index):
            return json.dumps({"manifests": [{"digest": index if contradiction else fact["platformDigest"],
                "platform": {"os": "linux", "architecture": fact["platform"].split("/")[1]}} for fact in facts]})
        return json.dumps({"config": {"digest": facts[0]["configDigest"]}})
    monkeypatch.setattr(image, "run", registry)
    monkeypatch.setattr(image, "require_absent", lambda ref: None)
    monkeypatch.setenv("GITHUB_OUTPUT", str(tmp_path / "outputs"))
    output = tmp_path / "index.json"
    if contradiction:
        with pytest.raises(version.ValidationError, match="contradicts"):
            image.publish_index(release, inputs, output)
        assert not (tmp_path / "outputs").exists()
    else:
        image.publish_index(release, inputs, output)
        assert (tmp_path / "outputs").read_text() == "digest=" + index + "\n"
    assert created[0][-2:] == tuple(image.IMAGE + "@" + fact["platformDigest"] for fact in facts)
    result = json.loads(output.read_text())
    assert result["manifestDigest"] == index and result["platforms"] == facts
    assert ("verification pending" in result["qualification"]) == contradiction
