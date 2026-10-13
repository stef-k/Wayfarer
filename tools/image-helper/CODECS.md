# Wayfarer #858: focused codec comparison

**Overall NO-GO for the unchanged full Rust/ImageSharp parity gate.** Specific
codec gains are real. The native WebP decoder alone passes both measured WebP
profiles, including both independent warm sessions and the observed cold gates.
JPEG web processing still fails. The combined decoders also fail the web WebP
p95 gate in one warm session and in the pooled observations; that counterexample
is retained.

JPEG encoding with `libjpeg-turbo-rs 0.8.0` is substantially faster in isolation,
but its allocating public API cannot preserve this prototype's incremental
encoding byte ceiling. No full-pipeline encoder swap was made. Experimental
WebP-to-WebP output saves bytes at matched fidelity proxies, with roughly nine
to ten times the JPEG encoding cost. It receives no JPEG/PNG parity claim.

Authority: [focused authorization](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6153539608).
Workspace: `/home/stef/wayfarer-research/858-codec-comparison-20261013-zufsdz12`.
Branch: `wip/858-codec-comparison-20261013`.
Base: `8a857230c0751d87b71e227431365d0fb16a7ca4`.
Measured microbenchmark source: `e18859a5` (full SHA in ARTIFACTS.json).
Measured decoder source: `d87978aaa2ec89b3ed83e3b8126d3cc5bd7f69d7`.
Final documentation head, exact binary identities and evidence hashes are in
`ARTIFACTS.json` and `SHA256SUMS`. Paths below are relative to this workspace.
`tools/image-helper/CODECS.md` retains a copy of this report in the scratch clone.

The original source/evidence were read only. No production edits, architecture
redesign, library survey, trip instrumentation, HTTP/cache changes, push, PR,
merge, release or issue closure occurred. ImageSharp remains in production.

## Candidate disposition

PASS below means the stated local comparison, not production or platform acceptance.

| Candidate/path | Measured result | Compatibility / next decision |
| --- | --- | --- |
| libjpeg-turbo-rs decode | PASS independent gain; FAIL full JPEG warm parity | RGB-only guarded substitution; original grayscale/CMYK/recovery and metadata routes remain |
| libjpeg-turbo-rs encode | PASS codec speed/photographic fidelity; FAIL current bounded-writer fit | Full pipeline UNPROVEN; a bounded upstream encoding API is needed before integration |
| webp/libwebp decode | PASS both WebP warm/cold profile gates | Most credible measured candidate; native build/security maintenance and wider qualification remain |
| Both decoders | NO-GO: JPEG warm failure and web WebP tail failure | Preserve the counterexample; do not infer parity from median improvement |
| WebP-to-WebP lossy output | PASS focused fidelity/alpha controls; FAIL JPEG encode-time comparison | Experimental output-policy option only; keep accepted JPEG/PNG output |
| Native lossless WebP | Exact focused pixels/alpha; severe CPU regression | No full pipeline extension justified |

## Exact method and baseline correction

The live comment calls the JPEG baseline zune-jpeg. At the exact requested
commit, `src/raster.rs` actually calls `jpeg-decoder 0.3.2` for source and
completed-derivative decoding. `zune-jpeg 0.5.15` is enabled through `image
0.25.10`, but is not the CLI JPEG route. Both were measured in isolation;
full-operation comparisons use the actual preserved CLI.

Inputs are the retained 1920x863 photograph in two encodings:

- JPEG: 460422 bytes; SHA-256 `166807a3de2d35931156b56e05db36664d49d85e956b09943d16bc9bffa05964`.
- WebP: 485820 bytes; SHA-256 `802b8ccd7aa35d4cad8625bd0b565a7af6d64faf6560964e9b6dc530b53a3b93`.

Web is 2048x2048/Q90, without enlargement; this fixture remains 1920x863.
PDF remains 600x8192/Q85, producing 600x269. Quality-95 override behavior and
the accepted JPEG/PNG format choices are unchanged. These experimental recipes
do not change current production defaults or high-fidelity behavior.

