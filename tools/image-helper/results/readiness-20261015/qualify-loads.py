#!/usr/bin/env python3
"""Run only the six finite standalone observations through the existing xUnit host."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(os.environ["WAYFARER_IMAGE_RESEARCH"])
COMMAND = ["dotnet", "test", "tests/Wayfarer.Tests/Wayfarer.Tests.csproj", "-c", "Release",
           "--no-build", "--no-restore", "--filter",
           "FullyQualifiedName~ImageHelperPerformanceTests.RecordStandaloneLoad"]


def main():
    """Separate process/cgroup per case preserves attributable kernel memory high water."""
    directory = ROOT / "evidence/loads"
    directory.mkdir()
    for mode, count in [("batch", 1), ("batch", 4), ("batch", 10), ("batch", 20),
                        ("burst", 20), ("cancel", 4)]:
        report = directory / f"{mode}-{count}.json"
        variables = {name: os.environ[name] for name in
                     ("WAYFARER_IMAGE_HELPER", "WAYFARER_IMAGE_FIXTURES", "NUGET_PACKAGES", "DOTNET_CLI_HOME")}
        variables.update({"WAYFARER_IMAGE_PERF_INPUT": "photo.jpg",
                          "WAYFARER_IMAGE_PERF_REPORT": str(report),
                          "WAYFARER_IMAGE_LOAD_MODE": mode, "WAYFARER_IMAGE_LOAD_COUNT": str(count)})
        command = ["systemd-run", "--user", f"--unit=wayfarer-858-{mode}-{count}-20261015",
                   "--wait", "--pipe", "-p", "MemoryMax=3G", "-p", "MemorySwapMax=0",
                   "-p", "TasksMax=192", "-p", "CPUQuota=400%", f"--working-directory={ROOT / 'source'}",
                   *[f"--setenv={key}={value}" for key, value in variables.items()], *COMMAND]
        result = subprocess.run(command, capture_output=True, text=True, timeout=120)
        report.with_suffix(".log").write_text(result.stdout + result.stderr)
        with (ROOT / "evidence/load-commands.jsonl").open("a") as log:
            log.write(json.dumps({"argv": command, "exit": result.returncode}) + "\n")
        if result.returncode or not report.exists():
            raise RuntimeError(f"Observation failed: {mode}/{count}: {result.stdout[-1500:]} {result.stderr}")
        data = json.loads(report.read_text())
        if not data["helpersReaped"] or not all(row["Reaped"] for row in data["observations"]):
            raise RuntimeError(f"Child cleanup failure: {report}")
        if mode == "batch" and data["success"] != count:
            raise RuntimeError(f"Finite admitted batch failure: {report}")
        if mode == "burst" and (data["success"], data["rejected"]) != (4, 16):
            raise RuntimeError(f"Non-queuing burst failure: {report}")
        if mode == "cancel" and (data["canceled"] == 0 or data["recovery"] != "Success"):
            raise RuntimeError(f"Cancellation/recovery failure: {report}")
        print(mode, count, json.dumps({key: value for key, value in data.items()
                                     if key not in ("observations", "cpuStat")}), flush=True)


if __name__ == "__main__":
    main()
