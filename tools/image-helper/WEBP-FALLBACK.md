# #858 existing pure-Rust WebP fallback — 2026-10-15

**NO-GO.** Small-WebP PDF warm p50 misses the unchanged performance gate, and a
container-consistent malformed VP8L input aborts the pinned decoder in Release.
The supervisor fails closed, but decoder safety qualification is not closed.
No fallback is selected, no optimization retry is authorized, and no integration follows.

Authority: [one bounded fallback comparison](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6194984703).
Exact starting research head: `a67267660fc958bb019557c400ca5fa06d9d1852`.
The fresh isolated worktree is `wip/858-webp-fallback-20261015` at
`/home/stef/wayfarer-research/858-webp-fallback-20261015/source`.
All selected runtime source, default features, Cargo.lock, fixtures, supervisor,
notices and previous optimizations remain byte-identical to the starting head.
Only documentation and verified research evidence are published.

## Artifact and fair comparison

The candidate is an exact external copy of the existing helper. Its only runtime
configuration change removes `webp-candidate-decode` from the default feature list.
The existing complete `image_webp::WebPDecoder::read_image` fallback runs with
`image-webp =0.2.4`; selected Rust JPEG decoding, bounded native TurboJPEG output,
SIMD Lanczos3, opaque-sRGB routing, metadata handling and C# supervision remain.
One existing Rust WebP test's native-only cfg gate was removed in the test copy.
No codec source was patched and no additional codec was examined.

Locked Release in a separate clean `target-default-only` has exactly the features
`default`, `jpeg-candidate-decode`, `native-jpeg-encode`. Neither benchmark nor
diagnostic features are present. Rust checks use a separate `target-checks`.
The measured executable SHA-256 before and after all observations is
`c8b4dd3bbf89938e365f107c7e53c2f753166c25d8594393f19f7dbef0441b97`.
Build identity, source hashes, exact candidate manifest and test-only patch are
retained in [the evidence directory](results/webp-fallback-20261015/).

Ubuntu 24.04/WSL2 AMD64, non-root UID 1000, i7-10700K; Rust 1.90.0,
GCC 13.3.0, CMake 3.28.3, NASM 2.16.01, .NET SDK 10.0.112.
No host CPU flags or native-library overrides. The actual ImageSharp assembly
matches the unchanged **3.1.12** NuGet package; both hashes are retained.

The unchanged paired collector times ImageSharp `OptimizeImage + ValidateRaster`
against complete supervisor admission, child startup, prlimit/exec, IPC, full source
decoding, processing, capped encoding, complete derivative verification and reap.
Both receive identical retained bytes and recipes: web **2048×2048/Q90**, PDF
**600×8192/Q85**. Small is 1920×863 lossy VP8; large is the retained CC0 Vilaplana
4000×3000 **lossless VP8L**, not another scene or an easier input.
Two reversed warm sessions each use 20 warmups + 24 measured samples/backend.
Eight fresh managed hosts/cell alternate order for cold results, with no OS-cache
flush. Every helper invocation is a fresh process, including warm observations.
p50 is median, p95 nearest rank, throughput inverse mean sequential wall time.
Builds/tests and timing batches did not overlap; there were no timing retries.

## WebP measurements

All times are milliseconds; rates are completed images/second, Rust / ImageSharp.
The unchanged gate requires **both p50 and p95 ≤110%**, throughput **≥90%**.

| Input/profile/phase | Rust p50 / p95 | ImageSharp p50 / p95 | Rust / IS images/s | Gate |
| --- | --- | --- | --- | --- |
| Small web warm | 74.509 / 81.752 | 68.930 / 87.367 | 13.269 / 13.943 | PASS |
| Small PDF warm | 61.904 / 66.110 | 54.872 / 77.841 | 15.960 / 17.415 | **FAIL p50** |
| 12 MP web warm | 323.936 / 332.477 | 458.603 / 487.917 | 3.085 / 2.169 | PASS |
| 12 MP PDF warm | 287.148 / 335.942 | 400.792 / 463.425 | 3.406 / 2.450 | PASS |
| Small web cold | 103.994 / 117.889 | 364.773 / 476.402 | 9.350 / 2.712 | PASS |
| Small PDF cold | 93.060 / 107.447 | 342.886 / 444.596 | 10.548 / 2.856 | PASS |
| 12 MP web cold | 380.983 / 434.083 | 874.211 / 961.429 | 2.618 / 1.143 | PASS |
| 12 MP PDF cold | 322.186 / 339.410 | 743.400 / 778.335 | 3.102 / 1.348 | PASS |

