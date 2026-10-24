# Final #918 performance and resource qualification

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
Tests/builds finish before timed reads. TestServer excludes real TCP/TLS/proxy cost.

One owner has **2,628,000 five-minute captures**, approximately 25 years from
2001-01-01 through 2025-12-25. A 210,240-row owner reuses the earlier small-fixture
shape for mixed use; a third owner receives ten new keyed captures. Synthetic
prepared inserts are not backfill/preparation throughput measurements. The seed
checks 220 samples per seeded owner against production time/text derivation,
including observations spread across the date range.

Notes are distinct per record, with changing diary/address vocabulary and all eleven
searchable fields populated. Geography is about 80% dense Athens, 10% Tokyo, 5% New
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
