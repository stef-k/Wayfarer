# #858 Stage 1 readiness qualification — 2026-10-15

**BLOCKED.** Linux AMD64 measurements and finite supervisor loads passed the observed
gates. Windows containment is unimplemented; ARM64 execution is unproven. The measured
CLI also includes the existing `codec-bench` feature, so default-only artifact performance
is unproven. No integration or shipping approval follows.

Authority: [Stage 1 assignment](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6188087992),
[accepted #821 contract](https://github.com/stef-k/Wayfarer/issues/821#issuecomment-6088772411).
Fresh worktree: `/home/stef/wayfarer-research/858-readiness-20261015-KKforFH8/source`,
starting at `21bac96303ea2040dcf892da6cfee52b65bed73a`. All Rust runtime sources,
supervisor sources, Cargo pins/lock, selected optimizations and previous reports remain
byte-identical to that baseline. Only an opt-in observation case, fixtures and evidence
were added; no production source, dependency, workflow or cache/HTTP behavior changed.

## Exact artifacts and method

Ubuntu 24.04/WSL2, Linux AMD64, UID 1000; i7-10700K; Rust 1.90.0 Release, .NET
SDK 10.0.112/runtime 10.0.12, GCC 13.3.0, CMake 3.28.3, NASM 2.16.01. No host-specific
compiler flags. Builds, tests and measurement batches did not overlap. Native Windows
used its installed .NET 10 SDK and a non-elevated account; receipt records exact versions.

Initial default-only locked Release SHA-256:
`5934de5bfdb5e05a2c21caa57fdb0176a811f7208b6190f0e80586317672a864`.
Cargo tests then rebuilt the CLI with **default + codec-bench** before all observations.
Measured CLI SHA-256:
`7ae92f471217bcbe0f0bcf7ba24cfc467ec1c93d66e92fbb85a1f188748c301b`.
Diagnostic timing was disabled. This build-identity gap is retained; these numbers must
not be relabeled as default-only executable qualification. No replacement timing run was
started after the platform stop. [Build identities](results/readiness-20261015/build-identity.json).

The unchanged paired xUnit collector measures ImageSharp 3.1.12 `OptimizeImage` plus
`ValidateRaster` against complete C# supervisor launch/IPC/Rust normalization/output
verification/reap. Warm: two reversed sessions, 20 warmups + 24 measured samples/backend
each (48 pooled). Cold: eight fresh managed hosts/cell, alternating order; no OS cache
flush. p50 is median, p95 nearest rank; images/s is inverse mean sequential wall time.

R = retained 1920×863 photograph, JPEG/WebP hashes unchanged. V = Beireke1’s
[CC0 Vilaplana photograph](https://commons.wikimedia.org/wiki/File:Vilaplana.jpg),
4000×3000 (12M pixels), 4,941,658-byte JPEG, SHA-256
`7b2f7ca7bca961c099946255582d20d8c8b2db81da8a9401d386f70e6716a769`.
Its untimed Pillow 10.2.0/libwebp 1.3.2 lossless RGB WebP is 10,493,738 bytes, SHA-256
`fb6f75989678e4daf03c8a9cd14eb00f34a3c2ff02ef718c8b86e5ce3918ccf0`.
These are two representations of one new photograph, not two independent scenes.
Web = 2048×2048/Q90; PDF = 600×8192/Q85. V really downscales to 2048×1536 / 600×450.

## Matched source-processing results

Every measured cell passes the original p50/p95 ≤110%, throughput ≥90% gate.
This is local evidence for the artifact above. The original archive’s JPEG warm p50
33.044/29.971 ms web and 21.581/19.290 ms PDF failures remain valid retained observations;
the maintainer’s conditional exception is not a new universal speed limit.

| Warm profile/input | Rust p50/p95 ms | ImageSharp p50/p95 ms | Rust / IS images/s | Throughput ratio |
| --- | --- | --- | --- | --- |
| web/R-JPEG | 32.599 / 34.684 | 30.461 / 46.780 | 30.399 / 31.247 | 97.3% |
| web/R-WebP | 47.186 / 51.310 | 67.320 / 82.883 | 20.996 / 14.375 | 146.1% |
| web/V-JPEG | 138.318 / 146.753 | 219.834 / 253.440 | 7.169 / 4.473 | 160.3% |
| web/V-WebP | 266.593 / 349.479 | 536.545 / 671.505 | 3.487 / 1.825 | 191.1% |
| pdf/R-JPEG | 21.934 / 28.429 | 30.427 / 73.275 | 42.010 / 27.474 | 152.9% |
| pdf/R-WebP | 45.639 / 52.829 | 68.775 / 86.758 | 23.033 / 14.771 | 155.9% |
| pdf/V-JPEG | 117.057 / 160.282 | 177.166 / 206.453 | 8.061 / 5.852 | 137.8% |
| pdf/V-WebP | 218.025 / 228.800 | 392.980 / 420.108 | 4.553 / 2.527 | 180.2% |

| Cold profile/input | Rust p50/p95 ms | ImageSharp p50/p95 ms | Rust / IS images/s | Throughput ratio |
| --- | --- | --- | --- | --- |
| web/R-JPEG | 70.351 / 74.658 | 167.946 / 194.550 | 14.656 / 5.963 | 245.8% |
| web/R-WebP | 80.178 / 90.541 | 354.446 / 404.153 | 12.673 / 2.789 | 454.4% |
| web/V-JPEG | 184.010 / 192.661 | 1005.749 / 1041.126 | 5.458 / 1.008 | 541.7% |
| web/V-WebP | 311.608 / 338.915 | 892.540 / 979.528 | 3.215 / 1.110 | 289.6% |
| pdf/R-JPEG | 48.882 / 69.272 | 161.023 / 183.950 | 19.175 / 6.211 | 308.7% |
| pdf/R-WebP | 65.351 / 77.671 | 335.600 / 381.314 | 14.891 / 2.987 | 498.6% |
| pdf/V-JPEG | 135.993 / 156.494 | 775.948 / 849.912 | 7.302 / 1.278 | 571.5% |
| pdf/V-WebP | 259.409 / 280.239 | 758.559 / 885.225 | 3.840 / 1.286 | 298.7% |

## Fidelity and single-image resources

Both backends used matching dimensions, quality and 4:2:0 quantization tables. Rust
outputs contain a generated 612-byte sRGB ICC profile. Retained orientation/P3/CMYK/gray,
16-bit, alpha/static GIF, malformed/animation, completion, byte-cap and termination
controls passed. Independent LittleCMS display PSNR versus matched ImageSharp ranges
38.817–46.740 dB; it is a difference proxy, not blind perceptual acceptance. Inspection of
the large web JPEG pair found no obvious gross artifact/color shift. No broad visual
or all-camera/ICC acceptance is claimed.

Separate direct-CLI resource probes used the existing collector, three warmups and six
samples/cell. They supplement full-supervisor timing; independent medians are not summed.
ImageSharp CPU below is from the paired host; its shared cumulative RSS cannot be
attributed to one backend. Full parent snapshots and all retained-input resources are in
[the summary](results/readiness-20261015/qualification-summary.json).

| Large input/profile | Output bytes Rust / IS | Rust child CPU p50 ms | IS parent CPU p50 ms | Rust child sampled HWM MiB |
| --- | --- | --- | --- | --- |
| JPEG/web | 795809 / 901972 | 133.404 | 224.827 | 70.777 |
| WebP/web | 795809 / 790077 | 252.905 | 612.919 | 94.559 |
| JPEG/pdf | 66468 / 105154 | 95.340 | 187.205 | 65.949 |
| WebP/pdf | 66468 / 66593 | 212.507 | 423.238 | 94.539 |

## Finite standalone concurrency and controls

Each case had its own cgroup: memory.max 3 GiB, swap.max 0, pids.max 192, CPU quota
400%. Existing per-child controls stayed AS 1 GiB, CPU 20/25s, nofile128, core0,
non-root and race-checked parent death. The four originals have distinct encoded hashes:
V JPEG, V lossless WebP, retained alpha-large PNG and retained photographic GIF. The two
V files depict the same scene. The 10/20 batches cycle these four inputs through an
explicit four-call feeder; the supervisor itself never queues admission.

RSS is sampled every ~2 ms across the actual managed hosts, launchers and children in
the case cgroup; shared mapped pages can be counted more than once. Kernel memory.peak
is the separately measured aggregate cgroup charge, not RSS. CPU is the kernel usage
delta during the batch, including reaped children. These observed peaks do not establish
a four-child production RSS limit. WSL systemd-run’s printed post-exit memory peak is
incorrect here; the report uses live kernel counters captured before process exit.

| Case | Success/reject/cancel | First success / total ms | Sampled aggregate RSS MiB | Kernel charge peak MiB | Aggregate CPU ms |
| --- | --- | --- | --- | --- | --- |
| batch-1 | 1/0/0 | 160.452 / 161.356 | 436.887 | 232.340 | 261.915 |
| batch-4 | 4/0/0 | 137.938 / 315.905 | 567.133 | 356.867 | 973.640 |
| batch-10 | 10/0/0 | 148.164 / 686.937 | 658.875 | 445.375 | 2465.408 |
| batch-20 | 20/0/0 | 126.522 / 1291.646 | 681.293 | 471.707 | 4853.381 |
| burst-20 | 4/16/0 | 130.807 / 309.992 | 559.707 | 349.836 | 928.676 |
| cancel-4 | 0/0/4 | — / 171.142 | 540.996 | 330.766 | 716.045 |

Burst first rejection: 5.265 ms; all sixteen rejected responses had no child PID.
At most four helpers were observed. Caller cancellation at 100 ms canceled all four
real transforms; every observed child was gone at completion and a subsequent transform
succeeded. All batches had zero failures, no OOM/limit events, and complete reaping.
Retained timeout/crash/pipe-overflow/AS/CPU/non-queuing fault checks also passed.
This does not exercise HTTP, cache, trip loading, stale refresh or production deployment.

## Platform stop and dependency findings

| Platform | Actual evidence | Disposition |
| --- | --- | --- |
| Linux AMD64 | Non-root actual helper + supervisor; Release builds, 74 safety cases, six Rust tests, paired matrix and six load cases | Scoped local PASS |
| Linux ARM64 | No native ARM64 host/registered emulator; no helper/supervisor execution. Both source guards reject ARM64 | UNPROVEN / unsupported source path |
| Windows x64 developer | Native non-elevated supervisor Release build passed, zero warnings/errors. Valid JPEG + existing trusted EXE returned Unavailable (status 3), null PID/bytes, exit 1, no output | BLOCKED before child launch; Rust Windows build/runtime not qualified |

Windows needs an OS launch/control path for memory/CPU limits and parent-loss cleanup;
the existing owner uses `/usr/bin/prlimit` and Linux PDEATHSIG. Rust `controls()` and C#
`TransformAsync` deliberately refuse other platforms. Removing guards would remove
enforced containment. Implementing and qualifying Windows control/launch ownership is
beyond a narrow target/packaging correction. **Stopped at this material portability gate**;
no new platform architecture, ARM guard change, native Windows codec build or extra
qualification round followed. The first Windows invocation hit PowerShell 5.1’s native
stderr/error mapping during .NET startup; one corrected invocation retained the real
native exit code. Both infrastructure witness and final receipt are archived.

Pinned libjpeg-turbo is the exact 3.1.0 tag `20ade4dea9589515a69793e447a6c6220b464535`
through turbojpeg-sys 1.2.0. Pinned libwebp-sys 0.9.6 vendors
`95ea5226c870449522240ccff26f0b006037c520` (1.3.1 plus fixes), descendant of the
CVE-2023-4863 fix `902bc9190331343b2017211debcec8d2ab87e17a`; its Huffman .c/.h bytes
match upstream 1.3.2. All four native/wrapper crate archive hashes match Cargo.lock.
[Upstream WebP NEWS](https://chromium.googlesource.com/webm/libwebp/+/refs/tags/v1.6.0/NEWS)
documents later security hardening; this old vendor snapshot has no complete current
security closure. [JPEG upstream changelog](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/ChangeLog.md)
also contains later fixes; no dependency update or claim of universal safety was made.

Archive notices retain libwebp BSD-3-Clause/COPYING, AUTHORS and WebM patent terms;
JPEG requires IJG acknowledgment and TurboJPEG BSD terms, with SIMD zlib notices.
Wrapper MIT/Unlicense and permissive transitive terms also remain applicable. Actual
distribution must carry required notices; archive files do not prove native/Compose/CRT
package closure. No LGPL choice is required for the selected permissive alternatives.
[Pinned provenance and scoped findings](results/readiness-20261015/security.json).

## Verification, reproduction and publication boundary

Locked Release build, `cargo fmt --check`, all-target/all-feature Clippy with warnings
denied, six Rust tests, and 74/74 retained xUnit safety cases passed (no skips/failures).
The six new opt-in load invocations passed; ordinary CI skips the observation-only facts
without their explicit environment. No timing assertions were added. Linux C# builds
had zero errors and existing dependency/analyzer warnings; Windows standalone build was clean.

Code Guard covers the qualification delta and complete archived branch against
`origin/main`; exact results are retained beside the evidence. The new 98-line load
collector REVIEW is accepted: it owns one finite observation with sampling cleanup and
one report, without introducing a benchmark framework or a production abstraction.
Existing parser/statistics REVIEWs remain bounded archival authorities; policies,
exclusions and baselines are unchanged. No FAIL/INCOMPLETE is accepted.

Reproduce in a fresh WORK/source checkout; copy archived fixtures into WORK/evidence/fixtures,
create WORK/evidence/performance, and set WAYFARER_IMAGE_RESEARCH=WORK,
WAYFARER_IMAGE_FIXTURES=WORK/evidence/fixtures and CARGO_TARGET_DIR=WORK/target.
With the documented Rust/native/.NET tools:

```sh
cargo build --manifest-path source/tools/image-helper/Cargo.toml --release --locked
cargo test --manifest-path source/tools/image-helper/Cargo.toml --release --locked --features codec-bench
export WAYFARER_IMAGE_HELPER="$CARGO_TARGET_DIR/release/wayfarer-image"
dotnet build source/tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release
```

Run the retained `qualify-paired.py` under the recorded finite user cgroup and
`qualify-loads.py` with explicit task-local NUGET_PACKAGES/DOTNET_CLI_HOME. Exact 80 paired
and six load invocations are in `commands.jsonl`. Resource commands reuse `benchmarks/diagnose.py`
with `--modes recovered --candidate "$WAYFARER_IMAGE_HELPER" --count 6 --warmups 3`, the four
named photographic inputs and web/PDF recipes above. The Windows script runs beside an
unchanged copy of `supervisor/` and `static.jpg` in a fresh native temporary directory.

Raw samples, summaries, commands, validation receipts, representative derivatives and
hashes are in [results/readiness-20261015](results/readiness-20261015/). The publication
fast-forwards only `research/858-rust-imaging` after rechecking its remote head. History,
the original worktree, archived counterexamples and selected optimizations are preserved.
No production integration, PR, merge, release, issue closure or additional research was performed.
