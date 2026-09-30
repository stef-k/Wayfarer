"""Build, publish once, or anonymously qualify the Wayfarer application image."""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# Keep validation from creating untracked files in the release checkout.
sys.dont_write_bytecode = True
import version

IMAGE = "ghcr.io/stef-k/wayfarer"
SOURCE = "https://github.com/stef-k/Wayfarer"
PLATFORMS = ("linux/amd64", "linux/arm64")


def native_platform(machine: str | None = None) -> str:
    """Only native Linux AMD64 and ARM64 may build or execute release payloads."""
    architecture = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}.get(machine or platform.machine())
    if platform.system() != "Linux" or architecture is None:
        raise version.ValidationError("native Linux AMD64 or ARM64 is required")
    return "linux/" + architecture


PLATFORM = native_platform()
RID = "linux-x64" if PLATFORM == "linux/amd64" else "linux-arm64"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def run(*args: str) -> str:
    """Run a bounded release operation; retain tool failures without masking them."""

    return version.run_command(args).stdout.strip()


def identity(tag: str | None, source: str | None) -> dict:
    """Reuse the version authority; only explicit stable inputs enable publication."""

    if tag is not None:
        release_version = version.stable_source(tag, source or "")
    else:
        release_version = version.read_version_state().version
        source = run("git", "rev-parse", "HEAD")
        tag = f"v{release_version}"
    return {"tag": tag, "version": release_version, "sourceRevision": source,
            "source": SOURCE, "image": IMAGE, "platform": PLATFORM}


def labels(release: dict) -> dict:
    """Generate the small OCI identity set from validated release metadata."""

    return {"org.opencontainers.image.source": SOURCE,
            "org.opencontainers.image.revision": release["sourceRevision"],
            "org.opencontainers.image.version": release["version"],
            "org.opencontainers.image.title": "Wayfarer",
            "org.opencontainers.image.description": "Wayfarer application image; deployment bundle separate"}


def build(release: dict) -> str:
    """Build the root Dockerfile for this native platform with no registry exporter."""

    image = f"{IMAGE}:{release['tag']}"
    command = ["docker", "buildx", "build", "--platform", PLATFORM, "--load",
               "--provenance=false", "--sbom=false", "--tag", image]
    for key, value in labels(release).items():
        command.extend(["--label", f"{key}={value}"])
    subprocess.run([*command, "."], cwd=version.REPO_ROOT, check=True)
    return image


def require_absent(image: str) -> None:
    """Allow only explicit manifest absence; auth/network/registry failures block push."""

    result = subprocess.run(["docker", "manifest", "inspect", image], text=True,
                            capture_output=True, check=False)
    if result.returncode == 0:
        raise version.ValidationError("stable image already exists; never rebuild/overwrite it")
    # Docker emits this exact diagnostic for MANIFEST_UNKNOWN, not denied/transport errors.
    if result.stderr.strip() not in {"manifest unknown", f"no such manifest: {image}"}:
        raise version.ValidationError("cannot prove stable image absent; check GHCR access/availability")


def inspect_image(release: dict, image: str) -> dict:
    """Verify architecture, OCI identity and the executable's compiled version."""

    inspected = json.loads(run("docker", "image", "inspect", image))[0]
    if f"{inspected['Os']}/{inspected['Architecture']}" != PLATFORM:
        raise version.ValidationError("image platform does not match the native release platform")
    actual_labels = inspected["Config"].get("Labels") or {}
    if any(actual_labels.get(key) != value for key, value in labels(release).items()):
        raise version.ValidationError("image OCI metadata does not match release")
    compiled = run("docker", "run", "--rm", "--pull=never", "--read-only", "--network", "none", image, "version")
    if compiled != f"Wayfarer {release['version']}":
        raise version.ValidationError("compiled version does not match release")
    return inspected


def qualify(release: dict, image: str) -> dict:
    """Exercise the same accepted non-root/immutable/browser seam before and after push."""

    inspected = inspect_image(release, image)
    run("bash", "tools/release/image-smoke.sh", image)
    return inspected