Small PDF warm p50 is **112.82%** of baseline, about **1.545 ms above its 110%
bound**. Each session independently misses: 61.832/54.313 ms (113.84%) and
62.051/55.435 ms (111.93%). Its throughput is 91.65%, so that gate passes.
No small-miss waiver is requested or implied: the separate reachable decoder
failure also prevents selection.

The preserved native-WebP warm results at the starting head were small web
48.943/53.743 ms, small PDF 35.691/37.106 ms, large web 255.962/275.793 ms,
large PDF 220.674/238.783 ms. Corresponding native-path rates were 20.260,
27.892, 3.879 and 4.519 images/s. The fallback increases those p50 values by
52.2%, 73.4%, 26.6% and 30.1%. These are historical context from
[LINUX-QUALIFICATION.md](LINUX-QUALIFICATION.md), not another native execution.

The short JPEG smoke retains the prior boundary: web 33.251/35.133 ms versus
30.193/46.796 ms, rates 29.888/31.407; p50 is 110.13%, still above the gate.
PDF is 20.805/25.654 ms versus 43.970/56.487 ms, rates 46.593/25.246.
This one-session smoke establishes no new JPEG qualification or performance waiver.

## Fidelity and resources

Every representative WebP derivative decodes to **exactly the same pixels** as
the preserved native-path output, with identical geometry, JPEG sampling and
quantization. Generated ICC timestamps may differ. Independent Pillow/LittleCMS
display comparisons against fresh ImageSharp yield **43.122–46.686 dB PSNR**.
This is focused difference evidence, not broad perceptual/camera acceptance.
All outputs have canonical ICC and no EXIF/XMP. No work or fidelity was omitted
to obtain the timing results.

| Profile/input | Bytes Rust / IS | Child CPU p50 ms | Maximum sampled child HWM MiB |
| --- | --- | --- | --- |
| Small web | 501268 / 498676 | 70.901 | 17.563 |
| Small PDF | 43327 / 42685 | 59.561 | 11.816 |
| 12 MP web | 795809 / 790077 | 319.016 | 95.105 |
| 12 MP PDF | 66468 / 66593 | 274.417 | 95.133 |

The unchanged direct-child collector separately used three warmups + six samples.
These CPU/HWM observations do not replace paired end-to-end timing. Shared managed
host CPU/RSS snapshots for both backends remain in the raw paired observations;
they are not independently attributable backend peaks. The finite paired cgroup
had memory.max 3 GiB, swap.max 0, pids.max 192 and CPU quota 400%; actual kernel
memory.peak was **436617216 bytes**, with no max/OOM events. This is aggregate
memory charge, not RSS. systemd-run's misleading post-exit printed peak is unused.

## Safety and actual license closure

