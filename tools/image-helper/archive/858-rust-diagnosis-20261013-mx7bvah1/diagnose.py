#!/usr/bin/env python3
"""Finite single-image subprocess measurements using retained fixtures and limits."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import resource
import subprocess
import time

from PIL import Image

ROOT = Path(__file__).resolve().parent
LIMITS = ["--as=1073741824:1073741824", "--cpu=20:25", "--nofile=128:128", "--core=0:0", "--"]
RECIPE = ["transform", "--width", "600", "--height", "8192", "--quality", "85",
          "--output-format", "auto", "--max-input-bytes", "52428800",
          "--max-output-bytes", "52428800"]


def measure(fixture, mode, index):
    """Time launch through communicate/reap; inspect output only after the timer."""
    path = ROOT / "retained-evidence" / "fixtures" / fixture
    if not path.exists():
        path = ROOT / "controls" / fixture
    source = path.read_bytes()
    executable = ROOT / "bin" / "wayfarer-image-original"
    if mode == "instrumented":
        executable = ROOT / "source/tools/image-helper/target/release/wayfarer-image"
    command = ["/usr/bin/prlimit", *LIMITS, str(executable), *RECIPE]
    if mode == "io":
        command = ["/usr/bin/prlimit", *LIMITS, "/usr/bin/cat"]
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    start = time.perf_counter_ns()
    child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE,
                             env={**os.environ, "WAYFARER_IMAGE_PARENT_PID": str(os.getpid())})
    launched = time.perf_counter_ns()
    try:
        output, trace = child.communicate(source, timeout=30)
    except subprocess.TimeoutExpired:
        child.kill()
        child.communicate(timeout=2)
        raise
    end = time.perf_counter_ns()
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    if child.returncode != 0:
        raise RuntimeError(f"{fixture}: exit {child.returncode}, {trace.decode()}")
    stages = []
    for line in trace.decode().splitlines():
        if line.startswith("WF_DIAG "):
            _, name, milliseconds = line.split()
            stages.append({"stage": name, "ms": float(milliseconds)})
    with Image.open(io.BytesIO(output)) as image:
        pixels = image.convert("RGBA").tobytes()
        geometry = image.size
        sampling = getattr(image, "layer", None)
    row = {"mode": mode, "fixture": fixture, "iteration": index,
           "wall_ms": (end-start)/1e6, "popen_ms": (launched-start)/1e6,
           "child_cpu_ms": ((after.ru_utime-before.ru_utime)+(after.ru_stime-before.ru_stime))*1000,
           "output_bytes": len(output), "geometry": geometry, "jpeg_sampling": sampling,
           "output_sha256": hashlib.sha256(output).hexdigest(),
           "pixels_sha256": hashlib.sha256(pixels).hexdigest(), "stages": stages,
           "stderr": trace.decode()}
    if index == 0:
        destination = ROOT / "outputs" / f"{fixture}.{mode}.bin"
        destination.parent.mkdir(exist_ok=True)
        destination.write_bytes(output)
    return row


def main():
    """Run a bounded sequence; this is a diagnostic collector, not a parity gate."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", nargs="+", default=["photo.jpg", "photo.webp", "opaque.png", "photo.gif"])
    parser.add_argument("--modes", nargs="+", choices=["original", "instrumented", "io"], default=["original", "instrumented"])
    parser.add_argument("--count", type=int, default=12)
    parser.add_argument("--warmups", type=int, default=3)
    parser.add_argument("--report", default="evidence/direct.json")
    args = parser.parse_args()
    if not 1 <= args.count <= 40 or not 0 <= args.warmups <= 20:
        parser.error("finite measurement bounds exceeded")
    rows = []
    for fixture in args.fixtures:
        for index in range(-args.warmups, args.count):
            order = args.modes if index % 2 == 0 else list(reversed(args.modes))
            for mode in order:
                rows.append(measure(fixture, mode, index))
        print(f"Completed {fixture}: {args.count} measured samples per mode", flush=True)
    (ROOT / args.report).write_text(json.dumps(rows, indent=2)+"\n")


if __name__ == "__main__":
    main()
