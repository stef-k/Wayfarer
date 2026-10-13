# Wayfarer #858: diagnosis of the preserved Rust prototype

**Recommendation: ARCHITECTURAL LIMITATION of the current pinned codec/resampler
composition against the frozen performance budget.** The implementation contains
substantial avoidable gamma and buffer work, but a small cleanup of that work
cannot meet the gates while its measured decoding, Lanczos3 and verified encoding
paths remain in place. The process boundary costs only a few milliseconds.
This is an economic assessment of this implementation, not proof that another
Rust implementation could never pass.

Prototype: `c998da079838894da32cb331f4f338cd9597047f`, implementation
`388ed351a2f441c184f80ee9dabb06f813223eb2`, baseline
`9d8ee6b4132dfef53beb167b86b282be31c88973`. Original workspace:
`/home/stef/wayfarer-research/858-rust-prototype-20261012-oopKvIMF`.
All investigation writes, builds and executions used the independent scratch
clone rooted at `/home/stef/wayfarer-research/858-rust-diagnosis-20261013-mx7bvah1`.

## 1. Benchmark validity

The retained comparison is meaningful as a **source-to-verified-derivative product
comparison**, and its JPEG/WebP failure reproduces. It is not an equal-work codec
microbenchmark or a completed quality/performance qualification.

- Both sides process the same bytes within each format: the four retained sources
  are 1920x863; both produce 600x269 using 600x8192 bounds, no enlargement and
  JPEG quality 85. The timed ImageSharp seam is `OptimizeImage` followed by
  `ValidateRaster`; Rust includes supervision, launch, IPC, transformation,
  derivative verification and reap. Source file reads, evidence writes, hashing
  and the benchmark's additional ImageSharp inspection are outside the timer.
  No network, cache hit, original-byte bypass or high-fidelity alias is compared.
- Rust 1.90.0 builds are locked Release, `opt-level=3`, `panic=abort`, with ordinary
  checksum-enabled dependencies. All 53 original library fingerprints have empty
  `rustflags`; scratch verbose compilation confirms optimization and no fuzzing
  configuration. There is no debug-build mistake. No custom CPU/LTO settings are
  present. C# uses Release, SDK 10.0.112/runtime 10.0.12 and ImageSharp 3.1.12.
- JPEG quality integers and quantization tables match, but **Rust emits 4:4:4;
  ImageSharp emits 4:2:0**. Rust's pinned encoder has no public subsampling setter;
  its comments mentioning 4:2:2 disagree with actual SOF factors. Retained JPEG
  outputs are 56,371 versus 43,327 bytes; WebP-to-JPEG outputs are 55,437 versus
  42,685 bytes. Equal quality integers do not establish equivalent fidelity.
- Rust explicitly converts ICC/PNG color declarations to sRGB, or assumes sRGB
  for untagged RGB, then resizes in linear light with premultiplied alpha. It
  strips source EXIF/XMP and embeds a fresh 612-byte sRGB profile. The current
  ImageSharp seam resizes without companding, retains source profiles/metadata
  where its encoders support them, and performs no explicit CMS normalization or
  orientation mutation. These source fixtures require no orientation change.
  Their product work differs under the accepted normalized-output contract.
- PNG policies differ: ImageSharp uses BestCompression and preserves RGB/indexed
  output choices; Rust uses Fast/Adaptive and always RGBA. Retained PNG sizes are
  352,754 versus 301,570 bytes; GIF-to-PNG sizes are 347,706 versus 108,691 bytes,
  with indexed ImageSharp output. The retained PNG latency win is accompanied by
  different compression/output work; it does not establish matched-size parity.
- Retained warm measurements have three warmups and 20 alternating pairs; cold
  has eight fresh test hosts per format, without flushing the OS page cache.
  Raw-data recalculation reproduces the published statistics. Baseline warm
  samples include JIT settling and substantial variance. The new JPEG/WebP
  diagnostic batches use 20 warmups and 20 alternating pairs. Direct CLI batches
  use three warmups and 12 pairs. These are diagnosis workloads, not the full
  repeated-session qualification protocol. No tight PASS is claimed.

