# #858 final JPEG tests and consolidated Rust archive

**NO-GO for production; ready for the maintainer's separate decision.** The best
safe retained implementation uses native TurboJPEG encoding, guarded pure Rust
JPEG decoding, the passing native WebP decoder, SIMD RGB Lanczos3, and the
recovered opaque-sRGB pipeline. JPEG remains outside the unchanged warm p50 gate.
ImageSharp 3.1.12, production HTTP/cache behavior, and release state are unchanged.

Authority: [single final assignment](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6155331235),
[prior checkpoint](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6155228367).
The clean starting commit was `8d6e488f13d7de0b36c93ce10c2b3b5dcea9ccb4`,
with verified ancestry from `8a857230c0751d87b71e227431365d0fb16a7ca4`.
Only the two authorized JPEG encoders were tested; no new codec survey, WebP
output policy, production integration, PR, merge, release, or review cycle occurred.

## Encoder decisions and safety

| Requested route | Result | Disposition |
| --- | --- | --- |
| `libjpeg-turbo-rs 0.8.0 compress_into()` | FAIL incremental output ceiling | Benchmark-only negative trial; never a CLI encoder |
| `turbojpeg 1.5.1` / `turbojpeg-sys 1.2.0`, vendored libjpeg-turbo 3.1.0 | PASS focused caller-buffer safety and functionality | Selected research encoder; production acceptance remains NO-GO |

Pure Rust's published `src/api/high_level.rs::compress_into` first calls the
allocating encoder to obtain a complete internal `Vec`, then checks its length
against the caller slice. A 16-byte canary destination returns `BufferTooSmall`
with the already computed full output size; an exact-size destination succeeds.
It has no ICC parameter. `Encoder::encode` supports ICC but returns a complete
`Vec`; `compress_to_writer` similarly buffers before writing. Source inspection
and behavioral tests establish a late refusal, not an incremental allocation cap.
Consequently a safe end-to-end encoder substitution is unavailable. No unbounded
CLI trial, codec fork, or custom native wrapper was built to bypass this failure.

The native safe `Compressor::compress_to_slice` API uses a borrowed `OutputBuf`
and sets `TJPARAM_NOREALLOC`. Its vendored `jdatadst-tj.c` destination raises
`JERR_BUFFER_SIZE` when full instead of allocating a larger encoded buffer.
Tests exercise real overflow with untouched outside canaries and handle recovery.
The native entropy writer can conservatively refuse a completely full buffer
even when the eventual JPEG length would fit; this is explicitly retained.
An initial test incorrectly expected exact-fit success; its failed witness is
retained locally, and the corrected test records the actual refusal semantics.

The integrated encoder fallibly allocates one caller-controlled extent no larger
than the recipe cap or the codec's worst-case bound plus ICC space. It reserves
space for the existing generated 612-byte sRGB profile before encoding, then
inserts one canonical APP2 segment in that same allocation. This avoids a second
full JPEG allocation; no source EXIF/GPS/XMP is copied. Native scratch remains
subject to the inherited process address-space limit, not an invented per-codec
RSS guarantee. Web/PDF Q90/Q85 use 4:2:0; quality 95 retains 4:4:4.

Source-byte admission, JPEG bounded recovery/fallback, exact PNG completion,
static GIF/WebP policy, alpha, orientation, ICC normalization, no enlargement,
full derivative decoding/verification, fixed diagnostics, and failure classes
remain at their original seams. Nothing reaches stdout until complete success.
The unchanged C# supervisor keeps four non-queuing slots, bounded pipes, timeout,
caller cancellation, termination and reaping. Cancellation kills the whole
isolated child; neither new encoder API offers cooperative cancellation.

Hard controls remain encoded input/output <=50 MiB, axes <=8192, <=12M pixels,
64 MiB decoded estimate, 1 GiB process AS, CPU20/25s, nofile128, core0, non-root,
PDEATHSIG and fail-closed cleanup. These do not prove an aggregate four-child RSS
ceiling. The retained transparent 4000x3000 near-limit image correctly refuses
forced JPEG; its successful PNG resource observations remain a separate control.
At the web recipe it produces 2048x1536 PNG, with observed old/selected child
VmHWM 328.75/328.79 MiB and CPU 931.75/914.04 ms; this is not JPEG encoder evidence.

