---
title: History derivation and upgrade preparation
---

# History derivation and upgrade preparation

Issue #918 package I918-A supplies internal data and upgrade readiness. It does
not deliver the history routes, advertise consumer capability, or complete #918.
I918-B owns routed reads and dependent-operation gates; I918-C owns routed
qualification and consumer documentation. #604 owns any actual M6/maintainer
instance work. This procedure grants no production operation authority.

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
ICU sort version. Deploy identical qualified runtime/globalization artifacts across
writers/readers; an identity change requires the affected rebuild. Never relabel
old facts as new rules. Future collection cursors must bind these identities.

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
classified evidence, counted separately from pending. I918-B must inspect status
in the response snapshot: capture predicates depend on time readiness, nonempty
search on text readiness. Affected operations return `historyPreparationRequired`;
unaffected UTC/viewer/nontext reads and direct detail retain their contracts.
I918-A exposes no routes or readiness/capability advertisement to consumers.

## Text equivalences and matching

`LocationHistoryDerivation.NormalizeText` is the shared stored/query implementation.
It applies compatibility decomposition (NFKD), the explicitly selected accent fold,
Unicode whitespace collapse/trim, invariant scalar uppercase and NFC composition.
It removes U+0300/0301/0302/0303/0304/0306/0307/0308/030A/030B/030C/0313/0314/0342
only following Latin or Greek base letters. Thus `cafe`, `CAFÉ`, and decomposed
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

Store search text with C collation. Future queries use ordinal literal substring:
parameterized LIKE with escaped backslash/percent/underscore for useful trigrams;
exact `strpos` for short/punctuation-only terms. No minimum phrase length or raw-Notes
application scan is needed. The qualification checks LIKE/strpos parity with the
accent-folded representation and confirms the GIN path; common terms may legitimately
scan broadly. Routed predicate/performance qualification belongs to I918-B/C.

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
rows with distinct diary content and all stored address fields. It uses the actual
production preparer, 500-row transactions, no provider calls and no raw pre-extracted
text COPY. The trial took 10.70 seconds; maximum batch elapsed/lock upper bound was
365 ms. Table+TOAST grew 4,136,960 → 17,932,288 bytes; all indexes grew
19,193,856 → 32,858,112 bytes, with four history indexes growing
4,751,360 → 18,399,232 bytes. Observed preparation WAL was 82,773,880 bytes.
These are pre-vacuum physical footprints including MVCC/GIN pending-list growth and
existing test-database allocation, not steady-state per-row or production estimates.
Allow at least the observed growth plus index-build temporary space and WAL retention;
measure the restored installation's real headroom before its authorized upgrade.

Equivalent 100-row canonical SQL trials, each rolled back, compared baseline versus
the same transaction plus production derivation/storage: create/import 21.66/112.48 ms,
Notes/address edit 46.83/129.12 ms, enrichment 46.69/105.80 ms. Observed WAL respectively
189,456/754,080; 433,560/2,574,328; 464,864/939,632 bytes. Transaction/lock elapsed was
within 0.3 ms of those totals. These isolate added derivation at the SQL seam;
they do not measure complete routed/import/provider workflows or sustained load.
WAL is cluster-observed and can include other work. A forced eligible GIN plan for
folded `CAFE` took 4.4 ms locally; this proves amended-normalizer compatibility,
not routed query performance or a production SLO.

Focused tests cover folded scripts, safe visible Notes, historical folds/gaps/non-hour
offsets, final partial edits, both fenced publication branches, import original/dedup
preservation, real checkpoints, interruption after a derived UPDATE, concurrent
edit/delete, all-owner accounting and independent rebuilds. Consumer/native/browser,
production population/headroom, and later routed performance remain unqualified here.