The current host is Ubuntu 24.04/WSL2, kernel 6.18.54.1, Intel i7-10700K,
8 cores/16 logical CPUs with AVX2; affinity permits all 16. The retained archive
does not freeze a complete historical CPU/quota/environment record. Current
environment, versions and processes are retained in `evidence/environment.json`.

| Warm launch-inclusive result | ImageSharp p50 / p95 ms | Rust p50 / p95 ms | Rust throughput / baseline |
| --- | ---: | ---: | ---: |
| Retained JPEG, 3 warmups | 27.01 / 44.48 | 114.66 / 122.80 | 26.2% |
| Current JPEG, 20 warmups | 18.99 / 36.08 | 117.15 / 129.09 | 18.4% |
| Retained WebP, 3 warmups | 55.36 / 159.65 | 148.41 / 152.41 | 46.1% |
| Current WebP, 20 warmups | 54.35 / 68.28 | 144.99 / 146.75 | 38.8% |

Current JPEG ranges are 18.03–38.76 ms baseline and 109.15–129.69 ms Rust;
WebP ranges are 53.48–77.55 and 143.35–149.46 ms. These large failures survive
the observed variance. Throughput is inverse mean sequential transform time,
not concurrent capacity. Retained process-cold results favor Rust: JPEG
148.21/156.17 versus 239.20/247.46 ms, WebP 189.38/212.98 versus 414.82/573.53 ms.
First-use managed setup/JIT contributes to those results; cold wins do not offset
warm failures. New cold qualification was unnecessary for this diagnosis.

A **supplemental reference**, enabling ImageSharp linear-light Lanczos3 and
4:4:4 JPEG, takes 31.85 ms JPEG and 69.51 ms WebP p50, versus Rust 110.41 and
151.82 ms in those batches. This accounts for some additional work but leaves
a large implementation gap. It does not add Rust's complete normalized contract
and **cannot replace the frozen baseline**. An independent LittleCMS display
comparison gives PSNR 32.67/33.14 dB against the original JPEG/WebP outputs,
and 40.22/42.05 dB against the supplemental reference. Full JPEG outputs were
inspected visually. These differences and observations are not blind perceptual
acceptance, nor qualification of the broader ICC/CMYK/16-bit/alpha corpus.

## 2. Measured bottlenecks

Minimal `Instant`/`Stopwatch` probes retain the original processing and limits.
Stages below are **individual medians**, so they do not sum to the median total.
JPEG/WebP use 20 supervised samples; PNG/GIF use 12 direct contained CLI samples.
Nested codec/CMS probes are broken out below, not added a second time.

| Stage, ms | JPEG | WebP | PNG | GIF |
| --- | ---: | ---: | ---: | ---: |
| Source admission, metadata and complete decoding | 12.0 | 52.7 | 25.8 | 31.0 |
| ICC/color conversion and float-raster preparation | 14.9 | 10.1 | 16.1 | 12.4 |
| Orientation and source release | 0.2 | 0.2 | 0.4 | 0.3 |
| Transparency scan/output selection | 1.8 | 1.7 | 2.2 | 2.8 |
| Linear-light conversion/premultiplication | 43.3 | 40.8 | 45.9 | 44.4 |
| Lanczos3 resizing | 23.5 | 22.1 | 26.9 | 24.6 |
| Unpremultiplication/inverse gamma/quantization | 7.5 | 7.2 | 8.0 | 7.8 |
| Fresh encoding, profile generation and buffering | 4.5 | 4.4 | 1.5 | 1.4 |
| Complete output decoding/verification | 1.9 | 2.1 | 2.3 | 2.2 |
| Complete transform wall time, including boundary | 117.1 | 145.0 | 134.5 | 139.0 |

