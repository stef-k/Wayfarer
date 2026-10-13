#!/usr/bin/env python3
"""Run the unchanged retained paired CLI collector on the bounded codec candidates."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(os.environ.get("WAYFARER_IMAGE_RESEARCH", Path(__file__).resolve().parent))
PROJECT = "tests/Wayfarer.Tests/Wayfarer.Tests.csproj"
COMMAND = ["dotnet", "test", PROJECT, "--configuration", "Release", "--no-build", "--no-restore",
           "--filter", "FullyQualifiedName~ImageHelperPerformanceTests.RecordMatchedProfile"]
PROFILES = {"web": (2048, 2048, 90), "pdf": (600, 8192, 85)}
# Only the bounded native encoder can enter the complete pipeline safety gate.
CELLS = [("native-encode", profile, fixture)
         for profile in PROFILES for fixture in ["jpeg", "webp"]]


def run_cell(variant, profile, fixture, phase, session):
    """Warm hosts alternate all three backends; cold uses a fresh two-backend host each time."""
    directory = ROOT / "evidence/performance" / variant
    directory.mkdir(parents=True, exist_ok=True)
    report = directory / f"{profile}-{fixture}-{phase}-{session}.json"
    width, height, quality = PROFILES[profile]
    helper = ROOT / "bin" / ("wayfarer-image-baseline" if variant == "baseline"
                             else f"wayfarer-image-{variant}-final")
    variables = {"WAYFARER_IMAGE_HELPER": str(helper),
        "WAYFARER_IMAGE_PERF_INPUT": "photo.jpg" if fixture == "jpeg" else "photo.webp",
        "WAYFARER_IMAGE_PERF_REPORT": str(report), "WAYFARER_IMAGE_PERF_PHASE": phase,
        "WAYFARER_IMAGE_PERF_REPETITIONS": "24" if phase == "warm" else "1",
        "WAYFARER_IMAGE_PERF_WARMUPS": "20" if phase == "warm" else "0",
        "WAYFARER_IMAGE_PERF_WIDTH": str(width), "WAYFARER_IMAGE_PERF_HEIGHT": str(height),
        "WAYFARER_IMAGE_PERF_QUALITY": str(quality), "WAYFARER_IMAGE_PERF_ORDER": str(session % 2)}
    if phase == "warm":
        variables["WAYFARER_IMAGE_PERF_ORIGINAL"] = str(ROOT / "bin/wayfarer-image-baseline")
    environment = {**os.environ, **variables}
    if phase == "cold": environment.pop("WAYFARER_IMAGE_PERF_ORIGINAL", None)
    if report.exists(): raise RuntimeError(f"refusing to replace retained observation {report}")
    result = subprocess.run(COMMAND, cwd=ROOT / "source", env=environment,
                            capture_output=True, text=True, timeout=120)
    report.with_suffix(".log").write_text(result.stdout + result.stderr)
    with (ROOT / "evidence/pipeline-commands.jsonl").open("a") as log:
        log.write(json.dumps({"argv": COMMAND, "cwd": str(ROOT / "source"),
                              "environment": variables, "exit": result.returncode}) + "\n")
    if result.returncode or not report.exists():
        raise RuntimeError(f"{variant}/{profile}/{fixture}/{phase}: {result.stdout[-3000:]} {result.stderr}")


def main():
    """No concurrent builds, tests or benchmarks overlap this sequential measurement matrix."""
    for session in [1, 2]:
        cells = CELLS if session == 1 else list(reversed(CELLS))
        for variant, profile, fixture in cells:
            run_cell(variant, profile, fixture, "warm", session)
            print(f"Warm {session}: {variant}/{profile}/{fixture}", flush=True)
    for variant, profile, fixture in CELLS:
        for session in range(1, 9): run_cell(variant, profile, fixture, "cold", session)
        print(f"Cold 8 fresh hosts: {variant}/{profile}/{fixture}", flush=True)


if __name__ == "__main__":
    main()