Decode-only comparisons return owned packed RGB8 (RGBA8 for the alpha control).
Encode-only comparisons use the exact existing admitted, color-normalized,
oriented/resized pixel buffer, with the same dimensions, explicit Q85/Q90,
box-averaged photographic 4:2:0 and generated sRGB ICC for both JPEG encoders.
JPEG quantization tables and horizontal/vertical sampling factors match; component IDs differ (0/1/2 versus 1/2/3), without changing that sampling intent; neither encoder optimizes Huffman
tables. Canonical ICC construction is outside the codec-only clock; JPEG profile
embedding is inside it. Source read/sample write and inspection are untimed.
Raw WebP codec outputs have no ICC; this is explicitly an incomplete output
integration, while their input pixels are already normalized sRGB.

Micro decode: two reversed-order sessions, 24 measured observations plus three
warmups each, giving 48 observations/codec. Encode: two sessions of 12 plus
three warmups, giving 24 observations/cell; calibration probes are separate.
Warm complete CLI: two independent hosts, 20 warmups then 24 observations per
backend, giving 48/cell. Original Rust and ImageSharp are paired in each warm
host. Cold: eight fresh managed hosts/cell, one operation/backend, alternating
order; original Rust cold uses its own hosts. OS caches are not flushed, and
source identification can warm metadata code before a cold sample.

The unchanged C# collector uses ImageSharp 3.1.12 `OptimizeImage + ValidateRaster`
and the unchanged Rust supervisor. Rust clocks include four-slot admission,
thread/process startup, prlimit/exec, IPC, complete normalization, the original
encoder, full derivative decode/verification and reap. Network/cache/rendering
are absent. P50 is median; p95 nearest rank; throughput inverse mean sequential
operation time. Frozen gate: p50 and p95 <=110% of paired ImageSharp and
throughput >=90%, for each profile/phase. It is not a four-operation throughput
or real trip measurement.

The host is WSL2 Linux AMD64/i7-10700K. Rust is 1.90.0, portable Release;
RUSTFLAGS/CFLAGS/TARGET_CPU are unset. Builds, tests and other task benchmarks
do not overlap primary timed batches. Shared-host/tail variability is retained,
including the combined WebP p95 failure. It was not rerun to erase it.

## Decode only

| Codec/input | p50 ms | p95 ms | Median CPU ms | Process HWM MiB |
| --- | --- | --- | --- | --- |
| Actual CLI JPEG baseline | 10.914 | 11.778 | 13.508 | 16.21 |
| zune-jpeg reference | 7.883 | 11.156 | 7.886 | 16.21 |
| libjpeg-turbo-rs | 6.424 | 7.190 | 6.427 | 16.21 |
| image-webp baseline | 51.414 | 55.574 | 51.410 | 16.21 |
| webp/libwebp | 28.993 | 31.161 | 28.978 | 16.21 |

JPEG decode saves 41.1% against the actual CLI codec and
18.5% against zune-jpeg. Native WebP decode saves 43.6%.
The native RGB photograph and retained RGBA still WebP decode to exactly the
same pixels as image-webp. JPEG decoded differences versus jpeg-decoder have a
maximum channel delta of three; observational metrics are in
`evidence/codec-summary.json`. Process HWM is cumulative over a finite micro
process, not a per-call allocation bound or aggregate RSS claim.

## JPEG encode only

| Profile/pixels | jpeg-encoder p50/p95 ms | Candidate p50/p95 ms | p50 saving | Bytes old/new | CPU old/new ms | Process HWM old/new MiB |
| --- | --- | --- | --- | --- | --- | --- |
| web/photo.jpg | 20.738 / 22.096 | 5.488 / 5.630 | 73.5% | 498531 / 498545 | 20.740 / 5.489 | 21.0 / 21.0 |
| web/photo.webp | 20.439 / 20.806 | 5.565 / 6.043 | 72.8% | 501261 / 501268 | 20.442 / 5.566 | 21.0 / 21.0 |
| pdf/photo.jpg | 2.012 / 2.045 | 0.518 / 0.554 | 74.2% | 43582 / 43383 | 2.014 / 0.520 | 21.0 / 21.0 |
| pdf/photo.webp | 2.008 / 2.042 | 0.520 / 0.553 | 74.1% | 43524 / 43327 | 2.010 / 0.521 | 21.0 / 21.0 |

