# Contained image processing (#858)

This is the selected runtime imported from qualification commit
[`c7323737d5ebefd236ade5f76602b0c5025a56b0`](https://github.com/stef-k/Wayfarer/tree/c7323737d5ebefd236ade5f76602b0c5025a56b0/tools/image-helper).
Historical experiments and results remain on `research/858-rust-imaging`.
Authority: [the integration assignment](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6197378228).

**Temporary production dependency approved by the maintainer:** the unmodified
official `image-webp` Git revision `f4d80bd965df2c81e65b6f43c1f70e0750bd4b0f`,
under [the shipping/platform decision](https://github.com/stef-k/Wayfarer/pull/869#issuecomment-6220923425).
The qualified pin is permitted until an official fixed crates.io release is
available; a released-crate-only prohibition no longer blocks this PR. The
previously assessed published 0.2.4 crate lacks the qualified malformed-VP8L fix.
This accepts Git-source availability and maintenance risk to ship that security
fix without changing the qualified decoder. Cargo.toml and Cargo.lock identify
the same immutable official SHA; builds must use `--locked` and verify source
and complete notices. Git provenance does not waive future security updates.
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
there is no provenance shortcut. One process-wide validation budget covers disk
reads, retained untrusted bytes and validation through child cleanup/reap. Each
read reserves `max(1, ceil(actual file bytes / 25 MiB))` of eight units before
allocating its exact-sized array or reading the full file. No MemoryStream/copy is
used for cache reads. The same lease is borrowed by the validator, with no second
admission or release between reading and validation. Fresh, stale and concurrent
refresh reads use this boundary; admission waits never hold the cache write lock.

Ordinary inputs up to 25 MiB retain eight-way validation. Equal-sized 50/100/200
MiB inputs have effective concurrency four/two/one; the admin input limit remains
5–200 MiB. Cache reads wait cancellably for up to 15 seconds before allocation.
The framework limiter admits oldest waiters first and bounds pending demand to
32 units (at most 32 small-input waiters or four 200 MiB waiters). Queue saturation
or timeout returns Unavailable/503 without origin refetch, stale refresh or a
false corrupt-cache classification. An absent entry still reaches the unchanged
four-operation nonqueuing origin budget. Direct source validation is nonqueuing
and shares the weighted validation budget; the four transform slots remain separate.
Invalid bytes remain inert misses. This bounds live untrusted cache input arrays
to 200 MiB and validation threads/children to eight; see [memory accounting and
qualification limits](VALIDATION.md#r3-pre-allocation-cache-admission--2026-10-16).

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

Production image processing is supported on native Linux AMD64/ARM64. WSL2
provides the complete development path with the checkout on the Linux filesystem.
Native Windows application development/builds exclude the Linux helper target;
native Windows production deployment is unsupported. Valid image-proxy operations
return **HTTP 501 with `Cache-Control: no-store`**, for both optimization modes and
public trip cover images. URL/SSRF rejection remains 400; absent/nonpublic covers
remain 404. The common controller decides before cache validation, origin access,
helper startup or image ETags/304. Unrelated routes and local/static images remain
available. There is no Windows helper or ImageSharp fallback. Other unsupported
hosts retain fail-closed Unavailable behavior. See [native Windows qualification
and its startup prerequisite](VALIDATION.md#native-windows-and-shipping-policy--2026-10-16);
a successful build/controller test does not qualify actual application startup.

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
No active project depends on ImageSharp. Test-only Magick.NET 15.0.0 independently
checks output pixels, dimensions, frames, orientation and ICC/EXIF/XMP. Frozen,
hashed source encodings replace runtime fixture generation; see the
[oracle and fixture provenance](../../tests/Wayfarer.Tests/Fixtures/Imaging/README.md).
The research qualification corpus and Rust production dependencies remain unchanged.

Tracking under **#858/#807**: replace this temporary WebP Git source when an
official crates.io version containing the qualified fix is available. Confirm
that release's source contains the fix; explicitly update Cargo.toml, Cargo.lock
(including the registry checksum), DEPENDENCIES.json and all complete shipped
notices. Run the retained 236-byte malformed-VP8L rejection and focused codec/
supervisor regressions, applicable native checks and a small independent review
before adoption. The temporary approval has no automatic upgrade or security
update exemption and makes no claim that a fixed release is already available.

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

Before merge: successful applicable exact-head `ci-gate`, fresh independent review
and maintainer acceptance, with the Windows startup qualification gap explicitly
reported. The temporary official Git pin and Linux/WSL2 imaging policy are approved.
The draft must remain unmerged, with #807/#858 open. No release/deploy is authorized.
