---
title: Hardware and history performance
---

# Hardware and history performance

Use this guide to plan a host for personal location history. The backend in
[#924](https://github.com/stef-k/Wayfarer/pull/924) provides three independent reads:
chronology pages, a complete map overview of the selected scope, and complete
selected-record detail. The map includes all matching records without waiting for
chronology pages; detail remains accessible independently of the current filters.
Exceptional unresolved time evidence is labelled incomplete, rather than silently
discarded. These are backend capabilities; mobile presentation and deployed-client
acceptance are separate responsibilities.

**The sub-second query-plus-projection target remains unmet for complete multi-year
overviews and combined searches.** The measured limitations still require explicit
maintainer acceptance under [#918](https://github.com/stef-k/Wayfarer/issues/918).
The evaluation budget below does not waive that target or promise normal latency.

## Representative measurements

These are local synthetic-data observations from 24 October 2026. Times in the
table include bearer authentication, MVC routing, database reads, full response
projection and buffered JSON through TestServer. They exclude real TCP, TLS and
reverse-proxy costs. Query-plus-projection companions in the reports are separate
executions and must not be added to, or subtracted from, routed times.

| Evidence and read | Owner records and selection | Measured routed time |
| --- | --- | --- |
| Latest correction: complete capture-date overview | 2,628,000; worldwide, capture days 2001-01-01–2025-12-31, no text/activity/hour filter | Median **6.88 → 4.98 s**; three measured reads per configuration after warmup |
| Earlier: complete UTC overview | 210,240 / 1,051,200; whole period, no additional filters | Warm **0.248–0.253 / 0.973–1.017 s** |
| Earlier: complete filtered overviews | 2,628,000; capture-date range with Notes/address text and/or recurring local 11:00–15:00 | Warm **4.21–8.70 s**, depending on selection |
| Earlier: first chronology page | 2,628,000; 100 items, full UTC period / recurring hours plus common phrase | **0.938–0.988 / 5.146–5.363 s** |
| Earlier: selected detail | 2,628,000; one complete 2.3 KB response. Older 1,051,200-record fixture: approximately 1.2 MB long detail | **35–49 ms**; older long detail **43–51 ms** |
| Earlier: ordinary reads during heavy history work | 2,628,000 and 210,240-record history owners; activity catalog and ordinary date-existence reads | **30–42 ms** |

The latest comparison changes only the UTC index's covered status column. Capture
selection uses Europe/Athens as viewer zone and the default worldwide grid of 16;
it contains 2,627,999 definite matches and zero uncertain candidates. One early
Honolulu event falls on the preceding capture-local date. Complete results agree
before/after. The corrected range was 4.94–5.04 s; **27.6% lower median** describes
this small, fixed-order sample. A single full-period UTC control was slower,
2.59 → 2.87 s, so this is not a general speedup or nonregression guarantee. The
latest independent review observed capture reads of 8.55 and 4.62 s at the unchanged
implementation head, illustrating variability rather than a new paired estimate.
See the [committed correction report](../development/history-final-qualification.md#status-coverage-delivery)
and [latest independent review](https://github.com/stef-k/Wayfarer/pull/924#pullrequestreview-5639453171).

**All rows marked earlier predate the status-coverage correction.** Smaller broad
overview timings come from the independently reviewed #923 implementation. The
2.628-million-record qualification uses distinct Notes and all eleven searchable
fields; its text markers match about one record in seven and its common phrase
`cafe station` about half. Notes/address selections use the same combined shipped
predicate, not separate field-specific searches. Geography is mostly dense Athens
with Tokyo, New York, London and sparse distant records. These distributions and
repeated coordinates limit extrapolation to other histories.

In the earlier bounded mixed trial, three heavy overviews completed in 4.00–7.98 s,
ten keyed ingestions persisted in 179–508 ms, and independent detail took 43 ms.
Two excess attempts received controlled admission 503 responses; a deliberate
disconnect cancelled work and released its resources. All 49 measured history
reads completed successfully; no natural deadline failure occurred. An earlier
independent replay observed an 8.76 s filtered overview and ingestion up to 678 ms.
Those samples demonstrate modest mixed use, not sustained throughput or current
corrected-index concurrency gains. Sources:
[earlier scale, detail and mixed-use report](../development/history-final-qualification.md#new-routed-latency-and-target-results),
[smaller-scale provenance](../development/history-final-qualification.md#reused-smaller-scale-evidence)
and [earlier independent replay](https://github.com/stef-k/Wayfarer/pull/924#pullrequestreview-5638656105).

## Measured environment and hardware planning

The test host ran Ubuntu 24.04.5 under Linux/WSL2 on an Intel i7-10700K with
16 logical CPUs and approximately 15.6 GiB RAM. It used Debug assemblies,
.NET SDK 10.0.113/runtime 10.0.13 and PostgreSQL 18.6/PostGIS 3.6.4, with
`shared_buffers=128 MB`, `work_mem=4 MB`, two parallel workers per gather and JIT
enabled. Fixture loading and vacuuming preceded prepared reads; OS caches were
warm, including first-use observations. No disk-cold or isolated storage-device
performance was measured. This shared workstation is a test environment, not a
minimum specification or a guarantee for another machine.

The following are **planning projections**, not measured alternative-host results:

- **CPU:** multi-year filtering, readiness and aggregation still scan substantial
  data. Faster cores and spare CPU for simultaneous ordinary work may help; neither
  clock speed nor core count predicts a proportional latency reduction.
- **RAM/cache:** leave memory for PostgreSQL, the application, OS cache and other
  services. More cache may reduce storage pressure, but cannot eliminate repeated
  processing. `work_mem` applies per operation/session and is not a total-memory
  cap; increasing it cannot cure every observed cost, including zero-spill scans.
- **SSD and free space:** an SSD is a reasonable planning choice for database,
  temporary-file and WAL I/O, with additional space for backups and index rebuilds.
  No SSD comparison or physical-I/O bottleneck was established; faster storage
  does not imply sub-second warm searches.

There is no qualified minimum CPU/RAM/SSD specification, Raspberry Pi performance
qualification or 25-simultaneous-user guarantee. ARM64 deployment support alone
does not supply those results. Production hardware, cold caches, Release builds,
network/proxy overhead, sustained writes, percentiles and arbitrary content remain
unqualified. The fixture's storage footprint is not a production sizing formula.

## Evaluation budgets, admission and failures

Chronology and overview have a **15-second evaluation budget**; selected detail
has a separate **30-second budget**. Evaluation covers token lookup, SQL,
projection and complete buffered JSON before success. Remaining-time SQL timeout
is transaction-local, and disconnect cancellation reaches the ongoing work.
The budget does not bound network delivery time; bounded rollback and disposal
may follow a failed evaluation.

Admission is nonwaiting and **per application process**: chronology/overview share
two concurrent reads per owner and four globally; detail has an independent pool
of four per owner and sixteen globally. Ingestion uses separate admission. These
are protective limits, not user capacity or a cluster-wide bound across replicas.

Owner saturation returns 429 `historyOwnerAdmissionExceeded`; global saturation
returns 503 `historyAdmissionUnavailable`, with global precedence when both apply.
Both carry `Retry-After: 5`. Deadline exhaustion returns 503
`historyBudgetExceeded`, without partial successful items, cells or coverage.
Pending required capture/text preparation returns 503 `historyPreparationRequired`;
transient store failure returns 503 `historyStoreUnavailable`. Successes and errors are
`no-store`. A cancelled client receives no success guarantee; resources are released.
Clients should honor admission retry hints. See the
[API limits and error contract](../development/api.md#preparation-and-protective-limits).

## Upgrade planning

Follow the [history preparation runbook](../maintainer/history-preparation.md)
and the installation's [supported update/recovery workflow](operations.md).
Existing histories require explicit resumable derivation preparation; ordinary
startup and reads do not backfill them. Dependent capture/text operations remain
gated until their required generation is ready.

The status-coverage migration replaces the history UTC index in an ordinary
transaction. **Quiesce all application/read/import/enrichment traffic** for this
replacement, with a verified recovery set. Its lock blocks table access through
commit. Allow the old and replacement indexes, build-sort temporary files, WAL
and recovery reserve together. The local 4.61 s actual upgrade and approximately
32 MiB final index growth are observations, not downtime or peak-space bounds.
If completion is ambiguous, inspect migration history and the physical index before
retrying. See the [index replacement requirements](../maintainer/history-preparation.md#utc-index-status-coverage-correction).

Actual restored-population headroom, downtime and recovery remain unqualified under
[#604](https://github.com/stef-k/Wayfarer/issues/604). Backend acceptance does not
accept a production deployment or mobile/native/deployed-client behavior. Those
owners retain their separate responsibilities.

[Self-hosting guide](index.md) · [Documentation home](../README.md)
