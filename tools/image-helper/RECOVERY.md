# Wayfarer #858: bounded web image performance recovery

**NO-GO.** The existing Rust prototype improves substantially, but every warm
JPEG/WebP profile still fails the accepted latency and throughput gates.
No additional optimization iteration, PNG/GIF performance extension or trip-load
qualification was undertaken.

Authority: [approved recovery scope](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6150358778).
Workspace: `/home/stef/wayfarer-research/858-web-recovery-20261013-93ijKYrk`.
Branch: `wip/858-web-image-recovery-20261013`.
Original prototype: `c998da079838894da32cb331f4f338cd9597047f`.
Processing checkpoint: `27b53d23`.
Exact measured source/collector head: `220b1be574b0cc0898f04ee4aacc846872470e71`.
The final documentation head is recorded in the workspace's `ARTIFACTS.json`.

The independent clone, toolchains, package caches, executables and fixtures are
scratch-owned. Protected source/report hashes and Git identities are verified in
`evidence/preservation-after.json`. The diagnosis report matched the approved
SHA-256 `c64584be8b55d5e7af78b3fe92e636f6351c5a6c29b20b2244c0b58ebf38e8f6`.
No push, PR, merge, release, production HTTP/cache integration or GitHub write occurred.

## Implementation and profiles

Opaque 8-bit sources now normalize into RGB8 and use conventional sRGB Lanczos3.
Untagged RGB transfers ownership without copying; proven opaque RGBA compacts in
its owned buffer; gray expands only when needed. Application-level float-raster
expansion, gamma round trips and alpha arithmetic are avoided on this path.
Tagged sources retain bounded relative-colorimetric CMS conversion. Arbitrary
ICC descriptions are never trusted as sRGB identity hints. PNG sRGB/untagged
declarations avoid CMS through the existing precedence rules.

Transparent, CMYK and 16-bit inputs retain the original precision/color and
premultiplied linear-light pipeline. Every decoder/container check and full
derivative decode remains enabled. Fresh PNG can now be RGB when opaque; alpha
output remains RGBA. The existing JPEG encoder has no supported subsampling
setter and still emits 4:4:4 at caller quality. No dependency pin was changed.

Only the standalone C# constructor owns defaults: both absent bounds resolve to
2048×2048 and quality to 90. A supplied single bound leaves the other at the
mandatory 8192 ceiling. Explicit PDF width 600 and quality 85 retain 600×8192/85.
Explicit larger/high-fidelity settings remain possible within existing ceilings.
The CLI requires all six explicit arguments and has no optimize flag. Production
`optimize=false` handling is unchanged; its separately resolved intent must not
call the ordinary web-default recipe.

An opt-in `diagnostic-timing` Cargo feature reuses fixed-label Instant probes.
Gate measurements use the normal build without emitted probes. Original and
recovered stage builds each preserve their respective uninstrumented decoded
output pixels at both profiles.

Changed repository files: `src/color.rs`, `src/transform.rs`, `src/lib.rs`,
`src/main.rs`, `Cargo.toml` and `supervisor/ImageTransform.cs` under
`tools/image-helper/`; `ImageHelperPrototypeTests.cs` and
`ImageHelperPerformanceTests.cs` under `tests/Wayfarer.Tests/Util/`;
this report and `README.md`. Production proxy/service/configuration and
`Cargo.lock` are unchanged.

## Functional, visual and safety validation

**74 focused xUnit cases passed**: all 67 retained cases and seven new profile/pixel
cases. See `evidence/test-results/recovery-functional.trx`. New tests failed
against the original behavior first: an opaque black/white downscale returned
linear-light 188 instead of sRGB approximately 128; the alpha control already
passed. The recovered implementation passes both.

Retained cases exercise actual JPEG/PNG/WebP/static GIF; all eight JPEG/PNG
orientations and post-IDAT eXIf; real sRGB/P3/gray/CMYK ICC and 16-bit/gamma/cICP;
alpha and explicit JPEG rejection; PNG CRC, exact raster count, Adler/Deflate
termination and Adam7; malformed/animated input; byte/resource classifications;
crashes, pipe overflow, timeout/cancellation, repeated reaping, kernel
address-space/CPU limits and four non-queuing slots. Orientation cases now check
representative PNG pixels as well as dimensions.

Five additional full-CLI checks prove 4000×3000→2048×1536,
3000×4000→1536×2048, 800×600 unchanged, PDF 4000×3000→600×450,
and an explicit 4096/8192 quality-95 override retaining 4000×3000.
Independent decoding checks color, canonical ICC and stripped EXIF.
These uniform generated inputs are geometry/color controls, not performance
photographs. See `evidence/geometry-results.json` and `evidence/geometry/`.

