# Private Timeline calendar (#953 W1/W2)

The private chronological page uses the accepted W2 calendar, filter/record reads,
independent summary/path and tab restoration. W1 GET wire remains available and
unchanged for its original day/month/year unfiltered consumers. No feature flag,
migration, provider call, cache, worker or read repair is introduced. Nonempty search
requires existing text qualification/preparation; ordinary reads do not. Release,
deployment, mobile and independent final review remain separate authorities.

## W2 private transport and page ownership

All five operations have explicit antiforgery-protected cookie POST actions:
`period`, `statistics`, `analysis`, `records`, `availability` below
`/User/Timeline/calendar`. The browser sends form-urlencoded bodies, same-origin
credentials and `RequestVerificationToken`, never filters in URLs. Scalar allowlists,
duplicates, transport, 8,192-byte aggregate and HistoryQuery normalization are shared.
POSTs reject URL queries. Only records accepts pageSize/continuation; only analysis
accepts connection controls. Availability accepts year/month/basis/zone/non-date
filters, and evaluates the whole month even beyond the navigation horizon.

Presets require dateType/year/month/day; Week additionally requires frozen
weekStartsOn (1=Monday…7=Sunday). Custom requires fromDate/throughDate and rejects
preset components. Both inclusive endpoints must be in 1900–2100, From≤Through,
and From≤basis horizon. Full Through is retained, including future dates. Availability
uses hard bounds without the browsing horizon. Existing HistoryQuery days, serving
offsets and capture facts own membership; no database timezone substitute is used.

POST envelopes use private-calendar/2, subjectId, canonical selection/HistoryScope,
timeRules, matchingRules (HistoryCursor.Rules and conditional textVersion), observationId,
asOfUtc and response-snapshot. Analysis retains stored-history-connections/1 with
calendarContract=private-calendar/2. Records reuse HistoryReadService.PageAsync and
HistoryCursor, order eventUtc-desc,id-desc, default 100/max 500, items and
nextContinuation. Each page discloses live-request consistency; exhaustion/dedup do
not establish a multi-request snapshot. Availability returns whole-month selection,
displayedMonth, sorted presentDates and conservative hasUnresolvedCandidates.
Unmapped known viewer matches can establish presence; unknown marks never mean empty.

HistoryPredicate now exposes owner-only source restriction and full three-valued
membership. Reliable text/activity exclusion is coalesced false; analytical SQL
restricts by owner/event/envelope only. Excluded intermediates and mixed ties remain
barriers. Owner invalid event-time EXISTS also ignores filters: an excluded unplaceable
row still prevents establishing adjacency. Selective filters do not reduce that source
scan. Collections keep their existing reliable-filter restriction and membership.

The concrete TimelinePage serializes period then analysis even after period failure,
with optional records/availability/geography queued afterwards. Epoch, analysis/month
and detail generations fence results; aborted fetches do not certify lease release.
429/503 Retry-After is displayed for deliberate retry. The first successful response
binds effective normalized filters and serving identities; conflicts invalidate the
group for explicit revalidation. Matching readiness failures retain filters.

The URL adapter uses viewType, public date components, basis/zone, frozen week start
and optionally an independently random filterContext. One subject-bound versioned
sessionStorage slot holds applied raw filters and small exact return intent only.
Persistence/read-back precedes filter commitments. A date-only URL never adopts old
filters; lost/foreign/malformed context blocks dependent reads pending explicit
recovery. Record pages/results/Notes/availability are never retained there. Observed
account/session loss clears this intent and publications, without touching protected
Location originals/drafts/attempts. Undetected logout and browser recovery cannot
provide immediate erasure. Rules/basis use one versioned whole localStorage preference
per origin/subject; rule draft precision is independent of display units and queries.