**Launch and IPC:** direct original/instrumented JPEG p50 is 109.65/109.19 ms;
WebP is 147.50/149.74 ms. All four formats produce identical decoded-pixel hashes
between the original and instrumented binaries. A supervised sample's time
outside Rust `run()` is about 2.6 ms, measured per sample. `Process.Start` is
0.38 ms, cleanup/pipe completion/reap 0.03 ms, stdout final copy 0.007 ms.
C# stdin transfer spans 1.9–2.0 ms, overlapping Rust reads of 0.8–1.0 ms and
scheduling; pipe spans overlap processing and must not be summed as overhead.
Rust stdout takes about 0.01 ms for JPEG. A `prlimit`/`cat` control takes
4.14/4.51 ms for JPEG/WebP and 12.18 ms for PNG p50; it echoes the much larger
source bytes and performs no validation, so it is only a startup/I/O control.

**Decoding and validation:** JPEG header identification is 0.012 ms, source
pixels 11.88 ms, ICC parsing 0.017 ms. `read_info` does not duplicate a complete
JPEG decode. WebP admission, including an encoded payload copy/header probe,
is 0.19 ms; its actual pixel decoder is 51.15 ms. PNG admission inflation/CRC is
9.61 ms and raster decoding/finish 13.69 ms. GIF admission/LZW is 10.61 ms,
configured LZW decoding 10.81 ms, and palette rendering/final drain 8.34 ms.

**Color and memory:** JPEG CMS creation/application is 0.53/2.67 ms; allocating
the 25.3 MiB RGBA32F raster is 6.56 ms. The rest includes conversion, copies and
filling. Removing only JPEG ICC APP2 segments preserves identical decoded source
pixels and reduces preparation from roughly 15.0 to 10.3 ms in the direct
controls. It leaves the 41 ms forward-gamma and 23 ms resize work. ICC removal
is not a production optimization proposal. An alpha PNG control retains the
same dominant gamma/resize pattern, with 119.90 ms direct p50.

**Encoding and verification:** canonical ICC generation is about 0.01 ms;
JPEG encoding is approximately 4.5 ms versus ImageSharp's 1.0–1.2 ms default
encoding. Output JPEG decoding costs about 1.9–2.1 ms; output PNG has roughly
0.9–1.0 ms admission plus 1.2–1.3 ms raster decoding. Rust performs one complete
source JPEG decode and one required derivative decode. C# only checks canonical
boundaries afterward. The benchmark's additional decode is outside timing.

## 3. Causes supported by the evidence

1. **Repeated generic `powf` in Wayfarer color code dominates JPEG.** Forward
   gamma can evaluate up to 4.97 million powers at source size; inverse gamma
   up to 0.484 million at output size. Linear-light behavior is required, but
   recomputing the same 8-bit transfer values per pixel is avoidable.
   Sampling the unchanged preserved executable attributes 36.73% of workload
   user cycles to `libm`, whose hot addresses are in the selected `powf`
   implementation, and 19.34% to `imageops::resize`. The installed `perf` lacks
   libelf/DWARF unwinding; existing ELF symbols and `addr2line` map file offsets,
   but no complete caller tree is claimed. Stopwatches are primary evidence.
2. **The selected `imageops` float resampler is expensive.** Its generic
   separable passes take 22–27 ms and allocate an intermediate RGBA32F image.
   At this geometry, input/intermediate/output float rasters are approximately
   25.3/7.9/2.5 MiB. ImageSharp's default resize is about 7.1–7.2 ms; enabling
   its companding raises the cost, but does not reproduce Rust's combined
   preparation/gamma/resize expense. SIMD JPEG IDCT/color conversion and AVX2
   CMS execution are present in the original profile; disabling crate defaults
   did not disable all optimized execution. No evidence supports a missing
   release setting as the main explanation.