Locked offline Release builds, formatting, Clippy with warnings denied and the
Release C# test build pass. Two existing unrelated xUnit analyzer warnings remain.
Containment remains non-root Linux x64, AS 1 GiB, CPU 20/25 seconds, nofile 128,
core zero, race-checked parent-death SIGKILL and supervised reap. The original
parent-loss witness was copied and labeled as prior evidence; caller SIGKILL was
not newly replayed. Its control/supervision implementation is unchanged.

## Performance measurements

The identical retained source bytes are loaded once per host/backend comparison.
JPEG and WebP sources are 1920×863. PDF outputs are 600×269; the normal web box
retains 1920×863 without enlargement. This corpus demonstrates real photo
downscaling at 600/85 and unscaled web recompression, not large-photo web
downscaling performance.

Each profile/format has two warm sessions with 20 recorded warmups and 40
measured observations per backend per session: **80 warm samples** per cell.
Order alternates. Cold has eight fresh managed test hosts per cell with
alternating order, without flushing OS caches. Every Rust transform launches a
fresh process in both phases. Input reads, expected-geometry identification,
evidence writes and extra image inspection are outside timing. Identification
can warm managed metadata code during cold observations.

ImageSharp 3.1.12 uses unchanged `OptimizeImage` plus full `ValidateRaster`
with identical bounds/quality. Rust includes C# supervision, launch, IPC,
normalization, completed verification and reap. Original Rust uses the same
supervisor/resolved recipe in the same batches. p50 is median; p95 is nearest
rank; throughput is inverse mean sequential transform time, not trip capacity.

Warm latency in milliseconds:

| Profile / input | Original Rust p50 / p95 | Recovered Rust p50 / p95 | ImageSharp p50 / p95 | Throughput / baseline |
| --- | ---: | ---: | ---: | ---: |
| 600/85 JPEG | 110.92 / 123.06 | 58.06 / 62.65 | 18.04 / 19.79 | 31.9% |
| 600/85 WEBP | 148.84 / 170.51 | 95.53 / 107.11 | 54.08 / 64.23 | 57.2% |
| 2048/90 JPEG | 214.14 / 227.39 | 80.26 / 83.31 | 29.85 / 42.11 | 38.9% |
| 2048/90 WEBP | 249.54 / 279.32 | 116.36 / 127.55 | 67.90 / 82.02 | 58.8% |

Cold latency in milliseconds:

| Profile / input | Original Rust p50 / p95 | Recovered Rust p50 / p95 | ImageSharp p50 / p95 | Throughput / baseline |
| --- | ---: | ---: | ---: | ---: |
| 600/85 JPEG | 130.00 / 167.41 | 72.63 / 97.32 | 121.99 / 202.52 | 165.2% |
| 600/85 WEBP | 167.99 / 187.18 | 122.38 / 138.80 | 269.84 / 380.32 | 225.8% |
| 2048/90 JPEG | 248.00 / 271.35 | 101.30 / 121.13 | 175.27 / 196.03 | 167.6% |
| 2048/90 WEBP | 281.31 / 309.43 | 134.63 / 178.08 | 319.46 / 392.83 | 231.1% |

Cold passes do not offset warm failures. Frozen thresholds remain p50 **and** p95
≤110% of baseline and throughput ≥90%, per profile/format. Both individual warm
sessions fail. Recovery is approximately 1.6–2.7× faster by warm p50.

The ordinary ImageSharp seam emits 4:2:0; Rust emits 4:4:4. A separate supported-API
ImageSharp control matches conventional Lanczos3, 4:4:4, quality and dimensions,
with bounded source loading and full derivative validation. It is a fidelity/work
comparison, not a replacement for the frozen gate:

| Profile / input | Matched ImageSharp 4:4:4 p50 / p95 ms | Recovered p50 / p95 ratio | Throughput / matched baseline |
| --- | ---: | ---: | ---: |
| 600/85 JPEG | 18.64 / 19.10 | 3.11 / 3.28 | 32.4% |
| 600/85 WEBP | 54.71 / 62.49 | 1.75 / 1.71 | 57.5% |
| 2048/90 JPEG | 35.35 / 38.30 | 2.27 / 2.18 | 44.7% |
| 2048/90 WEBP | 72.14 / 82.45 | 1.61 / 1.55 | 62.2% |

Every matched control also fails. No quality or verification requirement was
discarded for speed. Raw samples and paired outputs are in
`evidence/performance/`; `evidence/summary.json` includes per-session statistics.
One stale-assembly collector attempt failed geometry before producing valid
samples; its retained log is excluded.

## Quality, bytes and resources

