#!/usr/bin/env python3
"""Summarize retained and diagnostic timings, without changing gate definitions."""
import collections
import hashlib
import io
import json
import math
from pathlib import Path
import platform
import statistics

from PIL import Image, ImageChops, ImageCms, ImageStat, __version__ as PILLOW_VERSION

ROOT = Path(__file__).resolve().parent
EVIDENCE = ROOT / "evidence"


def stats(values):
    """Use the retained nearest-rank p95 and inverse-mean sequential transform rate."""
    return {"n": len(values), "p50_ms": statistics.median(values),
            "p95_ms": sorted(values)[math.ceil(.95 * len(values)) - 1],
            "mean_ms": statistics.mean(values), "min_ms": min(values), "max_ms": max(values),
            "stddev_ms": statistics.stdev(values) if len(values) > 1 else 0,
            "sequential_images_per_second": 1000 / statistics.mean(values)}


def parse_trace(path):
    """Keep overlapping spans and nested probes distinct in the retained raw trace."""
    rows = []
    current = None
    for line in path.read_text().splitlines():
        if line.startswith("WF_SAMPLE "):
            _, backend, fixture, phase, index = line.split()
            current = {"backend": backend, "fixture": fixture, "phase": phase,
                       "iteration": int(index), "stages": []}
            rows.append(current)
        elif line.startswith(("WF_BASE ", "WF_CS ", "WF_DIAG ")) and current is not None:
            kind, stage, milliseconds = line.split()
            current["stages"].append({"kind": kind, "stage": stage, "ms": float(milliseconds)})
        elif line.startswith("WF_SAMPLE_END ") and current is not None:
            current["wall_ms"] = float(line.split()[1])
    return rows


def stage_statistics(rows, backend):
    """Number repeated decoder probes; compute the hypothetical budget on each sample."""
    samples = collections.defaultdict(list)
    residuals = []
    kept = []
    for row in rows:
        if row["backend"] != backend or row["iteration"] < 0:
            continue
        counts = collections.defaultdict(int)
        for stage in row["stages"]:
            number = counts[stage["stage"]]
            counts[stage["stage"]] += 1
            samples[f"{stage['kind']}.{stage['stage']}#{number}"].append(stage["ms"])
        if backend == "rust":
            stages = {x["stage"]: x["ms"] for x in row["stages"] if x["kind"] == "WF_DIAG"}
            residual = row["wall_ms"] - stages["run"]
            residuals.append(residual)
            kept.append(sum(stages[k] for k in ["source_decode", "resize", "encode", "output_verify"]) + residual)
    result = {key: stats(values) for key, values in samples.items()}
    if backend == "rust":
        result["outside_rust_run"] = stats(residuals)
        result["hypothetical_kept_stages_budget"] = stats(kept)
    return result


def inspect_image(path):
    """Render each embedded profile to the same independent LittleCMS sRGB reference."""
    with Image.open(path) as image:
        image.load()
        profile = image.info.get("icc_profile")
        rgb = image.convert("RGB")
        if profile:
            rgb = ImageCms.profileToProfile(rgb, ImageCms.ImageCmsProfile(io.BytesIO(profile)),
                                           ImageCms.createProfile("sRGB"), renderingIntent=1, outputMode="RGB")
        metadata = {"path": str(path.relative_to(ROOT)), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    "bytes": path.stat().st_size, "geometry": image.size, "mode": image.mode,
                    "icc_bytes": len(profile or b""), "jpeg_sampling": getattr(image, "layer", None),
                    "jpeg_quantization": getattr(image, "quantization", None),
                    "exif_orientation": image.getexif().get(274, 1),
                    "alpha_range": image.convert("RGBA").getchannel("A").getextrema()}
        return rgb, metadata


def quality_pair(fixture, baseline, candidate, reference):
    """Report differences as observations; PSNR supplies no perceptual acceptance threshold."""
    baseline_image, baseline_meta = inspect_image(baseline)
    candidate_image, candidate_meta = inspect_image(candidate)
    difference = ImageChops.difference(baseline_image, candidate_image)
    count = baseline_image.width * baseline_image.height * 3
    mse = sum((index % 256) ** 2 * amount for index, amount in enumerate(difference.histogram())) / count
    return {"fixture": fixture, "reference": reference, "baseline": baseline_meta, "rust": candidate_meta,
            "display_psnr_db": 10 * math.log10(255 ** 2 / mse),
            "mean_absolute_rgb_delta": ImageStat.Stat(difference).mean,
            "quantization_tables_equal": None if baseline_meta["jpeg_quantization"] is None else
                baseline_meta["jpeg_quantization"] == candidate_meta["jpeg_quantization"]}


