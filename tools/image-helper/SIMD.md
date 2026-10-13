# Wayfarer #858: isolated SIMD resize/JPEG experiment

**NO-GO for the accepted ImageSharp parity contract.** Both targeted changes work
and improve speed without an observed correctness or visual regression in the
retained cases. All four combined warm profiles still fail at least one gate;
both warm sessions fail independently. Cold passes do not offset warm failures.
The experiment stops after these two changes.

Authority: [maintainer authorization](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6151576904).
Workspace: `/home/stef/wayfarer-research/858-simd-experiment-20261013-mfkfwgez`.
Main experiment branch: `wip/858-simd-experiment-20261013`.
Starting commit: `017e7495c42336d7205b2d6263828ec96e0d980c`.
Final documentation head and binary/evidence identities are in `ARTIFACTS.json`
and `SHA256SUMS` at the workspace root. No push, PR, merge, release,
production integration, GitHub write or issue closure occurred.

## Source identity and independence

| Variant | Exact measured source | Location |
| --- | --- | --- |
| Recovered baseline | `017e7495c42336d7205b2d6263828ec96e0d980c` | Baseline binaries in `bin/` |
| Resize only | `29bfcfe91481466d565f7de48642e9e31e8f5522` | First standalone processing commit |
| Encoder only | `a13c90bc12c6e28a715e5637320fd25bd88ee531` | `encoder-source/`, branch `wip/858-simd-encoder-only-20261013` |
| Combined | `0e83d22fdacfc7b1f952c8d9f2083577b7cfb7bc` | `source/` |

The encoder commit cherry-picks cleanly onto the recovered starting commit.
The optimizations can be reverted independently. A subsequent test-only correction
`45c03f88` on the combined branch (`5e515719` on encoder-only) removes dependence
on the CMS writer's current ICC creation date. Later changes are documentation.
The final locked Release binary is byte-identical to the measured combined binary.

Changed repository files, all under `tools/image-helper/`: `Cargo.toml`,
`Cargo.lock`, `src/transform.rs`, `tests/jpeg_output.rs`, `README.md`, `SIMD.md`,
`notices/fast_image_resize-6.1.0.txt` and `notices/jpeg-encoder-0.7.1.txt`.
Only the existing external `diagnose.py` and `analyze.py` collectors were adapted
for variant binary paths, separate output directories and per-variant summaries.
The C# performance collector, ImageSharp seam, CLI/control code, decoders,
color normalization and all source-admission files are unchanged; see
`evidence/unchanged-contract-seams.json`.

## Dependencies, features and behavior