JPEG quantization tables match at each quality. Outputs were rendered through
their profiles to independent LittleCMS sRGB. A full paired contact sheet was
inspected: no obvious new artifacts appeared on these photographs. This is
focused visual evidence, not blind perceptual acceptance. Both no-resize web
outputs are decoded-pixel-identical to the original same-quality outputs.

| Profile / input | ImageSharp 4:2:0 / matched 4:4:4 / recovered bytes | PSNR versus 4:2:0 / matched 4:4:4, dB |
| --- | ---: | ---: |
| 600/85 JPEG | 43327 / 56422 / 55278 | 34.99 / 40.54 |
| 600/85 WEBP | 42685 / 54561 / 54493 | 35.73 / 42.36 |
| 2048/90 JPEG | 498696 / 599461 / 581670 | 44.53 / 41.61 |
| 2048/90 WEBP | 498676 / 568433 / 565023 | 43.00 / 46.27 |

Recovered byte sizes are within approximately 0.1–3% of matched 4:4:4; ordinary
4:2:0 is smaller. PSNR measures differences, not a quality pass threshold.
Metadata, hashes, subsampling, quantization and mean RGB deltas are in
`evidence/summary.json`. Visuals are in `evidence/quality-comparison.png`
and the full paired JPEGs.

| Profile / input | ImageSharp host median CPU ms | Original → recovered child median CPU ms | Recovered supervisor-host median CPU ms | Original → recovered sampled child peak RSS |
| --- | ---: | ---: | ---: | ---: |
| 600/85 JPEG | 18.13 | 115.46 → 61.87 | 2.72 | 40.2 → 17.4 MiB |
| 600/85 WEBP | 56.39 | 147.47 → 94.16 | 2.99 | 40.5 → 17.9 MiB |
| 2048/90 JPEG | 30.07 | 222.00 → 88.05 | 4.23 | 39.2 → 14.4 MiB |
| 2048/90 WEBP | 69.62 | 254.90 → 120.15 | 4.25 | 37.1 → 14.6 MiB |

Child CPU is RUSAGE_CHILDREN from separate direct batches, including prlimit/exec;
managed CPU is from the paired host. Separate-run medians are not an exact
combined operation total. Child RSS is sampled VmHWM, not a hard memory guarantee.
The shared managed-host peaks span approximately 207–266 MiB in warm batches; the
same host runs all backends, so its cumulative peak is not attributable to one
backend/operation. Four-image aggregate memory remains unqualified.

## Remaining bottlenecks and stop decision

Recovered stage medians in milliseconds; individual medians are not additive:

| Stage | PDF JPEG | PDF WebP | Web JPEG | Web WebP |
| --- | ---: | ---: | ---: | ---: |
| Admission/full source decoding | 12.12 | 53.05 | 12.10 | 53.20 |
| Color preparation / conditional CMS | 4.50 | <0.01 | 4.46 | <0.01 |
| Conventional sRGB resizing | 31.35 | 31.99 | 0 | 0 |
| JPEG encoding | 4.39 | 4.34 | 44.68 | 44.40 |
| Complete derivative verification | 1.83 | 1.90 | 13.31 | 13.15 |

Application gamma passes are eliminated on the opaque path. At 600/85 the pinned
resampler dominates JPEG: its RGB8 API still uses an internal RGBA32F intermediate
and generic floating weighted channel processing (`image` 0.25.10,
`imageops/sample.rs`). It costs more than the original float-input resize alone,
while avoiding much larger preparation/gamma work. WebP decoding remains about
53 ms. Unresized web JPEG is dominated by encoding and verification.

Direct process-launch return medians are approximately 0.37–0.47 ms.
Wall-minus-Rust-run residual medians are about 1.9–2.8 ms, including launch,
remaining IPC/reap and the auxiliary RSS sampler. Primary gate measurements use
the uninstrumented supervised path; probes cannot replace those measurements.

**Stop here and keep ImageSharp in production.** A resampler/encoder replacement
or another iteration requires new maintainer authorization. No daemon, pool,
custom codec or general imaging framework was added. PNG/static-GIF performance,
larger photographic web downscales, real 10/20-image trips, aggregate resources,
broader ICC/alpha/YCCK fidelity and ARM64/Windows packaging remain UNPROVEN.

Complete recovery-scope Code Guard has no FAIL/INCOMPLETE. The unchanged 90-line
PNG color-precedence function receives callable-size REVIEW; keeping its complete
precedence/validation authority together remains clearer. The external
summarizer receives complexity REVIEW for fixed grouping/filtering comprehensions
and aggregation; formulas were independently recalculated. No policy, exclusion
or baseline was altered. Commands, hashes and final identity accompany the evidence.
