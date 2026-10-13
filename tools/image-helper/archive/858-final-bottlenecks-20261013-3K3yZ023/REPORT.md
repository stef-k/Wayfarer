# Wayfarer #858: final bottleneck diagnosis

**NO-GO. Phase A does not justify a small implementation. Phase B was not started.**
The remaining web costs are predominantly codec computation, rather than redundant
Wayfarer copying. No existing supported API offers a credible material recovery
of the JPEG/WebP gaps while retaining fidelity and complete verification.

Authority: [complete maintainer authorization](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6152722718).
Workspace: `/home/stef/wayfarer-research/858-final-bottlenecks-20261013-3K3yZ023`.
Source: `/home/stef/wayfarer-research/858-final-bottlenecks-20261013-3K3yZ023/source`.
Branch: `wip/858-final-bottlenecks-20261013`.
Starting and final source commit: `8a857230c0751d87b71e227431365d0fb16a7ca4`.
Repository files changed: **none**; the source checkout is clean. Results and this
report are outside Git. Final identities and hashes are in `ARTIFACTS.json`
and `SHA256SUMS`. No new checkpoint is needed for the unchanged clean commit.

## Method, source identity and accepted gate

The complete live #858 issue, authorization, #821 contract and repository
`AGENTS.md` were inspected. The scratch repository is a standalone
`git clone --no-hardlinks` of the authorized source, with its own branch,
object database, build outputs and copied Rust/NuGet caches. No earlier checkout
or evidence path was reused for writes. The .NET installation is read as an
immutable tool; CLI state and caches are local to this workspace.

Fresh locked/offline Release binaries were built with Rust 1.90.0. The normal
binary used for measurement and all 74 C# tests has SHA-256
`b07da6943b3d5f52eca00a656e9a8509e596c32cb6c67160db4530a87603ff23`;
the diagnostic binary is
`06a2bcdcf5b75bd73e8a6b2a790479f037370c926e250dc48d78523fbd734f4a`.
The final normal rebuild matches the measured binary. The new build is not
byte-identical to the older workspace's binary; source and locked dependencies
are identical, and every measured decoded output matches the retained output.
Nine processing, supervision and benchmark seams were also hashed independently.

Same input bytes were used by both backends:

| Input | Bytes | SHA-256 |
| --- | ---: | --- |
| photo.jpg | 460422 | 166807a3de2d35931156b56e05db36664d49d85e956b09943d16bc9bffa05964 |
| photo.webp | 485820 | 802b8ccd7aa35d4cad8625bd0b565a7af6d64faf6560964e9b6dc530b53a3b93 |

Both photographic sources are 1920×863. Web 2048×2048/90 recompresses without
upscaling or resizing; PDF 600×8192/85 produces 600×269. Large photographic
web downscaling is not measured here. The unchanged explicit recipe honors both
axes and quality; this experiment does not implement HTTP defaults or alter
the separately accepted normalized high-fidelity `optimize=false` intent.

The unchanged C# collector runs ImageSharp 3.1.12
`OptimizeImage + ValidateRaster` against the unchanged Rust supervisor.
Every timed Rust operation includes four-slot non-queuing admission, owning
thread creation, prlimit/exec, stdin/stdout/stderr communication, full processing,
complete derivative verification, process cleanup and reap. Source fixture
reads, expected-geometry identification and evidence inspection/writes are
outside timing. No network/cache times are substituted.

Each of four cells has two independent warm hosts, 20 warmups and 24 measured
operations per backend per host: **48 fresh warm observations per backend/cell**.
Order alternates and reverses across sessions. Builds, tests and other timed
batches did not overlap these measurements. The WSL/i7-10700K host is shared;
host/process snapshots are retained. There are no timing assertions in tests.

P50 is median, p95 nearest rank and sequential throughput inverse mean
operation time. Frozen acceptance is **p50 and p95 ≤110% of matched ImageSharp,
and throughput ≥90%**, separately for each cold/warm profile. Calculations were
independently recomputed from every raw observation, rather than accepted from
the reused summary collector alone.

## Fresh complete-operation measurements

Warm Rust / ImageSharp, milliseconds and transforms per second:

| Profile/input | p50 ms | p95 ms | Throughput ips | Rust / IS throughput | Gate |
| --- | ---: | ---: | ---: | ---: | --- |
| PDF JPEG | 27.525 / 19.297 | 30.889 / 62.295 | 35.834 / 34.723 | 103.20% | FAIL p50 |
| PDF WebP | 63.921 / 55.039 | 75.585 / 68.281 | 15.413 / 17.508 | 88.03% | FAIL all |
| Web JPEG | 55.950 / 29.930 | 65.834 / 46.799 | 17.507 / 31.647 | 55.32% | FAIL all |
| Web WebP | 91.962 / 67.560 | 94.710 / 83.726 | 10.817 / 14.300 | 75.64% | FAIL all |

Remaining distance to the frozen gate; negative means that particular metric
passes in this batch:

| Profile/input | p50 excess over allowed ms | p95 excess over allowed ms | Throughput shortfall ips | Shortfall percentage points |
| --- | ---: | ---: | ---: | ---: |
| PDF JPEG | 6.299 | -37.635 | -4.583 | -13.20 |
| PDF WebP | 3.378 | 0.475 | 0.344 | 1.97 |
| Web JPEG | 23.027 | 14.354 | 10.976 | 34.68 |
| Web WebP | 17.646 | 2.612 | 2.053 | 14.36 |

The authorized 22.54/17.72 ms web p50 gaps reproduce as **23.027/17.646 ms**.
Rust web p50 is within approximately 1% of the retained result. Each web session
individually fails all three metrics; all four cells individually fail p50 in
both sessions. PDF JPEG has substantial ImageSharp tail/host variation, so its
aggregate p95/throughput passes are not an optimization result or a stable
parity claim. Raw per-session statistics preserve that variation.

Retained cold evidence is preserved, not rerun or relabeled as fresh:

| Profile/input | Retained cold Rust / IS p50 ms | Retained cold p95 ms | Retained throughput ratio |
| --- | ---: | ---: | ---: |
| PDF JPEG | 37.07 / 157.86 | 68.43 / 181.90 | 370.9% |
| PDF WebP | 78.48 / 330.62 | 112.74 / 384.70 | 400.0% |
| Web JPEG | 68.70 / 165.67 | 101.67 / 195.65 | 225.2% |
| Web WebP | 102.42 / 355.06 | 119.35 / 381.67 | 343.6% |

Those eight-fresh-host cold observations passed previously; OS caches were not
flushed and metadata identification could warm managed code. Repeating cold
qualification cannot rescue the repeated warm failure. Phase A stops before
new candidate cold, PNG/GIF performance or trip-load qualification.

## Stage attribution and containment accounting

Existing diagnostic probes were reused without source edits: three warmups and
12 observations per normal/diagnostic binary, format and profile. Decoded pixels
agree between normal and diagnostic builds in all four cells.

Disjoint-stage medians, milliseconds; these medians must not be added:

| Profile/input | Source decode | Conditional color/layout | Orientation/drop | Resize | JPEG encode | Complete output verify |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| PDF JPEG | 12.270 | 4.625 | 0.282 | 2.836 | 2.089 | 1.787 |
| PDF WebP | 54.619 | 0.0004 | 0.0008 | 2.576 | 2.096 | 1.891 |
| Web JPEG | 12.240 | 4.532 | 0.243 | 0.0001 | 21.668 | 12.196 |
| Web WebP | 55.775 | 0.0005 | 0.0007 | 0.0000 | 21.456 | 12.141 |

Input-byte admission is enforced while reading stdin; trusted argument/process
controls take about 0.010 ms. Web stdin takes 0.447/0.718 ms for JPEG/WebP;
stdout takes 0.569/0.583 ms. JPEG source admission is `read_info` plus geometry
checks before raster allocation. WebP admission performs the strict RIFF walk
and an encoded payload copy/header inspection. Inspection of pinned
`WebPDecoder::new/read_data` confirms that this is **not a second pixel decode**.
Both are contained in `source_decode`, together with source ICC assembly/parsing
and full codec decode; their individual wall times are not independently probed.

Conditional ICC conversion and tagged-RGB allocation are in `color_prepare`.
Untagged RGB WebP transfers its owned buffer without a copy; its near-zero
color stage demonstrates that a float/layout detour is already gone.
The JPEG fixture still needs its tagged color transform, with sampled AVX2 CMS.
RGB-to-YCbCr conversion and averaged 4:2:0 chroma preparation are codec encode
work, not an additional separately summed layout stage. Canonical ICC generation
is inside encode; output ICC parsing and JPEG pixel decode are inside verify.