Candidate JPEG geometry, 4:2:0, Q tables, canonical ICC and privacy match the
photographic intent. Independent LittleCMS display PSNR against normalized
pixels differs by less than 0.01 dB on these cells. Unscaled crops were inspected
with no obvious new color shift, artifacts or unacceptable softening. This is
focused photograph evidence, not blind perceptual or broad color acceptance.

**Why no encoder CLI comparison:** the pinned `Encoder::encode` returns a full
Vec. `compress_into` first creates a full internal Vec and only then checks the
caller buffer; `stream::compress_to_writer` likewise buffers before writing.
`ScanlineEncoder::finish` also returns a Vec. These supported APIs do not expose
the original incremental bounded writer. Checking length after encoding would
weaken that existing intermediate/output allocation boundary. No fork, C-ABI
integration, private codec change or limit relaxation was attempted. The
independent gain is measured; its complete-pipeline benefit remains UNPROVEN.
The same API limitation applies even though ICC encoding is supported.

## Experimental WebP-to-WebP output

| Profile | JPEG/WebP Q | JPEG p50/p95 ms | WebP p50/p95 ms | JPEG/WebP bytes | Byte saving | JPEG/WebP RGB PSNR dB | JPEG/WebP CPU ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| web | 90 / 88 | 20.439 / 20.806 | 183.409 / 203.737 | 501261 / 445904 | 11.0% | 40.701 / 41.323 | 20.44 / 183.41 |
| pdf | 85 / 78 | 2.008 / 2.042 | 21.369 / 26.715 | 43524 / 28602 | 34.3% | 33.135 / 33.307 | 2.01 / 21.36 |

Integer WebP quality was bracketed around the observed fidelity crossing.
Q88 web and Q78 PDF are the lowest sampled values with no lower RGB PSNR than
the matched JPEG derivative; final crops were inspected separately. Numeric Q90
was never assumed equivalent across formats. PSNR is a proxy and cannot prove
perceptual equivalence. Raw WebP lacks the 612-byte canonical ICC carried by
JPEG, so byte savings also exclude the small eventual ICC/mux overhead.

Matched codec-process cumulative HWM observations are web: JPEG 20.96 / WebP 79.43 MiB; pdf: JPEG 20.96 / WebP 79.43 MiB. They include retained input and repeated codec allocation, not a hard allocation bound.

Both lossless WebP encoders reproduce the prepared photographic pixels exactly:

| Profile | image-webp lossless p50 ms | libwebp lossless p50 ms | Old/native bytes |
| --- | --- | --- | --- |
| web | 13.217 | 946.914 | 2099372 / 1823882 |
| pdf | 1.309 | 177.989 | 268718 / 227748 |

On the retained transparent 64x32 WebP, native/old decoded RGBA is exact.
Lossy and both lossless encoded controls retain alpha exactly (range 0..252).
This is codec compatibility evidence; it does not complete the deferred product
alpha/output-policy audit. See `evidence/alpha-codec.json`.

Lossy encoding costs roughly 9x web and 10x PDF JPEG encode time; native lossless
encoding is much slower still. Neither justifies a new full CLI output route in
this task. The original encode path, output enum/MIME contract, metadata privacy,
limits and verification are unchanged. Serving WebP would need its own future
contract/consumer/cache decision and bounded canonical-metadata/output path.

## Complete CLI: warm measurements

| Variant/profile/input | p50 candidate/original/IS ms | p95 candidate/IS ms | Candidate/IS ips | Throughput ratio | Gate |
| --- | --- | --- | --- | --- | --- |
| jpeg-decode/web/jpeg | 49.357 / 56.984 / 30.729 | 56.439 / 48.564 | 19.88 / 30.85 | 64.4% | FAIL |
| jpeg-decode/web/webp | 92.904 / 94.680 / 71.175 | 108.807 / 86.163 | 10.58 / 13.61 | 77.8% | FAIL |
| jpeg-decode/pdf/jpeg | 22.916 / 28.453 / 19.358 | 29.752 / 38.096 | 41.78 / 43.86 | 95.3% | FAIL |
| jpeg-decode/pdf/webp | 66.099 / 65.339 / 57.640 | 78.326 / 81.039 | 14.46 / 16.45 | 87.9% | FAIL |
| webp-decode/web/webp | 68.564 / 93.945 / 71.777 | 83.739 / 89.047 | 14.03 / 13.34 | 105.2% | PASS |
| webp-decode/pdf/webp | 39.012 / 64.657 / 56.246 | 43.408 / 74.765 | 25.20 / 17.05 | 147.8% | PASS |
| combined-decode/web/jpeg | 51.889 / 59.338 / 32.549 | 86.495 / 64.969 | 17.39 / 26.16 | 66.5% | FAIL |
| combined-decode/web/webp | 66.999 / 96.541 / 72.743 | 232.019 / 205.586 | 10.92 / 11.21 | 97.5% | FAIL |
| combined-decode/pdf/jpeg | 23.359 / 28.155 / 19.949 | 30.030 / 37.504 | 41.25 / 44.86 | 92.0% | FAIL |
| combined-decode/pdf/webp | 37.822 / 65.816 / 56.534 | 43.356 / 78.978 | 26.07 / 16.97 | 153.7% | PASS |