Calendar dialogs use immediate Bootstrap transitions so activation during opening
can close deterministically and return focus. Keyboard movement browses only; Day/Week
activation commits directly. Custom and filters each own Apply/Cancel. Query editors
exclude one another. Shared protected Location activity/form/delete owners remain the
only write authority; marker/page arrays never authorize mutations. Geographic first/
last clocks resolve actual normalized hierarchy endpoint identities, including ties.

Production request timing logs method/path only. Controller errors log fixed type,
never body/scope/SQL parameters. EF sensitive-data logging is not enabled. The
routed privacy assertion and managed-host log check qualify the exercised paths,
not arbitrary external proxy/analytics infrastructure.

## Routes and scope

Active cookie users with role `User` can GET `/User/Timeline/calendar/period`,
`statistics` and `analysis`. No owner parameter is accepted. These responses and
errors are private/no-store, including authentication/authorization short circuits.
Bearer history collections remain bearer-only; existing protected writes and
selected detail keep their authority.

Every route accepts `dateType=day|month|year`, Gregorian `year/month/day` in
1900–2100, `timeBasis=capture|viewer` and mandatory explicit `viewerZone`.
Omitted basis means capture; omitted date means Today in the validated viewer zone.
An explicit year defaults missing month/day to 1. Dates must be valid: navigation
clamps before constructing another month/year, while malformed supplied dates fail.
The serving TZDB canonicalizes zone aliases and freezes that zone in the response.
Missing/unknown zones fail; detected UTC is valid. No stored device/import label
supplies membership. Periods are half-open civil boundaries using `HistoryQuery`
`days`; DST folds retain both instants and gaps invent none.

`selection` echoes selected civil components; `scope` echoes exact normalized
history membership. `timeRules` identifies serving time semantics/TZDB.
`subjectId`, response-local `observationId`, `asOfUtc` and `consistency=response-snapshot`
attribute one read-only repeatable-read observation. `freshness=current-at-observation`
is an as-of statement, not a sync token, global revision or continuing-currentness
promise. Separate responses do not share a snapshot. The page fences the entire
publication group and revalidate conflicting serving rules.

Navigation returns viewer-zone Today, the basis-specific horizon and previous/next
civil selections. Viewer horizon is viewer Today; capture uses the conservative
maximum offset across serving TZDB zones. Empty past periods stay reachable.
Future navigation is policy, not a stored-data cutoff or proof of absent history.

## Period and statistics

`period` sequences counts, rich markers, summary, actual endpoint facts, existence
and navigation under one collection permit. `statistics` deliberately reads the
same geographic hierarchy and endpoint facts, without markers. Both use the exact
same `HistoryPredicate` membership before any aggregation or display sampling.
Decimal-string counts distinguish definite known/mapped/unmapped matches from
unresolved candidates. Existence is `present`, `absent` or `unknown`; candidates
cannot prove absence. Statistics count matching unmapped records too.

Day markers are unsampled. Week/Custom/Month/Year with more than 3,000 definite matches select
newest five per geohash-5 cell, capped at 3,000 with deterministic event/ID ties.
Only valid positions enter markers. The shared `HistoryDetail` allowlist preserves
rich stored fields, normalized Notes, unavailable-field reasons, canonical event
instants and qualified capture/viewer facts. Original clock/zone evidence stays
separate. Group normalization, home detection and representative rules reuse
`LocationStatsService`; invalid positions contribute no representative coordinate.
First/last endpoint facts use actual event/ID order and normalized hierarchy keys.
An average geographic point never supplies endpoint capture time.

## Connections and optional drawing

`analysis` additionally accepts positive finite binary64 `maximumGapSeconds`
(default 7200), `speedEnabled` (default true), `maximumSpeedMetresPerSecond`
(default 350) and `includeGeometry` (default false). Invalid/repeated/unknown
parameters return 400 without supplied content. No additional numeric product cap
or unit rounding applies. The page converts explicit hours/minutes/km/h edits once and
preserves untouched canonical precision.

