# #858 Linux qualification follow-up — 2026-10-15

**BLOCKED; not READY FOR INTEGRATION.** The default-only AMD64 artifact is verified,
with one original performance-gate deviation retained. ARM64 execution is **UNPROVEN**,
as the maintainer confirmed no existing approved host is available. Native WebP
security hardening and the binding's redistribution notice remain **BLOCKED**.

Authority: [three finite approved tasks](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6189016280).
Started at `17a568c0af200588294e6725cf48a6b24fae8a38` in the fresh isolated worktree
`/home/stef/wayfarer-research/858-linux-qualification-20261015/source`.
Runtime source commit: `ed6be50595f06d44e0d85ff00738eaed656d23ce`.
The only runtime edits admit Linux AArch64 alongside AMD64 in the Rust/C# guards.
All containment controls, codec pins, Cargo.lock, fixture bytes, previous reports
and raw historical results remain unchanged. Native Windows is deferred,
unqualified and not formally waived. No production integration follows.

## Default-only artifact and method

Fresh `target-default-only`; locked Release, Rust 1.90.0, GCC 13.3.0, CMake 3.28.3,
NASM 2.16.01, .NET SDK 10.0.112/runtime 10.0.12; Ubuntu 24.04/WSL2 AMD64,
UID 1000, i7-10700K. No host CPU flags or native-library override.
Compiler artifact features are exactly `default`, `jpeg-candidate-decode`,
`native-jpeg-encode`, `webp-candidate-decode`: **no codec-bench or diagnostic feature**.
The executable SHA-256 before and after tests, measurements and container execution is:
`9ae71918e83017fc477332b8b346628436472b3e05c23fe09ce45b8c3efa7673`.
All-feature Rust checks use a separate `target-checks` and never replace that binary.

The unchanged paired C# collector compares ImageSharp 3.1.12
`OptimizeImage + ValidateRaster` with complete supervisor admission, dedicated
launch thread, prlimit/exec, IPC, normalization, encoding, output verification and reap.
Two reversed warm sessions use 20 warmups + 24 measured samples/backend each;
eight cold managed hosts/cell alternate order, without OS cache flushes.
Median p50, nearest-rank p95, inverse-mean sequential throughput. Builds, tests
and the two measurement batches did not overlap. No timing assertions or retries.

R = retained 1920×863 JPEG/WebP photograph. V = the already retained CC0
Vilaplana 4000×3000 photograph in JPEG/lossless WebP, two encodings of one scene.
Input hashes/attribution remain in the [previous qualification](QUALIFICATION.md).
Web = 2048×2048/Q90; PDF = 600×8192/Q85. V outputs are 2048×1536 and 600×450.

### Warm paired results

| Profile/input | Rust p50/p95 ms | ImageSharp p50/p95 ms | Rust / IS images/s | Throughput ratio | Original gate |
| --- | --- | --- | --- | --- | --- |
| web/R-JPEG | 33.472 / 37.328 | 30.181 / 47.138 | 29.664 / 31.204 | 95.1% | FAIL p50 |
| web/R-WebP | 48.943 / 53.743 | 68.961 / 85.551 | 20.260 / 14.056 | 144.1% | PASS |
| web/V-JPEG | 139.190 / 166.813 | 221.517 / 270.008 | 6.963 / 4.397 | 158.3% | PASS |
| web/V-WebP | 255.962 / 275.793 | 457.027 / 502.628 | 3.879 / 2.152 | 180.2% | PASS |
| pdf/R-JPEG | 20.999 / 25.602 | 29.472 / 61.173 | 46.611 / 28.724 | 162.3% | PASS |
| pdf/R-WebP | 35.691 / 37.106 | 54.086 / 70.478 | 27.892 / 17.835 | 156.4% | PASS |
| pdf/V-JPEG | 100.496 / 107.852 | 143.835 / 176.159 | 9.851 / 6.748 | 146.0% | PASS |
| pdf/V-WebP | 220.674 / 238.783 | 397.167 / 448.880 | 4.519 / 2.475 | 182.6% | PASS |

### Cold paired results

