# #858 upstream WebP fix qualification — 2026-10-15

**UPSTREAM FIX QUALIFIED** for this bounded Linux x64 research check at official
`image-webp` commit `f4d80bd965df2c81e65b6f43c1f70e0750bd4b0f`.
Wayfarer's exact 236-byte VP8L counterexample returns a controlled source error
instead of aborting. This unpublished Git dependency is a qualification input;
production ImageSharp and the preserved selected research checkout remain unchanged.

Authority: [maintainer decision and finite task](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6195912944).
Starting research head: `64e704bcee433a077b212975dd64a22106379e3d`.
Tested implementation head: `fe4b053ed3091f07d7521dd72fc5f31e6e23825f`.
The final documentation/evidence commit follows these observations without changing
the tested implementation. The isolated branch is `wip/858-webp-upstream-20261015`;
its workspace is `/home/stef/wayfarer-research/858-webp-upstream-20261015`.

## Official upstream provenance and release blocker

Live crates.io metadata still reports **0.2.4**, published 2025-08-27, as the
latest stable release. [Official package](https://crates.io/crates/image-webp),
[published documentation](https://docs.rs/crate/image-webp/latest).

[PR #165](https://github.com/image-rs/image-webp/pull/165) merged as
`4a794cd8b88efe5e7ea82ce35f97979e5d0c64b1` on 2025-11-12. The exact main pin is
11 commits ahead of that fix, with no commits behind. All **91 tracked files** in
Cargo's upstream checkout match their Git blobs. The only extra checkout file is
Cargo's `.cargo-ok` marker. No upstream source was modified or vendored.

The checked `release-0.2.5` branch remains
`75f810915d02ae4ff55d3f825bf6a4b07efdf994`; its merge base with the fix is
`08b57d473c90148dd4b37a075df365afc3dfcc2b`, so it still lacks PR #165.
[#183](https://github.com/image-rs/image-webp/issues/183) remains open.
The existing 2026-10-14 [#118 report](https://github.com/image-rs/image-webp/issues/118)
already requests release inclusion. No upstream message was posted.

**Release blocker:** an official published crate containing the fix is still
required before production merge under the maintainer's decision. This task does
not authorize shipping the research Git pin or maintaining a private backport.

## Exact candidate and selected dependency graph

Only this fresh worktree changes the research helper's default features and
dependency selection. Default Release selects `jpeg-candidate-decode` and
`native-jpeg-encode`, retaining Rust JPEG decoding, bounded TurboJPEG output,
SIMD resizing, metadata handling and the C# supervisor. Diagnostic timing and
codec benchmarks are absent from the artifact.

| Route | Selected dependency |
| --- | --- |
| Complete WebP raster decode | `image-webp 0.2.4`, official Git SHA above |
| WebP transitive dependencies | `byteorder-lite 0.1.0`, `quick-error 2.0.1` |
| Existing image adapters | `image 0.25.10`, JPEG/PNG/GIF features only |
| Selected Rust JPEG decoder | `libjpeg-turbo-rs 0.8.0` |
| Bounded JPEG output | `turbojpeg 1.5.1`, `turbojpeg-sys 1.2.0`, libjpeg-turbo 3.1.0 |
| SIMD resize | `fast_image_resize 6.1.0` |

The `image/webp` adapter is now benchmark-only in this candidate. Its old registry
decoder remains in the lockfile for that dormant feature, but **is neither compiled
nor linked into default Release**. The active graph, compiler artifacts, ELF
symbols and dynamic dependencies contain no native `webp`/`libwebp-sys`.
`image_webp::WebPDecoder::read_image` and `tj3Compress8` are verified linked.
Only the existing native JPEG archives are built.

The isolated Release helper SHA-256 is
`35eb0cc6fe365df4b774e4f9920299781db4033319e1bc47a3dda5a1b5e8f0c7`,
unchanged after all observations. Build and test artifacts use separate target
directories. Rust 1.90.0, .NET SDK 10.0.112, Ubuntu 24.04/WSL2 x64, non-root UID
1000; no host CPU tuning or native-library override was introduced.

## Counterexamples, product contracts and fidelity

| Retained input | Exact bytes / SHA-256 | Default Release result |
| --- | --- | --- |
| Wayfarer container-consistent VP8L | 236 / `fe8ddb4c9fe18af4b4f62f94b756ea3af49810ed73ae789c594a94832ca4ffa8` | Exit **4**, zero stdout, controlled malformed-source error; no abort |
| Original upstream Huffman control | 235 / `05d231a5305be24c8d5780cb7ac4f95c5ae9e73d9c9544c498901d71e400929c` | Exit **4**, zero stdout; existing container rejection |
| Upstream partition-claim control | 72 / `37d662ab4e583cbc8650eb6a21526087f3c4e049d63df12eaf4ffbaaf2866a58` | Exit **4**, zero stdout; existing container rejection |

The 235-byte fixture also matches the original upstream comment's base64 exactly.
No fixture repair or payload substitution was made during this check. The 236-byte
fixture is the already retained exact-extent counterexample from
[WEBP-FALLBACK.md](WEBP-FALLBACK.md), where Release aborted with signal 6.
The new xUnit cases require **Failed**, exact child exit **4**, null returned bytes
and no `/proc/PID` entry after supervisor completion for both Huffman controls.

The unchanged control script passes all **16** retained cases: valid alpha/ICC,
combined ICC/EXIF/XMP orientation and privacy, malformed/truncated input,
animation declarations, explicit JPEG alpha refusal, encoded/output caps,
8192-axis and 12M-pixel ceilings, and the three upstream controls above.
Alpha is exact and visible RGB is exact. The combined metadata control's pixels
equal the correctly rotated ICC control. Its EXIF/XMP is removed.

A four-image smoke uses the existing collector once for each small/12MP WebP and
web/PDF profile, with no warmups or repetitions. Every derivative has identical
decoded pixels, geometry, JPEG quantization/sampling and canonical ICC bytes
(except the existing timestamp) to its retained fallback counterpart. The alpha
and ICC controls also match their retained derivatives exactly. No new performance
gate or broad perceptual qualification is inferred from these checks.

## Focused validation, resources and notices

- Locked, default-only Release build: **PASS**.
- Rust formatting, five applicable Release tests, all-target default-feature
  Release Clippy with warnings denied: **PASS**. Benchmark-only tests stay excluded.
- Focused xUnit: **76 passed, zero failed/skipped**. This includes supervisor exit
  mapping, reaping, cancellation, admission, pipe overflow, AS/CPU enforcement,
  color/orientation/alpha and PNG completion. The .NET build has zero errors and
  70 pre-existing dependency/analyzer warnings.
- All 13 helper runtime and supervisor source files remain byte-identical to the
  starting head. AS 1 GiB, CPU 20/25s, NOFILE 128, CORE 0, parent-death handling,
  deadlines, independent pipe caps and four non-queuing slots are preserved.
- The resolved graph has 49 unchanged registry packages plus the Git decoder;
  every registry archive matches Cargo.lock and all **3,109** packaged source files
  match the build sources. All **106** retained registry license/notice texts are
  present in the existing complete bundle. The Git decoder's official MIT text
  matches that bundle exactly; its full Apache alternative is separately retained.
  The decoder adds no dependency or notice obligation beyond this verified delta.
  Existing JPEG BSD/IJG/SIMD-zlib, wrapper MIT and Unicode texts remain retained.
- Code Guard: task delta **PASS**; complete research branch **REVIEW**, with exactly
  15 inherited findings, unchanged source hashes and measurements. Cohesive format
  admission/color rules, the finite collector and preserved historical analyses
  retain their recorded justifications. No policy, exclusion or baseline changed.
  The full comparison uses existing `origin/main` because this isolated repository
  has no local `main` branch. Detailed dispositions are retained with the receipts.

## Preservation, reproduction and stopping point

[Qualification receipts](results/webp-upstream-20261015/) contain the exact graph,
artifact identity, upstream/source/license proofs, controls, fidelity, focused test
results, Code Guard dispositions and finite command record. Complete raw evidence,
binaries and derivatives remain in this isolated workspace. Earlier worktrees,
selected source, reports, fixtures and performance evidence are preserved.

For local reproduction, source the retained `env.sh` in the workspace root and run
the commands in `method.json` against `source/`. The two new regression fixture
names refer to exact copies in `evidence/fixtures`, including the preserved
236-byte control. The existing tools are reused without a new harness.

The maintainer's small-WebP PDF warm p50 waiver applies only to the previously
measured cell. This task does not rerun or widen that waiver, qualify aggregate
RSS, packaging, ARM64/Windows runtime, production integration, CI or independent
review. Production ImageSharp stays selected until the separately authorized
integration and remaining approval gates pass.

**STOP:** bounded qualification complete. Research commits remain local. No PR,
production/main edit, push, merge, release, issue update/closure, upstream posting,
new codec search or optimization cycle follows.
