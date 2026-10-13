#!/usr/bin/env python3
"""Reuse the retained diagnosis statistics and LittleCMS inspection for recovery evidence."""
import argparse
import collections
import hashlib
import io
import json
import math
import os
from pathlib import Path
import statistics

from PIL import Image, ImageChops, ImageCms, ImageStat

ROOT = Path(os.environ.get("WAYFARER_IMAGE_RESEARCH", Path(__file__).resolve().parent))
EVIDENCE = ROOT / "evidence"


def stats(values):
    """Use the retained nearest-rank p95 and inverse-mean sequential transform rate."""
    return {"n": len(values), "p50_ms": statistics.median(values),
            "p95_ms": sorted(values)[math.ceil(.95 * len(values)) - 1],
            "mean_ms": statistics.mean(values), "min_ms": min(values), "max_ms": max(values),
            "stddev_ms": statistics.stdev(values) if len(values) > 1 else 0,
            "sequential_images_per_second": 1000 / statistics.mean(values)}


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
            "display_psnr_db": None if mse == 0 else 10 * math.log10(255 ** 2 / mse),
            "display_pixels_equal": mse == 0,
            "mean_absolute_rgb_delta": ImageStat.Stat(difference).mean,
            "quantization_tables_equal": None if baseline_meta["jpeg_quantization"] is None else
                baseline_meta["jpeg_quantization"] == candidate_meta["jpeg_quantization"]}


def main(variant):
    """Summarize only retained paired measurements from this bounded recovery attempt."""
    summary = {"variant": variant, "statistics": "p50 median; p95 nearest rank; throughput inverse mean wall time",
               "gate": {"maximum_latency_ratio": 1.10, "minimum_throughput_ratio": .90},
               "paired": {}, "quality": [], "direct": {}}
    for profile in ["pdf", "web"]:
        for fixture in ["jpeg", "webp"]:
            key = profile + "-" + fixture
            for phase in ["warm", "cold"]:
                files = sorted((EVIDENCE / "performance" / variant).glob(f"{key}-{phase}-*.json"))
                rows = [row for path in files for row in json.loads(path.read_text()) if row["iteration"] >= 0]
                if not rows:
                    continue
                backends = sorted({row["backend"] for row in rows})
                measurements = {backend: stats([row["milliseconds"] for row in rows if row["backend"] == backend])
                                for backend in backends}
                comparisons = {}
                for baseline in [backend for backend in ["imagesharp", "original"] if backend in measurements]:
                    ratios = {percentile + "_ratio": measurements["rust"][percentile + "_ms"] / measurements[baseline][percentile + "_ms"]
                              for percentile in ["p50", "p95"]}
                    ratios["throughput_ratio"] = (measurements["rust"]["sequential_images_per_second"] /
                                                 measurements[baseline]["sequential_images_per_second"])
                    ratios["pass"] = (ratios["p50_ratio"] <= 1.10 and ratios["p95_ratio"] <= 1.10 and
                                      ratios["throughput_ratio"] >= .90)
                    comparisons[baseline] = ratios
                sessions = {}
                for path in files:
                    session_rows = [row for row in json.loads(path.read_text()) if row["iteration"] >= 0]
                    sessions[path.name] = {backend: stats([row["milliseconds"] for row in session_rows if row["backend"] == backend])
                                          for backend in backends}
                parent_resources = {backend: {
                    "median_parent_cpu_ms": statistics.median([row["parentCpuMilliseconds"] for row in rows if row["backend"] == backend]),
                    "maximum_shared_host_peak_rss_kib": max(row["hostPeakRssKiB"] for row in rows if row["backend"] == backend)}
                    for backend in backends}
                summary["paired"][key + "-" + phase] = {"files": [str(path.relative_to(ROOT)) for path in files],
                                                       "backends": measurements, "comparison": comparisons,
                                                       "sessions": sessions, "parent_resources": parent_resources}
            base = EVIDENCE / "performance" / variant / (key + "-warm-1.json")
            for backend in [backend for backend in ["imagesharp", "original"] if Path(str(base) + "." + backend + ".jpg").exists()]:
                summary["quality"].append(quality_pair(key, Path(str(base) + "." + backend + ".jpg"),
                                                       Path(str(base) + ".rust.jpg"), backend))
    for path in sorted(EVIDENCE.glob(f"direct-{variant}-*.json")):
        rows = json.loads(path.read_text())
        for fixture, mode in sorted({(row["fixture"], row["mode"]) for row in rows}):
            selected = [row for row in rows if row["fixture"] == fixture and row["mode"] == mode and row["iteration"] >= 0]
            stages = collections.defaultdict(list)
            for row in selected:
                for stage in row["stages"]:
                    stages[stage["stage"]].append(stage["ms"])
            summary["direct"][path.stem + ":" + fixture + ":" + mode] = {
                "wall": stats([row["wall_ms"] for row in selected]),
                "median_child_cpu_ms": statistics.median(row["child_cpu_ms"] for row in selected),
                "max_sampled_child_peak_rss_kib": max(row["sampled_child_peak_rss_kib"] for row in selected),
                "median_sampled_child_peak_rss_kib": statistics.median(row["sampled_child_peak_rss_kib"] for row in selected),
                "stages": {name: statistics.median(values) for name, values in stages.items()},
                "pixels_sha256": sorted({row["pixels_sha256"] for row in selected}),
                "output_bytes": sorted({row["output_bytes"] for row in selected})}
    summary["decision"] = "NO-GO" if any(not row["comparison"]["imagesharp"]["pass"]
                                         for row in summary["paired"].values()) else "UNPROVEN"
    (EVIDENCE / f"summary-{variant}.json").write_text(json.dumps(summary, indent=2) + "\n")
    for key, row in summary["paired"].items():
        print(key, row["comparison"]["imagesharp"])
    print("Decision:", summary["decision"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--variant", choices=["native-encode"], default="native-encode")
    main(parser.parse_args().variant)