| Profile/input | Rust p50/p95 ms | ImageSharp p50/p95 ms | Rust / IS images/s | Throughput ratio | Original gate |
| --- | --- | --- | --- | --- | --- |
| web/R-JPEG | 69.218 / 78.760 | 166.598 / 197.885 | 14.593 / 5.968 | 244.5% | PASS |
| web/R-WebP | 75.450 / 91.693 | 359.481 / 446.627 | 12.879 / 2.740 | 470.1% | PASS |
| web/V-JPEG | 173.468 / 204.911 | 1067.525 / 1176.810 | 5.704 / 0.946 | 603.1% | PASS |
| web/V-WebP | 297.356 / 310.905 | 894.791 / 935.126 | 3.344 / 1.123 | 297.8% | PASS |
| pdf/R-JPEG | 50.540 / 61.173 | 165.686 / 239.021 | 19.806 / 5.820 | 340.3% | PASS |
| pdf/R-WebP | 68.405 / 78.556 | 327.754 / 349.014 | 14.534 / 3.058 | 475.3% | PASS |
| pdf/V-JPEG | 129.425 / 149.278 | 757.114 / 797.192 | 7.540 / 1.323 | 569.9% | PASS |
| pdf/V-WebP | 262.719 / 295.099 | 726.456 / 863.797 | 3.751 / 1.343 | 279.4% | PASS |

The original p50/p95 ≤110%, throughput ≥90% gate passes **15/16 pooled cells**.
R-JPEG/web warm p50 exceeds its bound by **0.272 ms**
(110.90% of ImageSharp). The two warm sessions are
33.570/30.141 ms (111.38%) and 33.343/30.297 ms (110.06%); both miss.
All observed warm p95/throughput and cold gates pass, including both large inputs.
The original archived JPEG misses and maintainer's conditional exception remain
historical authorities; this report does not grant a new blanket performance waiver.
No additional materially slower large-image profile was observed.

## Fidelity and RSS

All eight representative Rust outputs decode to exactly the same pixels as the
previously measured default+codec-bench artifact. Their generated 612-byte sRGB ICC
profiles match except for creation-time bytes 24–35; file hashes consequently vary.
All matched output geometries, 4:2:0 sampling and quantization tables agree.
Independent retained Pillow/LittleCMS inspection gives matched-ImageSharp display
PSNR **38.817–46.740 dB**. Rust outputs have no EXIF entries. This is a difference
proxy and focused pixel comparison, not new broad visual/camera acceptance.

The existing direct-child collector separately observes three warmups + six samples
per cell. The child HWM is sampled from its /proc status while alive. The managed
host RSS comes from paired collector snapshots and includes both backends, xUnit
and loaded application code; it cannot be attributed to one backend or summed
with independent child medians. No four-child aggregate RSS claim is made.

| Profile/input | Bytes Rust / IS | Child CPU p50 ms | Child sampled HWM MiB | Shared host peak RSS MiB |
| --- | --- | --- | --- | --- |
| web/R-JPEG | 498699 / 498696 | 31.246 | 16.859 | 214.223 |
| web/R-WebP | 501268 / 498676 | 48.255 | 17.285 | 230.906 |
| web/V-JPEG | 795809 / 901972 | 168.193 | 70.719 | 284.781 |
| web/V-WebP | 795809 / 790077 | 300.670 | 94.590 | 426.691 |
| pdf/R-JPEG | 43354 / 43327 | 24.260 | 14.160 | 195.918 |
| pdf/R-WebP | 43327 / 42685 | 44.683 | 11.797 | 220.129 |
| pdf/V-JPEG | 66468 / 105154 | 129.246 | 65.832 | 245.582 |
| pdf/V-WebP | 66468 / 66593 | 263.205 | 94.586 | 393.039 |

The paired batch cgroup retained memory.max 3 GiB, swap.max 0, pids.max 192,
CPU quota 400%; its actual kernel memory.peak was 451215360 bytes (430.312 MiB),
with zero OOM/max events. That is aggregate memory charge, not RSS. systemd-run's
incorrect post-exit printed memory peak is not used. Prior concurrency results remain
in QUALIFICATION.md and were not relabeled as this default-only artifact's evidence.

## Linux platform and packaging disposition