def manifest_matches(image: str, digest: str, config_digest: str) -> None:
    """Reject indexes or a manifest whose config differs from the tested image."""

    if not DIGEST.fullmatch(digest):
        raise version.ValidationError("invalid registry digest")
    manifest = json.loads(run("docker", "manifest", "inspect", image))
    if "manifests" in manifest or manifest.get("config", {}).get("digest") != config_digest:
        raise version.ValidationError("expected the tested single-platform image manifest")


def selected_digest(reference: str, selected: str = PLATFORM) -> str:
    """Resolve exactly one supported platform from an immutable OCI index; reject ambiguous descriptors."""
    if selected not in PLATFORMS or not DIGEST.fullmatch(reference.rsplit("@", 1)[-1]):
        raise version.ValidationError("immutable supported platform selection required")
    manifest = json.loads(run("docker", "manifest", "inspect", reference))
    if "manifests" not in manifest:
        return reference.rsplit("@", 1)[1]
    matches = [item["digest"] for item in manifest["manifests"]
               if item.get("platform", {}).get("os") == "linux"
               and item.get("platform", {}).get("architecture") == selected.split("/")[1]
               and item.get("platform", {}).get("variant", "") in ("", "v8")]
    if len(matches) != 1 or not DIGEST.fullmatch(matches[0]):
        raise version.ValidationError("index must contain exactly one matching platform manifest")
    return matches[0]


def publish_index(release: dict, directory: Path, output: Path) -> None:
    """Join two already anonymously qualified native artifacts into one create-only release identity."""
    facts = [json.loads(path.read_text()) for path in sorted(directory.glob("*.json"))]
    if len(facts) != 2 or {fact.get("platform") for fact in facts} != set(PLATFORMS):
        raise version.ValidationError("two distinct native platform evidence files required")
    repository = release["image"]
    for fact in facts:
        if any(fact.get(key) != release[key] for key in ("image", "sourceRevision", "version", "tag")) or not fact.get("qualification", "").startswith("anonymous pull"):
            raise version.ValidationError("index inputs must be qualified exact-source release artifacts")
    for fact in facts:
        manifest_matches(repository + "@" + fact["platformDigest"], fact["platformDigest"], fact["configDigest"])
    ref = repository + ":" + release["tag"]
    require_absent(ref)
    run("docker", "buildx", "imagetools", "create", "--tag", ref,
        *[repository + "@" + fact["platformDigest"] for fact in facts])
    inspected = run("docker", "buildx", "imagetools", "inspect", ref)
    matches = re.findall(r"^Digest:\s+(sha256:[0-9a-f]{64})$", inspected, re.MULTILINE)
    if len(matches) != 1:
        raise version.ValidationError("index digest unavailable; reconcile registry, never republish")
    digest = matches[0]
    evidence = {**release, "manifestDigest": digest, "platforms": facts, "qualification": "index created; verification pending"}
    write_evidence(output, evidence)
    for fact in facts:
        if selected_digest(repository + "@" + digest, fact["platform"]) != fact["platformDigest"]:
            raise version.ValidationError("published index contradicts qualified native artifact")
    write_evidence(output, {**evidence, "qualification": "two qualified native manifests bound to immutable index"})
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
        stream.write(f"digest={digest}\n")


def config_digest(inspected: dict) -> str:
    """Docker containerd uses manifest IDs; classic engines use config IDs."""

    descriptor = inspected.get("Descriptor", {})
    digest = descriptor.get("annotations", {}).get("config.digest", inspected["Id"])
    if not DIGEST.fullmatch(digest):
        raise version.ValidationError("invalid image config digest")
    return digest


