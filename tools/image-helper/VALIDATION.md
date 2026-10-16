# #858 integration qualification — 2026-10-15

Scope: the main-based draft integration, based on
`ec55871f896309c26862d21c810989d0e8fe17a6`. Historical performance and upstream
qualification remain separate, at research head
`c7323737d5ebefd236ade5f76602b0c5025a56b0`.
No merge, release, deployment or issue closure is qualified or authorized here.

## Preserved qualification

Verified the clean local qualification checkout, ancestor relationship from
`64e704bcee433a077b212975dd64a22106379e3d`, all 39 receipt-manifest hashes,
76/76 retained xUnit results, five Rust results and 16 WebP controls. Safely
fast-forwarded the unchanged checked remote research head to `c7323737...` and
verified the remote SHA. Retained reports/results were not modified or imported.

The active upstream source remains exact, official and unmodified:
`image-webp f4d80bd965df2c81e65b6f43c1f70e0750bd4b0f`; all 91 tracked source files
match Git blobs. Verified all 48 registry archives against Cargo.lock and all
3,087 packaged source files against the actual build sources. The full closure is
49 packages including the Git decoder. The dependency/notice inventory contains
106 complete texts, including Rust stdlib and native JPEG obligations. Standard
library HTML has trailing whitespace normalized without removing notice text.
No native WebP package is selected. ELF symbols contain the Rust WebP reader and
`tj3Compress8`; dynamic dependencies are the normal Linux runtime libraries.

## Local behavioral evidence

| Check | Result |
| --- | --- |
| Locked Release helper build; Rust format check | PASS |
| Rust Release product tests | 5 passed |
| Release all-target Clippy, warnings denied | PASS |
| Focused image/supervisor/service/HTTP tests | 236 passed; 0 failed/skipped |
| Separate HTTP/cache storage/cancellation selection | 71 passed; 0 failed/skipped |
| Service integration, including caller overrides and final 20-request burst | 14 passed |
| CI scope classifier | 39 passed |
| Native Linux AMD64 `dotnet publish` | PASS; executable and notices bundled |
| Published dependency graph | ImageSharp absent |
| Docker AMD64 build/runtime smoke | PASS; UID 1654, read-only, network disabled |

The exact 236-byte VP8L control has SHA-256
`fe8ddb4c9fe18af4b4f62f94b756ea3af49810ed73ae789c594a94832ca4ffa8`.
It returns helper exit 4, supervisor Failed, no bytes and no live/zombie child.
The service refuses it as an origin source in both optimize modes and treats
fresh/stale legacy copies as unusable cache misses without publication.

Seven caller-recipe regressions were first reproduced as Unavailable/503. The
bounded correction restores success for large positive no-upscale bounds, the
Failed/404 path for nonpositive bounds and invalid JPEG quality, and PNG/GIF
indifference to JPEG quality. These cases run through the real service/supervisor.

Other focused controls cover source/output caps, pipe overflow, AS/CPU limits,
timeout/cancellation/reap, all JPEG/PNG orientation geometries, ICC/CMYK/gamma,
WebP combined EXIF/XMP privacy, PNG completion, alpha and format rejection.
Existing cache/HTTP tests cover canonical MIME, byte ETags/304, stale refresh,
rollback/failed replacement, origin ownership, cancellation, SSRF and error mapping.

A deterministic 20-request burst holds four originals and rejects the remaining
16 without queuing. Eight cache hits still complete independently while those
four originals are held. The origin budget and cache trust boundary remain separate.

## Finite resource observation

Reused the existing xUnit service seam and retained mixed JPEG/WebP/PNG/GIF fixtures.
The origin transport and cache publication are controlled test substitutes; these
are service-level trip loads, not browser/network latency or new codec-parity claims.
A native SDK container ran as UID 1000, read-only/network-disabled, with 3 GiB memory,
no extra swap, four CPUs and 192 PIDs. Seven integration cases passed.

| Images | First usable image | Completion | Success/rejection |
| --- | --- | --- | --- |
| 10 | 85.352 ms | 445.493 ms | 10 / 0 |
| 20 | 207.839 ms | 904.463 ms | 20 / 0 |
| 40 stress | 81.181 ms | 1704.594 ms | 40 / 0 |