**AMD64 execution verified.** 74/74 focused xUnit cases passed on the exact artifact:
JPEG/WebP end-to-end, allowed/rejected formats, dimensions and byte caps, color and
orientation, malformed completion, AS/CPU controls, parent loss, cancellation,
pipe overflow, nonqueueing admission and reap. The six Rust cases, locked Release,
fmt and all-target/all-feature Release Clippy with warnings denied also passed.
The .NET build had zero errors and 70 existing dependency/analyzer warnings;
no comparator or unrelated application dependency was upgraded.

**ARM64 UNPROVEN.** The Linux guards now allow `aarch64` / `Architecture.Arm64`.
The same libc-defined RLIMIT_AS 1 GiB, CPU 20/25s, NOFILE 128, CORE 0,
non-root check, parent-PID race checks, PDEATHSIG, deadline and cleanup remain.
No native build, execution, resource, cleanup or performance evidence exists for
ARM64. There are no configured repository runners or supplied approved ARM64 host.
Existing hosted ARM64 jobs require a PR or publish release assets. No new workflow,
PR, emulated substitute or host infrastructure was introduced. The maintainer
explicitly selected “No existing host; record UNPROVEN.”

**AMD64 container bundling smoke passed.** The exact executable and standalone C#
supervisor were mounted read-only into the existing application runtime image
`ghcr.io/stef-k/wayfarer@sha256:796ec45f9f81af54282123f1bd4c9d55ea6a45cf4f7a61d9ca95e19c66a47b1e`.
Both retained JPEG and WebP inputs produced successful verified JPEG output as
UID/GID 1654, read-only root, all capabilities dropped, no-new-privileges, no network,
finite 1 GiB/96-PID/2-CPU container, and a bounded writable /tmp. Existing prlimit
2.39.3 and glibc 2.39 suffice. This demonstrates mount/binary compatibility and
non-root execution, not an integrated image or release bundle. Native Linux
execution is also covered by the non-root host tests. ARM64 packaging remains unproven.

**Native security/redistribution BLOCKED.** See [the exact-source review](notices/NATIVE-CODECS.md).
The CVE-2023-4863 backport is verified; later decoder hardening is absent.
`webp`'s newest compatible native binding is still 0.9.6, so no small supported
pin replacement closes that gap. Its wrapper MIT notice is also absent from the
published crate and exact source tree. No fork, patch series or invented notice.

## Reproduction and retained evidence

In fresh WORK/source, install the documented Rust/native/.NET tools, copy the
retained fixtures into WORK/evidence/fixtures, and set WAYFARER_IMAGE_RESEARCH,
WAYFARER_IMAGE_FIXTURES and WAYFARER_IMAGE_HELPER as in the earlier collector.
Build with the exact commands below; the two target directories must stay separate:

```sh
cargo build --manifest-path source/tools/image-helper/Cargo.toml --locked --release \
  --target-dir "$WORK/target-default-only" --message-format=json
cargo test --manifest-path source/tools/image-helper/Cargo.toml --locked --release \
  --features codec-bench --target-dir "$WORK/target-checks"
dotnet build source/tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release
```

Reuse `results/readiness-20261015/qualify-paired.py` under the recorded finite user
cgroup. Direct RSS uses the existing `benchmarks/diagnose.py --modes recovered
--candidate "$WAYFARER_IMAGE_HELPER" --count 6 --warmups 3` with the four named
inputs and the same web/PDF recipes. Commands, build identity/features, both warm
sessions, all cold raw samples, resources, fidelity and native source/library
hashes are in [results/linux-qualification-20261015](results/linux-qualification-20261015/).
Compressed JSON receipts retain raw warmups as well as measured samples; no
large generated binaries, duplicate derivative images or unrelated logs are published.
Code Guard covers this delta and the complete archived branch against origin/main;
its REVIEW dispositions are retained with the evidence. No policy changed.

Publish one verified fast-forward only to `research/858-rust-imaging` after checking
that its remote head is still the supplied starting SHA. Preserve the prior worktrees,
archive and results. Stop with **BLOCKED / ARM64 UNPROVEN**, without production/main
edits, native Windows support, PR, merge, release or issue closure.
