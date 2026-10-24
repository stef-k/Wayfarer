---
title: History derivation and upgrade preparation
---

# History derivation and upgrade preparation

Issue #918 package I918-A supplies internal data and upgrade readiness. It does
not by itself deliver the history routes or complete #918.
I918-B supplies [routed reads and dependent-operation gates](../development/api.md#personal-history-bearer-reads-918-i918-b).
I918-C documents backend composition/fidelity and the measured performance limitation
below; backend #918 acceptance is not yet supported. #604 owns actual M6/maintainer
instance qualification. This procedure grants no production operation authority.

## Authorities and upgrade boundary

Follow [Versioning](versioning.md), the [Release contract](release-contract.md)
and the installation's supported [operator lifecycle](../self-hosting/wayfarerctl.md).
Do not turn a schema migration or ordinary startup/read into a history backfill.
The early `history-data` application CLI exits before web startup, migrations,
seeding, logging sinks, Quartz, storage initialization or provider registration.
It consumes the normal application configuration, including
`Database:PasswordFile`; it never prints connection strings or source content.

Before an authorized upgrade, preserve the normal database/uploads/key-ring
recovery set. Stop/quiesce all old application/import/enrichment writers at the
supported upgrade boundary. Apply migration `20261023175322_LocationHistoryPreparation`
using that release's normal schema-maintenance procedure and exact target binary.
New writers must all be from that binary before preparation/readiness is accepted.
An old binary or arbitrary external SQL writer can violate these invariants and
must not run concurrently. Whole-database restores require this check again.

The migration adds nullable facts, coherence constraints, a two-row preparation
state table and four indexes. It requests trusted extension `pg_trgm` alongside
existing PostGIS/citext. Have the database owner provision the extension if the
maintenance role cannot do so; never grant the application superuser rights.
Missing privileges or failed index creation leave preparation unavailable.
The migration uses ordinary transactional index builds while writers are quiesced;
allow its table/index locks and disk headroom. No concurrent-build release protocol
or release manifest/operator changes are introduced here.

## Commands and recovery

Run the target application's CLI under the same protected configuration/database
identity used by its supported maintenance process. For native/source qualification:

~~~sh
dotnet Wayfarer.dll history-data status
dotnet Wayfarer.dll history-data prepare
dotnet Wayfarer.dll history-data status
~~~

`prepare` resumes existing compatible progress. It defaults to both generations;
`time` or `text` selects one. JSON progress reports target/active versions, all-owner
total/pending/unresolved counts, examined/prepared counts, last committed ID,
physical index readiness and last actionable failure. It contains no owner IDs,
Notes, addresses or provider credentials. Exit codes: 0 selected generations ready,
1 pending/failed, 2 invalid arguments, 130 cancellation. `status` is read-only and
observes readiness/counts in one repeatable-read database transaction.

Each batch locks/reloads at most 500 rows in ascending ID order, derives from their
current stored values, writes only derived columns and commits progress in the same
transaction. Per-batch lock timeout is five seconds and statement timeout sixty
seconds. Ctrl-C cancels the current batch; process termination also leaves committed
checkpoints recoverable. Resume `prepare`; do not reset import checkpoints, replace
canonical rows, or retry provider requests. An operational failure records a safe
resumable category when the database permits it and never becomes unresolved evidence.
If recording the failure also fails, the primary error and earlier committed progress
remain authoritative. A completed cursor is followed by a stale-version sweep;
readiness never relies on maximum ID alone. New supported writers already produce
current facts. Edits/deletions serialize with each short row lock; deleted rows are
not recreated. No locks span provider HTTP.

Before activation the command verifies the required valid/ready index definitions,
trigram operator class and C text collation, then records the active generation and
writer contract. It runs `ANALYZE "Locations"` after selected preparation. Final
status rechecks physical prerequisites and all-owner versions. A durable ready flag
alone is insufficient. Repair a missing index through authorized schema maintenance,
then resume; do not weaken matching or indexes. Restored mixed/stale rows defeat
status even when an old preparation receipt says ready, and `prepare` sweeps them.

## Independent rules rebuilds and readiness

~~~sh
dotnet Wayfarer.dll history-data rebuild time
dotnet Wayfarer.dll history-data rebuild text
~~~

A rebuild marks selected generations unready before processing any rows, resets
only their progress and scans every row even for a same-version rebuild. Interrupted
rebuilds resume with `prepare time`/`prepare text`, rather than starting another
rebuild. Changed target rules require explicit `rebuild`; arbitrary operator version
labels are rejected. There is one stored generation, so declare maintenance for
affected reads while rebuilding. Unselected facts/progress stay intact.

Time identity includes converter/validation version, GeoTimeZone 6.1.0 mapping
package and the serving NodaTime TZDB identity. Text identity includes extraction/
folding version, AngleSharp assembly identity, .NET runtime and invariant Unicode/
[Unicode sort version and identity](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.sortversion?view=net-10.0). Deploy identical qualified runtime/globalization artifacts across
writers/readers; an identity change requires the affected rebuild. Never relabel
old facts as new rules. Collection cursors bind these identities.

Pending time has null version/status/facts; a mismatched version is also pending.
Resolved time carries the coordinate-derived IANA zone, wall timestamp and historical
offset together. Current-version unresolved time carries a safe evidence reason and
no invented capture values. Invalid/empty/nonfinite/out-of-range coordinates,
unusable event evidence (including PostgreSQL infinity sentinels) and unrepresentable
local conversion are exceptional evidence; unexpected resolver/tool/store failures
remain unfinished work. Stored UTC `LocalTimestamp`, `Timestamp`, coordinates and
imported `TimeZoneId` are preserved; the imported zone does not override coordinates.

Readiness requires matching target/active/writer identities, valid indexes and zero
pending rows across every owner. Current-version unresolved time is terminal
classified evidence, counted separately from pending. Collection reads check the
required generation identities, physical prerequisites and a single all-owner
pending-row existence predicate in the response snapshot. They do not compute
unused operator totals; `history-data status` retains its full progress accounting.
Capture predicates depend on time readiness, nonempty search on text readiness.
Affected operations return `historyPreparationRequired`;
unaffected UTC/viewer/nontext reads and direct detail retain their contracts.
I918-A exposes no routes or readiness/capability advertisement to consumers.

## Text equivalences and matching

`LocationHistoryDerivation.NormalizeText` is the shared stored/query implementation.
It applies compatibility decomposition (NFKD), the explicitly selected accent fold,
Unicode whitespace collapse/trim, invariant scalar uppercase and NFC composition.
It removes U+0300/0301/0302/0303/0304/0306/0307/0308/030A/030B/030C/0313/0314/031B/
0323/0324/0325/0326/0327/0328/032D/032E/0330/0331/0342 only following Latin or Greek
base letters, including cedilla, ogonek, comma/dot below and Vietnamese horn/tone marks. Thus `cafe`, `CAFÉ`, and decomposed
`cafe` + acute agree; Greek tonos, dialytika, breathing and circumflex accents fold.
Greek final sigma agrees through uppercase. Iota subscript, Arabic marks, Indic
virama and Japanese dakuten remain meaningful. Letters without an applicable
compatibility decomposition (for example ø, ł, ß) are not transliterated. This is
not translation, stemming, fuzzy matching or a wildcard/token language.

Notes first use `RichNotes.Normalize` and parsed visible text. Inline words stay
continuous; block/list/line boundaries add spaces, including discarded legacy
block containers. Parsing decodes entities once; attributes, URLs, scripts/styles,
images and removed content never contribute text. No media is fetched. Each of
the eleven authorized fields normalizes independently, then joins with newline.
A normalized query cannot contain that delimiter, so phrases cannot cross fields.
Original Notes/address fields are never rewritten by preparation.

Store search text with C collation. Collection queries use ordinal literal substring:
parameterized LIKE with escaped backslash/percent/underscore for useful trigrams;
exact `strpos` for short/punctuation-only terms. No minimum phrase length or raw-Notes
application scan is needed. The qualification checks LIKE/strpos parity with the
accent-folded representation and confirms the GIN path; common terms may legitimately
scan broadly. I918-B qualifies exact routed predicates; I918-C records scale performance below.

## Writer coverage and canonical isolation

| Writer | Atomic integration |
| --- | --- |
| Web create/edit/bulk Notes and manual address edits | Shared tracked save boundary locks an untracked current row, overlays only modified canonical properties and derives from that final merge. Provider clears participate. |
| Bearer log/check-in/create/coordinate refresh | Same save boundary inside existing account/deletion/provider fences. Both synchronous and async save overloads are covered. No added admission/receipt authority. |
| CSV/GPX/KML/Google Timeline/Wayfarer GeoJSON imports | Existing final normalization, legacy/keyed AddRange and conflict-retry saves use the same derivation; UUID/dedup/delete/checkpoint ownership is unchanged. |
| Retained reverse geocoding | Existing tracked publication saves include final retained fields. |
| Both fenced enrichment ExecuteUpdate branches | Successful final-row reload and derived text update happen before commit in the existing fenced transaction; repair retains nonempty fields/current Notes. Failed publication writes no derived provider content. |
| Preparation and row/account deletion | Preparation writes only derived columns; same-row derived data disappears with ordinary deletion/cascade. Workflow-only bulk updates change no Location inputs. |

Time is recomputed for event/coordinate changes or stale time rules; text for any
of the eleven text inputs (including clears) or stale text rules. Activity remains
the ordinary catalog FK. Internal properties have nonpublic setters, BindNever and
JsonIgnore; parsers/exports retain explicit canonical mappings. API whole-entity
snapshot comparison excludes only the eight new derived properties while retaining
all previous canonical checks. Preparation therefore creates no new owner edit
conflict or idempotency equality/export field.

## Local qualification and measured limits

The retained I918-A receipt uses PostgreSQL 18.6/PostGIS on Linux/WSL, 10,000 synthetic
rows in a fresh fixture-owned database, with distinct diary content and all stored
address fields. Rules were GeoTimeZone 6.1.0, TZDB 2026c (mapping 48.2), RichNotes-2,
AngleSharp 1.7.1.0, .NET 10.0.13 and Unicode 31129 with the invariant sort identity
recorded in CLI status. It uses the actual
production preparer, 500-row transactions, no provider calls and no raw pre-extracted
text COPY. The trial took 13.14 seconds; maximum batch elapsed/lock upper bound was
448 ms. Table+TOAST grew 4,349,952 → 14,991,360 bytes; all indexes grew
6,881,280 → 24,576,000 bytes, with four history indexes growing
3,096,576 → 16,949,248 bytes. Observed preparation WAL was 86,780,568 bytes.
These are pre-vacuum physical footprints including MVCC/GIN pending-list growth and
fixture allocation, not steady-state per-row or production estimates.
Allow at least the observed growth plus index-build temporary space and WAL retention;
measure the restored installation's real headroom before its authorized upgrade.
Migration/index-build elapsed time and peak temporary disk were not measured by this
preparation trial; instance qualification must budget those separately.

Equivalent 100-row canonical SQL trials, each rolled back, compared baseline versus
the same transaction plus production derivation/storage: create/import 35.45/124.15 ms,
Notes/address edit 68.80/154.43 ms, enrichment 61.93/126.45 ms. Observed WAL respectively
177,904/724,360; 454,872/2,528,088; 478,328/960,888 bytes. Transaction/lock windows were respectively 27.85/117.27, 61.81/147.34 and
54.93/119.53 ms; totals include connection/rollback overhead. These isolate added derivation at the SQL seam;
they do not measure complete routed/import/provider workflows or sustained load.
WAL is cluster-observed and can include other work. A forced eligible GIN plan for
folded `CAFE` took 5.8 ms locally; this proves amended-normalizer compatibility,
not routed query performance or a production SLO.

Focused tests cover folded scripts, safe visible Notes, historical folds/gaps/non-hour
offsets, final partial edits, both fenced publication branches, import original/dedup
preservation, real checkpoints, interruption after a derived UPDATE, concurrent
edit/delete, all-owner accounting and independent rebuilds. Consumer/native/browser,
production population/headroom remain separate. I918-C routed evidence follows below.

## Routed qualification and readiness limitation (#918 I918-C)

The actual routed trial at production revision
`c4196b94fe10b5bdfdeba471ea2cc6e5b8d01c5d` exposes an unmet performance requirement:
both 210,240 and 1,051,200-record owners' ordinary full-period UTC overviews
returned 503 `historyBudgetExceeded` in all three first/warm observations (about
five seconds each). The corrected five-minute fixture verifies all requested owner
rows lie within that period. A fast
page therefore does not qualify the independently usable complete overview.
No filter, sparse destination, result or protective limit was weakened.

The fixture has 210,240 and 1,051,200-record synthetic owners plus 210,240 unrelated
rows, distinct Notes/address content, all retained production indexes, PostgreSQL
18.6/PostGIS 3.6.4 and the actual registered bearer MVC routes. This is local Debug
TestServer evidence on WSL2, with warm OS cache and no true disk-cold, network,
production-host or sustained-load qualification. Prepared fixture insertion is not
measured backfill throughput. Affected ordinary workloads cannot yet be declared
accepted merely because the preparation command reports data ready.

The captured 210,240-owner broad aggregate's separate natural plan took 7.73 seconds,
including 6.82 seconds of PostgreSQL JIT compilation. The larger aggregate takes
28.44 seconds, including 7.00 seconds of JIT, so compilation is not its only cost.
The pure four-read rounds return nine 503s across sixteen requests. A separate
diagnostic round and one console-recovered log observation are classified in the
retained report. After 500 tracked address edits without vacuum, the count plan
reports 716 heap fetches versus zero prepared; all post-write broad reads still
time out. Readiness also performs
all-owner operational counts on collection requests. These are measured query costs;
prior prototype timings do not qualify these endpoints. A separate correction must
retain complete predicates, uncertainty and the reviewed exact partition edges.

A data-ready receipt verifies versions, pending rows, writers and physical indexes;
it is not a performance/capacity receipt. Preserve its normal gates during any
subsequent correction. The reviewed finite year-0001 capture limitation remains
honestly unresolved, with UTC/viewer/detail facts retained. Production fixes require
a separate assignment; do not raise deadlines, omit exceptional candidates, narrow
ranges, sample cells or add an index solely to conceal this counterexample.

Retained reproducible commands, raw requests/responses, timings/plans and the
acceptance matrix are in
`/home/stef/.local/share/wayfarer-investigations/918-20261024/i918-c/`.
See also [testing and evidence classification](../development/testing.md#personal-history-reads-918-i918-bc).
The I918-A actual preparation/write/storage receipts above are reused, not rerun.
Their physical MVCC/GIN allocation, WAL and the independent disposable-only
compaction experiment do not prescribe production compaction or a per-row disk
multiplier. #604 must measure its authorized restored population, target release,
index-build temporary headroom, WAL retention and migration/recovery duration.
Mobile/native and deployed qualification remain separate; #853 C4 is untouched.