The contract is `stored-history-connections/1`, source
`effective-stored-owner-history`. All valid-event owner records inside the
conservative serving-offset envelope are streamed in event/ID order through a raw
Npgsql reader. Exact membership is projected, including false/null barriers.
There is no filtered page, row cap, centroid, marker sample, EF tracking or retry
buffer in this analytical stream. The same snapshot checks unplaceable owner time.
That makes adjacency unknown globally; independent matching counts remain useful.
Finite unresolved membership/invalid positions break touching edges and preserve
qualified islands. Whole coincident qualified ties supply one position; mixed or
disagreeing groups break touching edges. Record counts remain separate.

Both endpoints must match, elapsed time must be positive and at most the applied
gap, and shortest great-circle distance divided by elapsed seconds must not exceed
the enabled speed ceiling. Equality passes; Off removes only speed. Radius is
6,371,008.8 m. Bounded radians, clamped roundoff and compensated edge addition keep
finite arithmetic. Original eligible edges are counted once before drawing.
Canonical longitudes and render-only antimeridian seam vertices preserve breaks.
There is no road routing, actual-travel bound, active time or midnight allocation.

`coverage=complete|partial|unknown` is independent of freshness and geometry.
`distanceMetres` is null for unknown or partial without any qualified edge; qualified
complete evaluation alone can establish zero. Counts disclose examined source rows,
known/mapped/unmapped matches, placeable candidates, eligible edges and policy/tie
breaks. `unplaceableEventExists` is an independent all-owner uncertainty fact.
Geometry is `not-requested|available|unavailable`; overflow reports
`geometryBudgetExceeded`, keeps the completed summary and discards the whole path.

Drawing retains at most **20,000 vertices, 1,024 islands and 2 MiB serialized island
bytes**, including seams/brackets/commas. Summary-only constructs no drawing owner.
These are output-retention budgets, not source/period eligibility quotas. They allow
an unreduced 20,000-point daily trace while bounding small-array/object overhead;
the island limit separately bounds fragmented paths. The byte bound protects the
serialized payload without relying on coordinate precision. Focused calibration
uses a reusable 20,100-row fixture, a faithful 20,000-vertex response and whole-month
overflow; no simplification platform or production-capacity guarantee follows.

## Resource ownership and W2 assembly

All five POST and three legacy GET reads share the existing nonwaiting per-process **4 global / 2 owner**
history collection pool. Selected detail retains its independent **16/4** pool;
ingestion remains independent. Period bundling allows one ordinary period request
plus one automatic analysis without starving that page's summary. The page coalesces
and sequences refresh/detail work; deliberate excess requests receive 429/503 with
Retry-After 5 rather than queueing.

The 15-second linked evaluation budget includes route authorization, readiness,
SQL, reduction and complete buffered serialization. SQL uses the remaining budget
as transaction-local statement timeout; disconnect cancels work. A timeout never
publishes a successful prefix. Shared snapshot rollback/disposal has a separate
five-second cleanup bound that preserves a primary failure. Serving time must be
qualified in both bases. Capture also requires the existing all-owner prepared time
generation, writer/index and pending checks; viewer nontext is independent of
capture/text preparation. Qualification/preparation 503 is a visible hold.

Rich day marker and geographic group buffers can still be large. Constant reducer
state does not bound database scans, sorting/spill, readiness or owner uncertainty
checks; include those in measurements. The guarded tests record natural source
plans, examined rows, routed cookie-host latency/bytes, process allocations,
service evaluation including read/reduction, serialization and disconnect/admission.
Synthetic warm local Debug/TestServer results are not browser, native, deployment,
production or universal sub-second evidence.

The former GetChronologicalData/GetChronologicalStats/GetChronologicalStatsDetailed/
HasDataForDate/CheckNavigationAvailability actions retain their legacy contracts;
the private chronological page no longer calls them. Bearer chronology, Group,
public visibility/delay/Hidden Areas and all-time stored-Timestamp statistics keep
their existing contracts. W2 does not reuse private results or change the all-history
page's stored-time meaning.


