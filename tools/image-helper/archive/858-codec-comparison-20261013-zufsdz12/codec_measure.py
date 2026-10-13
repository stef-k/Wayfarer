#!/usr/bin/env python3
"""Finite codec matrix using existing normalized pixels, limits and retained corpus."""
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent
BIN = ROOT / "target/release/examples/codec_bench"
FIXTURES = ROOT / "evidence/fixtures"
OUT = ROOT / "outputs/micro"
LIMITS = ["/usr/bin/prlimit", "--as=1073741824:1073741824", "--cpu=20:25",
          "--nofile=128:128", "--core=0:0", "--", str(BIN)]


def invoke(name, arguments):
    """Save exact argv and every stdout observation; do not hide candidate failures."""
    command = [*LIMITS, *map(str, arguments)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60)
    with (ROOT / "evidence/micro-commands.jsonl").open("a") as log:
        log.write(json.dumps({"name": name, "argv": command, "exit": result.returncode,
                              "stderr": result.stderr}) + "\n")
    if result.returncode:
        raise RuntimeError(f"{name}: exit {result.returncode}: {result.stderr}")
    rows = [json.loads(line) for line in result.stdout.splitlines()]
    (ROOT / f"evidence/micro/{name}.json").write_text(json.dumps(rows, indent=2) + "\n")
    return rows


def main():
    """Two independently ordered sessions; preparation and file writes are untimed."""
    OUT.mkdir(parents=True)
    (ROOT / "evidence/micro").mkdir()
    decoded = {"photo.jpg": ["jpeg-decoder", "zune-jpeg", "libjpeg-turbo-rs"],
               "photo.webp": ["image-webp", "libwebp"]}
    for session in [1, 2]:
        for fixture, codecs in decoded.items():
            for codec in codecs if session == 1 else list(reversed(codecs)):
                invoke(f"decode-{fixture}-{codec}-{session}",
                       ["decode", codec, FIXTURES / fixture,
                        OUT / f"decode-{fixture}-{codec}.raw", 24])
        print(f"Decode session {session} complete", flush=True)
    for profile, width, height, quality in [("web", 2048, 2048, 90), ("pdf", 600, 8192, 85)]:
        for fixture in decoded:
            raw = OUT / f"{profile}-{fixture}.rgb"
            geometry = invoke(f"prepare-{profile}-{fixture}",
                              ["prepare", FIXTURES / fixture, raw, width, height, quality])[0]
            geometry["sha256"] = hashlib.sha256(raw.read_bytes()).hexdigest()
            (OUT / f"{profile}-{fixture}.json").write_text(json.dumps(geometry, indent=2) + "\n")
            codecs = ["jpeg-encoder", "libjpeg-turbo-rs"]
            if fixture.endswith("webp"):
                codecs += ["libwebp-lossy", "image-webp-lossless", "libwebp-lossless"]
            for session in [1, 2]:
                for codec in codecs if session == 1 else list(reversed(codecs)):
                    extension = "jpg" if codec in ["jpeg-encoder", "libjpeg-turbo-rs"] else "webp"
                    invoke(f"encode-{profile}-{fixture}-{codec}-q{quality}-{session}",
                           ["encode", codec, raw, OUT / f"{profile}-{fixture}-{codec}-q{quality}.{extension}",
                            geometry["width"], geometry["height"], quality, 12, 3])
            print(f"Encode {profile}/{fixture} complete", flush=True)
            if fixture.endswith("webp"):
                for webp_quality in [75, 80, 85, 90, 95, 100]:
                    invoke(f"calibrate-{profile}-q{webp_quality}",
                           ["encode", "libwebp-lossy", raw,
                            OUT / f"{profile}-{fixture}-libwebp-lossy-q{webp_quality}.webp",
                            geometry["width"], geometry["height"], webp_quality, 1, 3])
                print(f"Quality calibration {profile} complete", flush=True)


if __name__ == "__main__":
    main()