## Matched complete-pipeline measurements

Host: Ubuntu 24.04/WSL2 Linux AMD64, i7-10700K; portable Rust 1.90.0 Release,
.NET SDK 10.0.112/runtime 10.0.12; no host-specific Rust/C target flags.
Source fixtures are the same 1920x863 photograph: JPEG 460422 bytes, SHA-256
`166807a3de2d35931156b56e05db36664d49d85e956b09943d16bc9bffa05964`;
WebP 485820 bytes, SHA-256
`802b8ccd7aa35d4cad8625bd0b565a7af6d64faf6560964e9b6dc530b53a3b93`.
Web is 2048/90 without upscale; PDF is 600/85, producing 600x269.

The unchanged paired xUnit collector measures ImageSharp `OptimizeImage` plus
`ValidateRaster`, and the Rust supervisor through process startup, IPC,
normalization, full output verification and reap. Warm: two reversed-order
sessions, 20 warmups and 24 observations/backend each, 48 pooled observations.
Cold: eight fresh managed hosts/cell, alternating order, no OS cache flush.
Throughput is inverse mean sequential wall time, not concurrent capacity.
Builds and other task benchmarks did not overlap these timed batches.

| Warm profile/input | Selected p50/p95 ms | Prior optimized Rust p50/p95 ms | ImageSharp p50/p95 ms | Selected/IS images/s | Throughput ratio | Gate |
| --- | --- | --- | --- | --- | --- | --- |
| Web JPEG | 33.044 / 37.953 | 48.000 / 57.173 | 29.971 / 46.286 | 29.400 / 30.824 | 95.4% | FAIL p50 |
| PDF JPEG | 21.581 / 27.799 | 22.938 / 26.308 | 19.290 / 35.589 | 44.732 / 44.173 | 101.3% | FAIL p50 |
| Web WebP | 47.968 / 54.022 | 63.132 / 71.987 | 69.136 / 91.869 | 20.334 / 13.825 | 147.1% | PASS |
| PDF WebP | 35.711 / 38.142 | 37.141 / 39.910 | 54.315 / 68.436 | 27.765 / 17.747 | 156.5% | PASS |

JPEG p50 ratios are **1.102509 web** and **1.118756 PDF**: 0.0752 ms and
0.3618 ms beyond their paired 110% allowances. Both first warm JPEG sessions
pass; both second sessions and both pooled JPEG p50 gates fail. The counterexamples
remain; no rerun or threshold relaxation was used. Both WebP sessions/profiles
pass. Cold results cannot compensate for warm failures.

| Cold profile/input | Selected p50/p95 ms | ImageSharp p50/p95 ms | Throughput ratio | Observed gate |
| --- | --- | --- | --- | --- |
| Web JPEG | 72.271 / 77.288 | 174.629 / 217.437 | 250.9% | PASS |
| PDF JPEG | 56.003 / 72.192 | 167.950 / 216.799 | 310.7% | PASS |
| Web WebP | 79.217 / 89.862 | 356.832 / 389.166 | 432.8% | PASS |
| PDF WebP | 72.295 / 80.719 | 361.221 / 388.383 | 480.5% | PASS |

Frozen #821 gate: each profile/phase p50 and p95 <=110% of matched ImageSharp,
throughput >=90%. Small remaining JPEG regressions may be a separate maintainer
decision; this archive does not accept them. No real trip/network/cache acceptance
or GIF/PNG requalification is inferred from these JPEG/WebP measurements.

## Encode-only, fidelity and resources

Identical admitted normalized RGB buffers; two reversed sessions of 12 plus three
warmups give 24 observations/cell. ICC generation and file I/O are untimed;
native/old ICC embedding is timed. The failed pure `compress_into` route cannot
embed ICC and its byte counts exclude that 630-byte envelope.