Native WebP alone passes each individual warm host for both profiles, as well
as the pooled results. It cuts complete web median by 27.0% and PDF by 39.7%
against its paired original Rust. JPEG-only decoding improves JPEG web median
by 13.4% and PDF by 19.5%, but both still miss the p50 gate. Its WebP-input PDF
median regresses 1.2%; small mixed-host offsets should not be overinterpreted.

Combined JPEG web is 1.594x ImageSharp p50, with 66.5% throughput; it needs
another 16.085 ms p50 reduction to reach the paired allowed bound. Combined
PDF JPEG misses p50 by 1.415 ms. Combined web WebP p95 is 232.019 versus
205.586 ms ImageSharp, **5.875 ms beyond the 110% bound**. Its first warm host
also fails p95 (1.413x IS); the second passes. These tails remain part of the
result, with no exclusions or automatic rerun. Cold success does not clear the
warm failures. Full per-session ratios and parent resources are retained in
`evidence/summary-*.json` and the raw performance files.

## Complete CLI: cold observations

| Variant/profile/input | p50 Rust/IS ms | p95 Rust/IS ms | Throughput ratio | Observed gate |
| --- | --- | --- | --- | --- |
| baseline/pdf-jpeg | 63.408 / 170.308 | 71.551 / 193.057 | 269.1% | PASS |
| baseline/pdf-webp | 95.703 / 342.671 | 106.637 / 367.120 | 351.5% | PASS |
| baseline/web-jpeg | 91.531 / 178.866 | 97.020 / 205.156 | 196.3% | PASS |
| baseline/web-webp | 138.343 / 377.039 | 144.794 / 393.861 | 280.3% | PASS |
| jpeg-decode/pdf-jpeg | 64.416 / 179.214 | 76.397 / 239.561 | 300.4% | PASS |
| jpeg-decode/pdf-webp | 119.730 / 392.772 | 244.801 / 683.251 | 325.8% | PASS |
| jpeg-decode/web-jpeg | 86.559 / 195.886 | 96.201 / 233.776 | 217.2% | PASS |
| jpeg-decode/web-webp | 129.515 / 400.899 | 149.616 / 434.547 | 302.2% | PASS |
| webp-decode/pdf-webp | 79.970 / 383.225 | 98.013 / 453.444 | 456.5% | PASS |
| webp-decode/web-webp | 128.467 / 494.419 | 342.429 / 945.368 | 351.1% | PASS |
| combined-decode/pdf-jpeg | 62.950 / 184.337 | 66.755 / 201.212 | 291.6% | PASS |
| combined-decode/pdf-webp | 74.026 / 376.434 | 95.861 / 548.329 | 512.7% | PASS |
| combined-decode/web-jpeg | 89.060 / 195.965 | 290.058 / 310.957 | 178.3% | PASS |
| combined-decode/web-webp | 99.044 / 381.317 | 107.966 / 399.976 | 376.2% | PASS |

These eight-sample p95 values are the maximum observation. Fresh managed hosts
and alternating order preserve the existing small cold method; caches and host
scheduling remain shared. Large tails, including 342 ms Rust / 945 ms ImageSharp
in the native web WebP cell, are retained. These observations do not constitute
a broad cold-start or cross-platform performance guarantee.

## Stage cost and resources

