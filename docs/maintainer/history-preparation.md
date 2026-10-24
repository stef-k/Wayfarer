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

The earlier timeout correction retains the existing routes, prepared-data
architecture, shared representable partition edges, complete filters, uncertainty
and five-second cancellation. Overview SQL streams numeric extraction and validation
through subquery boundaries before exact edge comparisons; the actual grouping
plan no longer expands PostGIS conversion/validation into every comparison.
Collection readiness uses one all-owner pending-row existence check for required
generations instead of unused operator totals. Physical index, target/active/writer
version and scanning/checkpoint guarantees remain in the response snapshot.

Fresh comparable before/after trials use the retained 210,240 and 1,051,200-record
owners plus 210,240 unrelated rows, distinct Notes and all eleven searchable fields,
five-minute events, PostgreSQL 18.6/PostGIS 3.6.4, Debug and actual bearer MVC routes.
Each selection has first use plus two warm observations. This is local WSL2
TestServer evidence with warm OS cache; no disk-cold, network, sustained-load or
production-host guarantee. Prepared fixture insertion is not backfill throughput.
The under-one-second target remains an engineering target on these workloads.

| Warm routed request | 210,240 before → after ms | 1,051,200 before → after ms |
| --- | --- | --- |
| Full-period UTC overview | 503 at 5,016–5,018 → 570–580 | 503 at 5,017–5,018 → 1,892–1,918 |
| First broad chronology page | 98–101 → 107–113 | 439–444 → 423–451 |
| Capture day overview | 1,205–1,249 → 665–698 | 1,304–1,332 → 835–912 |
| Recurring capture hours/common phrase overview | 2,656–2,923 → 1,300–1,325 | 503 at 5,010–5,016 → 2,321–2,384 |
| Combined Tokyo/year/activity/common overview | 1,613–1,631 → 1,152–1,193 | 2,455–2,536 → 2,275–2,423 |
| Absent short phrase (`zz`) overview | 3,354–3,504 → 891–903 | 3,558–3,732 → 1,427–1,459 |
| Complete long detail | 45–48 → 48–49 | 44–47 → 43–51 |

All 158 corrected requests return 200, versus 25 failures before correction. All
five pure four-read rounds complete: zero failures in twenty requests versus nine
in twenty before, at 2,091–3,725 ms after correction. Both receipts retain exactly
158 unique intact observations; appends are serialized and no concurrent round
contains diagnostic replays. The original qualification's nine failures in sixteen
clean requests and logging collision/recovery remain separate historical evidence.

**The sub-second target is not fully met:** 80 corrected requests are under one
second and 78 miss it, including every concurrent and post-write request. Warm
narrow days/months, broad chronology, rare/absent long phrases, detail, small-owner
broad overview and small-owner absent short text succeed under one second. Recurring
hours with/without common text, combined Tokyo scopes and punctuation overviews
miss at both sizes; large broad/short-text overviews and absent-short-text pages
also miss. The first small-owner day overview takes 1,050 ms. Completion inside
five seconds alone does not establish I918-C performance acceptance.

Natural broad aggregate replays improve from 7,201/30,785 ms to 456/1,559 ms for
small/large owners. JIT stays enabled: its totals fall from 5,998/9,120 ms to
34/35 ms, while planning falls from 3.128/6.189 ms to 0.871/0.840 ms. Grouping keys
shrink from 35,181 characters to about 3,480 without repeated coordinate expressions.
Execution work improves as well as compilation. The large aggregate still examines
the 1,471,680-row relation and reads about 247,913 shared blocks. Its external sort
writes 15,973 temporary 8 KiB blocks (about 125 MiB), up from 10,061 before; the
small aggregate no longer spills. These are the earlier correction's measurements;
the compact-cell follow-up below removes the remaining broad spill without changing work_mem.

Readiness remains a required all-owner scan: the combined pending probe takes
about 361 ms in the large recurring/common plan. Its exact uncertainty count still
takes about 1,085 ms and the complete aggregate about 931 ms in separate natural
replays. The Tokyo candidate plan has lossy bitmap rechecks; its count takes about
1,232 ms. These are remaining concrete query-work costs, not serialization costs.
Further target work needs a separately bounded investigation of those repeated
required scans and filtered aggregation spill/access paths; no new indexes, fused
query architecture or weakened readiness is approved by either correction.

Historical whole-cluster samples retain 169.15 → 110.90 PostgreSQL CPU seconds and
2,664 → 2,730 MiB summed RSS over 25.16 → 16.18 wall seconds. They include background
work and double-count shared pages, so they are excluded from attributable query
CPU/private-memory claims. Direct application peak RSS is 393 → 436 MiB. Admission,
worker and memory settings are unchanged; no memory reduction or sustained capacity
is established. The compact key's separate concurrency result below remains unproven.

After the same 500 tracked address edits without explicit vacuum, all corrected
post-write routes succeed: broad overview 1,933–2,013 ms, recurring/common
2,380–2,480 ms. The count's covering index has 716 heap fetches versus two in the
prepared observation. The separate post-write broad aggregate takes 1,567 ms and
retains the large sort spill at that stage. These do not qualify write throughput improvements.
Complete long detail remains 1,203,344/1,203,347 response bytes; the observed
serialization work is about 2–3 ms, with no truncation or invented unavailable facts.