The `run` span encloses all leaf probes. For each individual observation,
`run - sum(disjoint leaves)` is positive: web median residual 0.164 ms JPEG and
0.131 ms WebP; PDF 0.462/0.418 ms. This residual includes unprobed geometry,
format selection, checks and timing/reporting overhead. It is computed per
observation, never by subtracting unrelated medians.

Direct Python launch-to-reap minus the same observation's `run` is about
2.49/2.48 ms for web, including launcher, final pipe drain, sampler stop and reap.
This is **not a measured C# overhead**. C# admission/IPC/reap remains inside the
primary full-operation clock; parent CPU observations are separate, and the
collector has no dedicated C# overhead span. Subtracting medians from separate
C# and Python populations would not produce attributable overhead. Source
admission/ICC leaves and exact C# overhead therefore remain attribution limits,
not hidden excluded work or a zero-cost claim.

## Two hotspots and realistic recovery opportunities

CPU-clock sampling of 40 normal full-CLI web operations per format supports the
stage diagnosis. It is supplemental CPU evidence, not another wall-time gate.
Only self samples in the helper executable are used; Python/Pillow evidence
inspection is excluded. Sampling includes decoder threads.

The installed perf build lacks ELF/DWARF symbol/unwind support. Exact addresses
were resolved offline using the existing `nm -n -S -C` symbol ranges and
`readelf -l`: the executable text LOAD has virtual address = file offset +0x1000.
All 1,794 JPEG and 3,299 WebP helper samples resolve inside exact symbol sizes;
there are zero lost samples. No inclusive call-chain attribution is claimed.
At 999 Hz, sample count × 1000/999 ÷40 gives a rough CPU-ms estimate per image;
inlining, scheduler effects and finite sampling prevent treating it as a
wall-time bound or proven optimization gain.

1. **Common JPEG encoding, about 21.5–21.7 ms wall.** JPEG chroma block averaging
   accounts for roughly 7.46/7.13 ms sampled CPU on JPEG/WebP inputs; AC entropy
   emission roughly 5.01/4.35 ms. RGB conversion and DCT already use upstream AVX2.
   The public averaged 4:2:0 route is the matched quality contract. Switching to
   chroma Skip/nearest filtering changes fidelity; owning/forking these hot loops
   exceeds the small supported-API boundary. Huffman optimization is already off,
   so there is no redundant optimization pass to remove. Out-of-line bounded
   writes plus `write_all` account for only approximately **0.626/0.400 ms CPU**.
   A buffered writer might change that fraction, but it cannot credibly recover
   a meaningful part of the 23.03/17.65 ms gaps. Inlined writer work is not fully
   separable; no exact speedup from buffering is claimed.

2. **WebP VP8 decode, about 55.8 ms wall.** The existing codec's self samples
   account for roughly 51.3 ms CPU. Arithmetic tree decoding alone accounts for
   about **20.75 ms CPU**, followed by coefficients, filtering, transforms and
   fancy chroma interpolation. The duplicate-looking constructor only reads
   container/payload headers. Removing the header copy cannot remove this
   entropy work. The pinned public API exposes upsampling choice, not a supported
   faster entropy decoder or equivalent SIMD/parallel mode. Nearest chroma
   interpolation or disabled codec work changes displayed quality/correctness;
   replacing codecs is not a justified copying optimization.

Complete output verification remains about 12.1–12.2 ms on web JPEG derivatives.
It is required full decode and metadata/orientation checking, not a redundant
input pass. The pinned JPEG decoder already samples SSSE3 IDCT/color conversion.
Its `ColorTransform::None` API could theoretically avoid derivative RGB conversion,
but all sampled source-plus-verification JPEG color conversion totals only
about **2.08 ms CPU on JPEG input**, and **0.90 ms on WebP input**. The derivative-only
recoverable subset is smaller. Combining even the unrealistic elimination of
all those conversions and out-of-line writes gives only about **2.70/1.30 ms CPU**,
versus **23.03/17.65 ms wall** gaps. This is an opportunity screen, not a guarantee
that those sums would translate into wall savings.