| Combined profile/input | Decode ms | Color ms | Resize ms | Encode ms | Verify ms |
| --- | --- | --- | --- | --- | --- |
| web/photo.jpg | 8.199 | 4.808 | 0.000 | 22.010 | 8.248 |
| web/photo.webp | 27.772 | 0.001 | 0.000 | 22.020 | 8.111 |
| pdf/photo.jpg | 8.179 | 4.669 | 2.774 | 2.110 | 0.644 |
| pdf/photo.webp | 27.681 | 0.008 | 2.982 | 2.142 | 0.793 |

Stage medians are disjoint labels but must not be added across observations.
Baseline web JPEG source/verify are 12.439/12.282 ms, compared with combined
8.199/8.248 ms. Baseline WebP source is 54.077 ms versus combined 27.772 ms.
The original JPEG encoder still costs about 22 ms at web size. Native external
output avoids the convenience decoder's RGB copy; retained RIFF admission,
image-webp header/metadata work and full picture verification remain included.
Normal and diagnostic builds produce identical decoded pixels in every cell.

| Normal full CLI | Median child CPU ms | Median sampled child HWM MiB |
| --- | --- | --- |
| baseline/pdf/photo.jpg | 31.32 | 14.07 |
| baseline/pdf/photo.webp | 64.05 | 11.64 |
| baseline/web/photo.jpg | 61.36 | 14.13 |
| baseline/web/photo.webp | 93.96 | 12.62 |
| jpeg-decode/pdf/photo.jpg | 23.02 | 14.29 |
| jpeg-decode/pdf/photo.webp | 64.69 | 11.80 |
| jpeg-decode/web/photo.jpg | 46.67 | 14.26 |
| jpeg-decode/web/photo.webp | 87.64 | 12.93 |
| webp-decode/pdf/photo.webp | 38.22 | 11.41 |
| webp-decode/web/photo.webp | 68.74 | 12.51 |
| combined-decode/pdf/photo.jpg | 20.95 | 14.44 |
| combined-decode/pdf/photo.webp | 36.35 | 11.93 |
| combined-decode/web/photo.jpg | 46.26 | 14.46 |
| combined-decode/web/photo.webp | 61.64 | 12.98 |

Direct child resource batches are separate from the primary C# gate. CPU is
RUSAGE_CHILDREN; RSS is sampled child VmHWM. Primary summaries retain parent CPU
and mixed-backend host peak RSS separately. Neither independent medians nor
host/child cumulative peaks are summed into an operation or four-child bound.
Hard inherited controls stay 1 GiB AS, CPU20/25s, nofile128, core0, non-root,
PDEATHSIG and four non-queuing operations. Aggregate/worst-case native memory
and a new caller-SIGKILL witness were not qualified.

## Safety, provenance and maintenance

JPEG substitution is limited to RGB24, terminal-EOI inputs and matching metadata.
Strict candidate decoding is used; original decoding handles grayscale/CMYK,
incomplete/trailing sources, supported bounded recovery and metadata appearing
later than read_info's SOF boundary. Geometry is admitted first; candidate axes,
pixels and estimated scratch are bounded. Scratch refusal remains Unavailable.
An initial lenient-wrapper regression admitted missing-EOI/truncated controls;
its failing witness and unaccepted binaries are preserved. The corrected route
passes the new rejection/recovery regression test and all final admission checks.

WebP keeps the original exact RIFF walk, animation rejection, ICC/EXIF extraction,
color/orientation processing and alpha selection. The webp convenience API only
returns Option on decode failure. The complete CLI therefore uses the same pinned
libwebp through its advanced API into admitted fallible RGB/RGBA storage, with
explicit out-of-memory classification and native buffer cleanup. It still uses
webp 0.3.1 features and benchmarks its convenience decode separately.

Crate pins and archives are preserved under `evidence/upstream/`; checksums match
Cargo.lock. libjpeg-turbo-rs 0.8.0 source revision is
`ee3f3de097647964e0c3ad8c0aa20d6265b5f2c5` (2026-07-28). It declares MIT OR
Apache-2.0, Rust1.87, and defaults only std/SIMD here. It adds no native binary;
this young pre-1.0 pin still requires codec/security/API maintenance. x86 SIMD
uses runtime detection with scalar fallback; AArch64 NEON implementation exists.
No native-only target CPU flag was used; ARM runtime was not measured.

