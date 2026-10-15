# Contained image processing (#858)

This is the selected runtime imported from qualification commit
[`c7323737d5ebefd236ade5f76602b0c5025a56b0`](https://github.com/stef-k/Wayfarer/tree/c7323737d5ebefd236ade5f76602b0c5025a56b0/tools/image-helper).
Historical experiments and results remain on `research/858-rust-imaging`.
Authority: [the integration assignment](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6197378228).

**Draft only: production merge is blocked.** `image-webp` is the unmodified official
Git revision `f4d80bd965df2c81e65b6f43c1f70e0750bd4b0f`. The official published
0.2.4 crate lacks the qualified malformed-VP8L fix. Before merge, select an official
published crate containing the fix, update Cargo.lock and the inventory/notices,
then rerun the exact retained counterexample, focused tests and native CI.
No private patches, vendored fork, alternate decoder or native WebP linkage is used.

## Runtime boundary

`ImageProxyService` retains fetch/SSRF, non-queuing four-origin admission,
coalescing, the total 100-second origin deadline, cache storage, stale refresh and
HTTP results. `ImageProxyHelper` delegates codec work to `ImageHelperSupervisor`
and the fixed `AppContext.BaseDirectory/image-helper/wayfarer-image` executable.
No executable path, shell, URL or filename comes from an image request.

Each operation owns a short-lived child and a launch thread kept alive until reap
for Linux parent-death semantics. Before exec, `/usr/bin/prlimit` supplies:

- 1 GiB address space; CPU soft/hard 20/25 seconds;
- 128 descriptors and no core dumps;
- a 30-second wall deadline, also cancelled by the owning operation;
- the configured 5–200 MiB input limit, a separate 50 MiB derivative output limit,
  and 4096-byte stderr drainage;
- a two-second termination/reap watchdog. Failed cleanup disables further admission.

The helper rejects root, unsupported OS/architecture, absent containment and parent
mismatch. A supervised parent may legitimately be PID 1 in a container; positive
PID validation, both parent-match checks and parent-death handling still apply.
Input geometry is limited to 8192 per axis, 12M pixels, one static frame
and conservative 64 MiB decoded-byte accounting. Codec scratch limits and kernel
limits are complementary; an address-space ceiling is not an aggregate RSS ceiling.
The effective configured input limit applies while downloading, to the helper recipe
and stdin reader, and to full legacy-cache validation. Trusted arguments cannot
raise the 200 MiB input or 50 MiB output ceilings.

Source MIME is byte-derived. JPEG, PNG, WebP and static GIF are accepted. PNG
requires complete CRC/checksum-verified zlib and exact container completion; GIF
requires complete LZW termination; WebP uses the qualified upstream decoder and
bounded container authority. Existing C# PNG/WebP early frame rejection is retained.
Declared animation is rejected even when a declaration claims a single frame.
The bounded JPEG recovery path remains the qualified codec behavior.

Transforms apply EXIF orientation including PNG eXIf, convert supported ICC/CMYK/
gamma to canonical sRGB, preserve alpha, resize with Lanczos3 without enlargement
and strip source EXIF/XMP. Opaque JPEG/WebP produces JPEG; PNG/GIF and transparent
WebP produce PNG. TurboJPEG writes into a bounded caller-owned output buffer. Every
derivative is fully decoded and checked inside the child before any stdout bytes.
C# additionally checks canonical JPEG/PNG boundaries and complete successful exit.

Web defaults are 2048x2048 / quality 90; PDF remains explicit width 600 / quality 85.
An explicit single axis leaves the other at 8192; explicit dimensions/quality win
within the mandatory ceilings. Larger positive bounds resolve to 8192 because no
admitted source can exceed that axis; nonpositive bounds and invalid JPEG quality
retain the Failed/404 path. PNG/GIF continue to ignore JPEG quality. The existing SHA-256 key formula and byte-derived
`raster-v1` ETag remain. Null quality now resolves to 90 in both key normalization
and encoding, so it aliases explicit 90 and does not collide with explicit 95.
Previously stored keys are never migrated or declared trusted.

`optimize=false` preserves the established safe-original contract: the validator
fully decodes the source before its original bytes/MIME can be stored or returned.
It does not apply resize, orientation or metadata stripping to those originals.
Every cache hit, including legacy/rollback bytes, goes through full validation;
there is no provenance shortcut. Validation has eight process-wide non-queuing
slots, separate from the four origin/transform slots. Admission happens before
creating a supervisor thread or child; the slot is retained through cleanup/reap.
Saturation returns Unavailable/503 without classifying cached bytes as corrupt,
refetching the origin or retaining inputs in a validation queue. Invalid bytes
remain inert cache misses. The cache read itself precedes validation admission;
the budget bounds helper/thread concurrency, not all concurrent cache reads.

Failures carry no image bytes: positive source limits map to TooLarge (HTTP 400),
malformed source to Failed (404), launch/timeout/pipe/output exhaustion to Unavailable
(503). Caller cancellation completes after cleanup; origin waiter cancellation
retains the existing coalesced ownership rules. Source diagnostic text is never logged.

## Build and packaging

Use native Linux AMD64 or ARM64, Rust 1.90.0, a C/C++ compiler, CMake, NASM on x64,
and `pkg-config`. Install `build-essential cmake nasm pkg-config` on Ubuntu 24.04,
then install the pinned Rust toolchain using rustup. `dotnet build` and
`dotnet publish` build the locked helper and copy its executable and complete notices
to `image-helper/` alongside the application. Cross-RID native builds are refused;
build natively on the target architecture. The runtime needs `/usr/bin/prlimit`
from `util-linux`, glibc and the ordinary system runtime libraries; it needs no Rust,
C/C++ toolchain, writable helper directory or native WebP library.

The Dockerfile uses a digest-pinned native Rust build stage and the existing
Noble application runtime. Build tools stay out of runtime. The non-root,
read-only, network-disabled image smoke exercises the packaged executable, malformed
rejection and output exhaustion. Native `application-image-arm64` CI separately
executes helper + C# supervisor and verifies transform, caps, malformed input,
cancellation and reaping; image construction alone is insufficient.

**Native Windows remains unqualified and unwaived.** Use WSL2 with the checkout on
the Linux filesystem. Before merge the maintainer must explicitly decide whether
to accept Linux/WSL2-only image processing or require a separately implemented and
qualified Windows containment/package path. This change does not claim Windows
support. Other unsupported hosts fail unavailable rather than silently falling back.

## Dependencies and licenses

[DEPENDENCIES.json](DEPENDENCIES.json) records the locked runtime/build closure and
notice hashes. [THIRD-PARTY-LICENSES.txt](THIRD-PARTY-LICENSES.txt) contains complete
license texts, native libjpeg-turbo BSD/IJG/SIMD terms, Unicode and official Rust
standard-library copyright inventory, and is shipped beside the helper.
This software is based in part on the work of the Independent JPEG Group.
Registry packages retain Cargo.lock checksums; the WebP source uses the exact Git
revision above. Rust JPEG decode is `libjpeg-turbo-rs 0.8.0`, resize is
`fast_image_resize 6.1.0`, and JPEG output is `turbojpeg 1.5.1` /
`turbojpeg-sys 1.2.0` with bundled libjpeg-turbo 3.1.0.
ImageSharp 3.1.12 remains only in the test project for fixture creation and independent
output assertions; it is absent from the published application dependency graph.
The focused fixtures are retained unchanged from the linked research qualification.

## Validation and rollback

Run `cargo fmt -- --check`, `cargo test --release --locked` and
`cargo clippy --release --locked --all-targets -- -D warnings` in this directory.
Build `tests/Wayfarer.Tests` and select `ImageHelper`, `ImageProxy`, `ProxyImage`,
`WebpFrame`, `DecodedImage` and `ImageCacheProxyQualification` tests.
The real service tests include the exact 236-byte malicious WebP, four occupied
origin operations with eight cache hits, bounded 10/20/40-image loads, metadata
privacy and unchanged raw/stale cache behavior. Measurements are local observations,
not new waivers of the frozen research parity gate. Only the maintainer's specific
small-WebP PDF p50 and previously recorded tiny-JPEG exceptions are accepted.

Rollback by deploying the retained previous reviewed application image/native publish
tree using the established operator workflow. No database migration, cache format,
cache-row rewrite or filesystem migration is introduced. Keep the previous executable,
browser bundle and deployment authority; replace the complete application payload,
not just the helper. Preserve durable data and key rings. Existing cache entries
remain subject to the selected application's full byte validation and normal stale
replacement/LRU; do not bless bytes or weaken validation during rollback.

Before merge: official fixed crate publication/pin, native Windows maintainer decision,
successful exact-head `ci-gate`, fresh independent review and maintainer acceptance.
The draft must remain unmerged, with #807/#858 open. No release/deploy is authorized.
