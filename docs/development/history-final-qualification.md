# Final #918 performance and resource qualification

The [status-coverage delivery](#status-coverage-delivery) below adds the final bounded
index correction to the original qualification. Earlier index/source claims and
timings retain their historical revisions. The earlier overall acceptance
recommendation has **not** been accepted by the maintainer; #918 and the sub-second
target remain open pending an explicit performance decision.

This bounded local qualification supports a measured readiness decision. The
maintainer-approved **15-second history/page and overview evaluation ceiling**
retains the independent **30-second selected-detail budget**, existing admission,
disconnect cancellation and truthful failure responses. Remaining-time PostgreSQL
statement cancellation is transaction-local; general application/database timeouts
are unchanged. The **under-one-second query-plus-projection target remains in force**.

## Source and evidence

Verified remote main/base: `d94b6a35e99fc4acf162ee3cb30a49d263f5ddfd`.
The isolated branch is `fix/918-history-deadline-final`. Only the controller's
collection duration changes production behavior; predicates, completeness,
preparation/readiness, indexes, admission and detail behavior retain their source.
The [maintainer decision](https://github.com/stef-k/Wayfarer/issues/918#issuecomment-6384111392)
is recorded in the live contract. Fresh independent review and maintainer
acceptance remain required; this report does not close #918.

Evidence root:
`/home/stef/.local/share/wayfarer-investigations/918-final-20261024/`.
It preserves copied/adapted probe source, commands, assembly hashes, full responses,
routed/status/byte receipts, separate stages, parameterized SQL/natural plans,
resource samples, strict summaries, tests and cleanup receipts. Earlier evidence
folders and shared checkouts remain unchanged. No production access occurs.

## Environment and method

Ubuntu 24.04.5 on Linux/WSL2, i7-10700K (16 logical CPUs), approximately 15.6 GiB RAM;
.NET SDK 10.0.113/runtime 10.0.13, Debug application/test assemblies, PostgreSQL
18.6/PostGIS 3.6.4. Settings remain shared_buffers=128 MB, work_mem=4 MB, two parallel
workers per gather and JIT enabled. No global tuning, cache flush or infrastructure
addition. Actual bearer MVC routes, full DTOs and buffered JSON run through the
existing TestServer host and guarded disposable PostgreSQL migration fixture.
One initial setup exceeded the inherited 600-second insert command limit before
any timed reads; its receipt is retained in `setup-failed/`. The one repaired setup
bulk-loads with secondary Location indexes deferred, then restores and compares
their exact catalog definitions before VACUUM ANALYZE. This affects fixture loading
and index packing, not production schema or runtime settings. A failed preflight
on the constraint-backed alternate key is preserved separately; the corrected
selector excludes constraint-backed indexes, which stay in place throughout. Tests/builds finish
before timed reads. TestServer excludes real TCP/TLS/proxy cost.

One owner has **2,628,000 five-minute captures**, approximately 25 years from
2001-01-01 through 2025-12-25. A 210,240-row owner reuses the earlier small-fixture
shape for mixed use; a third owner receives ten new keyed captures. Synthetic
prepared inserts are not backfill/preparation throughput measurements. The seed
checks 220 samples per seeded owner against production time/text derivation,
including observations spread across the date range.

Notes are distinct within each owner; owners share numbered templates. Each row
has varied diary/address vocabulary and all eleven searchable fields populated. Geography is about 80% dense Athens, 10% Tokyo, 5% New
York and 5% London, plus sparse Sydney/Honolulu records. Activities vary, including
unassigned records. Coordinates and city sequences are repetitive and do not model
real travel continuity or arbitrary global spread. Rich-text/address lengths are
synthetic; the earlier complete large-detail witness is reused. Historical time
rules derive from current coordinate mapping; original device zones are not stored.

Equal-length ten-character markers occur once every seven records: `diaryprobe`
only in Notes, `placeprobe` only in Address, and `jointprobe` in both on the same row.
Each selection uses the **same shipped Notes OR address/place search predicate** on
HistorySearchText. These compare controlled content distributions, not separate
field-specific SQL predicates; matching both fields counts a record once.
The retained common phrase `cafe station` matches approximately half the owner.

Ten complete-overview scopes cover UTC broad control, capture-date range alone,
date+each text marker, date+recurring capture-local 11:00–15:00, each marker+hours,
and common phrase+hours. Each has one first-use and two warm routed observations;
first-use follows seed/VACUUM ANALYZE and warm OS cache, not cold disk. Separate
companions measure readiness, whole query/materialization/projection and serving
serialization. Captured-command plans are separate diagnostic executions.
EF ExecuteReader spans belong to the actual request; reader-lifetime observations
are a separate phase and must not be mislabeled as total SQL execution time.

Representative broad/combined chronology and direct detail run twice each. One
500-row tracked address edit precedes all ten post-write overviews without explicit
vacuum; autovacuum is disabled only on this owned disposable table. No repeated
optimization cycle, exhaustive matrix, field-specific API or sampled result occurs.

## Reused smaller-scale evidence

The tree of independently reviewed PR #923 head
`c74ffbd0ce486afaa5f16768b87f61e9530f7bae` exactly equals this base tree
(`e9c8a644cb4b43e2f6bc97a39a4730e972e0d1f8`). Its receipt at
`923-independent-review-20261024T113146Z/` under the investigation parent retains:

| Earlier warm routed overview | Latency ms | Separate query+projection ms |
| --- | ---: | ---: |
| Broad, 210,240 records | 248–253 | 217 |
| Broad, 1,051,200 records | 973–1,017 | 968 |
| Recurring/common, 1,051,200 records | 2,279 | 2,319 |
| Post-write broad, 1,051,200 records | 980 | 997 |

These are reused observations at the former five-second ceiling. The same review
retains slower delivery observations: large broad query/projection 1,028 ms,
recurring/common 1,611/2,562 ms small/large and post-write broad 1,854 ms. Do not
replace that variability with the faster review selection. Its broad plans use the
existing covering index, with zero prepared heap fetches/temp writes; filtered
large aggregation still spills 494 temporary 8 KiB blocks in that replay.

The earlier `918-performance-20261024/after/` receipt supplies wider day/month,
rare/absent/short/punctuation/Tokyo, page and complete ~1.2 MB detail controls,
and twenty successful four-read observations. Those timings predate the later
compact-group/deferred-UUID changes and are historical workload/correctness evidence,
not current-head speed or concurrency gains. I918-A preparation/storage/write-cost
receipts are reused; no new preparation throughput or production sizing is claimed.

## New routed latency and target results

All **49/49** measured history requests returned complete buffered 200 responses
with no-store: 30 prepared overviews, six page/detail controls, ten post-write
overviews and three completed mixed heavy reads. Natural budget failures: **0**.
Each overview retained complete membership and zero unresolved candidates for this
prepared synthetic fixture. All first responses are retained; nothing is sampled.

| Complete overview selection | Known matches | First ms | Warm ms (two) | Separate query+projection ms | Post-write ms (one) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Full UTC period control | 2,628,000 | 3,010 | 2,438–2,513 | 2,422 | 2,457 |
| Capture date range alone | 2,627,999 | 7,410 | 7,005–7,279 | 7,019 | 6,809 |
| Date + text, Notes marker | 375,428 | 8,512 | 8,232–8,696 | 8,558 | 8,109 |
| Date + text, Address marker | 375,429 | 8,034 | 7,679–7,938 | 7,664 | 7,959 |
| Date + text, both-fields marker | 375,428 | 4,587 | 4,527–4,534 | 4,541 | 4,700 |
| Date + recurring capture hours | 438,010 | 4,877 | 4,719–4,719 | 4,705 | 4,701 |
| Date + Notes marker + hours | 62,571 | 6,760 | 6,632–6,953 | 7,027 | 6,747 |
| Date + Address marker + hours | 62,575 | 6,451 | 6,396–6,496 | 6,303 | 6,460 |
| Date + both-fields marker + hours | 62,574 | 4,274 | 4,210–4,337 | 4,180 | 4,140 |
| Date + common phrase + hours | 219,002 | 4,992 | 5,125–5,168 | 5,037 | 5,110 |

The one-record UTC/capture-date difference is correct: the early Honolulu event
falls on 31 December 2000 locally. Raw marker populations are 375,428/375,429/375,429;
the capture-date selection excludes that Honolulu both-fields row. Hours differ by
at most four matches. Geography/cell counts and lexical terms also differ slightly.
Equal selectivity does not isolate a field's CPU cost; no Notes-only/address-only
predicate or independent field-performance conclusion is implied.

Every new overview companion **misses** the one-second target. Overview responses
are only 2,597–3,552 bytes and companion serialization is 0.048–0.248 ms.
Readiness is included in query/projection, normally 746–900 ms for capture/text
selections. No subtraction of separate EXPLAIN/companion measurements is valid.

The broad chronology control returns 100 items in 988/938 ms (71,646 bytes), with
935 ms query/projection: **target met in that companion**, with limited headroom.
Combined recurring/common chronology takes 5,146/5,363 ms (71,671 bytes), with
5,198 ms query/projection: **target missed**. Selected detail takes 49/35 ms
(2,303 bytes), with 6.8 ms query/projection. It remains independent and complete.
The earlier allowed-large-detail witness is reused; it is not freshly repeated at
this scale because selected-detail work is owner/ID bounded and unchanged.

The tracked 500-row Address edit takes 929 ms through production derivation and
persistence. No explicit vacuum follows. All ten post-write overviews succeed,
with unchanged counts, but none reaches the latency target. This is one write
observation, not ingestion throughput, sustained churn or a migration-cost result.

## Plans and remaining cost

Natural captured-command replays keep parameters separate and retain exact SQL.
Broad aggregation uses the existing covering history index over all 2,628,000 owner
entries, zero prepared heap fetches, a 40 KiB one-batch hash and zero temporary
writes. The independent broad uncertainty count also scans all owner entries:
899 ms diagnostic execution, alongside 1,767 ms aggregate execution.

| Separate aggregate diagnostic | Execution ms | Root shared read blocks | Temp read/write 8 KiB blocks |
| --- | ---: | ---: | ---: |
| Prepared broad UTC | 1,767 | 47,792 | 0 / 0 |
| Prepared date + both-fields marker | 2,103 | 507,006 | 0 / 0 |
| Prepared marker + hours | 1,672 | 506,979 | 353 / 354 |
| Prepared common phrase + hours | 1,918 | 507,005 | 1,234 / 1,238 |
| Post-write common phrase + hours | 1,979 | 507,067 | 1,234 / 1,238 |

Filtered plans choose parallel sequential heap scans, not a field-specific search
path or a lossy text-bitmap plan in these replays. Three loops collectively examine
all 2,838,240 fixture rows. Repeated required readiness, uncertainty and match scans
remain material. Common-hours sorting uses external merge with 9,872 KiB disk
space; marker-hours uses 2,824 KiB. Root buffer/temp counters are reported once,
without adding inclusive parent/child totals. Shared reads are PostgreSQL buffer
misses and do not prove physical disk reads.

For one actual warm common-hours request, EF ExecuteReader spans are readiness
738 ms, uncertainty 2,459 ms and aggregate 1,895 ms within 5,125 ms routed total.
The Notes-marker request has uncertainty/aggregate spans of 3,372/4,459 ms within
8,696 ms. These belong to their respective routed requests, unlike plan replays.
The captured aggregate plans' JIT totals are 612–1,124 ms; parallel JIT
accounting is not directly subtractable wall time. Broad already has no spill, so
raising memory cannot address all remaining cost.

## Bounded mixed use and resources

One synchronized start admits four heavy recurring/common overviews: two for the
large owner and two for the small owner. One small-owner request is deliberately
disconnected after release; no timeout is induced in this mixed trial. Sixteen
ordinary baseline reads precede the heavy start; another sixteen run during it.
Ten distinct keyed automatic captures use separate timestamps/positions to avoid
expected duplicate/time/distance skips, with no configured external provider.

| Mixed-use work | Result | Routed latency ms |
| --- | --- | ---: |
| Heavy reads allowed to finish | 3/3 complete 200 | 4,005 small; 6,950 and 7,983 large |
| Activity catalog | 8/8 200 | 30–38 (baseline 27–54) |
| Ordinary date-existence read | 8/8 200 | 31–42 (baseline 27–47) |
| New location ingestion | 10/10 200; ten durable rows/current derivations | 179–508 |
| Independent selected detail | 1/1 complete 200 | 43 |
| Two excess history attempts | 2/2 controlled 503, historyAdmissionUnavailable | 18–20 |
| Deliberate disconnect | Client cancellation observed; permit/SQL cleanup complete | 2,480 from request start |

Both excess attempts occur with four global permits occupied, so existing global
503 precedence applies even to the already-full owner. Owner-only 429 behavior is
covered by the focused routed regression, not inferred from this mixed trial.
After all requests, collection identity buckets and other active sessions in this
owned database are zero. The guarded fixture then disposes its database.

Across all **95** HTTP observations: **92 successful 200**, **two expected admission
503**, **one deliberate cancellation**; no unexpected HTTP error or natural budget
failure. This is evidence for this modest shared use, **not capacity for 25
simultaneously active users**, sustained traffic or long-tail latency.

The pure mixed window ends at the cleanup receipt before the final diagnostic
statistics scan; `resource-window.json` preserves that cutoff. Approximately
7.91 sampled seconds account for 43.93 cluster PostgreSQL CPU core-seconds
(about 5.55 cores on average) and 3.48 application CPU seconds. Application peak
RSS is 303.6 MiB; system available memory stays at least 13,992 MiB, with no material
net swap growth in that sampled window. Cluster CPU includes background work and
sampling can miss short-lived workers. This is not isolated per-query CPU.
No PostgreSQL RSS sum or private-memory peak is claimed; shared buffers and
per-operation work_mem are not a total-memory formula. Prepared/post-write resource
phases include separate diagnostics and cannot be treated as pure request CPU.

After writes, all owners total 2,838,250 rows. Fixture table+TOAST is 4,284,825,600
bytes and all indexes 3,547,234,304 bytes, approximately 7.30 GiB combined. This
bulk-loaded, vacuumed synthetic shape lacks years of MVCC churn, arbitrary Notes
sizes, real import/enrichment skew and other production workloads. These are fixture
footprints, not a production migration delta, storage formula or headroom guarantee.

## Conditional research and narrowly justified follow-up

The bounded official-documentation search supports the measured attribution.
[Index-only scans](https://www.postgresql.org/docs/18/indexes-index-only-scans.html)
need covered values and visible pages; GIN cannot supply whole original text as an
index-only result, and duplicating wide text in B-tree payload has storage/tuple-size
costs. [Bitmap combination](https://www.postgresql.org/docs/18/indexes-bitmap-scans.html)
still visits heap rows. Increasing
[work_mem](https://www.postgresql.org/docs/18/runtime-config-resource.html)
multiplies per operation/session and cannot cure the already-zero-spill broad case.
No new index or memory setting is justified as a demonstrated sub-second fix.

One **unimplemented, nonblocking proposal** is a separately authorized A/B check of
transaction-local JIT configuration for these exact heavy reads. The observed
612–1,124 ms aggregate JIT totals justify that narrow question. PostgreSQL's
[JIT decision documentation](https://www.postgresql.org/docs/18/jit-decision.html)
explains the compilation/execution tradeoff and plan-cost thresholds. Disabling
JIT may reduce compilation but slow row processing; parallel totals are not wall
time and no gain is demonstrated here. This is not a promised one-second correction
or an authorized next optimization cycle. No JIT setting or additional performance
change is applied. Real population, restore/headroom and deployed-host qualification
remain with #604, and mobile/deployed-cohort behavior remains separately owned.

## Acceptance matrix and closure recommendation

| Classification | Observed evidence | Disposition |
| --- | --- | --- |
| 1. Targets met | New broad page query/projection 935 ms; direct detail 6.8 ms. Reused small broad overview 217 ms. Mixed ordinary reads and ingestion all under one second routed. | Accept these measured cases; retain narrow samples/headroom limits. |
| 2. Reliable behavior above the original target | All 40 new sequential/post-write overviews, both combined pages and three completed mixed heavy reads succeed; overview companions 2.42–8.56 s. | Explicit target misses, despite reliable completion inside the protective budget. |
| 3. Budget failures/demonstrated limitations | Zero natural evaluation failures. Two expected global-admission 503s, one deliberate disconnect; focused stalled SQL returns historyBudgetExceeded near 15 s without partial results. Common filtered spill and repeated full-scope scans remain. | Protection behaves correctly; admission rejection is not a successful search or capacity promise. |
| 4. Unqualified | Production/cache-cold/TCP/proxy/Release-build percentiles, sustained writes/load, 25 active users, arbitrary content/geography and native/mobile/deployed behavior. | No acceptance claim; #604 and consumer gates remain separate. |
| 5. Narrow follow-up | Optional transaction-local JIT A/B question; real deployment qualification under #604. | Proposal only, not dispatched and not a blocker supported by this run. |

**Recommend backend performance acceptance with the explicit measured latency
limitations above**, after fresh independent review, applicable exact-head CI and
maintainer acceptance. Practical observed range: complete selections over one
25-year/2.628-million-record owner finish in roughly 2.4–8.7 seconds sequentially;
the fixed mixed use preserves ordinary responsiveness and durable ingestion.
This is a documented local range, not an API range cap or a production guarantee.

The one-second goal is **not met** for the complete 25-year overviews or combined
chronology. Acceptance therefore needs an explicit maintainer disposition of those
named deviations; raising the ceiling alone does not supply it. No reachable
correctness failure, natural timeout or ordinary-work starvation in this bounded
run justifies demanding another unbounded optimization cycle. **#918 stays open**
and I918-C remains pending that disposition; this recommendation is not closure.

## Validation and next independent review

The 49-test focused selection passes with zero failures/skips: routed history,
query/cursor, predicate and actual-plan tests. The final elapsed-time assertions
on both six-second success cases pass again (2/2). The original five-second source
fails the deadline regression, as expected. Preserved superseded attempts include
test-only delay defects, the seed command timeout and constraint-index preflight;
none is presented as a product failure. Build and pending-model checks pass; no
schema/migration is added. Existing dependency/analyzer advisories remain, and no
full-suite or hosted CI success is claimed.

Complete-branch Code Guard is REVIEW only. Accepted findings retain the cohesive
routed owner-read fixture, canonical multi-API reference and chronological changelog
(including its pre-existing Fixed section). Splitting them adds no clarity to this
bounded change. All policies, thresholds, exclusions and baselines are unchanged;
no FAIL/INCOMPLETE or configuration error remains in the final checks.

The next independent review must pin the actual draft PR head, inspect the complete
seven-file delta and this acceptance matrix, verify the 15/30-second separation and
transaction-local timeout/admission/cancellation, and audit retained counts, timings,
resource cutoff, fixture/index-restore provenance and explicit target misses.
Reuse the expensive scale receipt when the final source delta proves production
query inputs unchanged. CI is only observed once for startup; its final result and
fresh independent review remain outstanding. Do not merge or close #918 here.

## Status coverage delivery

24 October 2026. Remote main/base remains
`d94b6a35e99fc4acf162ee3cb30a49d263f5ddfd`; draft #924 remains open, so this delivery
extends `fix/918-history-deadline-final`, preserving its prior head
`75870fcce3c446d530026847c20ed9b3598e62b5`. Qualified production/model/test revision:
`43a40b5aedbd267bb63821c0151617f22f9b0cf2`. Subsequent delivery changes document
these results; the final exact head is recorded on #924 and #918. The former
independent PASS at `75870fc…` is historical and does not review the new head.

Evidence:
`/home/stef/.local/share/wayfarer-investigations/918-status-delivery-20261024/`.
It retains exact source/assembly hashes, commands, all complete responses, captured
SQL/typed parameters, natural plans/results, write/persistence/cleanup receipts,
migration/failure checks and resource samples. It reuses the authorized retained
database, OID 1708055, owner postgres, with 2,628,000 large-owner and 210,240
small-owner rows; identity, candidate definition and zero other clients were
verified before restoring the original through the retained guarded script.
No population reconstruction or production database is used.

### Correction and migration

The supported experiment found missing `HistoryTimeStatus` coverage in the UTC
index. This delivery appends only that INCLUDE column; keys, existing payload and
absence of a predicate are preserved. The exact resulting definition is:

```sql
CREATE INDEX "IX_Location_HistoryUtc" ON public."Locations"
USING btree ("UserId", "LocalTimestamp", "Id")
INCLUDE ("Coordinates", "CaptureLocalTimestamp", "ActivityTypeId", "HistoryTimeStatus");
```

The EF configuration, snapshot and migration
`20261024172605_LocationHistoryUtcStatusCoverage` agree. Physical readiness accepts
the original and corrected exact payloads, preserving the existing preparation
prerequisite and recognizing the added coverage; it still checks valid/ready state,
method, keys, attributes and no predicate. This metadata change does not alter
preparation queries, generations or completeness. Migration/model state owns
installation of the corrected index. No filter, projection, uncertainty policy,
admission, JIT/memory setting or timeout changes; the approved 15-second collections,
30-second detail and transaction-local remaining-time SQL timeout remain.

The actual EF migration was applied **from the restored original index** on the
large fixture in 4,608 ms. Fresh-chain creation and Down/Up are also tested. The
ordinary transaction holds DROP's table access lock through rebuild and history
commit. A deliberately invalid CREATE after DROP proves transaction rollback
restores the original index and leaves the correction unapplied. This explicit
failure injection uses the same replacement transaction layout as generated SQL;
it is separate from the successful real EF upgrade. No concurrent DDL, transaction
suppression or migration framework is added. See the
[maintenance requirements](../maintainer/history-preparation.md#utc-index-status-coverage-correction).

Immediately before/after the real migration, index size is 428,507,136 →
462,086,144 bytes (+32.02 MiB). The original had grown 16 KiB during the preceding
write trial; the supported experiment's prepared-index delta remains +32.04 MiB.
Database build-temp writes increase by 384.10 MiB, and shared-cluster WAL insert LSN
advances 395.61 MiB. Sampled filesystem growth during the candidate process reaches
797.18 MiB with at least 701.77 GiB free. Sampling includes other phases and shared
activity; it can miss peaks. Allow the old and replacement indexes, sort temp, WAL
and recovery reserve simultaneously. These observations do not qualify production
headroom, downtime or recovery; #604 retains that work.

### Fixed routed and write comparison

Both configurations execute the same freshly built Debug MVC/TestServer application,
settings, bearer owner, SQL and typed parameters. Capture scope is days 2001-01-01
through 2025-12-31, Europe/Athens, worldwide/default grid 16, with no text, activity
or recurring-hour filter. One warmup precedes exactly three measured capture reads;
one warmup precedes one UTC control per configuration. Both prepared phases receive
the same explicit VACUUM, without ANALYZE, cache flush or planner changes. Original
then corrected order, index builds, warm OS caches and a shared workstation limit
causal/production extrapolation. TestServer includes real bearer/MVC/DTO/buffering,
excluding TCP/TLS/proxy; diagnostic statements execute separately afterwards.

| Routed complete overview | Original ms | Corrected ms |
| --- | ---: | ---: |
| Capture warmup, excluded | 9,698 | 5,748 |
| Capture measured observations | 6,881 / 6,714 / 7,298 | 4,940 / 4,980 / 5,044 |
| Capture median | 6,881 | 4,980 (**27.6% lower**) |
| UTC control, one measured | 2,590 | 2,865 |
| Candidate after 500 indexed-fact edits, no vacuum | — | 4,531 |

All 13 complete responses return 200/no-store. Complete capture responses match
after removing only root observedAtUtc: 2,627,999 definite matches, zero uncertain
candidates and identical cells, extrema and singleton identity. UTC responses also
match completely apart from observation time, retaining 2,628,000 matches. The UTC
control is 275 ms slower in this single observation; no latency improvement or
stable regression estimate is established for that already efficient path.
Captured SQL and typed parameters match exactly across the two configurations.

For each configuration, one warmup and three measured 20-row batches use the
existing production tracked SaveChanges boundary. Inserts include canonical data
and current derivations; updates change event time and ActivityTypeId, affecting
indexed facts. Fresh contexts verify all persisted rows, current derived versions,
resolved status and revised capture seconds before deleting only those task-owned
rows/users. These are EF persistence timings, without routed/provider overhead.

| 20-row batch | Original median (range), ms | Corrected median (range), ms |
| --- | ---: | ---: |
| Insert | 40.6 (39.4–51.1) | 39.9 (39.0–42.0) |
| Update | 39.2 (37.2–40.0) | 43.5 (42.0–45.5) |

Observed update overhead is 4.3 ms per batch, about 11%; inserts show no increase
in this sample. Counts/data/order/cache treatment are comparable, with no cache
reset or interleaving. Do not extrapolate these small samples to sustained writes,
import throughput, production percentiles or general write capacity.

### Separate diagnostics, post-write evidence and limits

| Diagnostic execution, one per statement | Original ms | Corrected ms | Post-write corrected ms |
| --- | ---: | ---: | ---: |
| Preparation pending | 932 | 780 | 683 |
| Uncertainty | 2,802 | 1,846 | 1,518 |
| Aggregation | 4,100 | 2,854 | 2,378 |

Preparation still performs its complete heap scan. Both corrected membership scans
use IX_Location_HistoryUtc index-only, with zero prepared heap fetches. Post-write
diagnostics show 663 heap fetches in each scan, so the separate 4,531-ms routed
observation survives loss of fully vacuumed visibility. This is not a paired
post-write speedup claim or sustained-churn qualification. All three complete SQL
results match before/after and post-write, including preparation=false, uncertainty=0,
every cell field and singleton UUID. Do not sum diagnostic times into endpoint time.

The watchdog interrupted the candidate process **after all routed/write samples**,
mistaking initializing parallel workers with temporarily empty application names
for other clients. The receipt and original runner are retained. The repaired
watchdog checks actual client backends; one resume completed only the unfinished
post-write diagnostics and cleanup. No routed measurement or write trial was rerun.
The earlier partial pending plan remains separate. Source/assembly identities,
all 13 response receipts and the fixed sample counts are retained.

The 500 large-owner edits change ActivityTypeId, leaving this unfiltered overview's
membership unchanged. Original values were saved before writing and restored after
the resumed diagnostics. Whole-row logical checksums of all 500 match before/after.
All 160 temporary inserted rows and both temporary users are deleted; sequence
advances and physical dead tuples are retained. Final population is 2,838,240,
both generations remain ready, other index definitions are unchanged, no other
clients remain and no final VACUUM was run. The candidate index is installed,
valid/ready, with its migration recorded. Preserve this fixture for independent
review. The artifact's retention/cleanup instructions explain inspection and any
later explicitly authorized baseline restoration; do not rebuild or drop it here.

The one-second target is **not achieved**. The remaining preparation pass, broad
uncertainty work and cell processing remain material; no preparation optimization,
JIT/memory experiment, full filter/concurrency matrix or second index candidate is
part of this delivery. Reuse I918-A preparation/semantic/writer evidence, I918-B
correctness/composition, the prior full filter/detail/mixed-use matrix and historical
independent #924 review for their unchanged scope. Historical latency/concurrency
numbers are not fresh corrected-index qualification. Production/cold-disk/Release/
TCP/proxy/sustained-load/mobile acceptance remains unqualified. The earlier overall
performance recommendation remains unaccepted; #918 stays open for the maintainer.

### Validation and fresh independent review scope

All 56 focused tests pass with zero skips, including migration/model/row-preservation,
readiness, routed history, query/cursor, predicate and actual-plan checks. The first
run's nine readiness failures are retained as a corrected branch regression, not
environment debt. Application/test build, pending-model check, generated migration
transaction inspection, version consistency and diff hygiene pass. Existing
dependency/analyzer advisories remain; no full-suite or hosted CI success is claimed.

Fresh independent review must pin the new exact live #924 base/head and inspect
the complete combined change: the prior deadline/tests plus this model/migration,
exact dual-shape readiness metadata, focused regression and documentation. Audit
original-index restoration, real upgrade/failure rollback, write overhead, complete
SQL/response parity, interrupted-diagnostic recovery, row restoration, retained
fixture and explicit target misses. The previous review does not cover this head.
Applicable exact-head CI, independent review and maintainer acceptance remain
outstanding. Implementation observes CI startup once, without polling or reruns;
it does not merge, waive the target or close #918.