webp 0.3.1 declares MIT OR Apache-2.0; its image adapter is disabled. It resolves
to explicitly pinned libwebp-sys 0.9.6 (MIT), statically compiling bundled C and
SharpYUV. The vendored version files identify **libwebp 1.3.1 plus security
backport**, not an unmodified 1.3.1 or current upstream release. Source revision
`4007a323c1dcc4ad11d70ddadffc51ecfa1dbb5e` includes the CVE-2023-4863 Huffman
allocation fix: source inspection and captured GitHub ancestry confirm it.
This is one verified backport, not a complete current security audit. The old
vendor snapshot makes ongoing native patch/provenance work a concrete cost.

Bundled BSD copyright/license, authors and WebM patent grant are retained with
wrapper MIT/Apache/COPYRIGHT notices in tools/image-helper/notices. Ten packages
are added to the lock, including build helpers and platform transitive packages;
no existing version is removed or updated. Their declared licenses are in
`evidence/new-dependencies.json`; r-efi has permissive alternatives to its LGPL
alternative and is not a Linux runtime dependency. Existing IJG/MIT notices stay.

Native x64 builds use baseline SSE2; optional global SSE4.1/AVX2 flags are off.
The crate provides AArch64 NEON build support, requiring a target C toolchain.
Its parallel default accelerates compilation, not this single decode operation.
ARM64/Windows native packaging/runtime were not attempted. The existing helper's
launch containment is Linux x64 only, irrespective of library CPU support.
Legacy JPEG and image-webp remain for required authority/fallback paths; these
experiments add dependencies rather than eliminate their maintenance.