[fast_image_resize 6.1.0](https://docs.rs/crate/fast_image_resize/6.1.0) uses only
its `std` feature. The supported `ImageRef`/owned RGB8 destination APIs borrow
source pixels and use explicit `Convolution(Lanczos3)`. Owned destination
allocation remains fallible and admitted; internal scratch stays inside the
unchanged process limits. No `image` adapter, Rayon, thread pool, forced CPU
extension or `target-cpu=native` is used. On native x86_64, the upstream default
detects AVX2/SSE4.1; on AArch64 it detects NEON, with scalar fallback. The odd-axis
product test recorded AVX2 and agrees with forced scalar resizing within one
channel value. The unchanged float/alpha/precision route keeps its original
premultiplied linear-light processing.

[jpeg-encoder 0.7.1](https://docs.rs/jpeg-encoder/0.7.1/jpeg_encoder/) enables
`simd` explicitly. Its RGB API dispatches to AVX2 only after the same native
runtime capability check used by the resizer; unsupported x86 CPUs and other
architectures use scalar encoding. The feature tree and linked AVX2 conversion/
DCT symbols are retained in `evidence/jpeg-encoder-active-features.txt` and
`evidence/jpeg-encoder-avx2-symbols.txt`. Output uses explicit `F_2_2` and
`ChromaSubsamplingMethod::Average` at qualities up to 90, including web 90 and
PDF 85. Higher caller qualities retain `F_1_1` (4:4:4); the explicit quality-95
large control verifies this. These encoder gains include the authorized change
from recovered 4:4:4 to baseline-matched photographic 4:2:0, so they are not a
measurement of SIMD alone. PNG encoding is unchanged.

Only generated canonical sRGB ICC data reaches `add_icc_profile`. EXIF/XMP are
not passed to the encoder. Bounded writes, completed derivative decode, source
admission, orientation, geometry/no upscale, transparency and subprocess
classification/cleanup remain enabled. The explicit profiles remain
2048×2048/90 and PDF 600×8192/85. The CLI still requires all recipe arguments
and has no optimize flag; the separately resolved `optimize=false` intent and
production HTTP/cache behavior were not changed.

Both crates require Rust 1.87 and build with retained Rust 1.90.0. Resize uses
MIT under `MIT OR Apache-2.0`; JPEG uses MIT **and IJG**. Complete upstream
license text, additional AVX2 copyright notices and the required acknowledgment
are retained: “This software is based in part on the work of the Independent JPEG
Group.” The dependencies were not modified. The nine resize/transitive additions
and their MIT/Apache/Unicode terms are recorded in
`evidence/new-dependency-licenses.json`; their required notice text is retained.
JPEG adds one package with no runtime dependencies. No existing locked package
was removed or updated. Exact upstream revisions/API sources are preserved in
`evidence/upstream-source/` and the release-commit metadata JSON files.
Targeted published GitHub advisory queries returned no advisories for these two
new pins; this is a published-advisory check, not a full security audit.

## Method and baseline reproduction

Identical retained JPEG/WebP source bytes are 1920×863. PDF output is 600×269;
web retains 1920×863 without enlargement. This demonstrates photographic PDF
resizing and unscaled web recompression; large photographic web downscaling
performance remains unproven. Five retained uniform-source geometry controls
also pass, including 4000×3000→2048×1536 and 3000×4000→1536×2048.

Every variant/profile/format has two warm sessions with 20 warmups and 40 measured
observations each: **80 warm samples per cell/backend**. Cold has eight fresh
managed hosts per cell, without flushing OS caches. Backend order alternates.
Every Rust operation launches a fresh child through the unchanged C# supervisor,
including process startup, IPC, complete derivative verification and reap.
ImageSharp 3.1.12 uses unchanged `OptimizeImage` plus `ValidateRaster` at matching
bounds/quality. Source reads, expected-geometry identification, evidence writes
and additional inspection are outside timing. Identification can warm managed
metadata code before a cold observation. The original-baseline control is also
paired with every candidate. Builds and other benchmarks do not overlap timed
batches; this WSL host is shared. Host/process snapshots are retained.

Baseline warm p50 reproduced the authorization within approximately 1%:
PDF JPEG 58.68 ms, PDF WebP 95.22 ms, web JPEG 80.36 ms, web WebP 115.72 ms.
P50 is median, p95 nearest rank, throughput inverse mean sequential operation
time. The frozen per-format/profile gate requires p50 **and** p95 ≤110% of the
matched ImageSharp result and throughput ≥90%. Each candidate's own paired
ImageSharp measurements and both individual warm sessions are in the summaries;
the tables below show the combined batch's ImageSharp column for orientation.

## Complete-operation latency and throughput

Warm p50 / p95, milliseconds:

| Profile / input | Recovered baseline | Resize only | Encoder only | Combined | ImageSharp paired with combined |
| --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | 58.68 / 62.35 | 30.05 / 32.23 | 57.41 / 62.55 | 26.83 / 27.91 | 18.56 / 27.06 |
| 600/85 WEBP | 95.22 / 99.96 | 66.30 / 71.47 | 94.32 / 98.37 | 63.87 / 70.76 | 55.33 / 66.26 |
| 2048/90 JPEG | 80.36 / 84.05 | 80.89 / 87.24 | 55.95 / 60.68 | 55.64 / 57.57 | 30.09 / 34.02 |
| 2048/90 WEBP | 115.72 / 117.92 | 116.12 / 128.85 | 92.67 / 96.03 | 91.30 / 95.74 | 66.89 / 75.60 |


Cold p50 / p95, milliseconds:

| Profile / input | Recovered baseline | Resize only | Encoder only | Combined | ImageSharp paired with combined |
| --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | 87.65 / 107.59 | 44.80 / 64.71 | 79.35 / 98.74 | 37.07 / 68.43 | 157.86 / 181.90 |
| 600/85 WEBP | 130.40 / 141.55 | 79.47 / 105.00 | 106.41 / 136.88 | 78.48 / 112.74 | 330.62 / 384.70 |
| 2048/90 JPEG | 111.54 / 122.77 | 94.93 / 115.20 | 71.38 / 96.87 | 68.70 / 101.67 | 165.67 / 195.65 |
| 2048/90 WEBP | 155.61 / 179.09 | 127.21 / 163.42 | 110.77 / 146.13 | 102.42 / 119.35 | 355.06 / 381.67 |


Sequential transforms per second and complete combined gate:

| Profile / input | Phase | Baseline ips | Resize ips | Encoder ips | Combined ips | ImageSharp ips | Combined / IS | Gate |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | warm | 16.90 | 32.86 | 17.21 | 37.08 | 50.63 | 73.2% | FAIL |
| 600/85 WEBP | warm | 10.43 | 15.00 | 10.53 | 15.36 | 17.50 | 87.8% | FAIL |
| 2048/90 JPEG | warm | 12.36 | 12.21 | 17.64 | 17.94 | 31.97 | 56.1% | FAIL |
| 2048/90 WEBP | warm | 8.62 | 8.44 | 10.72 | 10.89 | 14.63 | 74.4% | FAIL |
| 600/85 JPEG | cold | 10.87 | 21.73 | 12.80 | 23.69 | 6.39 | 370.9% | PASS |
| 600/85 WEBP | cold | 7.65 | 12.13 | 9.12 | 12.04 | 3.01 | 400.0% | PASS |
| 2048/90 JPEG | cold | 8.95 | 10.29 | 13.53 | 13.71 | 6.09 | 225.2% | PASS |
| 2048/90 WEBP | cold | 6.39 | 7.52 | 8.88 | 9.69 | 2.82 | 343.6% | PASS |


All four combined warm p50 ratios exceed 1.10. Combined throughput is only
73.2%, 87.8%, 56.1% and 74.4% of ImageSharp for the table's four warm cells.
Cold passes cannot rescue those repeated warm failures. No test times are used
as timing assertions; the gate is evaluated from preserved observations.

## Isolated processing stages

Diagnostic median milliseconds, eight samples after three warmups per cell:

| Profile / input | Stage | Baseline ms | Resize only ms | Encoder only ms | Combined ms |
| --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | resize_srgb8 | 32.038 | 2.865 | 32.023 | 2.771 |
| 600/85 JPEG | encode | 4.388 | 4.330 | 2.151 | 2.076 |
| 600/85 WEBP | resize_srgb8 | 34.546 | 2.589 | 32.097 | 2.558 |
| 600/85 WEBP | encode | 4.340 | 4.341 | 2.133 | 2.096 |
| 2048/90 JPEG | resize_srgb8 | 0.000 | 0.000 | 0.000 | 0.000 |
| 2048/90 JPEG | encode | 44.785 | 44.998 | 21.812 | 22.097 |
| 2048/90 WEBP | resize_srgb8 | 0.000 | 0.000 | 0.000 | 0.000 |
| 2048/90 WEBP | encode | 45.532 | 43.856 | 21.530 | 21.838 |


The PDF resize call improves about 11–13×. Encoding improves about 2× on both
profiles. The web fixture has no resize stage. Stage medians are not additive.
Normal and diagnostic builds produce identical decoded pixels for every variant,
profile and fixture. Primary gates use uninstrumented binaries; probes and the
auxiliary RSS sampler cannot replace them.

## CPU and RSS observations

Separate direct normal-build batches include prlimit/exec and return/reap.
CPU is RUSAGE_CHILDREN delta; RSS is sampled child VmHWM. These observations are
not a hard aggregate-memory qualification.

| Profile / input | Base CPU ms | Resize CPU ms | Encoder CPU ms | Combined CPU ms | Base RSS MiB | Resize RSS MiB | Encoder RSS MiB | Combined RSS MiB |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | 61.55 | 33.87 | 63.03 | 30.02 | 17.5 | 14.1 | 17.4 | 14.4 |
| 600/85 WEBP | 103.25 | 66.55 | 93.70 | 62.94 | 18.0 | 11.7 | 18.1 | 12.2 |
| 2048/90 JPEG | 88.35 | 89.40 | 60.62 | 61.31 | 14.3 | 14.5 | 13.9 | 14.3 |
| 2048/90 WEBP | 120.52 | 120.72 | 93.37 | 96.90 | 14.4 | 15.0 | 12.3 | 12.9 |


Managed ImageSharp/supervisor CPU and shared-host RSS are in each summary's
`parent_resources`. The same host runs all backends, so its cumulative peak is
not attributable to one operation. Separate direct child CPU and paired managed
CPU medians are not an exact combined operation total. Four-image aggregate
memory remains unqualified; no containment limit was relaxed.

## Quality and encoded bytes

All paired JPEG outputs have matching geometry and quantization tables at caller quality.
Recovered/resize-only JPEG is 4:4:4; encoder/combined and ordinary ImageSharp are
4:2:0. Canonical ICC, decoded color, metadata privacy and output limits were
checked. Files were rendered through their embedded profiles into independent
LittleCMS sRGB. Paired full images, thumbnails and unscaled crops were visually
inspected: no obvious new artifacts, color shift or unacceptable softening was
observed on these photographs. This is focused evidence, not blind perceptual
acceptance or broad ICC/alpha/YCCK corpus qualification.

| Profile / input | IS bytes | Baseline bytes | Resize bytes | Encoder bytes | Combined bytes | Encoder vs IS dB | Combined vs IS dB |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | 43327 | 55278 | 55314 | 43603 | 43582 | 42.74 | 42.55 |
| 600/85 WEBP | 42685 | 54493 | 54472 | 43536 | 43524 | 44.04 | 43.09 |
| 2048/90 JPEG | 498696 | 581670 | 581670 | 498531 | 498531 | 46.68 | 46.68 |
| 2048/90 WEBP | 498676 | 565023 | 565023 | 501261 | 501261 | 46.66 | 46.66 |


PSNR measures displayed differences, not an acceptance threshold. Resize-only
PDF versus recovered output is about 43.6 dB; unscaled web pixels are unchanged.
Encoder and combined outputs are closer to the matched ImageSharp 4:2:0 pixels,
with output sizes close to ImageSharp at both profiles. Full paired outputs are
in `evidence/performance/`; independent profile inspection/metrics are in
`summary-*.json`, `quality-comparison.png` and `quality-crops.png`.

Encoded hashes vary between seconds because the **unchanged** CMS writer embeds
the current ICC creation timestamp. Saved warm/cold decoded pixels, byte lengths
and every ICC byte outside that date are stable; the diagnostic batches also
verify stable decoded pixels. The new regression test compares all canonical
bytes except the documented ICC creation-date field, preventing a clock-boundary
flake. See `evidence/icc-timestamp-observations.json` and
`evidence/summary-crosscheck.json`. No production timestamp behavior was changed.

## Functional checks and Code Guard

All 74 retained xUnit cases pass against baseline and combined; the focused
resize selection also passes 24 cases. The two focused Rust tests pass: odd RGB
strides/SIMD-scalar equivalence and complete JPEG profiles with ICC, odd geometry,
privacy and bounded output. The timestamp correction passes on both branches.
The five retained full-CLI geometry controls pass and independently decode with
canonical color and stripped source metadata. None of the timed collectors fails.

Retained tests cover actual JPEG/PNG/WebP/static GIF, orientation, transparency,
P3/gray/CMYK/gamma/16-bit/cICP, animation/malformed input, PNG CRC/checksum/exact
raster/termination, byte ceilings, crashes, pipes, cancellation/timeouts, repeated
reaping, CPU/address-space refusal and four non-queuing slots. The existing
PDEATHSIG/parent-loss implementation is unchanged; its earlier caller-SIGKILL
witness was not replayed as a new claim. Hard process limits remain 1 GiB AS,
20/25-second CPU, nofile 128 and core zero under non-root Linux x64.

Locked Release builds, formatting and Clippy with warnings denied pass. The
Release C# build passes with retained unrelated xUnit warnings and current NuGet
advisories for the intentionally unchanged ImageSharp baseline. ARM64 Linux and
Windows x64 **library** cross-compilation checks pass. Those checks do not qualify
runtime behavior, binary packaging or process containment on those platforms.
Resize scalar output was exercised explicitly; JPEG fallback was inspected and
cross-compiled, without a non-AVX hardware execution claim.

Code Guard over the complete new experiment scope is **PASS**. The repository-
required comparison against fetched live `main` is **REVIEW**, with no FAIL or
INCOMPLETE. Its findings are unchanged inherited admission/color authorities:
`color.png_profile` (90 LOC); `gif_input.admit` (108 LOC, depth 5, complexity 24);
`png_input.admit` (complexity 18); `webp_input.admit` (complexity 16). These cohesive
precedence/streaming validation functions are unchanged, guarded by retained
behavioral tests and kept together for clear container authority. Refactoring
them is outside this two-change experiment. The reused external summary function
has complexity 38 from fixed grouping/filtering/aggregation; all numerical
formulas were independently recomputed. Its REVIEW is accepted for that bounded
collector. No guard thresholds, baselines, exclusions or policy files changed.
Two initial Code Guard setup errors were corrected and retained; the earlier
analysis assertion about encoded-hash stability was corrected with the timestamp
evidence. Final checks have no unresolved tool/configuration errors.

## Preservation and next decision

All **14,714 protected file hashes** still match the starting snapshot; the
recovered/original/diagnosis Git identities and primary checkout head/status match.
An unrelated `/tmp/wayfarer-864-rereview-TtfVEsrn/source` worktree appeared in the
primary repository during this run at `09fbb99226782835b98635e936dee49b8e6f77e8`.
That shared inventory addition is recorded; no task command writes there.
See `evidence/preservation-before.json` and `preservation-after.json`.

Combined median bottlenecks, milliseconds:

| Profile / input | Decode ms | Color ms | Resize ms | Encode ms | Verify ms |
| --- | --- | --- | --- | --- | --- |
| 600/85 JPEG | 12.109 | 4.590 | 2.771 | 2.076 | 1.735 |
| 600/85 WEBP | 53.459 | 0.000 | 2.558 | 2.096 | 1.714 |
| 2048/90 JPEG | 12.449 | 4.645 | 0.000 | 22.097 | 12.481 |
| 2048/90 WEBP | 54.973 | 0.000 | 0.000 | 21.838 | 12.581 |


JPEG decoding and conditional color preparation remain significant at PDF size;
WebP decode remains about 54 ms; web JPEG encoding and full derivative verification
remain expensive. The SIMD resizer has removed the large PDF resize bottleneck,
and the encoder gain is real, but this process-per-image implementation still
misses the frozen gate. No third optimization, decoder replacement, daemon,
worker pool, HTTP service, custom framework, PNG/GIF performance extension or
trip-workload qualification was added.

**Recommendation:** preserve these two independent improvements for maintainer
technical review, retain the current production path, and stop this experiment.
Any attempt to address the remaining decode/CMS/verification cost requires a new
bounded authorization. Broader fidelity, aggregate resources, non-x64 runtime
and real trip acceptance remain UNPROVEN; this result grants no production gate.