## Focused local calibration, 5 November 2026

At source/test head `f84a3bea208f74a7a1f75c9dfa32852db661708b`, the reusable
PostgreSQL 18/PostGIS fixture contained 20,100 owner records across two days with
100 repeating nearby coordinate positions. .NET 10.0.14, Debug, production
Identity/MVC through TestServer, warm local Linux execution; one observation per
combination, no production workload or cold-start inference.

| Observation | Capture | Viewer |
| --- | ---: | ---: |
| Routed period, including count/markers/summary/endpoints/navigation | 377.35 ms | 358.10 ms |
| Period response | 15,992 bytes | 15,988 bytes |
| Routed summary-only analysis | 155.36 ms | 172.98 ms |
| Routed analysis with overflowed geometry | 169.52 ms | 158.18 ms |
| Faithful 20,000-vertex day response | 339,042 bytes | 339,040 bytes |
| Readiness, service-level observation | 11.93 ms | 0.27 ms |
| Service evaluation, including uncertainty/read/reduction | 102.50 ms | 118.29 ms |
| Complete summary/overflow serialization | 2.73 ms | 0.06 ms |

Natural source plans returned 20,100 rows via `IX_Location_HistoryUtc`, with 20,100
heap fetches, no explicit source sort and zero temporary read/write blocks. The
invalid-time probe assesses the all-valid owner fixture separately; its cost is
included in service evaluation, without an isolated probe timing/plan claim.
Process-wide allocations over each routed summary were 2.63/1.84 MB; geometry
overflow observations allocated 4.16/3.96 MB. These include host/reader work, are
not retained-geometry sizes or operation-specific peak memory. Sampled process
working set was 453–465 MB; peak attribution remains unmeasured.

While one analysis held its actual permit/snapshot, a competing routed period
completed in 75.53 ms. Exhausting the shared owner admission returned 429;
disconnect was observed in 9.26 ms. A separate real SQL delay exceeded the existing
15-second budget and returned 503 without a successful prefix. These observations
justify a modest faithful drawing buffer and explicit overflow behavior; they do
not establish a universal sub-second target, production capacity or whole-page
memory bound. Test logs/TRX and the initial failed fixture run are retained with
the implementation handoff.

## W2 implementation observations, 6 November 2026

The existing 20,100-row fixture now also observes a selective literal phrase across
analysis, compact records and whole-month availability. One warm Debug/Identity
TestServer run measured 158.87 ms / 1,559 bytes for analysis, 117.34 ms / 14,060 bytes
for records and 74.34 ms / 976 bytes for availability. Process allocation deltas were
3,554,992 / 1,782,048 / 752,784 bytes respectively; these are process observations,
not per-request memory guarantees. Actual SQL, typed synthetic parameters and natural
EXPLAIN ANALYZE/BUFFERS plans are retained in the implementation evidence. Analysis
still examined all 20,100 owner source rows. Selective matching is not a cheap-source
claim. Existing shared routed tests retain cancellation/admission/deadline evidence.

The first managed browser journey found fast activation could leave a transitioning
Bootstrap picker open; immediate dialog transitions correct it. The second journey
passed date discovery, keyboard activation/focus return and Custom commitment, then
held visibly at filtered text readiness. The reusable guarded database had old
history-text-1 and older TZDB activation; offline rebuild of its empty time/text
fixture generations now independently reports both ready. No canonical user history
was changed. The repository retry limit prevents a third environment rebuild in this
implementation turn. Remaining Records/detail/edit-return, pan/refresh, narrow/200%
zoom and final screenshot observations are unexecuted browser acceptance debt.
Lower-seam tests are not relabelled as that missing journey. The draft delivery is
not final acceptance; independent exact-head review and remaining browser evidence
must reconcile the entire accepted contract before maintainer acceptance.