Retained raw requests/responses, first/warm tables, CPU/RSS samples, stage timings,
SQL/plans/buffers/rechecks and reproducible commands are in
`/home/stef/.local/share/wayfarer-investigations/918-performance-20261024/`.
The original failure report remains at
`/home/stef/.local/share/wayfarer-investigations/918-20261024/i918-c/`.
See [testing and evidence classification](../development/testing.md#personal-history-reads-918-i918-bc).

### Compact cell grouping follow-up

The subsequent change groups the existing exact X/Y decisions by one reversible
integer key, then decodes and sorts the completed cells with the original null
ordering. Nullable positions become digits 0–16 in radix 17; keys 0–288 are unique
and safely fit PostgreSQL integers for every supported grid (1–16). Exact shared
edges, every match/count/extremum, sparse geography, singleton facts, uncertainty,
all-owner readiness, response snapshots, authority and deadlines remain unchanged.
There is no epsilon, sampling, new index, schema or planner-setting change.

The retained query-shape investigation is
`/home/stef/.local/share/wayfarer-investigations/918-query-shape-20261024/`.
Its final three-repetition SQL comparison has large broad medians 1,508 → 1,295 ms
(about 14%); the small broad median is essentially unchanged, 403 → 402 ms.
The large broad plan changes from sorting all 1,051,200 matches to a one-batch
40 KiB hash aggregate and a 25 KiB sort of six completed cells. Temporary writes
fall from 15,973 8 KiB blocks (124.79 MiB) to zero; the former sort's peak disk
footprint is 63,816 KiB, a different measure. All 1,471,680 relation rows are still
examined and 247,914 shared blocks touched. Buffer reads do not prove disk reads.

The investigation's warm large broad routed range is 1,846–1,879 → 1,656–1,705 ms.
Sequential measurements straddle a background memory release, so this full
latency difference cannot be attributed solely to the key. Its paired post-write
large broad SQL observation is 1,485 → 1,237 ms with identical shared-block totals
and 15,973 → zero temporary writes; routed ranges are 1,873–1,918 → 1,722–1,795 ms.
The modest latency gain leaves the query-plus-projection target unmet. Filtered
recurring/common aggregation still spills 668 temporary blocks, and readiness and
uncertainty scans remain material. No gain is claimed for the selective shapes.

Reuse the investigation's 36 compact-key aggregate parity receipts (plus 90 for
rejected alternatives), 50 complete routed response pairs and unchanged lower-seam
time/text/filter evidence. Its five
alternating four-read rounds all succeed, but summed makespans are 16.15 → 16.65 s;
the compact key demonstrates no concurrency/throughput improvement. No attributable
PostgreSQL CPU/private-memory reduction is established. These read-only prototype
results motivated the delivery change; they are not independent review or I918-C
acceptance. The bounded production repeat, commands, response parity and plans are
retained separately in
`/home/stef/.local/share/wayfarer-investigations/918-compact-cell-20261024/`.

That production repeat has 48/48 successful routed reads with no-store and eight
exact overview response pairs (only response observation time excluded). Its eight
reconstructed baseline commands equal the retained previous production SQL exactly;
the candidate commands are unchanged production SQL. The same seed/versions/settings
and 1,471,680-row shape are used. Three routed observations per shape and one separate
natural plan replay suffice here; the unaffected matrix and concurrency are reused.

| Fresh warm routed overview, ms | Previous grouping | Compact key |
| --- | ---: | ---: |
| Broad, 210,240 rows | 560–590 | 517–568 |
| Broad, 1,051,200 rows | 1,928–1,956 | about 1,801 |
| Recurring/common, 1,051,200 rows | 2,241–2,287 | 2,304–2,343 |
| Tokyo/activity/area, 1,051,200 rows | 1,711–1,713 | 1,689–1,771 |
| Post-write broad, 1,051,200 rows | 1,961–2,018 | 1,570–1,765 |

The separate fresh large broad SQL replay takes 1,633 → 1,295 ms, with the same
247,918 shared blocks and 15,973 → zero temporary writes. After 500 tracked address
edits without vacuum, it takes 1,576 → 1,159 ms, with identical 247,995 shared blocks
and 15,973 → zero temporary writes; both uncertainty counts have 680 heap fetches.
The candidate retains the 40 KiB one-batch hash and 25 KiB six-row output sort.
Large recurring/common aggregation still writes 668 temporary blocks. Companion
query-plus-projection remains 2,004 → 1,688 ms for large broad and 1,801 → 1,597 ms
post-write; selective results remain variable. Serialization is below 0.4 ms.
The roughly 7% warm broad routed gain is modest and does not meet the sub-second
target. Direct-probe sampled RSS is about 300 → 301 MiB; no CPU/private database
memory or new concurrency improvement is claimed. The owned fixture is cleaned up.