| Pixels/profile | Prior SIMD p50/p95 ms | Pure `compress_into` p50/p95 ms | Native bounded p50/p95 ms | Bytes old/pure/native |
| --- | --- | --- | --- | --- |
| JPEG/web | 20.635 / 27.643 | 5.861 / 8.431 | 5.302 / 6.750 | 498737 / 498069 / 498699 |
| JPEG/PDF | 2.011 / 2.229 | 0.537 / 0.582 | 0.482 / 0.533 | 43570 / 42724 / 43354 |
| WebP/web | 20.482 / 26.828 | 5.765 / 5.825 | 5.338 / 7.413 | 501261 / 500638 / 501268 |
| WebP/PDF | 2.020 / 2.091 | 0.543 / 0.598 | 0.498 / 0.547 | 43524 / 42697 / 43327 |

Native encode-only medians improve about 74-76%. CPU medians track these clocks;
the complete JSON records CPU/RSS and bytes for all candidates. Encode-only native
VmHWM is 13.53/13.55 MiB web and 4.89/4.96 MiB PDF; old is 9.02/9.21 and
4.27/4.32 MiB. These are current-executable high waters, not inherited getrusage
launcher peaks or hard bounds.

Independent LittleCMS display PSNR against prepared RGB pixels is JPEG/web
42.524 dB, JPEG/PDF 32.634 dB, WebP/web 40.701 dB and WebP/PDF 33.135 dB.
Old and selected encoder values differ by <0.001 dB. Sampling and quantization
intent match; canonical ICC is 612 bytes and EXIF/XMP are absent. Inspected
unscaled JPEG crops show no obvious new color shift or visible degradation.
These are focused photographic proxies, not broad or blind fidelity acceptance.
ImageSharp-versus-selected display comparisons are preserved separately.

| Full CLI, separate 12-sample resource probes | Old/selected child CPU ms | Old/selected sampled VmHWM MiB |
| --- | --- | --- |
| JPEG/web | 45.968 / 31.009 | 14.23 / 16.89 |
| JPEG/PDF | 21.013 / 19.456 | 14.20 / 14.29 |
| WebP/web | 60.789 / 46.063 | 12.58 / 17.38 |
| WebP/PDF | 35.817 / 34.716 | 11.62 / 11.88 |

Parent CPU/shared-host RSS from the paired C# collector are also retained. They
are reported separately from child CPU/HWM; medians and cumulative peaks are
not summed into a synthetic aggregate bound.

## Dependencies, portability and retained validation

The new native pins add nine lock entries and change no prior package version.
`turbojpeg` / `turbojpeg-sys` declare MIT OR Unlicense. Vendored libjpeg-turbo 3.1.0
requires IJG and BSD-3-Clause notices, with zlib SIMD notices; exact notices,
IJG acknowledgment and dependency checksums/license declarations are retained.
This software is based in part on the work of the Independent JPEG Group.

Native x64 SIMD was built with task-local NASM 2.16.01 and ordinary CMake/C
tooling, statically into the helper. No shared installation or packaging project
was changed. x64 runtime dispatch and ARM64 NEON source exist in both JPEG
candidates. An installed-target ARM64 pure Rust library check passes; ARM64
native C tooling/runtime and Windows packaging/runtime remain unqualified.
The current CLI and supervisor deliberately fail closed outside Linux x64.
The retained libwebp-sys 0.9.6 vendor is 1.3.1 with a previously verified security
backport, not a current complete security audit. Native patch/provenance work
remains a cost, and scalar/recovery codecs stay for required authority paths.

Selected Release build, Rust fmt, all-target/all-feature Clippy with warnings
denied, six focused Rust tests (including both encoder probes), and all 74 retained
xUnit safety/format cases pass. C# Release build has zero errors and unchanged
dependency/analyzer warnings. Fresh remote-checkout build/tests are the mandatory
cleanup gate and are recorded locally after publication; failure blocks deletion.

Code Guard retains REVIEW for cohesive existing format/container authorities and
historical statistics aggregators, plus the benchmark's encoder dispatch. Those
perform one explicit bounded parsing/aggregation/dispatch responsibility and
remain readable and tested. No policy, exclusion or baseline was changed.

