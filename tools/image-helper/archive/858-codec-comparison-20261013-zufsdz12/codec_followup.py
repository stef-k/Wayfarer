#!/usr/bin/env python3
"""Refine only quality matching and alpha controls; record readily available native JPEG reference."""
import io
import json
from pathlib import Path
import resource
import time

from PIL import Image, features
import codec_measure
from quality_codec import difference

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "outputs/micro"


def calibrated_webp():
    """Bracket the observed fidelity crossing rather than equating JPEG and WebP Q numbers."""
    for profile, qualities in [("web", [86,87,88,89]), ("pdf", [76,77,78,79])]:
        geometry = json.loads((OUT / f"{profile}-photo.webp.json").read_text())
        reference = Image.frombytes("RGB", (geometry["width"], geometry["height"]),
                                    (OUT / f"{profile}-photo.webp.rgb").read_bytes())
        for quality in qualities:
            codec_measure.invoke(f"calibrate-extra-{profile}-q{quality}",
                ["encode", "libwebp-lossy", OUT / f"{profile}-photo.webp.rgb",
                 OUT / f"{profile}-photo.webp-libwebp-lossy-q{quality}.webp",
                 geometry["width"], geometry["height"], quality, 1, 3])
        with Image.open(OUT / f"{profile}-photo.webp-jpeg-encoder-q{90 if profile == 'web' else 85}.jpg") as image:
            target = difference(reference, image.convert("RGB"))["psnr_db"]
        eligible = []
        for path in OUT.glob(f"{profile}-photo.webp-libwebp-lossy-q*.webp"):
            with Image.open(path) as image:
                score = difference(reference, image.convert("RGB"))["psnr_db"]
                if score >= target: eligible.append(int(path.stem.rsplit("q",1)[1]))
        quality = min(eligible)
        for session in [1,2]:
            codec_measure.invoke(f"encode-{profile}-photo.webp-libwebp-lossy-q{quality}-matched-{session}",
                ["encode", "libwebp-lossy", OUT / f"{profile}-photo.webp.rgb",
                 OUT / f"{profile}-photo.webp-libwebp-lossy-q{quality}.webp",
                 geometry["width"], geometry["height"], quality, 12, 3])
        print(f"Matched proxy quality {profile}: WebP Q{quality}, JPEG PSNR {target:.3f}", flush=True)


def alpha_control():
    """The retained still RGBA WebP provides identical codec-only alpha input without new fixtures."""
    for codec in ["image-webp","libwebp"]:
        codec_measure.invoke(f"decode-alpha-{codec}", ["decode", codec, ROOT / "evidence/fixtures/static.webp",
                                                       OUT / f"alpha-{codec}.rgba", 12])
    baseline = (OUT / "alpha-image-webp.rgba").read_bytes()
    native = (OUT / "alpha-libwebp.rgba").read_bytes()
    rows = {"decoded_rgba_equal": baseline == native, "outputs": {}}
    for codec in ["image-webp-lossless","libwebp-lossy","libwebp-lossless"]:
        output = OUT / f"alpha-{codec}.webp"
        codec_measure.invoke(f"encode-alpha-{codec}", ["encode", codec, OUT / "alpha-image-webp.rgba",
                                                       output, 64, 32, 90, 12, 4])
        with Image.open(output) as image:
            rgba = image.convert("RGBA").tobytes()
            rows["outputs"][codec] = {"bytes": output.stat().st_size, "alpha_exact": rgba[3::4] == baseline[3::4],
                                      "alpha_extrema": image.convert("RGBA").getchannel("A").getextrema()}
    (ROOT / "evidence/alpha-codec.json").write_text(json.dumps(rows,indent=2)+"\n")
    print("Retained alpha codec control complete", flush=True)


def native_jpeg_reference():
    """Use the existing Pillow/libjpeg-turbo build as a supplemental control, with no installation."""
    assert features.check_feature("libjpeg_turbo")
    rows = []
    tasks = []
    source = (ROOT / "evidence/fixtures/photo.jpg").read_bytes()
    def decode():
        """Return owned packed RGB bytes, including Pillow's necessary output copy."""
        with Image.open(io.BytesIO(source)) as image: return image.convert("RGB").tobytes()
    tasks.append(("native-jpeg-decode",decode,OUT / "native-jpeg-decode.rgb"))
    for profile, quality in [("web",90),("pdf",85)]:
        for fixture in ["photo.jpg","photo.webp"]:
            geometry = json.loads((OUT / f"{profile}-{fixture}.json").read_text())
            image = Image.frombytes("RGB", (geometry["width"],geometry["height"]), (OUT / f"{profile}-{fixture}.rgb").read_bytes())
            with Image.open(OUT / f"{profile}-{fixture}-jpeg-encoder-q{quality}.jpg") as jpeg:
                profile_bytes = jpeg.info["icc_profile"]
            def encode(pixels=image, q=quality, icc=profile_bytes):
                """Explicit box-average 4:2:0, baseline entropy and identical ICC/quality controls."""
                output=io.BytesIO()
                pixels.save(output,format="JPEG",quality=q,subsampling=2,progressive=False,optimize=False,icc_profile=icc)
                return output.getvalue()
            tasks.append((f"native-jpeg-encode-{profile}-{fixture}",encode,OUT / f"native-{profile}-{fixture}.jpg"))
    for session in [1,2]:
        for name, operation, path in tasks if session == 1 else list(reversed(tasks)):
            for iteration in range(-3,24):
                before=resource.getrusage(resource.RUSAGE_SELF)
                start=time.perf_counter_ns(); output=operation(); elapsed=(time.perf_counter_ns()-start)/1e6
                after=resource.getrusage(resource.RUSAGE_SELF)
                rows.append({"case":name,"session":session,"iteration":iteration,"milliseconds":elapsed,
                    "cpu_ms":((after.ru_utime+after.ru_stime)-(before.ru_utime+before.ru_stime))*1000,
                    "bytes":len(output),"process_peak_rss_kib":after.ru_maxrss})
                if iteration == 0: path.write_bytes(output)
    (ROOT / "evidence/native-jpeg-control.json").write_text(json.dumps({"libjpeg_turbo_version":features.version_feature("libjpeg_turbo"),
        "method":"Installed Pillow; not a third CLI integration or production qualification", "rows":rows},indent=2)+"\n")
    print("Installed native JPEG reference complete", flush=True)


if __name__ == "__main__":
    codec_measure.BIN = ROOT / "bin/codec-bench-measured"
    codec_measure.LIMITS[-1] = str(codec_measure.BIN)
    calibrated_webp()
    alpha_control()
    native_jpeg_reference()
