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

Production merge remains blocked on an official published WebP crate containing
the qualified fix and a maintainer decision about native Windows (use WSL2 now).
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
results do not substitute for it. Fresh independent exact-head review, the
official fixed WebP publication/pin, the native Windows maintainer decision and
maintainer acceptance remain mandatory. PR #869 remains draft/unmerged and
#807/#858 remain open. No merge, deployment or release is authorized.
