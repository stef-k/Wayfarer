# Rust imaging research archive (#858)

**2026-10-15 pure-Rust WebP fallback: NO-GO.** The single authorized comparison
misses the small-WebP PDF warm-p50 gate and reproduces a Release-mode malformed
VP8L decoder abort. See [WEBP-FALLBACK.md](WEBP-FALLBACK.md). The selected runtime,
features, lockfile and earlier optimizations remain unchanged; this adds evidence only.

**2026-10-15 Linux follow-up: BLOCKED; ARM64 UNPROVEN.** The default-only
Release artifact is now measured and identified. A retained JPEG warm-p50 gate
deviation, unresolved native WebP hardening and a missing wrapper license notice
remain explicit. See [LINUX-QUALIFICATION.md](LINUX-QUALIFICATION.md) and
[native codec notes](notices/NATIVE-CODECS.md). The Linux architecture guards
admit ARM64 with identical containment; native ARM64 execution is unqualified.

**2026-10-15 Stage 1: BLOCKED.** See [QUALIFICATION.md](QUALIFICATION.md) for
the final bounded readiness measurements, actual platform coverage, resource
observations and artifact limitations. The historical result below is preserved.

**Production NO-GO.** This branch selects the bounded native TurboJPEG encoder,
strict guarded pure Rust JPEG decoding, native WebP decoding, SIMD RGB resizing
and the recovered opaque-sRGB pipeline. Both warm JPEG p50 gates still fail;
WebP passes the observed web/PDF gates. This is a recoverable research archive,
with no production integration or shipping approval. See [REPORT.md](REPORT.md)
for the final single-pass assignment, measurements, caps and retained evidence.

This Linux prototype follows the [accepted design](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6131203483).
It does not connect to the production image proxy, HTTP or cache.

Build with Rust 1.90.0 using `cargo build --locked --release`. Native builds also
require a C compiler, CMake and NASM on x64; use the pinned vendored sources.
Default features enable the selected decoder/encoder routes. The archive pins
`fast_image_resize` 6.1.0 for RGB8 Lanczos3 and `turbojpeg` 1.5.1 /
`turbojpeg-sys` 1.2.0 (bundled libjpeg-turbo 3.1.0). Runtime detection selects
supported CPU instructions; alpha and precision routes retain the original resize.
JPEG qualities up to 90 use averaged photographic 4:2:0; higher-quality explicit
recipes retain 4:4:4. Canonical ICC embedding and complete output verification remain.
The dependency license texts and IJG acknowledgment are in `notices/`.
Invoke the resulting `target/release/wayfarer-image` executable through
`/usr/bin/prlimit` with address space 1 GiB, CPU 20/25 seconds, descriptors 128
and core dumps zero. Set `WAYFARER_IMAGE_PARENT_PID` to the launching process PID.
The launch thread must survive until the child has been reaped.

All six recipe options remain mandatory. Ordinary web settings are resolved in the
standalone C# `ImageTransform` constructor, then passed explicitly to the CLI:

```text
transform --width 2048 --height 2048 --quality 90 --output-format auto
          --max-input-bytes 52428800 --max-output-bytes 52428800
```

The explicit existing PDF/print recipe remains `--width 600 --height 8192
--quality 85`. Caller bounds and quality override defaults within mandatory
safety ceilings. Supplying only one dimension leaves the other unconstrained
except by the 8192-axis ceiling. No image is enlarged. The separately resolved
`optimize=false` high-fidelity intent requires explicit dimensions and quality;
it is not mapped to the ordinary web defaults. This CLI has no optimize flag,
and production HTTP/cache handling of that alias is unchanged.

Stdin supplies one bounded image through EOF. Stdout contains only a completed,
decoded and verified JPEG/PNG derivative. Diagnostics are fixed strings.
Exit 2 means invalid trusted arguments, 3 positive source limits, 4 malformed
or unsupported source/color, and 70 processing/control/I/O unavailability.
Every nonzero exit invalidates stdout. The supervisor must also reject crashes,
timeouts and output overflow as unavailable.

The executable supports static JPEG, PNG, WebP and GIF. PNG admission verifies CRCs, exact
container EOF, exact filtered-byte count and complete checksum-verified zlib
termination using the accepted immutable E+1 buffer. It rejects APNG declarations.
ICC conversion preserves JPEG CMYK channels and PNG 16-bit precision. Untagged
CMYK uses multiplicative conversion. EXIF orientation includes post-IDAT PNG eXIf.
Opaque 8-bit inputs use conventional sRGB Lanczos3 without float-raster expansion,
gamma round trips or alpha arithmetic in the application. Untagged RGB bytes are
reused, and opaque RGBA is compacted in its owned buffer. Tagged sources retain
bounded relative-colorimetric CMS conversion; no profile-name shortcut is used.
Transparent, CMYK and 16-bit inputs retain the original scene-linear
premultiplied-alpha processing. The pinned library's resampler still uses a float
intermediate internally. The selected JPEG encoder writes into one fallibly
allocated caller-owned buffer whose extent includes the canonical ICC and never
exceeds the recipe's cap. The native borrowed-slice API sets `TJPARAM_NOREALLOC`;
overflow fails during encoding. A completely full entropy destination can be
conservatively refused. No partial result escapes. `--no-default-features`
retains the earlier pure Rust SIMD encoder/decoders for reproduction; the rejected
`libjpeg-turbo-rs compress_into()` encoder remains a benchmark-only trial.
Fresh encoders retain only generated canonical sRGB ICC metadata.

JPEG ICC assemblies that the decoder cannot assemble are treated as untagged,
as accepted in the design. High-precision lossless JPEG (L16) is rejected: the
pinned API does not expose its sample precision for correct normalization.

WebP admission extends the existing bounded RIFF authority. Public codec APIs
read payload geometry/alpha; no VP8 parser is implemented. GIF streaming events
check the entire container, declarations, pixel count and palette indices.
The public configured GIF decoder provides strict LZW termination and rendering.
An extra real output sink continues past raster completion to DataEnd.

The standalone C# adapter lives in `supervisor/`. Build and demonstrate it with:

```sh
dotnet build supervisor/ImageHelper.Supervisor.csproj -c Release
dotnet supervisor/bin/Release/net10.0/ImageHelper.Supervisor.dll \
  "$PWD/target/release/wayfarer-image" INPUT OUTPUT
```

The fixed helper path is trusted operator/startup configuration. The adapter
uses four process-wide, non-queuing slots and a dedicated launch thread per child.
Timeout/cancellation/overflow kill and reap before completion; failed two-second
cleanup disables admission and retains the occupied slot. Only successful
canonical JPEG/PNG output carries bytes. No source diagnostics are forwarded.
Source-to-verified-output timing includes child launch, IPC and cleanup.

Focused xUnit cases reuse `tests/Wayfarer.Tests`. Set `WAYFARER_IMAGE_HELPER`
and `WAYFARER_IMAGE_FIXTURES` to the executable and copied/generated local
fixtures, then select `ImageHelperPrototype` and `ImageHelperPngCompletion`.
The observation-only `ImageHelperPerformanceTests` is skipped unless its
explicit local input/report environment variables are set. It never asserts timings.

See [PROTOTYPE.md](PROTOTYPE.md) for the original retained outcome, and
[RECOVERY.md](RECOVERY.md) for the bounded web-performance recovery decision.
See [SIMD.md](SIMD.md) for the separate resize-only, encoder-only and combined
experiment, measured source identities, retained evidence and stop decision.