3. **Codec costs matter, particularly WebP's remaining budget.** Rust source
   JPEG pixels cost about 11.9 ms versus ImageSharp 9.3 ms; WebP about 51.2
   versus 44.9 ms. This is a maintained-codec performance difference, not a
   repeated full WebP decode. JPEG's much larger *relative* regression arises
   because the same expensive float/gamma path is added to a fast baseline.
4. **Duplicated strict work is real but format-specific.** PNG's immutable E+1
   admission witness inflates before raster decoding; GIF's strict container
   pass performs LZW before configured rendering. These are accepted integrity
   mechanisms. PNG derivative verification repeats admission and raster decode
   at the smaller size. Their removal needs an equally strong supported proof;
   it cannot be treated as a free optimization. They do not explain JPEG.
5. **Unnecessary passes/allocations add smaller costs.** Float storage is zeroed
   then filled; CMS uses another RGB buffer; alpha is scanned after preparation;
   opaque pixels still multiply/divide by one; RGBA is copied to RGB for JPEG.
   Output/parent vectors grow and C# captures to a `MemoryStream` then copies.
   Measured parent copies and profile generation are negligible at this profile.

## 4. Ranked optimization opportunities

No optimization was implemented. Estimates below are not measured improvements;
diagnostic controls do not qualify altered production behavior.

| Rank | Concrete opportunity | Expected impact/confidence | Complexity and risk |
| --- | --- | --- | --- |
| 1 | Fuse 8-bit normalized-sRGB expansion and premultiplication using a 256-entry table computed with the existing transfer formula; retain the current 16-bit/non-equivalent fallback | Roughly 30–40 ms/image plausible; high confidence in hotspot, medium in savings | Small color-path change; must preserve CMS, orientation, exact alpha treatment and resulting pixels. Does not solve resizing/codec budget. |
| 2 | Initialize float storage once and collect opacity during preparation; avoid separate scans for known non-alpha layouts | Low single-digit milliseconds plausible; medium confidence | Small allocation/loop change using fallible initialized storage. Page-fault and necessary write costs remain. No unsafe uninitialized buffer shortcut. |
| 3 | Skip linear/premultiplied round trips when geometry does not change, preserving CMS and zero-alpha normalization | Potentially substantial for no-resize profiles; zero benefit for the measured downscale | Bounded additional path; unmeasured, requires 16-bit/alpha/rounding controls. Cannot address this failing profile. |
| 4 | Emit RGB PNG for proven opaque pixels; avoid unnecessary output channel/copy work | About 1–2 ms at most here; low-to-medium confidence, possible byte reduction | Small encoder-routing change; transparent output must retain alpha. GIF palette quantization cannot be copied blindly without fidelity proof. |
| 5 | Match JPEG chroma policy through a supported encoder API | At most part of the roughly 3 ms encoding gap; low confidence | Current pinned encoder exposes no such setting. A maintained encoder change and quality qualification would be needed. Merely lowering quality is inadmissible. |

Large additional gains would have to come from the 22–27 ms resampler and/or
selected JPEG/WebP decoding paths. No small configuration switch in the inspected
resizer supplies that improvement. Codec worker/build settings are unmeasured;
extra threads can increase aggregate CPU/resource costs. Changing maintained
primitives would require a further scope decision and fidelity/containment
validation. No replacement library, custom resampler, daemon or worker pool is
selected or proposed by this investigation.

## 5. Feasibility and next bounded action

The gate remains per-format/profile warm **and** cold p50/p95 <=110% of the
unchanged ImageSharp baseline, sequential throughput >=90% and comparable
dimensions/quality. None of the supplemental references changes that gate.

A deliberately optimistic arithmetic control sets **all** color preparation,
gamma, quantization, alpha/orientation work and some small I/O work to zero,
while retaining each sample's measured source decode, resize, encode, output
verification and outer boundary. It still yields p50 **45.67 ms JPEG and
83.64 ms WebP**. These exceed the original median budgets of **29.71/60.90 ms**
and the current better-warmed budgets of **20.89/59.79 ms**. This is an estimate
holding those paths unchanged, not an optimized executable measurement or an
absolute theoretical lower bound for Rust. Actual cleanup can only recover
part of the hypothetical savings.

