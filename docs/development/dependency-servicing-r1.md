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
| npm Quill | 2.0.2 | 2.0.3 |
| Copied Bootstrap; print CSS | 5.3.6; 5.3.0 | 5.3.8 |
| Copied Bootstrap Icons/font/SVG; print CSS | 1.13.1; 1.11.0 | 1.13.2 |
| jQuery Validation; Markercluster | 1.21.0; 1.4.1+master.37ab9a2 | 1.22.1; 1.5.3 |
| SortableJS; Tom Select | 1.15.6; 2.4.3 | 1.15.7; 2.6.2 |
| Rust libc; moxcms | 0.2.177; 0.8.0 | 0.2.191; 0.8.1 |
| Rust cc; find-msvc-tools; crc32fast | 1.8.0; 0.1.14; 1.5.2 | 1.10.0; 0.1.15; 1.5.3 |
| Noble SDK; .NET/ASP.NET runtime | 10.0.401; 10.0.12 | 10.0.403; 10.0.14 |

## Security and resolution

EF.SqlServer 10.0.14 selects SqlClient 6.1.7, which still admits package-local
PKCS 9.0.11. The application therefore adds **System.Security.Cryptography.Pkcs
9.0.22** as the smallest assessed servicing constraint preserving its 9.x family.
It addresses GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7. Recovery's updated
DataProtection parent supplies XML and PKCS 10.0.14; operator/test resolution
inherits that 10.x closure. Runtime servicing does not replace local package DLLs.
No audit warning is suppressed. Other transitives remain parent-owned.

The updated npm audit reports the existing upstream **Quill 2.0.3 HTML export XSS**
[advisory](https://github.com/advisories/GHSA-v3m3-f69x-jf25), with no patched release
listed. The copied editor was already 2.0.3. Production does not call
`getSemanticHTML`; it reads editor DOM and passes submitted Notes through the
existing client normalization and server RichNotes allow-list before storage and
publication. Retain the sanitizer and acknowledgement regressions. This is a
recorded advisory, not a claim that the upstream package is vulnerability-free.
No automatic audit fix, downgrade or sanitizer-policy change is applied.

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
explicitly prepare the affected derived generations through the existing
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
the updates. No licensing policy is changed.

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