Skipping source CMS would remove at most the observed 4.53 ms tagged color stage
while violating the color contract; web resizing already costs zero. Process
pooling, persistent services, weaker verification, quality reduction and codec
replacement are not proposed. Compiler-profile tuning has no measured gain in
this diagnosis and is not used to forecast a qualifying result.

**Phase A decision: NO-GO FOR SMALL FIX.** No additional small wrapper change is
justified by this evidence. Large savings would require codec-level optimization
or a different design, with unproven maintainability/behavior and separate
authority. No such follow-up was initiated. There are no individual/combined
new implementation measurements because no implementation was justified.

## Functional, quality, resource and static validation

All **74 retained xUnit functional/safety cases pass** using the fresh normal
binary, including actual JPEG/PNG/WebP/static GIF, EXIF orientations including
PNG, ICC/gray/CMYK/gamma/16-bit/cICP, alpha, metadata privacy, animation/malformed
inputs, PNG complete-picture admission, resource ceilings, pipes, timeouts,
cancellation, crashes/reaping and four non-queuing slots. The **two Rust tests
pass**, covering scalar/SIMD odd-stride resizing and complete odd-sized JPEG
quality overrides, canonical ICC/privacy and bounded output. No new tests or
harness internals were added.

Release/locked/offline builds, Rust fmt and Release Clippy with all targets,
all features and warnings denied pass. The C# Release build has zero errors and
32 retained warnings, including unchanged ImageSharp advisories and unrelated
xUnit analyzer warnings. No C# code changed, so no broad C# formatting was run.

All four fresh Rust paired outputs have **identical decoded pixels, byte counts,
sampling and quantization tables to the retained combined outputs**. Canonical
ICC bytes match except the unchanged writer's 12-byte creation-date field.
Geometry, orientation=1, no EXIF GPS and no XMP were checked. Independent
LittleCMS display comparisons against the current paired ImageSharp output:

| Profile/input | Rust bytes | ImageSharp bytes | Display PSNR vs IS dB |
| --- | ---: | ---: | ---: |
| PDF JPEG | 43582 | 43327 | 42.55 |
| PDF WebP | 43524 | 42685 | 43.09 |
| Web JPEG | 498531 | 498696 | 46.68 |
| Web WebP | 501261 | 498676 | 46.66 |

Quantization tables match ImageSharp. The paired web JPEG images were visually
inspected with no evident new artifact or color shift. PSNR is observational,
not an acceptance threshold; broad perceptual/color-corpus acceptance remains
unproven. ICC timestamp changes explain encoded hash variation without pixel
or other profile changes.

Separate normal-build direct child CPU/RSS observations:

| Profile/input | Median child CPU ms | Median sampled child VmHWM MiB |
| --- | ---: | ---: |
| PDF JPEG | 30.10 | 14.36 |
| PDF WebP | 64.90 | 11.98 |
| Web JPEG | 60.26 | 14.33 |
| Web WebP | 102.18 | 12.85 |

The paired collector records parent CPU and shared host peak RSS separately;
web Rust parent CPU medians are 5.98/4.83 ms, ImageSharp 30.30/71.90 ms.
Shared host peaks around 227–235 MiB are cumulative mixed-backend observations,
not attributable operation or aggregate host+four-child peaks. Independent
parent and child medians must not be summed. Existing hard 1 GiB AS, CPU20/25s,
nofile128/core0, non-root and PDEATHSIG controls are unchanged. Caller-SIGKILL and
aggregate four-operation memory were not freshly qualified.

No dependency, license, runtime feature or CPU-selection change was made.
The retained fast_image_resize 6.1.0 and jpeg-encoder 0.7.1 paths still use native
runtime SIMD detection and scalar fallback without `target-cpu=native`.
MIT/Apache/Unicode and JPEG MIT/IJG notices are preserved. Non-x64 runtime,
packaging and containment remain unproven; no new platform qualification occurred.

