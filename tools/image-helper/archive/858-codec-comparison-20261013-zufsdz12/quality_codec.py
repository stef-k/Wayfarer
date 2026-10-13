#!/usr/bin/env python3
"""Inspect codec-only observations with the retained statistics and LittleCMS display seam."""
import collections
import hashlib
import json
import math
from pathlib import Path
import statistics

from PIL import Image, ImageChops, ImageStat, ImageDraw
from analyze import inspect_image, stats

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "outputs/micro"


def difference(reference, candidate):
    """PSNR is a matched-pixel fidelity proxy, never a blind perceptual acceptance gate."""
    delta = ImageChops.difference(reference, candidate)
    histogram = delta.histogram()
    mse = sum((i % 256) ** 2 * count for i, count in enumerate(histogram)) / (reference.width * reference.height * 3)
    return {"psnr_db": None if mse == 0 else 10 * math.log10(255 ** 2 / mse),
            "mean_absolute_rgb": ImageStat.Stat(delta).mean,
            "maximum_channel_delta": max(hi for lo, hi in delta.getextrema()), "pixels_equal": mse == 0}


def main():
    """Summarize exact raw samples, independently decoded outputs and separately calibrated WebP quality."""
    groups = collections.defaultdict(list)
    for path in sorted((ROOT / "evidence/micro").glob("*.json")):
        if path.name.startswith(("prepare", "calibrate")): continue
        groups[path.stem.rsplit("-", 1)[0]].extend(row for row in json.loads(path.read_text()) if row["iteration"] >= 0)
    summary = {"timings": {}, "quality": {}, "decode_pixels": {}, "matched_webp_quality": {}}
    for key, rows in groups.items():
        summary["timings"][key] = {**stats([row["milliseconds"] for row in rows]),
            "median_cpu_ms": statistics.median(row["cpu_ms"] for row in rows),
            "maximum_cumulative_process_peak_rss_kib": max(row["process_peak_rss_kib"] for row in rows),
            "output_bytes": sorted({row["bytes"] for row in rows})}
    for fixture, baseline, alternatives in [("photo.jpg", "jpeg-decoder", ["zune-jpeg", "libjpeg-turbo-rs"]),
                                            ("photo.webp", "image-webp", ["libwebp"])]:
        reference = Image.frombytes("RGB", (1920, 863), (OUT / f"decode-{fixture}-{baseline}.raw").read_bytes())
        for codec in alternatives:
            data = (OUT / f"decode-{fixture}-{codec}.raw").read_bytes()
            actual = Image.frombytes("RGB", reference.size, data)
            summary["decode_pixels"][fixture + ":" + codec] = {**difference(reference, actual),
                "sha256": hashlib.sha256(data).hexdigest()}
    for profile, jpeg_quality in [("web", 90), ("pdf", 85)]:
        for fixture in ["photo.jpg", "photo.webp"]:
            geometry = json.loads((OUT / f"{profile}-{fixture}.json").read_text())
            reference = Image.frombytes("RGB", (geometry["width"], geometry["height"]),
                                        (OUT / f"{profile}-{fixture}.rgb").read_bytes())
            reference.save(OUT / f"{profile}-{fixture}-reference.png")
            for path in sorted(OUT.glob(f"{profile}-{fixture}-*")):
                if path.suffix not in [".jpg", ".webp"]: continue
                actual, metadata = inspect_image(path)
                summary["quality"][path.name] = {**metadata, **difference(reference, actual)}
            if fixture.endswith("webp"):
                jpeg = summary["quality"][f"{profile}-{fixture}-jpeg-encoder-q{jpeg_quality}.jpg"]
                candidates = [(q, summary["quality"][f"{profile}-{fixture}-libwebp-lossy-q{q}.webp"])
                              for q in [75,76,77,78,79,80,85,86,87,88,89,90,95,100]
                              if f"{profile}-{fixture}-libwebp-lossy-q{q}.webp" in summary["quality"]]
                qualified = [(q, record) for q, record in candidates if record["psnr_db"] >= jpeg["psnr_db"]]
                matched = min(qualified, key=lambda item: item[0]) if qualified else None
                summary["matched_webp_quality"][profile] = {"jpeg_quality": jpeg_quality,
                    "jpeg_psnr_db": jpeg["psnr_db"], "webp_quality": matched[0] if matched else None,
                    "webp_psnr_db": matched[1]["psnr_db"] if matched else None,
                    "basis": "lowest sampled WebP Q with no lower RGB PSNR, plus separately inspected crops; not numerical Q equivalence"}
                images = [("normalized", reference), (f"JPEG Q{jpeg_quality}", inspect_image(OUT / jpeg["path"].split("/")[-1])[0]),
                          (f"WebP Q{matched[0]}", inspect_image(OUT / matched[1]["path"].split("/")[-1])[0])]
                crop_size = (400, 260) if profile == "web" else (300, 240)
                sheet = Image.new("RGB", (crop_size[0] * 3, crop_size[1] + 28), "white")
                draw = ImageDraw.Draw(sheet)
                x = reference.width // 2 - crop_size[0] // 2
                y = reference.height // 2 - crop_size[1] // 2
                for index, (label, picture) in enumerate(images):
                    sheet.paste(picture.crop((x, y, x + crop_size[0], y + crop_size[1])), (index * crop_size[0], 28))
                    draw.text((index * crop_size[0] + 8, 6), label, fill="black")
                sheet.save(ROOT / f"outputs/quality-{profile}-webp.png")
    (ROOT / "evidence/codec-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({"decode_pixels": summary["decode_pixels"], "matched_webp_quality": summary["matched_webp_quality"]}, indent=2))


if __name__ == "__main__":
    main()