Thus the current stack has no demonstrated economical one-patch route to the
frozen gates. Its dominant avoidable work is clearly diagnosed; mandatory
duplicated validation is a smaller issue, and the measured supervisory boundary
is cheap. Passing would require substantial improvement of several remaining
primitives as well as color preparation. Confidence is high in the measured
causes and medium in the economic conclusion; untested compiler/codec changes
remain estimates. Broader quality/RID/aggregate-resource qualification remains
open and cannot be inferred from these timings.

**Next bounded implementation assignment: none justified for gate acceptance.**
Preserve the working prototype and its NO-GO decision. Do not extend trip/stress
loads. A gamma-only cleanup could make it faster, but would leave a decisive
performance failure and does not justify another optimization sequence under
the stated economic objective.

## 6. Artifacts and verification

Paths below are relative to the scratch root. `SHA256SUMS` records exact hashes
for retained evidence, inputs/outputs, instrumented source/binaries and report
artifacts; `ARTIFACTS.json` identifies the principal results. Toolchain/package
caches and scratch Git internals are excluded from that artifact manifest.

- `evidence/summary.json`: complete statistics, stage probes, output metadata,
  color-aware differences and recalculated retained results.
- `evidence/direct.json`, `io-control.json`, `icc-alpha-controls.json`:
  direct original/instrumented timing, bounded I/O and valid ICC/alpha controls.
- `evidence/csharp-{jpeg,webp}-warm.{json,log}` and
  `csharp-{jpeg,webp}-fidelity-control.{json,log}`: complete raw pairs and probes;
  associated `*-stages.json` files preserve parsed nested/overlapping timings.
- `evidence/jpeg-perf.data`, `jpeg-perf-*.txt`, `perf-symbol-summary.json`,
  `rust-elf.txt`, `rust-symbols.txt`, `libm-powf-ifunc.txt`: original-binary
  sampling and exact symbol/offset evidence, with capability limitations.
- `evidence/instrumentation-stages.patch`, `instrumentation-final.patch` and
  `bin/*`: stage-only and supplemental-control source/binary identities.
- `retained-evidence/`: independent copies of original fixtures, paired outputs,
  67-test evidence, resource records and live-contract snapshots. All 367
  original evidence-manifest entries verified. Hostile/resource-exhaustion
  cases and trip workloads were not rerun.
- `evidence/original-content-manifest.json` and
  `original-preservation-verification.json`: all 16,364 original files/symlinks
  checked, with zero additions, removals or content/link differences; original
  checkout remains clean at the specified commit and unchanged worktree list.
- `COMMANDS.md`, `env.sh`, `diagnose.py`, `analyze.py`, build logs and
  `evidence/code-guard-*.json`: reproduction and proportional checks.

Rust Release compilation/formatting and C# Release builds completed. Safe
single-image measurements completed through finite inherited process limits;
original/instrumented decoded pixels match across all four formats. The retained
67-test result is historical safety evidence, not a freshly rerun suite.
Code Guard has REVIEW only, no FAIL/INCOMPLETE or policy changes. Existing
PNG/GIF/WebP authorities remain cohesive; stopwatch probes and explicit
supplemental reference options account for growth in color/baseline callables.
Those findings were inspected and retained without refactoring the prototype.
The offline `analyze.main` complexity REVIEW (24) comes from explicit iteration
over fixed evidence families; statistics, trace parsing and color comparison
already have separate functions. Keeping the final aggregation sequential makes
its provenance auditable. The final source check selects all ten uncommitted
changed files; the committed-only comparison selects none and is retained as
`evidence/code-guard-committed-scope-empty.json`, not used as completion evidence.
Scratch instrumentation is intentionally uncommitted and retained with patches.
No production changes, commits, pushes, PRs, releases or GitHub writes occurred.