def evidence(release: dict, inspected: dict, digest: str | None, status: str) -> dict:
    """Produce image-only evidence; dry runs cannot claim an immutable registry identity."""

    if digest is not None and not DIGEST.fullmatch(digest):
        raise version.ValidationError("invalid registry digest")
    bases = [line.split()[1] for line in (version.REPO_ROOT / "Dockerfile").read_text().splitlines()
             if line.startswith("FROM ")]
    run_id = os.environ.get("GITHUB_RUN_ID")
    return {**release, "manifestDigest": digest, "platformDigest": digest,
            "configDigest": config_digest(inspected), "baseImages": bases,
            "qualification": status,
            "workflowRun": f"{SOURCE}/actions/runs/{run_id}" if run_id else None,
            "workflowAttempt": os.environ.get("GITHUB_RUN_ATTEMPT")}


def write_evidence(path: Path, data: dict) -> None:
    """Write parseable evidence outside the immutable checkout."""

    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def publish(release: dict, output: Path) -> None:
    """Build/test once, recheck identity/absence, push one tag and preserve its digest."""

    image = f"{IMAGE}:{release['tag']}"
    published = image + "-" + PLATFORM.split("/")[1]
    require_absent(image)
    require_absent(published)
    build(release)
    inspected = qualify(release, image)
    version.stable_source(release["tag"], release["sourceRevision"])
    require_absent(image)
    require_absent(published)
    run("docker", "tag", image, published)
    # --platform publishes a single manifest even on containerd-backed Docker engines.
    pushed = run("docker", "push", "--platform", PLATFORM, published)
    matches = re.findall(r"^\S+: digest: (sha256:[0-9a-f]{64}) size: \d+$", pushed, re.MULTILINE)
    if len(matches) != 1:
        raise version.ValidationError("push completed without one digest; inspect registry, do not republish")
    digest = matches[0]
    # Keep identity even if subsequent registry/anonymous qualification fails.
    write_evidence(output, evidence(release, inspected, digest, "pushed; anonymous qualification pending"))
    manifest_matches(f"{IMAGE}@{digest}", digest, config_digest(inspected))
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
        stream.write(f"digest={digest}\n")


def anonymous(release: dict, digest: str, output: Path) -> None:
    """Pull only the exact digest with a new empty client config; never authenticate."""

    if not DIGEST.fullmatch(digest):
        raise version.ValidationError("invalid registry digest")
    image = f"{IMAGE}@{digest}"
    with tempfile.TemporaryDirectory(prefix="wayfarer-anonymous-") as config:
        previous = os.environ.get("DOCKER_CONFIG")
        os.environ["DOCKER_CONFIG"] = config
        try:
            run("docker", "pull", "--platform", PLATFORM, image)
            inspected = qualify(release, image)
            manifest_matches(image, digest, config_digest(inspected))
            write_evidence(output, evidence(release, inspected, digest, "anonymous pull and smoke passed"))
        finally:
            if previous is None:
                os.environ.pop("DOCKER_CONFIG", None)
            else:
                os.environ["DOCKER_CONFIG"] = previous


def main() -> int:
    """Expose a non-push PR path and explicit stable publication/qualification paths."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["dry-run", "publish", "qualify", "index"])
    parser.add_argument("--inputs", type=Path)
    parser.add_argument("--tag")
    parser.add_argument("--source")
    parser.add_argument("--digest")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command != "dry-run" and not (args.tag and args.source):
            raise version.ValidationError("stable operations require --tag and --source")
        if args.command in ("publish", "index") and (
            os.environ.get("GITHUB_EVENT_NAME") != "release"
            or os.environ.get("GITHUB_REPOSITORY") != "stef-k/Wayfarer"
        ):
            raise version.ValidationError("publication requires the official release workflow")
        release = identity(args.tag, args.source)
        if args.command == "index":
            if args.inputs is None: raise version.ValidationError("qualified native --inputs required")
            publish_index(release, args.inputs, args.output)
        elif args.command == "publish":
            publish(release, args.output)
        elif args.command == "qualify":
            anonymous(release, args.digest or "", args.output)
        else:
            inspected = qualify(release, build(release))
            write_evidence(args.output, evidence(release, inspected, None, "local dry-run only"))
    except (version.ValidationError, subprocess.CalledProcessError) as exc:
        print(str(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