A data-ready receipt proves generation/index/pending prerequisites, not performance.
The reviewed finite year-0001 capture limitation remains honestly unresolved while
UTC/viewer/detail facts are retained. Fresh exact-head independent review, applicable
CI, maintainer acceptance and disposition of the measured target misses remain
outstanding. #918 stays open. Reuse I918-A's preparation/write/storage receipts;
#604 still owns restored-population headroom, WAL/index-build/migration/recovery
qualification. Mobile/deployed acceptance and #853 C4 remain separate.

### Deferred singleton UUID follow-up

Overview now aggregates singleton IDs before retrieving their nullable UUIDs by
owner and primary key in the same SQL statement. This removes the noncovered UUID
from broad aggregation inputs, allowing the existing `IX_Location_HistoryUtc`
covering path. Every matching row still contributes to exact cell membership,
counts and extrema. Response shape, shared edges/drill-down, uncertainty,
readiness, cancellation, repeatable-read snapshot and deadlines are unchanged.
There is no application query per cell, added index or combined-scan experiment.

The compiled delivery repeat uses the same research seed and two owner scales,
plus 210,240 unrelated-owner rows. All 30 routed reads return 200/no-store, and
all five complete response pairs agree except root observation time. Three
observations per variant cover only broad and recurring/common-text overviews at
both scales and one large-owner post-write broad control. Candidate SQL is unchanged
production SQL; reconstructed baseline SQL is checked against retained commands.
Use `qualified/` in the receipt below: the initial comparison attempt failed its
strict reconstruction check on a missing final newline, was preserved, and was
corrected once with all five SQL shapes checked before seeding again.

| Fresh warm routed overview, ms | Baseline | Deferred UUID |
| --- | ---: | ---: |
| Broad, 210,240 rows | 544–568 | 254–256 |
| Broad, 1,051,200 rows | 1,871–1,874 | 1,048–1,190 |
| Recurring/common, 210,240 rows | 1,364–1,393 | 1,364–1,662 |
| Recurring/common, 1,051,200 rows | 2,334–2,374 | 2,314–2,323 |
| Post-write broad, 1,051,200 rows | 1,937–2,013 | 1,932–2,030 |

Prepared broad aggregate plans examine 210,240 / 1,051,200 owner index entries
instead of all 1,471,680 heap rows, with zero prepared index heap fetches and two
singleton UUIDs recovered. Inclusive root shared-block references fall from
247,915 → 3,847 small and 247,918 → 19,244 large. After 500 tracked address edits
without vacuum, they fall from 247,995 → 19,454, with 680 covering-scan heap fetches.
Natural aggregate execution is 483 → 165 ms small, 1,466 → 717 ms large and
1,403 → 1,092 ms post-write. These are separate diagnostics; shared-block reads
do not establish physical disk reads. Broad temporary writes remain zero, with a
32/40 KiB one-batch hash and 25 KiB six-cell output sort.

The large filtered routed control shows no material regression in this selection.
Filtered improvement is not established: the small candidate has a 1,662 ms
observation, and separate query/projection companions are slower at both sizes.
Its slow request includes slower unchanged readiness and uncertainty commands as
well as aggregation. Filtered heap access remains; large aggregate spill falls
from 668 to 494 temporary write blocks, but that separate replay takes 918 →
1,206 ms. Post-write warm routed latency overlaps baseline despite lower broad
buffer work. These observations limit any latency claim; no additional run or
optimization is authorized by this receipt.

Separate candidate query-plus-projection is 227 ms for small broad (**meets** the
one-second target), 1,028 ms for large broad, 1,611/2,562 ms for small/large
recurring/common and 1,854 ms for large post-write broad (**misses**). Serialization
is below 0.4 ms for these compact responses. Five-second completion does not waive
these misses. Full first-use/warm observations, commands, plans, typed parameters,
parity and both attempts are retained at
`/home/stef/.local/share/wayfarer-investigations/918-singleton-delivery-20261024/`.
Both guarded fixtures completed cleanup. The wider named workload, lower-seam
semantics, preparation/write/storage and four-read evidence above are reused;
there is no newly measured concurrency gain.

This is a bounded broad-query resource improvement. **I918-C and #918 remain
incomplete.** Independent exact-head review and an explicit measured maintainer
readiness decision remain required; filtered workloads and universal sub-second
performance are unresolved. No further optimization campaign is automatically
authorized. Production/mobile/deployed acceptance remains separate.


## Final protective ceiling and operating range

The 24 October 2026 maintainer decision in
[#918](https://github.com/stef-k/Wayfarer/issues/918#issuecomment-6384111392)
sets history/page and overview evaluation to fifteen seconds, retaining the separate
thirty-second selected-detail ceiling. The SQL timeout remains transaction-local;
application-wide and database-wide defaults, preparation timeouts, admission and
disconnect handling are unchanged. Prior five-second observations above retain
their original provenance.

See the [final bounded performance/resource qualification](../development/history-final-qualification.md)
for the 25-year synthetic owner, field-distribution comparisons and mixed-use
results. Sub-second target misses require an explicit measured maintainer
disposition. No automatic acceptance, production sizing, issue closure or further
optimization authorization follows from raising the protective ceiling.
