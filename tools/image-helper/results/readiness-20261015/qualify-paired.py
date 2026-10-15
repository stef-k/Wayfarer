#!/usr/bin/env python3
"""Invoke the retained #858 paired collector once on the fixed readiness matrix."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(os.environ["WAYFARER_IMAGE_RESEARCH"])
COMMAND = ["dotnet", "test", "tests/Wayfarer.Tests/Wayfarer.Tests.csproj",
           "-c", "Release", "--no-build", "--no-restore", "--filter",
           "FullyQualifiedName~ImageHelperPerformanceTests.RecordMatchedProfile"]
PROFILES = {"web": (2048, 2048, 90), "pdf": (600, 8192, 85)}
FIXTURES = ["photo.jpg", "photo.webp", "large-vilaplana.jpg", "large-vilaplana.webp"]
CELLS = [(profile, fixture) for profile in PROFILES for fixture in FIXTURES]


def collect(profile, fixture, phase, session):
    """Keep all warmups/raw samples and exact environment, without timing assertions."""
    report = ROOT / "evidence/performance" / f"{profile}-{fixture}-{phase}-{session}.json"
    if report.exists():
        raise RuntimeError(f"Refusing to replace {report}")
    width, height, quality = PROFILES[profile]
    variables = {
        "WAYFARER_IMAGE_PERF_INPUT": fixture,
        "WAYFARER_IMAGE_PERF_REPORT": str(report),
        "WAYFARER_IMAGE_PERF_PHASE": phase,
        "WAYFARER_IMAGE_PERF_REPETITIONS": "24" if phase == "warm" else "1",
        "WAYFARER_IMAGE_PERF_WARMUPS": "20" if phase == "warm" else "0",
        "WAYFARER_IMAGE_PERF_WIDTH": str(width),
        "WAYFARER_IMAGE_PERF_HEIGHT": str(height),
        "WAYFARER_IMAGE_PERF_QUALITY": str(quality),
        "WAYFARER_IMAGE_PERF_ORDER": str(session % 2),
    }
    environment = {**os.environ, **variables}
    environment.pop("WAYFARER_IMAGE_PERF_ORIGINAL", None)
    environment.pop("WAYFARER_IMAGE_PERF_JPEG444", None)
    result = subprocess.run(COMMAND, cwd=ROOT / "source", env=environment,
                            capture_output=True, text=True, timeout=180)
    report.with_suffix(".log").write_text(result.stdout + result.stderr)
    with (ROOT / "evidence/paired-commands.jsonl").open("a") as log:
        log.write(json.dumps({"argv": COMMAND, "cwd": str(ROOT / "source"),
                              "environment": variables, "exit": result.returncode}) + "\n")
    if result.returncode or not report.exists():
        raise RuntimeError(f"Collector failed: {report}: {result.stdout[-2500:]}")


def main():
    """One sequential matrix; no builds, concurrent benchmarks or automatic retries."""
    for session in (1, 2):
        for profile, fixture in CELLS if session == 1 else list(reversed(CELLS)):
            collect(profile, fixture, "warm", session)
            print(f"Warm {session}: {profile}/{fixture}", flush=True)
    for profile, fixture in CELLS:
        for session in range(1, 9):
            collect(profile, fixture, "cold", session)
        print(f"Cold 8 hosts: {profile}/{fixture}", flush=True)
    group = Path("/proc/self/cgroup").read_text().strip().split(":")[-1]
    path = Path("/sys/fs/cgroup" + group)
    counters = {name: (path / name).read_text() for name in
                ("memory.peak", "memory.max", "memory.events", "cpu.stat", "pids.max")}
    (ROOT / "evidence/paired-cgroup.json").write_text(json.dumps(counters, indent=2) + "\n")


if __name__ == "__main__":
    main()
