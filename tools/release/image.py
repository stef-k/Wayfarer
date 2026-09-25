"""Build, publish once, or anonymously qualify the Wayfarer application image."""

from __future__ import annotations

import argparse
import json
import os
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
PLATFORM = "linux/amd64"
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
    """Build the root Dockerfile as one AMD64 image with no registry exporter."""

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
        raise version.ValidationError("image platform does not match linux/amd64")
    actual_labels = inspected["Config"].get("Labels") or {}
    if any(actual_labels.get(key) != value for key, value in labels(release).items()):
        raise version.ValidationError("image OCI metadata does not match release")
    compiled = run("docker", "run", "--rm", "--read-only", "--network", "none", image, "version")
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
    require_absent(image)
    build(release)
    inspected = qualify(release, image)
    version.stable_source(release["tag"], release["sourceRevision"])
    require_absent(image)
    # --platform publishes a single manifest even on containerd-backed Docker engines.
    pushed = run("docker", "push", "--platform", PLATFORM, image)
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
    parser.add_argument("command", choices=["dry-run", "publish", "qualify"])
    parser.add_argument("--tag")
    parser.add_argument("--source")
    parser.add_argument("--digest")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command != "dry-run" and not (args.tag and args.source):
            raise version.ValidationError("stable operations require --tag and --source")
        if args.command == "publish" and (
            os.environ.get("GITHUB_EVENT_NAME") != "release"
            or os.environ.get("GITHUB_REPOSITORY") != "stef-k/Wayfarer"
        ):
            raise version.ValidationError("publication requires the official release workflow")
        release = identity(args.tag, args.source)
        if args.command == "publish":
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
