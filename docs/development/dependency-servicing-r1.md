---
title: Dependency servicing R1
---

# Dependency servicing R1

Routine source maintenance against `7978d1e07cd45315a6c35ad06f6fa96e877ed3fc`,
using the [complete assessment inventory](https://github.com/stef-k/Wayfarer/issues/915#issuecomment-6420364730)
and its [assessment](https://github.com/stef-k/Wayfarer/issues/915).
Selected candidates were available on 2026-10-26. This prepares source and local
payloads; installed artifacts require a separately authorized replacement release.
Application version, database authority and migrations are unchanged.

## Selected versions

| Owner | Before | After |
| --- | --- | --- |
| ASP.NET Diagnostics/Identity/UI, EF Design/Sqlite/SqlServer/Tools, recovery DataProtection.Extensions, dotnet-ef | 10.0.11 | 10.0.14 |
| TestHost, EF InMemory/Sqlite/Sqlite.NetTopologySuite, Data.Sqlite.Core | 10.0.11 | 10.0.14 |
| HtmlSanitizer; parent-selected AngleSharp/CSS | 9.2.995; 1.7.1/1.0.1 | 9.2.1039; 1.7.2/1.0.2 |
| NodaTime; TZDB | 3.3.3; 2026c | 3.3.6; 2026e |
| TimeZoneConverter | 7.2.0 | 8.0.0 |
| Swashbuckle and Swagger/SwaggerGen/SwaggerUI | 10.2.3 | 10.3.1 |
| Test SDK/platform; xUnit VS adapter; coverlet | 18.9.0; 4.0.0; 10.0.1 | 18.11.0; 4.0.1; 10.1.0 |
| Moq; parent-owned Castle.Core | 4.20.72; 5.1.1 | 4.21.0; 5.2.1 |
| ReportGenerator | 5.3.10 | 5.5.11 |
| Code Guard | 0.5.0 | 0.5.1 |
| Vite; Vue plugin; vue-tsc/language-core | 8.3.3; 6.0.9; 3.3.12 | 8.3.5; 6.0.10; 3.3.13 |
| Vue and all compiler/runtime siblings | 3.5.43 | 3.5.44 |
| npm Quill | 2.0.2 | 2.0.2 (exact pin retained) |
| Copied Bootstrap; print CSS | 5.3.6; 5.3.0 | 5.3.8 |
| Copied Bootstrap Icons/font/SVG; print CSS | 1.13.1; 1.11.0 | 1.13.2 |
| jQuery Validation; Markercluster | 1.21.0; 1.4.1+master.37ab9a2 | 1.22.1; 1.5.3 |
| SortableJS; Tom Select | 1.15.6; 2.4.3 | 1.15.7; 2.6.2 |
| Rust libc; moxcms | 0.2.177; 0.8.0 | 0.2.191; 0.8.1 |
| Rust cc; find-msvc-tools; crc32fast | 1.8.0; 0.1.14; 1.5.2 | 1.10.0; 0.1.15; 1.5.3 |
| Parent-owned SqlClient; SNI runtime | 6.1.6; 6.0.2 | 6.1.7; 6.0.3 |
| SQLitePCLRaw bundle/core/provider/native | 2.1.12 | 2.1.13 |
| PKCS application graph; PKCS/XML recovery graph | 9.0.11; 10.0.11 | 9.0.22; 10.0.14 |
| SDK-owned ILLink task | 10.0.13 | 10.0.14 |
| Noble SDK; .NET/ASP.NET runtime | 10.0.401; 10.0.12 | 10.0.403; 10.0.14 |

## Security and resolution

EF.SqlServer 10.0.14 selects SqlClient 6.1.7, which still admits package-local
PKCS 9.0.11. The application therefore adds **System.Security.Cryptography.Pkcs
9.0.22** as the smallest assessed servicing constraint preserving its 9.x family.
It addresses GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7. Recovery's updated
DataProtection parent supplies XML and PKCS 10.0.14; operator/test resolution
inherits that 10.x closure. Runtime servicing does not replace local package DLLs.
No audit warning is suppressed. Other transitives remain parent-owned.

Retain the exact **npm Quill 2.0.2** pin and its matching lock entry. The initial
2.0.3 update contradicted the documented pin rationale and failed the unchanged
`npm audit` check in [run 40357847290](https://github.com/stef-k/Wayfarer/actions/runs/40357847290).
The [Quill investigation](https://github.com/stef-k/Wayfarer/pull/937#issuecomment-6421684278)
records the earlier decision and the
[HTML-export advisory](https://github.com/advisories/GHSA-v3m3-f69x-jf25).
Restoring the npm pin does not resolve the separately copied **Quill 2.0.3**
exposure: that distribution, including its restored notices/map, remains unchanged
and tracked in [#939](https://github.com/stef-k/Wayfarer/issues/939).
Production does not call `getSemanticHTML`; submitted DOM Notes retain the client
normalization and server RichNotes allow-list before storage and publication.
These controls do not establish that the copied package is vulnerability-free.
No editor replacement, audit threshold/suppression, CI selection or sanitizer
policy changes are included.

npm's lock update retains TypeScript wrapper 6.0.2 / JS compiler 6.0.3,
Playwright 1.63.0 and existing major families. Compatible parent-owned changes
include rolldown/bindings 1.2.12 to 1.2.15, OXC types 0.152.0 to 0.155.0,
PostCSS 8.5.29 to 8.5.31 and Babel parser/types/string-parser to 7.29.11.
Cargo changes only the five selected crates and retains all checksums and the
qualified WebP Git revision. No blanket transitive override is introduced.

## Behavior and persisted history

HtmlSanitizer retains Wayfarer's existing allow-lists and Notes contract.
TimeZoneConverter retains the used GetTimeZoneInfo API and host timezone database;
no stream initialization is enabled. Coordinate conversion uses NodaTime's
updated packaged TZDB. New conversions may reflect new upstream timezone rules.

History derivation already pins `DateTimeZoneProviders.Tzdb.VersionId` in
`TimeVersion`, and AngleSharp assembly/runtime/Unicode versions in `TextVersion`.
These updates naturally change the required generation. Existing preparation
readiness rejects mismatched generations. No startup rebuild, migration, historical
capture-fact rewrite or reinterpretation is added. Operators must review and
explicitly rebuild the affected derived generations through the existing
[history preparation contract](../maintainer/history-preparation.md) before relying
on prepared reads. Canonical timestamps, coordinates and imported timezone evidence
remain intact.

## Distribution provenance and notices

Copied assets are original bytes from exact official npm archives, verified against
the registry's integrity digest. Bootstrap includes its complete dist tree;
Bootstrap Icons includes paired font/CSS/SCSS/JSON and SVG files. Markercluster's
versioned path and every consumer move together. Validation, Sortable and Tom Select
retain supplied licenses and referenced maps. Quill's previously missing referenced
license text and map are supplied from its existing 2.0.3 distribution.
[Third-party notices](../../THIRD-PARTY-NOTICES.txt) and the helper's complete
[dependency/license inventory](../../tools/image-helper/DEPENDENCIES.json) accompany
the updates. No licensing policy is changed. The root Moq notice's stale MIT label is corrected
to the official BSD-3-Clause metadata; the assessed old/new package license is
unchanged. Older GeoTimeZone, Playwright and MvcFrontendKit notice labels are
reconciled to their unchanged actual inventory versions.

Immutable Noble image indices:

- SDK 10.0.403: `sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b`.
- ASP.NET 10.0.14: `sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee`.

Both native manifest/config pairs declare the selected servicing versions.
Preserve #936's signed CA installation before downloads, fixed pip wheel/hash,
Node 24.22.1 selector/pin and temporary patched browser-installer Node.

## Deferred boundaries

Playwright/browser 1.63.0 and Quartz 3.19.1 stay unchanged; their coupled candidate
batches remain separate. Preserve #474's compiler gate, #877's qualified WebP pin,
FluentAssertions 7 licensing, jQuery 3/Turf 6, UNKNOWN Leaflet.Editable provenance,
MvcFrontendKit's embedded tools, native libjpeg-turbo, Rust 1.90, Node-bundled npm,
pytest and all GitHub Action refs. No upstream project is changed.

The assessment's Caddy-current wording reused pre-correction evidence. Merged
**#936 already selects Caddy 2.11.7**, index
`sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f`.
This batch leaves those bytes and the old/new transition gate unchanged.
**#915 remains open** for earlier acquisition qualification, Caddy compatibility
and create-only publication obligations. Source maintenance does not replace
installed artifacts, authorize publication or waive native architecture gates.

## Original local validation and remaining evidence

The following records the original `3da73e4d` implementation, before the cleanup
and npm-pin corrections below. Using the official hash-verified SDK 10.0.403 /
runtime 10.0.14 on Linux x64:

- Release build and **264 focused .NET tests** pass, including Notes sanitization,
  rich-note contracts, generated Swagger, timezone/DST, history and helper behavior.
- The existing coverage utility passes **5,506 ordinary tests, zero skips** and
  generates nonempty Cobertura (29,793,080 bytes) and ReportGenerator HTML
  (285,559-byte index). Spatialite, privileged and .NET browser selections remain
  outside this ordinary selection; this is not a full-suite-green claim.
- All ten projects restore. Separate application, recovery, operator and test
  NuGet audits report no vulnerable packages after the constraint. Complete graphs
  contain 146 distinct ID/version pairs; the license inventory is inspected.
- **440 client tests**, typecheck, Vite build and built-asset smoke pass. npm audit
  retains the one low-severity Quill advisory documented above.
- **Six locked Rust tests**, formatting and Clippy pass. All 106 shipped helper
  notice hashes and 2,152 copied distribution files are verified. Native Linux x64
  app/operator/recovery publishes pass; self-contained payloads bind runtime 10.0.14.
- The Linux AMD64 image builds and passes the existing read-only/non-root,
  network-disabled Chromium/PDF/helper rejection/cap smoke. Local manifest is
  `sha256:b9ad5e6daa999e6ffc0f78ebb93344b03b0571bff2cb02a0bc5b3d59f6448326`
  (config `sha256:9b40eea1a047e98717a21cd2b3a7fc16eb9fbc50f1132b0aceb5561e8a713168`).
  Actual .NET/ASP.NET are 10.0.14; CA package is 20260816ubuntu0.24.04.1.
  Its extracted bundle excludes emSign G3 and retains ISRG X1 (120 roots).
  The initial registry-DNS attempt failed before building; its one recheck passed.
  ARM64 manifests/configs are verified metadata, with native execution pending CI.
  This local image has no publication or installed-system acceptance claim.
- Four existing Quill/server-normalized Notes acknowledgement browser regressions
  pass. Two existing mounted navbar/Notes/modal/print/editor journeys pass. The
  managed command nevertheless returns 1 after successful process and database
  cleanup: its artifact guard refuses retained CLR diagnostic FIFOs/socket. The
  first attempt stopped at key-readiness while tests overlapped; its single rerun
  passed that prerequisite. No third rebuild or harness redesign is included. Cluster expansion, selector
  keyboard/clear and drag gestures are not newly claimed by these two journeys;
  their distribution identity and existing lower-seam tests are narrower evidence.
- Code Guard 0.5.1 doctor is healthy. Complete-branch analysis is **REVIEW**, with
  no FAIL/INCOMPLETE and policy-relaxation PASS. Existing Viewer size stays within
  its 934-LOC allowance (929). Map utility callables are unchanged in size (113,
  104, 84, 116); only icon URLs change. The 1,532-line changelog and unchanged
  254-line historical Fixed section remain coherent release history. These reviews
  are accepted without policy, threshold, baseline or exclusion changes. Handwritten
  diff hygiene passes; original Markercluster whitespace and Sortable license CRLF
  are retained as verified upstream distribution bytes.

Evidence is retained separately at
`/home/stef/.local/share/wayfarer-investigations/r1-servicing-20261026/`, including
registry/archive hashes, before/after graph inventories, licenses, native payloads,
audit outputs, test/build logs, coverage HTML and a safe modal screenshot. Original
browser residue is retained at the path recorded in `evidence/browser-residue.json`.
The initial artifact-cleanup failure is fixture infrastructure evidence, with no
observed product failure; its local correction is recorded below. Independent exact-head
review, successful applicable native/image CI and maintainer acceptance remain
required. The original implementation opened a draft PR and checked CI startup
once. The completed run subsequently failed at npm audit, before later test-job
checks; the consolidation below starts no CI.

## Browser cleanup diagnosis and local correction (#937)

The live PR head was verified as `3da73e4d51f704f3c313bd043ff061ceef193db9`
before creating the isolated `fix/937-clr-artifact-cleanup` branch. The retained
`children.json` ties all three special entries to the launched Wayfarer host:
PID `134178`, Linux start tick `365531`, boot
`ff1d65d8-f131-4dd1-9549-566e7e1ce5da`. Its `TempRoot` contains
`clr-debug-pipe-134178-365531-in`, `clr-debug-pipe-134178-365531-out` and
`dotnet-diagnostic-134178-365531-soc`. The last name is the runtime's canonical
`-socket` path truncated to Linux's 107 usable socket-path bytes. The
[CLR diagnostic-port contract](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostic-port)
documents the TMPDIR, PID and process-start naming authority.

The supervisor and all 16 recorded children are absent from `/proc` on the same
boot. No Unix socket is bound at the original path; inspection found no accessible
process using the tree. Other-user process descriptors were inaccessible and were
left alone. All three entries belong to the task UID and have one link. The
retained log proves Playwright's two assertions and independent fixture/environment
cleanup verification passed before `BrowserRoot.remove()` failed. Its recursive
`ordinary()` check rejected the FIFOs/socket; the same strict check also prevented
writing the secondary diagnostic log. The original termination signal is not
recorded, so no specific signal is inferred.

The existing finalizer now passes its retained stopped children to `BrowserRoot`.
The owner derives an exact path/type allowlist only for their same-boot PID/start
tokens, after stop/reap, and requires those PIDs still be absent. Only the two CLR
FIFO names and byte-limited socket name directly in its private `TempRoot` qualify,
with the current UID and one link. Full-tree validation still precedes deletion.
Creation inode/marker, lexical path, ancestor, symlink/reparse, process and primary
failure checks remain enforced. Default `ordinary()` calls remain strict; there
is no stale-root adoption, arbitrary delete path or general special-file permission.

The new finalizer regression failed at the original artifact-removal seam before
the correction and passes afterward. All **43 existing Python tooling tests**
(including four added cleanup cases) and **four Node cleanup-safety tests** pass
on Linux. They exercise real FIFOs/socket, retained subprocesses, safe removal,
live/unretained/unknown owners, wrong birth tokens/boots/UIDs, reused PIDs,
inspection errors, wrong paths/types, symlinks and shared FIFO inodes. No application
build or browser-matrix rerun was needed: the existing ownership/finalizer seams
directly cover the change. Earlier browser product success and this cleanup success
are separate evidence; this does not claim a new end-to-end browser result or native
Windows validation.

Complete-change `code-guard . --base-ref main --ci` is **REVIEW**, with no
FAIL/INCOMPLETE and policy-relaxation PASS (30 selected, 17 analyzed, 13
inapplicable; 2,152 existing vendor exclusions). Accepted findings: Viewer remains
929 LOC within its existing 934 allowance; map utility callables remain
113/104/84/116 LOC with only the prior R1 icon URL edits; `BrowserRun.execute`
retains its unchanged 16-decision profile orchestration; the chronological
1,536-line changelog and 258-line Unreleased Fixed section remain navigable.
The cleanup implementation/tests pass all source guards. No policy, threshold,
baseline or exclusion changed. Correction diff hygiene passes.

Original residue remains intact at
`/tmp/wayfarer-browser-e2e-393e2e52-7648-4c5c-a080-15c376d93725`.
Before task cleanup, copies of original logs/ownership records, a 14,419-entry
hashed inventory and a process/socket snapshot were retained at
`/home/stef/.local/share/wayfarer-investigations/937-cleanup-20261026T174852Z/`.
Test-created roots/processes are finalized by their existing owners. Shared/manual
work and other agents' checkouts are untouched. This cleanup-only slice left the
Quill-pin and history-generation corrections separate. Its complete three-commit
range through `512898ad95e3160d344e789c907f200e7cc1c625` is integrated unchanged
in the local consolidation below. No push, new CI cycle, merge, release,
deployment or issue closure is authorized by this correction.

## Local cleanup and npm-pin consolidation (#937)

Verified live PR base `7978d1e07cd45315a6c35ad06f6fa96e877ed3fc` and head
`3da73e4d51f704f3c313bd043ff061ceef193db9` before editing. The isolated
`fix/937-cleanup-quill-pin` branch integrates the complete cleanup range above,
preserving its source/tests and recorded ownership rationale. Only the npm
manifest pin, root lock declaration and Quill lock node change for the editor;
the full 2.0.2 node matches the PR base and every other lock entry matches the
verified PR head. Copied Quill bytes, notices and map remain unchanged.

On Node 24.22.1 / npm 11.19.1, `npm ci` and the unchanged `npm audit` pass with
zero reported npm vulnerabilities. All **43 Python tooling tests** and **440
client tests** pass without skips, including the four cleanup-safety tests and
existing Notes/editor checks. Typecheck and the frontend build pass; Vite retains
its large-chunk warning. No additional product defect was observed.

Complete-change Code Guard against the verified PR base is **REVIEW**: 30 selected,
17 analyzed, 13 inapplicable and 2,152 existing vendor exclusions; no FAIL or
INCOMPLETE, policy-relaxation PASS. Accepted reviews retain the Viewer at 929 LOC
within its 934 allowance, the four unchanged map callables at 113/104/84/116 LOC,
and the unchanged 16-decision `BrowserRun.execute` orchestration. The 1,540-line
chronological changelog and its 258-line Unreleased Fixed section remain navigable.
No policy, baseline, threshold or exclusion changes were made.

Reuse the original four Notes acknowledgement browser assertions and two mounted
journeys as historical evidence; no browser run qualifies the consolidated head.
The original residue's 14,419 recorded entries retain matching metadata, including
all three diagnostic special files. Original evidence and shared/manual work are
preserved. Consolidation logs, preservation checks and recovery artifacts are at
`/home/stef/.local/share/wayfarer-investigations/937-consolidation-20261026T182200Z/`.

This is implementation-time validation. [#938](https://github.com/stef-k/Wayfarer/issues/938)
still owns the history-upgrade correction and holds acceptance of affected updates;
[#939](https://github.com/stef-k/Wayfarer/issues/939) owns copied-Quill exposure and
editor disposition. [#915](https://github.com/stef-k/Wayfarer/issues/915) retains
security publication gates. Keep this result local for #938 and final combined-head
review. No push, CI startup/rerun, merge, issue closure, release or deployment was
performed.