## Durable and local evidence

The sole publication branch is `research/858-rust-imaging` in `stef-k/Wayfarer`.
Its commit ancestry preserves prototype `c998da079838894da32cb331f4f338cd9597047f`,
recovery `017e7495c42336d7205b2d6263828ec96e0d980c`, SIMD
`8a857230c0751d87b71e227431365d0fb16a7ca4`, and comparison
`8d6e488f13d7de0b36c93ce10c2b3b5dcea9ccb4`. Measured final code is checkpoint
`4a1e791adde6151f4d3b5cfd9ca8e89db3133cc5`; subsequent default-feature selection
enables the same three measured routes. Runtime source bodies remain identical.
Measured binary hashes, raw sample hashes and commands are in the final manifest.

`archive/` preserves all prior root reports, commands, source identities/hashes,
compact summaries, and a 5211-byte source-history bundle for the distinct
encoder-only head `5e515719152537d03f1b1b0c6e0fccc8f99de6a0`.
The diagnosis's ten uncommitted instrumented files are preserved as an exact
binary-capable patch against the retained prototype commit; reconstruction was
byte-compared with all ten original files. They are historical diagnostic code,
not applied production changes. Historical scripts retain their original paths
and must not be sourced as the current reproduction setup.

`fixtures/` holds the retained approximately 9.3 MiB corpus, including small
malformed/completion/metadata controls and the exact photographic inputs.
`benchmarks/` holds the existing narrowed measurement drivers; `results/` holds
final summaries, hashes, commands, test counts and archive/cleanup manifests.
Large binaries, caches, output images, raw duplicate logs and the full original
evidence tree are not published. All 3836 original external evidence/artifact
files are preserved in a **local** SHA-256-addressed evidence vault (348.7 MiB
deduplicated) with per-workspace path manifests. It is intentionally not a remote
backup of every raw sample. Preserve the useful final workspace and that vault.

Only after remote SHA/file-list verification and a clean fresh-checkout Release
build/focused test pass, remove the individually inventoried seven obsolete
`858-*` workspaces and their owned nested encoder-only checkout. Source ancestry,
patch/bundle recovery, external evidence hashes, cleanliness and ownership are
verified per path first. The actual cleanup receipt and disk-space accounting
stay in the retained local workspace, not in a preclaimed remote success record.

## Reproduction and stop

Clone this research branch into `WORK/source`. Set task-local tool/cache locations,
provide Rust 1.90.0, .NET 10, CMake/C compiler and NASM, then:

```sh
cd WORK
cp -a source/tools/image-helper/fixtures fixtures
export WAYFARER_IMAGE_FIXTURES="$PWD/fixtures"
cargo build --manifest-path source/tools/image-helper/Cargo.toml --release --locked
cargo test --manifest-path source/tools/image-helper/Cargo.toml --release --locked --features codec-bench
export WAYFARER_IMAGE_HELPER="$PWD/source/tools/image-helper/target/release/wayfarer-image"
dotnet test source/tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~ImageHelperPrototypeTests|FullyQualifiedName~ImageHelperPngCompletionTests'
```

Copy fixtures for tests: retained xUnit cases write local completion controls.
For benchmark replay, use a fresh `WORK/evidence/fixtures`, `WORK/bin` with the
selected and preserved baseline binaries, task-local `CARGO_TARGET_DIR=WORK/target`,
and `WAYFARER_IMAGE_RESEARCH=WORK`. Build `codec_bench` with `--features codec-bench`.
The existing narrowed `benchmarks/codec_measure.py` and `pipeline_measure.py`
refuse to replace their observation directories/reports. Command manifests
identify the precise measured sources, recipes, flags and sample counts.

Deferred: separate maintainer adjudication of the two JPEG p50 regressions,
independent review/CI before shipping, native security/provenance and platform
packaging, broader color/hostile aggregate resources, and real trip/cache/burst
acceptance. This single pass stops here; none is authorized by this archive.