def main():
    """Serialize complete small-workload evidence and the unchanged retained summary."""
    summary = {"statistic_definition": "p50 median; p95 nearest rank; throughput inverse mean wall time",
               "current_warm": {}, "supplemental_fidelity": {}, "stage_statistics": {},
               "direct": {}, "io_only": {}, "controls": {}, "quality": [], "retained_recalculation": []}
    for name in ["jpeg", "webp"]:
        for phase, key in [("warm", "current_warm"), ("fidelity-control", "supplemental_fidelity")]:
            rows = json.loads((EVIDENCE / f"csharp-{name}-{phase}.json").read_text())
            trace = parse_trace(EVIDENCE / f"csharp-{name}-{phase}.log")
            (EVIDENCE / f"csharp-{name}-{phase}-stages.json").write_text(json.dumps(trace, indent=2) + "\n")
            observed = {backend: stats([x["milliseconds"] for x in rows if x["backend"] == backend and x["iteration"] >= 0])
                        for backend in ["imagesharp", "rust"]}
            for percentile in ["p50", "p95"]:
                observed[percentile + "_ratio"] = observed["rust"][percentile + "_ms"] / observed["imagesharp"][percentile + "_ms"]
            observed["throughput_ratio"] = observed["rust"]["sequential_images_per_second"] / observed["imagesharp"]["sequential_images_per_second"]
            summary[key][name] = observed
            summary["stage_statistics"][name + "-" + phase] = {b: stage_statistics(trace, b) for b in ["imagesharp", "rust"]}
    for filename, key in [("direct.json", "direct"), ("io-control.json", "io_only"), ("icc-alpha-controls.json", "controls")]:
        rows = json.loads((EVIDENCE / filename).read_text())
        for fixture, mode in sorted({(r["fixture"], r["mode"]) for r in rows}):
            selected = [r for r in rows if r["fixture"] == fixture and r["mode"] == mode and r["iteration"] >= 0]
            samples = collections.defaultdict(list)
            for row in selected:
                counts = collections.defaultdict(int)
                for stage in row["stages"]:
                    number = counts[stage["stage"]]
                    counts[stage["stage"]] += 1
                    samples[f"{stage['stage']}#{number}"].append(stage["ms"])
            summary[key][fixture + ":" + mode] = {"wall": stats([r["wall_ms"] for r in selected]),
                "child_cpu": stats([r["child_cpu_ms"] for r in selected]), "stages": {k: stats(v) for k, v in samples.items()},
                "pixels_sha256": sorted({r["pixels_sha256"] for r in selected})}
    for fixture in ["photo.jpg", "photo.webp", "opaque.png", "photo.gif"]:
        suffix = ".jpg" if fixture.endswith((".jpg", ".webp")) else ".png"
        prefix = ROOT / "retained-evidence/performance" / (fixture + ".warm.0.json")
        summary["quality"].append(quality_pair(fixture, Path(str(prefix) + ".imagesharp" + suffix),
            Path(str(prefix) + ".rust" + suffix), "each embedded ICC rendered to one LittleCMS sRGB reference"))
        for phase in ["warm", "cold"]:
            rows = []
            for path in sorted((ROOT / "retained-evidence/performance").glob(fixture + "." + phase + ".*.json")):
                rows.extend(json.loads(path.read_text()))
            observed = {b: stats([r["milliseconds"] for r in rows if r["backend"] == b and r["iteration"] >= 0]) for b in ["imagesharp", "rust"]}
            summary["retained_recalculation"].append({"fixture": fixture, "phase": phase, "stats": observed})
    for name in ["jpeg", "webp"]:
        prefix = EVIDENCE / f"csharp-{name}-fidelity-control.json"
        summary["quality"].append(quality_pair(name, Path(str(prefix) + ".imagesharp.jpg"),
            Path(str(prefix) + ".rust.jpg"), "supplemental linear-light/4:4:4 reference; same LittleCMS display transform"))
    summary["analysis_tools"] = {"python": platform.python_version(), "Pillow": PILLOW_VERSION,
                                 "LittleCMS": ImageCms.core.littlecms_version}
    (EVIDENCE / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print("Wrote evidence/summary.json")


if __name__ == "__main__":
    main()