The highest observed cgroup peak during those cases was 367,697,920 bytes
(350.7 MiB); memory max/oom/oom_kill events stayed zero. This is one bounded
mixed-corpus observation, not an aggregate ceiling for arbitrary inputs or unlimited
cache-hit traffic. Host transient-service memory accounting was unusable and is
excluded from the resource claim. The final burst assertion adds only immediate
admission rejections and was separately rerun after this resource observation.
The later parent-PID correction does not change codec work or resource ceilings;
the timings above remain the earlier observation, rather than a rerun of that load.

## Maintainability and remaining authority

Code Guard over the complete branch is REVIEW with no FAIL/INCOMPLETE or policy
changes. Accepted findings retain their responsibilities:

- Existing TripViewer size and Index/Preview callable reviews are unchanged by the
  image API documentation update; refactoring those actions is outside this seam.
- ImageProxyService file/callable size reflects existing download/cache lifecycle
  ownership; the small asynchronous codec substitution preserves that owner.
- The retained C# PNG scan and Rust PNG/WebP/GIF admission state machines keep
  container completeness checks together. GIF nesting/complexity follows public
  streaming events, ICC collection and exact frame/end authority.
- PNG color-policy callable size represents one ordered set of supported color
  declarations and conflict checks; separating it would scatter that policy.
- CHANGELOG is the existing chronological release history, with one bounded entry.

Local build warnings include existing NuGet advisories in the application/recovery
closure; this integration does not refresh unrelated dependencies. CI must report
its own exact-head results. Native ARM64 helper + supervisor and Docker evidence
must come from the existing `application-image-arm64` job on `ubuntu-24.04-arm`.
Local AMD64 results do not qualify ARM64 or Windows.

At this historical stage, production merge was blocked on a published fixed WebP
crate and a native Windows decision. The maintainer superseded both policy gates
on 2026-10-16; see the shipping-policy section below. The recorded execution
evidence here remains unchanged.
The first hosted run passed the main test job, native ARM64 helper/supervisor
(202 cases), native ARM64 Docker/lifecycle and AMD64 Docker/Compose substrate.
Its later AMD64 host probe build failed because NASM was not provisioned for the
new transitive application dependency. The corrected workflow provisions the native
build prerequisites there too; final exact-head CI must rerun rather than borrowing
the earlier green jobs as a complete gate.

A subsequent existing Docker-smoke check, with the caller made container PID 1,
reproduced helper exit 70. The launcher had excluded PID 1 despite a matching,
non-root supervised application. It now accepts any positive matching parent PID,
retaining both parent checks, parent-death signaling and every resource control.
The image smoke owns PID 1 and passes against the rebuilt image. The retained
C# supervisor CLI also successfully transforms a JPEG as container PID 1 against
the new helper, under non-root/read-only/network-disabled controls. The current
application supervisor is separately covered by the focused application tests.
Codec source,
ordinary recipes and resource ceilings are unchanged; this is a launcher correction,
not an upstream decoder patch or new codec qualification.

