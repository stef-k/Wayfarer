# #858 isolated prototype result

**NO-GO for this implementation against the accepted performance gate.**
The functioning Rust executable and standalone C# invocation boundary are retained.
No production proxy/cache/HTTP integration, deployment, push, PR, merge, release
or issue closure was performed.

Baseline: Wayfarer main `9d8ee6b4132dfef53beb167b86b282be31c88973`.
Functional/performance implementation: `388ed351` on
`wip/858-rust-imaging-prototype-20261012`. The following checkpoint adds only
documentation; the measured implementation remains unchanged.
Workspace: `/home/stef/wayfarer-research/858-rust-prototype-20261012-oopKvIMF`.
Its `evidence/` directory and toolchain/package caches remain available.

## Working behavior and validation

- Actual JPEG/PNG/WebP/static GIF inputs produce verified JPEG/PNG output through Rust and C#.
- Bounding-box resizing never enlarges; fractional axes truncate as in the existing proxy.
- All eight JPEG/PNG orientations and post-IDAT PNG eXIf are exercised.
- Real sRGB, Display P3, gray and CMYK ICC samples match independent LittleCMS controls
  within the checked color tolerance. Untagged CMYK/YCCK complete; tagged YCCK is untested.
- PNG gamma, SDR cICP and 16-bit samples are exercised; HDR cICP rejects.
- Transparent input stays transparent; explicit JPEG rejects transparency.
  Fresh output retains generated sRGB ICC, with source EXIF/XMP discarded.
- CRC-valid absent/truncated/wrong Adler trailers, unterminated Deflate and E-1/E+1
  raster counts reject. Valid split/empty IDATs, verified-stream suffixes, packed-bit
  Adam7 empty passes and 16-bit controls pass.
- Retained malformed PNG/GIF/WebP and animation controls reject. A full-raster GIF
  without an LZW end code rejects.
- **67 focused xUnit cases passed**, including source/output classification, crash,
  partial output, both pipe overflows, timeout, cancellation, repeated reaping,
  effective address-space/CPU limits and four-slot non-queuing admission.
- Locked release Cargo build, formatting, Clippy with warnings denied, C# build
  and complete-scope Code Guard ran. Existing NuGet ImageSharp advisory warnings
  remain in logs; the required 3.1.12 comparison dependency was unchanged.

Evidence: `evidence/test-results/prototype-final.trx`,
`prototype-tests-final.log`, `rust-build-final.log`, `rust-clippy-final.log`,
`rust-four-formats/`, `color-results/` and `performance/`.
The original retained fixtures were copied with hashes and never edited.

## Containment and observations

The helper verifies non-root Linux x64, address space 1 GiB, CPU 20/25 seconds,
descriptors 128 and zero core dumps before stdin. It installs race-checked
PDEATHSIG(SIGKILL). C# holds the launching thread alive through reap.

CPU exhaustion produced SIGXCPU/exit 152 before the 30-second wall deadline.
A 2 GiB allocation attempt failed under the 1 GiB address-space limit.
Killing the C# caller during real Rust decoding produced observed signal 9;
an independent subreaper adopted/reaped that child. Normal ownership checks
found no remaining child/zombie after completion.

A 4000x3000 image completed with sampled child RSS **237704 KiB (232.1 MiB)**,
sampled supervisor RSS 50000 KiB and maximum observed combined RSS 275184 KiB.
Sampled child CPU was approximately 0.61 seconds. These are observations,
not hard RSS ceilings or four-image aggregate qualification.
See `near-limit-resources.json`, `near-limit-kernel-limits.txt` and `parent-loss.json`.

## Initial performance decision

Matched sources are 1920x863, with a 600x8192 box, JPEG quality 85, auto output
and identical 600x269 output geometry. ImageSharp uses unchanged
`ImageProxyHelper.OptimizeImage` followed by full `ValidateRaster`.
Rust timing includes launch, all IPC, its derivative verification and reap.
There are eight cold pairs per format in fresh test hosts and twenty warm pairs
after three warmups. Cold does not mean a flushed OS page cache.
Throughput below is the sequential measured transform rate, not a trip/burst result.

| Input | Phase | ImageSharp p50/p95 ms | Rust p50/p95 ms | Rust/baseline throughput |
| --- | --- | --- | --- | --- |
| JPEG | Cold | 239.20 / 247.46 | 148.21 / 156.17 | 163.6% |
| JPEG | Warm | 27.01 / 44.48 | 114.66 / 122.80 | 26.2% |
| PNG | Cold | 387.01 / 411.83 | 162.92 / 172.14 | 242.1% |
| PNG | Warm | 154.38 / 188.95 | 126.91 / 138.23 | 123.0% |
| WebP | Cold | 414.82 / 573.53 | 189.38 / 212.98 | 225.7% |
| WebP | Warm | 55.36 / 159.65 | 148.41 / 152.41 | 46.1% |
| GIF | Cold | 377.38 / 414.28 | 165.05 / 176.19 | 234.0% |
| GIF | Warm | 89.84 / 140.96 | 123.03 / 127.76 | 81.8% |

Warm JPEG p50/p95 are **4.25x/2.76x** baseline. Warm WebP/GIF also fail p50 and
throughput. The accepted gate requires each p50/p95 <=110% and throughput >=90%.
Cold wins do not offset those failures. No 10/20-image trip extension was run.

Paired images have matching format/geometry and were inspected visually.
Pairwise PSNR is 32.7/39.2/33.1/35.5 dB for JPEG/PNG/WebP/GIF; this measures
differences, not independent perceptual-quality acceptance. Rust output sizes are
56371/352754/55437/347706 bytes versus 43327/301570/42685/108691 bytes.
Broader visual parity remains unqualified. Raw timings, retained paired outputs,
byte counts and hashes are in `performance-summary.json` and `performance/`.

## Scope, limitations and next decision

Changed code is limited to `tools/image-helper/`, four focused xUnit source files
and their project reference. Production processing, dependency versions, HTTP,
cache, release/build packaging and existing worktrees are untouched.

The source uses maintained decoders/CMS; no custom codec or general parser was
introduced. GIF authority uses streaming events plus the configured public
frame decoder, because its streaming constructor cannot take public options.
PNG gAMA values outside the supported fixed-point curve range reject.
High-precision lossless JPEG is explicitly rejected; malformed JPEG ICC assemblies
become untagged according to the accepted policy.

Remaining gaps include tagged YCCK and broader color/alpha/corpus fidelity,
maximum-depth/full-size input combinations, deliberate hard-CPU-limit exhaustion,
four real simultaneous image resources, platform/native packaging and later
HTTP/cache/buffer lifetime qualification. They cannot overturn this NO-GO.

Code Guard returns REVIEW, with no FAIL/INCOMPLETE. The accepted findings concern
the cohesive PNG color-precedence function (90 lines), GIF admission event loop
(108 lines, complexity 24, depth 5), and PNG/WebP admission complexity 18/16.
Keeping each complete authority and its invariants together makes rejection and
ownership auditable. No thresholds, exclusions or baselines were changed.

The smallest next step is maintainer assessment of the retained working code and
performance counterexample. Any further optimization experiment requires a new
bounded instruction. No daemon, process pool or production migration was started.