Code Guard changed-work source scope is **PASS (empty)**. The complete frozen
branch compared with fetched live main `f4dbba8cb72213798d24d8b0697187bd46295ca0`
is **REVIEW**, with no FAIL/INCOMPLETE: unchanged `color.png_profile` (90 LOC),
`gif_input.admit` (108 LOC, depth5, complexity24), `png_input.admit` (complexity18),
and `webp_input.admit` (complexity16). Their cohesive color precedence and
stream/container admission responsibilities remain together, unchanged and
covered by retained behavioral tests. Refactoring is outside this diagnosis.
The unmodified reused `analyze.main` collector has complexity38 from explicit
grouping/filtering/statistics; its REVIEW is accepted after independent formula
recomputation. Only required callable-size, nesting and complexity policies
were read. No policies, exclusions, thresholds or baselines were changed.

One scratch fetch was initially attempted outside Git and corrected in the
scratch source; the failed log is retained. An unsupported perf script option
was corrected to the documented `-G` option. Perf's symbol/unwind limitation is
retained and addressed by exact offline symbol mapping. No unresolved
Code Guard tool/configuration error remains.

## Reproduction and retained artifacts

Run commands only in this scratch workspace. Read `local-env.sh` before sourcing;
it selects task-local caches and the retained immutable .NET installation.
The Python collectors are byte-identical copies of the starting collectors.

```bash
cd /home/stef/wayfarer-research/858-final-bottlenecks-20261013-3K3yZ023/source
source ../local-env.sh
cargo build --manifest-path tools/image-helper/Cargo.toml --release --locked --offline
cargo fmt --manifest-path tools/image-helper/Cargo.toml -- --check
cargo clippy --manifest-path tools/image-helper/Cargo.toml --release --locked --offline --all-targets --all-features -- -D warnings
cargo test --manifest-path tools/image-helper/Cargo.toml --release --locked --offline
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~ImageHelperPrototypeTests|FullyQualifiedName~ImageHelperPngCompletionTests'
code-guard . --changed-only --json --json-mode compact
code-guard . --base-ref main --ci
```

For a fresh paired web JPEG observation, choose an unused report name:

```bash
WAYFARER_IMAGE_PERF_INPUT=photo.jpg \
WAYFARER_IMAGE_PERF_REPORT="$PWD/../evidence/reproduction-web-jpeg.json" \
WAYFARER_IMAGE_PERF_PHASE=warm \
WAYFARER_IMAGE_PERF_REPETITIONS=24 \
WAYFARER_IMAGE_PERF_WARMUPS=20 \
WAYFARER_IMAGE_PERF_WIDTH=2048 \
WAYFARER_IMAGE_PERF_HEIGHT=2048 \
WAYFARER_IMAGE_PERF_QUALITY=90 \
WAYFARER_IMAGE_PERF_ORDER=1 \
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~ImageHelperPerformanceTests.RecordMatchedProfile
```

Repeat with photo.webp and/or width600/height8192/quality85 for the other cells.
The recorded two sessions use order offsets1 and0 respectively. Recorded
diagnostics use `diagnose.py --fixtures photo.jpg photo.webp --modes recovered
instrumented --count 12 --warmups 3 --candidate bin/wayfarer-image-baseline
--diagnostic bin/wayfarer-image-diagnostic`, with explicit web/PDF recipe and
unused report names; binary arguments must be absolute paths.
Perf uses `perf record -e cpu-clock:u -F 999 --call-graph dwarf` around the same
collector with one fixture, recovered mode, count40 and warmups0; use the normal
binary. Offline `perf report --no-children --sort comm,dso,symbol --call-graph none
--percent-limit 0 --show-nr-samples --show-total-period --field-separator '|'`
and `nm -n -S -C` support the exact symbol/period calculations above.

Primary evidence: `evidence/performance/baseline/` raw observations and outputs;
`summary-baseline.json`, `summary-crosscheck.json`, `gate-gaps.json`,
`stage-accounting.json`, `cpu-attribution.json`, perf data/counts/symbol ranges,
`retained-output-equality.json`, test TRX and build/static-check logs.
`evidence/phase-a-decision.json` records the pre-implementation NO-GO.
The starting report/identity/manifest and cold summary are copied to `retained/`.

All **73,135 protected prior file hashes** were recorded before work. Final
verification details are in `evidence/preservation-after.json`, including
previous repository heads/status/worktrees and the primary checkout identity.
The hashes/identity checks are finalized before the artifact manifest. This
report does not grant production, broad corpus, aggregate-resource, real trip or
cross-platform acceptance. The experiment ends here with **NO-GO**.

