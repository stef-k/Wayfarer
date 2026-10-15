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
| Focused image/supervisor/service/HTTP tests | 229 passed; 0 failed/skipped |
| Separate HTTP/cache storage/cancellation selection | 71 passed; 0 failed/skipped |
| Service integration, including final 20-request burst | 7 passed |
| CI scope classifier | 39 passed |
| Native Linux AMD64 `dotnet publish` | PASS; executable and notices bundled |
| Published dependency graph | ImageSharp absent |
| Docker AMD64 build/runtime smoke | PASS; UID 1654, read-only, network disabled |

The exact 236-byte VP8L control has SHA-256
`fe8ddb4c9fe18af4b4f62f94b756ea3af49810ed73ae789c594a94832ca4ffa8`.
It returns helper exit 4, supervisor Failed, no bytes and no live/zombie child.
The service refuses it as an origin source in both optimize modes and treats
fresh/stale legacy copies as unusable cache misses without publication.

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
Fresh independent exact-head review, successful `ci-gate` and final maintainer
acceptance remain required. The local helper artifact is
`a8aad54ac49623138126c1d19bf036fcfab27276fee033bfe835768e594e0005` (SHA-256),
identical to the native published copy. Docker builds independently from the same
locked sources. Rollback instructions are in [README.md](README.md#validation-and-rollback).
Raw local logs, binaries and derivatives are excluded from this draft's source.