Fresh independent exact-head review, successful `ci-gate` and final maintainer
acceptance remain required. The original integration helper artifact was
`5ed4228b3b1aa87df680119ed7e4d6379c431909942d6acca2b17ffeeb7d4354` (SHA-256),
identical to the native published copy. Docker builds independently from the same
locked sources. Rollback instructions are in [README.md](README.md#validation-and-rollback).
Raw local logs, binaries and derivatives are excluded from this draft's source.

## Targeted R1/R2 correction — 2026-10-15

Authority: [the bounded correction assignment](https://github.com/stef-k/Wayfarer/pull/869#issuecomment-6200454834),
starting at `a6c8785695d39cc9bc9de0abde384afd91cbdbb7`.
The service/helper and container observations below were measured at
`b2360af203d1bc29de5b0636c1385fbfda24096a`. The subsequent product delta at
`8378255e60307ba190a38d9931d42afcb3279e65` adds the controller's immediate 503
return before anonymous origin admission and extends its existing HTTP test.
That delta does not change codec execution or the resource measurements below.
The documentation-only receipt carries these observations without implying a rerun.

Local checks: **132 service/supervisor/cache cases passed**, plus **27 HTTP proxy
cases passed**, with zero failures/skips. The initial red regressions reproduced
uncapped validation and the silent input ceiling; the separate HTTP regression
reproduced a second, origin-enabled service call after cache Unavailable.
Six Rust tests, formatting and warnings-denied all-target Release Clippy passed.
The real helper SHA-256 is
`0a7a293b69f1e1cb5e7b0d55bf09e3dc03187fe1c03b255627d763697811f48c`;
the test payload and retained reviewer probe used that same executable.

Reused the reviewer's real service/supervisor probe and retained CRC-valid
4000x3000 16-bit RGBA PNG, SHA-256
`e313964b5187752b66be41e3e6b364a15934cdfceccd5e2966d864608905443a`.
Each run used UID 1000, read-only/network-disabled native AMD64 SDK containers,
3 GiB memory with no extra swap, two CPUs and 192 PIDs. Requests used
`optimize=false` and allowed origin fetch so accidental refetches were observable.

| Concurrent cache hits | FreshHit | Unavailable | Origin requests | Cgroup memory peak, bytes | OOM kills |
| --- | --- | --- | --- | --- | --- |
| 20, same key | 8 | 12 | 0 | 1,646,551,040 | 0 |
| 32, same key | 8 | 24 | 0 | 1,567,928,320 | 0 |
| 32, distinct keys | 8 | 24 | 0 | 1,568,190,464 | 0 |

Every run also completed a single valid hit before contention and another after
reap. All memory max/oom/oom_kill events were zero; parents exited successfully.
This bounds helper/thread admission to eight without validation waiters. Cache
reads still precede admission; this is not a bound on all managed cache-read bytes
or a guarantee for every combined origin/cache load. The focused service regression
also proves fresh/stale saturation never starts origin work, warm-up fails closed,
and cancellation releases admission only after reap. The existing four-origin /
eight-cache-hit success case passed locally and in the constrained container.

The reviewer's exact **53,477,458-byte one-pixel PNG**, SHA-256
`97c152470373c3d2614caefeffd39899297b29ad81b33ce809882ec7e9740e2e`,
returned Fetched in both modes at **100 MiB**: raw bytes were identical; optimized
output was a verified 431-byte, 1x1 PNG. Legacy-original validation returned FreshHit.
At **50 MiB**, both origin modes returned TooLarge/no bytes and the oversized
legacy entry remained an inert miss. Container peaks were 406,675,456 and
123,375,616 bytes respectively, with zero memory max/oom/oom_kill events.

Five existing service cases also passed together under those container controls:
the four-origin/eight-hit case and four configured-input rows. Those rows prove
53,477,458 bytes rejected at 50 MiB and accepted at 100 MiB, exactly **209,715,200
bytes accepted at 200 MiB** in both modes and legacy-cache validation, and
209,715,201 bytes rejected. Peak was 1,654,362,112 bytes; all memory events were zero.
Rust contract checks separately reject trusted input arguments above 200 MiB and
output arguments above 50 MiB. Geometry, decoded-memory, process, metadata,
animation, quality and derivative-verification controls are unchanged.

Full-branch Code Guard remains **REVIEW: the same 14 findings**, no FAIL/INCOMPLETE,
new findings or policy/baseline changes. Controller LOC is 609 within its 637
allowance; service LOC is 466 versus 455, retaining the same lifecycle owner.
Its download callable remains 89 lines. Existing parser/color/GIF complexity,
nesting and callable dispositions above still apply. CHANGELOG grew three lines
within its chronological responsibility. Scratch probe Code Guard passed.

No dependencies or selected codec sources changed. Existing unrelated NuGet
advisories remain debt. Final-head CI is reported by the PR checks; these local
results do not substitute for it. Fresh independent exact-head review and
maintainer acceptance remain mandatory. The then-pending published-crate and
Windows-policy gates are superseded by the decision below. PR #869 remains draft/unmerged and
#807/#858 remain open. No merge, deployment or release is authorized.

## R3 pre-allocation cache admission — 2026-10-16

Authority: [the focused R3 assignment](https://github.com/stef-k/Wayfarer/pull/869#issuecomment-6202251664)
and [formal CHANGES REQUIRED review](https://github.com/stef-k/Wayfarer/pull/869#pullrequestreview-5539870184).
Starting live head was `1e2870bd1ce58a6dee32d3da301bb9f4d1b43926`.
The product/test tree below is `e823a0751f3294d0cf09c9a100ec0124779639de`;
the following documentation receipt does not imply additional runtime measurement.
All work used a new isolated worktree. Earlier checkouts and experimental evidence
were preserved; the interrupted resource probe was not executed or replaced.

### Admission and ownership proof

`ProxiedImageCacheService.ReadBoundedFileAsync` opens an unbuffered file handle,
checks actual length against the effective input limit, then awaits
`ImageHelperSupervisor.AdmitCacheReadAsync` **before** its allocation hook,
`new byte[length]`, or any full-file read. Both the ordinary fresh/stale path and
the concurrent-refresh path call this reader. The latter releases the cache
write/missing-file lock before admission can wait. There is no cache MemoryStream,
capacity growth or ToArray copy. Exact-length reading rejects truncation/growth.

One framework `ConcurrencyLimiter` replaces the old validation semaphore. Each
read reserves `max(1, ceil(length / 25 MiB))` of eight units; pending demand is
limited to 32 units, oldest first, with a cancellable 15-second admission timeout.
Waiters have no full image buffers or children. Equal-sized 25/50/100/200 MiB
inputs admit eight/four/two/one concurrent validations. This is an explicit
concurrency limit, with the admin's 5–200 MiB input range unchanged.

The cache result owns the lease after reading. `ImageProxyService.GetSafeCacheAsync`
disposes that result only after preflight and full helper validation finish.
The helper borrows this same lease instead of acquiring/releasing another permit;
its completion still follows pipe cleanup and child reap. Launch failure,
cancellation, malformed input and read failure release ownership. Uncertain reap
still disables all further helper admission, including waiting disk readers.
An admission timeout/full waiter budget propagates infrastructure Unavailable to
the existing early HTTP 503 path, before rate limiting or origin-enabled lookup.
Missing metadata/files remain misses eligible for the four nonqueuing origin
operations. No trust flag, cache migration or codec change was introduced.

### Source-level memory accounting and limits

- Live untrusted cache input arrays total at most **200 MiB**, rounded upward in
  25 MiB reservations. Each read uses one exact-sized array plus a one-byte EOF
  check. A lease spans both the full read and retained input during validation.
- The shared validation budget also bounds validation children/owning threads to
  eight, or one when a cache input is 200 MiB. Child-owned encoded Vec capacity,
  full decode and codec scratch remain inside each child's 1 GiB address-space
  ceiling. The 64 MiB decoded admission guard is not a complete scratch bound.
- The separate four-origin budget remains nonqueuing. Its unchanged download
  reader can transiently retain a growing MemoryStream and ToArray copy: a
  conservative payload accounting bound is under three times the configured
  input limit per operation, up to **2,400 MiB across four 200 MiB origins**.
  Transforms also retain source bytes and bounded stdout capture/copy storage
  (under 150 MiB of payload storage per transform at the 50 MiB output ceiling).
  The derivative ceiling remains independent. No origin buffers are
  attributed to the cache's 200 MiB reservation.
- At most eight validators plus four transforms can own children concurrently;
  their combined per-child address-space ceilings can total **12 GiB**, or 5 GiB
  with one 200 MiB cache validator and four transforms. These are source-derived
  containment ceilings, not measured physical-memory peaks or a 3 GiB capacity
  guarantee. GC/LOH retention, allocator overhead, validated HTTP responses held
  by slow clients and the rest of the application remain outside the live
  untrusted-input bound. Response ownership/HTTP behavior is unchanged.

The correction removes the unbounded *pre-validation cache-array collection*.
It does not claim whole-process peak memory or OOM absence under every combined
origin/cache workload. No new resource qualification or stress harness was run.
The completed R1/R2 cgroup observations and native ARM64 CI above remain evidence
at their recorded heads and mocked-cache seam, not measurements of this change.

### Deterministic checks

The initial real-disk regression failed because bytes returned while eight actual
validation children were occupied; it passed after the correction. **19 small-file
admission cases** now cover both keys/both cache ages/alternate reads, allocation
entry counts, cancellation, admission timeout, bounded waiters, length-change I/O
failures, crash/cancel/reap, lease lifetime, weighted admission without large
allocations, and origin-miss independence. All passed within **223 focused C#
cases**. The final four warm cases also passed after extending distinct-key bursts
to mixed JPEG/PNG/GIF/WebP: all 10/20 requests return FreshHit/canonical MIME,
with zero origin calls. Their barrier proves only eight enter the allocation
seam while the remaining two/twelve wait; all complete within the test deadlines.
These are service/disk/helper behavior observations, not browser latency claims.

**Four established encoded-boundary cases** passed in a non-root, read-only,
network-disabled SDK container limited to 3 GiB/no additional swap, two CPUs and
192 PIDs. They retain 53,477,458-byte rejection at 50 MiB/acceptance at 100 MiB,
exact 209,715,200-byte acceptance at 200 MiB and 209,715,201-byte rejection in both
modes and full legacy-cache validation. This ran existing tests, not a new memory
probe. **Two PostgreSQL cache/xmin regressions** passed with no skips. Combined:
**229 distinct C# cases**, zero failed/skipped, plus the four final warm-case reruns.
Full source/cache validation, the exact 236-byte WebP control, four-origin/eight-hit
behavior, ETag/304, stale refresh and rollback remain in the focused selection.

**Six Rust tests**, formatting and warnings-denied Release Clippy passed using the
locked, unchanged codec sources. Local C# tests used the retained identical-source
helper SHA-256 `0a7a293b69f1e1cb5e7b0d55bf09e3dc03187fe1c03b255627d763697811f48c`.
No dependency pins, codec recipes or product settings changed.

Full-branch Code Guard: **15 REVIEW findings**, no FAIL/INCOMPLETE or policy changes.
The prior 14 dispositions remain applicable; the new 81-line cache `GetAsync`
finding retains one metadata/read/error lifecycle with explicit Unavailable
propagation. No metric-driven splitting or allowance changes were made. The
service/controller orchestration, parser security decisions and chronological
changelog retain their documented responsibilities. Existing NuGet advisory debt
is unchanged. Applicable final-head CI is reported by the PR checks; fresh
independent exact-head review and all standing merge gates remain required.

## Native Windows and shipping policy — 2026-10-16

Authority: [the maintainer's final shipping/platform assignment](https://github.com/stef-k/Wayfarer/pull/869#issuecomment-6220923425),
starting at exact head `0a24a134faca6c68fb8a4d4e3706a18bc78da5f5` and its
[independent R3 technical PASS](https://github.com/stef-k/Wayfarer/pull/869#pullrequestreview-5550926820).
The official immutable WebP Git pin is now **temporarily approved for production**
until an official fixed crates.io release is available. Production imaging is
Linux AMD64/ARM64; Linux/WSL2 provides complete development imaging. Native
Windows application builds are desired, with image proxy explicitly unsupported
(501/no-store); native Windows production deployment is unsupported.

### Small controller delta and focused checks

The common `ServeProxyImageAsync` controller seam checks `OperatingSystem.IsWindows()`
before cache validation, origin admission/fetch, helper startup and image-response
headers. Both direct optimization modes and valid public cover URLs return 501
with `Cache-Control: no-store`; cached bytes and If-None-Match cannot bypass it.
The callers retain URL/SSRF 400 and missing/private/empty cover 404 precedence.
There is no middleware blanket, fallback decoder or platform abstraction.

- **Native Windows x64 / Windows 10.0.19045 / .NET SDK 10.0.402:** the actual
  application and existing test project built in Release in a native NTFS source
  snapshot. The Linux-only helper target was excluded; no `image-helper` output
  was copied. No project/startup/build-target/CI configuration needed changing.
- The eight new controller cases first ran on native Windows before the guard:
  three expected failures (both valid direct modes and valid public cover), five
  validation cases passing. After the guard, **22 native Windows cases passed,
  zero failed/skipped**: those eight plus the 14 existing disallowed-URL cases.
  Strict proxy mocks prove no image-service call for 501 and preceding 400/404;
  these are controller tests, not a running application or a real cover fixture.
- **64 focused Linux controller cases passed, zero failed/skipped**, preserving
  real-helper cache/ETag, origin limiting, 400/404/503/429 and settings semantics.
  Eight platform cases are included in that count. Linux builds still produce
  the helper and complete notices through the unchanged native targets.

### Actual native startup — BLOCKED on the established prerequisite

The native Windows user-scoped guarded connection names `wayfarer_import_tests`
at localhost:5432. A read-only native `psql.exe` query reports server version
`170002` (PostgreSQL 17.2). The established WSL test connection independently
reports PostgreSQL `180006`, PostGIS 3.6.4 and citext 1.8, but that server listens
only on WSL 127.0.0.1:5432. Native Windows already has its PostgreSQL 17 service
listening on port 5432. A safe native Windows-accessible PG18/PostGIS startup
environment has therefore not been established.

**The actual application was not launched on native Windows.** `/health/live`,
an ordinary MVC/static route, live proxy 501/no-store/no-origin behavior and live
cover-image 501/404 remain **UNPROVEN**. The 22 controller cases do not substitute
for those HTTP observations or comprehensive Windows compatibility. No smoke
migration/seeding, startup/readiness bypass, Data Protection change, database or
network reconfiguration, or new PostgreSQL/browser infrastructure was attempted.
The next native startup attempt requires the established safe PG18/PostGIS
prerequisite and isolated smoke storage/key paths, with normal startup checks.

### Source, notices and retained qualification

The active Cargo metadata and unchanged Cargo.lock resolve the same official
`image-webp f4d80bd965df2c81e65b6f43c1f70e0750bd4b0f`. Rechecked the official
upstream Git tree `0e909cdd4b49d4858330979478e8e05883a2e319` against all **91**
actual tracked source files, **48** registry archive checksums and **3,087**
packaged source files. All **106** complete shipped notice texts match their
inventory hashes; only the non-license policy preamble changed. Cargo.lock
SHA-256 remains `3208da5d649a6c47710c270153aacb93ffe438790f62259fe41716efbecbf3a9`.
The rebuilt Linux helper and its copied payload match SHA-256
`746dd533034ade530139d329f04848db0f136a2653075987b187683d4884d6ae`;
the application output includes the complete updated notices. ELF symbols and
link dependencies retain Rust WebP/TurboJPEG, with no native WebP or production
ImageSharp. This is a build/source review, not a new codec/performance qualification.

The prior exact-head R1/R2/R3 results remain recorded above. The **229 C# cases,
six Rust tests and 216 native ARM64 cases** accepted by the R3 review are reused
at their recorded heads: codec sources, lockfile, Linux supervisor/admission and
packaging behavior are unchanged. No heavy resource/OOM probe or codec benchmark
was rerun. The temporary Git-source security/availability trade-off and concrete
#858/#807 replacement/version/checksum/notices/regression/review tracking are in
[README.md](README.md#dependencies-and-licenses) and DEPENDENCIES.json; future
security updates remain required. Full rollback instructions remain unchanged.

Full-branch Code Guard against live main `c94c40f55a75113cd75571c719c2fd75bd260320`:
**15 REVIEW findings**, no FAIL/INCOMPLETE or policy/baseline changes. Inspected
the required LOC/callable/complexity/nesting/Markdown policies and source. The
controller remains within its 637-line allowance (616 counted); unchanged
Index/Preview and service/cache lifecycle callables retain cohesive ownership.
PNG/WebP/GIF container state machines and PNG color precedence retain their
readable security decisions; CHANGELOG remains chronological. The new guard and
tests introduce no new findings. The full-branch and nine-file delta scopes are
both covered. Final exact-head CI is reported on the PR, not inferred here.

Native startup qualification remains BLOCKED; existing unrelated NuGet advisories
remain. Pre-existing Rich Notes renderer defects are tracked separately in #874
and must be coordinated before the first Rust replacement deployment. PR #869
must stay draft/unmerged for a fresh independent exact-head review and separate
maintainer merge approval. No deployment, release or issue closure is authorized.
