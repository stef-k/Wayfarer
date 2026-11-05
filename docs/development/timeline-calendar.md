# Private Timeline calendar backend (#953 W1)

W1 adds explicit private calendar reads; it does not enable the new browser
experience. W2 owns selector/URL/preferences, all publication fences, controls,
independent summary/path refresh, protected edit return and browser acceptance.
There is no feature flag, new migration, provider call, read repair or text-generation
prerequisite. Release/deployment and mobile delivery remain separate.

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
promise. Separate responses do not share a snapshot. W2 must fence the entire
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

Day markers are unsampled. Month/year with more than 3,000 definite matches select
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
or unit rounding applies. W2 converts explicit hours/minutes/km/h edits once and
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

All three routes share the existing nonwaiting per-process **4 global / 2 owner**
history collection pool. Selected detail retains its independent **16/4** pool;
ingestion remains independent. Period bundling allows one ordinary period request
plus one automatic analysis without starving that page's summary. W2 must coalesce
and sequence refresh/detail work; deliberate excess requests receive 429/503 with
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

Until W2 adopts these routes, `GetChronologicalData`, `GetChronologicalStats`,
`GetChronologicalStatsDetailed`, `HasDataForDate` and `CheckNavigationAvailability` retain
their existing request/response clocks and parsing. W2 replaces their browser call
sites with explicit calendar routes and adopts qualified detail facts/captions;
it removes that temporary browser dependency, rather than maintaining parallel UI
owners. Legacy bearer chronology, Group, public visibility/delay/Hidden Areas and
all-time stored-`Timestamp` statistics keep their existing contracts. All-time UI
adoption must retain the distinct caption **All history · stored timestamps**.