**Safety NO-GO:** upstream [issue 118](https://github.com/image-rs/image-webp/issues/118)
reports a malformed lossless Huffman-tree panic fixed after the pinned release.
The published 235-byte input has inconsistent outer lengths and is correctly
refused by Wayfarer. Correcting only the RIFF and VP8L lengths plus required pad
produces a **236-byte**, container-consistent control that reaches
`image-webp 0.2.4/src/huffman.rs:117:36` and **SIGABRTs in Release**:
`index out of bounds: the len is 0 but the index is 131090`.
No pixel payload or dependency source was changed. This is a reproduced decoder
availability defect, not demonstrated memory corruption or process escape.
The actual C# supervisor maps exit 134 to `Unavailable`, returns no bytes and
reaps the child; no destination file is created. Containment does not close the
required decoder-safety gate. Both original controls and the exact-extent variant
are retained with hashes and reproduction commands.

The exact crate forbids unsafe Rust. That alone is insufficient: its documented
memory limit explicitly leaves some allocations uncovered. The published lossy
partition-claim control from [issue 194](https://github.com/image-rs/image-webp/issues/194)
is refused by existing extent authority; no blanket scratch-allocation guarantee
is inferred. Unchanged AS 1 GiB, CPU 20/25s, NOFILE 128, CORE 0, parent-death,
deadline, pipe caps and fail-closed supervision remain mandatory.

The live RustSec snapshot `edb58f5974b83eb8302332821cb6d389777aa351` has no
`image-webp` advisory. All five matching advisories for resolved dependency names
are outside these pinned versions' affected ranges. This does not erase the
reproduced failure. Later animation fixes concern refused ANMF routes; the
post-pin 16384-axis correction is outside the 8192-axis contract.

**Measured fallback license closure verified:** all **50** external resolved
package archives match Cargo.lock checksums; **3130** packaged files match the
actual build sources. Exact MIT notices exist for `image-webp 0.2.4`,
`byteorder-lite 0.1.0` and `quick-error 2.0.1`. The WebP MIT notice matches its
declared upstream commit. TurboJPEG remains **1.5.1 / turbojpeg-sys 1.2.0**, with
vendored libjpeg-turbo **3.1.0**. The exact sys-wrapper source's authoritative
MIT text matches the retained notice; BSD/IJG/SIMD-zlib terms and the IJG
acknowledgment are retained alongside all other packaged licenses, including
Unicode obligations. No invented attribution, copyleft, key or paid runtime
requirement was found in this artifact's dependency closure. Full notices and
per-package identities are retained, not just manifest license labels.

The active candidate dependency graph and linked ELF contain **no native `webp`
or `libwebp-sys`**, while `tj3Compress8` and the Rust WebP raster decoder are
verified linked. Only the vendored JPEG native archives are built. Dynamic system
libraries remain listed in the ELF receipt; this is not a fully static executable.
Because this candidate fails, the selected research defaults/optional dependencies
and lockfile are preserved, including their already documented native blockers.
No release bundle or broader redistribution approval is claimed.

## Tests, preservation and stopping point

Locked Release, fmt and default-feature all-target Release Clippy with warnings
denied pass. **Five applicable Rust cases** pass, including the existing WebP
alpha/admission case enabled for the fallback; the sixth historical case requires
the intentionally absent `codec-bench` feature. **74/74 focused xUnit cases** pass,
covering malformed completion, source/output limits, ICC/orientation/alpha,
crash/pipe failures, AS/CPU, parent loss, cancellation, four-slot admission and reap.
The .NET build has zero errors and 70 existing dependency/analyzer warnings.

Sixteen supplemental WebP controls retain **15 expected contracts and one decoder
counterexample**: alpha, ICC plus EXIF rotation/privacy, truncated/animated input,
VP8L-bearing animation declaration, byte/output/axis/12M-pixel ceilings and upstream
malformed controls. Alpha is exact, visible RGB is exact on the alpha control,
and combined ICC/EXIF output exactly matches the rotated ICC control. The retained
four-child cancellation observation uses its existing alpha-PNG workload: four
children observed and reaped, no OOM, next transform succeeds. This is not a
four-WebP aggregate-RSS qualification. A supplemental Pillow JPEG-property error
was corrected once; its infrastructure traceback is retained separately.

The [failed native-binding report](WEBP-CORRECTION.md) and all five associated
receipts are byte-for-byte copies verified against local commit
`5f9c56a8eac36a63ef33c1b910a45473043ee91e` and its preserved worktree.
No failed native implementation was cherry-picked or enabled. Previous worktrees,
history, reports, fixture bytes and performance evidence remain preserved.

Reproduction uses the exact candidate manifest/test patch, copied existing source,
retained fixture bytes, `env.sh`, `run-paired.py`, `check-webp-controls.py` and the
unchanged collectors. The [method receipt](results/webp-fallback-20261015/method.json)
records commands, finite scopes, hashes, deviations and the local raw-evidence root.
Compressed observations retain warmups and every measured sample. Large binaries
and derivative images stay in the isolated local workspace.

Code Guard passes the experiment/documentation delta. All **15** complete-branch
REVIEW findings retain identical measurements and source hashes; the parser,
finite-collector and historical-analysis justifications are recorded. No policies,
exclusions or baselines changed. Native ARM64 and Windows remain **UNPROVEN**.
No main/production changes, PR, merge, release, issue update/closure, additional
codec or optimization cycle follows. Publish evidence only to the research branch
after checking its exact remote head, then **STOP**.