Primary sources: [JPEG pin](https://docs.rs/crate/libjpeg-turbo-rs/0.8.0),
[webp 0.3.1 APIs](https://docs.rs/webp/0.3.1/webp/),
[native pin](https://docs.rs/crate/libwebp-sys/0.9.6), and the exact archived
source/API files and commit/security-ancestry JSON. No broader library selection
or packaging project was started.

## Optional existing native JPEG reference

| Installed Pillow / libjpeg-turbo 2.1.5 control | p50 ms | Output bytes |
| --- | --- | --- |
| native-jpeg-decode | 13.820 | 4970880 |
| native-jpeg-encode-pdf-photo.jpg | 0.815 | 43383 |
| native-jpeg-encode-pdf-photo.webp | 0.798 | 43327 |
| native-jpeg-encode-web-photo.jpg | 8.305 | 498545 |
| native-jpeg-encode-web-photo.webp | 7.676 | 501268 |

This uses the already installed Pillow/native JPEG build, with identical source
bytes, prepared RGB pixels, 4:2:0, Q and ICC intent. Its decode clock includes
Pillow image conversion/output copies. It is a binding-level reference, not a
bare C codec comparison or a third integration. No shared tooling was installed.

## Validation and evidence

- Locked/offline portable Rust Release builds for all three decoder variants and
  the pinned microbenchmark pass. Rust fmt and Release Clippy all-targets /
  all-features with warnings denied pass.
- The retained 74 xUnit cases pass at the original baseline and preliminary
  variants. **All 74 pass again on the final measured combined binary.** The
  final feature Rust suite passes four behavioral tests, including the two new
  JPEG recovery and WebP alpha/animation/metadata contracts.
- 48 final full-CLI admission controls (12 retained sources x four backends)
  independently confirm baseline exit classifications, no partial failures,
  canonical 612-byte ICC, format/geometry and no EXIF/XMP exposure.
- Every raw CLI cell/backend's sample count, median, nearest-rank p95 and inverse
  mean throughput were independently recomputed. Normal/diagnostic decoded pixel
  sets match in every direct cell. Final JPEG and calibrated WebP crops were
  visually inspected. No timing assertions or harness-internal tests were added.
- Code Guard task scope is REVIEW only for unchanged webp_input.admit complexity16.
  Comparison to fetched main e5f7298cea0450a862bcd710da1f0579cb0bcd50 retains
  unchanged color.png_profile (90 LOC), gif_input.admit (108 LOC, depth5,
  complexity24), png_input.admit complexity18 and webp_input.admit complexity16.
  These cohesive color/stream/container authorities remain together and tested;
  refactoring them is outside this codec experiment. Reused analyze.main and
  quality_codec.main aggregation reviews are accepted after raw calculation and
  output inspection. Required policies were read; no thresholds/exclusions or
  baselines changed. Final checks have no FAIL/INCOMPLETE or remaining tool error.
- Initial adapter error conversions, metadata-offline cache fetch, an external
  guard cwd/path, formatting and baseline diagnostic filename were corrected.
  One validation invocation inherited the baseline helper and is retained as
  baseline-repeat; the final combined run uses the explicit measured binary.
  An overstrict sampling checker compared component IDs; it was corrected to inspect actual factors/table selection. Initial failures/witnesses are retained and do not count as passing evidence.
  C# build has zero errors and the unchanged baseline advisory/analyzer warnings.
- **All 15418 protected original file hashes and all three original Git head/status
  snapshots match.** No prior source/evidence was modified.

Raw evidence: evidence/micro/, evidence/performance/, evidence/direct-*.json,
evidence/codec-summary.json, evidence/summary-*.json, evidence/alpha-codec.json,
evidence/codec-admission-controls-final.json, evidence/tests/combined-measured.trx,
evidence/statistics-and-probe-crosscheck.json and preservation-before/after.json.
Figures: outputs/quality-web-jpeg.png, quality-pdf-jpeg.png,
quality-web-webp.png and quality-pdf-webp.png. Exact executed argv/environment are
in micro-commands.jsonl and pipeline-commands.jsonl; builds/tests retain logs.
The retained CMS writer timestamps ICC creation fields; encoded hashes may vary while decoded pixels and byte lengths stay stable. Source recovery, binary and evidence hashes are in ARTIFACTS.json/SHA256SUMS.

## Reproduction and stopping

Read local-env.sh before sourcing; it selects task-local caches and the unchanged
.NET installation. The original collector and ImageSharp seam remain unchanged.
Existing reports are protected against overwrite by the pipeline driver; choose
new report directories for a future separately authorized rerun.

```bash
cd /home/stef/wayfarer-research/858-codec-comparison-20261013-zufsdz12
source local-env.sh
cargo build --manifest-path source/tools/image-helper/Cargo.toml --release --locked --offline --example codec_bench --features codec-bench
cargo build --manifest-path source/tools/image-helper/Cargo.toml --release --locked --offline --features jpeg-candidate-decode,webp-candidate-decode
cargo fmt --manifest-path source/tools/image-helper/Cargo.toml -- --check
cargo clippy --manifest-path source/tools/image-helper/Cargo.toml --release --locked --offline --all-targets --all-features -- -D warnings
cargo test --manifest-path source/tools/image-helper/Cargo.toml --release --locked --offline --features jpeg-candidate-decode,webp-candidate-decode
WAYFARER_IMAGE_HELPER="$PWD/bin/wayfarer-image-combined-decode-final" dotnet test source/tests/Wayfarer.Tests/Wayfarer.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~ImageHelperPrototypeTests|FullyQualifiedName~ImageHelperPngCompletionTests'
```

Micro operations use /usr/bin/prlimit AS1GiB/CPU20:25/nofile128/core0 and
`codec_bench decode CODEC INPUT OUTPUT COUNT` or
`codec_bench encode CODEC RGB OUTPUT WIDTH HEIGHT QUALITY COUNT CHANNELS`.
codec_measure.py and codec_followup.py specify the entire fixed matrix.
pipeline_measure.py sets explicit web/PDF recipes and calls the unchanged
ImageHelperPerformanceTests.RecordMatchedProfile. Direct stage/resource commands
use diagnose.py with --modes recovered instrumented --count12 --warmups3 and the
retained normal/diagnostic paths. Raw command records identify every measured
binary, source recipe and repetition count.

**Recommendation:** preserve the native WebP decoder evidence for a future
bounded integration decision with native provenance/maintenance closed. JPEG
encoder gain merits a narrowly authorized upstream bounded-output feasibility
step, not immediate integration. Keep JPEG/PNG optimized output and WebP input
support. Do not add WebP output or claim full Rust parity from this comparison.

Remaining gaps: broader fidelity/ICC/EXIF corpus, large photographic web resizing,
worst-case aggregate resources, non-x64 runtime/packaging, real trip/network/cache
behavior and complete candidate encoding integration. Those deferred work items
are unchanged. This focused comparison stops at the retained measured evidence.

