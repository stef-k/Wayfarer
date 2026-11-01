# Private Trip planning (WB1/WB2/WB3, #789)

WB1 adds optional owner planning to Trip, Region, Place, Area and Segment. WB2 adds
[complete native personal interchange](native-personal-interchange.md). WB3 adds
optional saved-version checks and bounded device-local check inputs in the Web editor.
Future own cloning/versioning and mobile implementation remain separately owned.
Mobile qualification does not block backend/Web acceptance. Final combined backend/Web
contract and adversarial acceptance is separate from this implementation package.

## Values and storage

Each entity has one nullable `Planning` JSONB scalar, mapped through typed records
and the strict format-1 codec. No planning is SQL null. A nonempty collection has
at most 128 ordered entries, distinct nonempty UUIDs, and optional labels of at most
200 characters. IDs are local entry keys, never ownership or database lookup authority.
Object member order/whitespace do not affect comparison; entry order does.

```json
{
  "format": 1,
  "entries": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "when": {
        "form": "at",
        "value": { "precision": "month", "local": "2027-03" }
      }
    }
  ]
}
```

`when.form` is `at`, `period`, `unspecified`, `unknown` or `flexible`. At has
exactly one `value`. Period has optional `start`/`end` bound objects with `state`
`known`/`unspecified`/`unknown`/`flexible`; only `known` carries a value and at least
one bound must be known. This supports end-only and mixed-precision periods.
Undated forms carry neither values nor bounds. At and period are exclusive.

Each value preserves `precision` (`month`, `date`, `dateTime`) and literal `local`
(`YYYY-MM`, `YYYY-MM-DD`, `YYYY-MM-DDTHH:mm`), Gregorian years 0001–9999. Seconds,
fractions, invalid dates, year zero and 24:00 reject. Only dateTime permits optional
`zone` (UTC or a serving NodaTime TZDB IANA identifier) and optional integral
`selectedOffsetSeconds`. Offset-only values reject. A named-zone gap or false
offset rejects; a fold can remain unresolved or select a valid offset. Civil
components are never converted on save, move, route reversal or device-zone change.

Optional `kind` is `window` on any target, `presence`/`stay` on Trip/Region/Place/Area,
or `travel` on Trip/Segment. Absence remains unclassified. Optional nonnegative
integral `allocationMinutes` is allowed on At or undated unclassified/presence/travel
entries, including explicit zero; it is forbidden on periods, windows and stays.
Optional `label` is preserved verbatim. Unknown members, duplicate JSON members,
unsupported forms/versions and invalid entry IDs reject without partial writes.

Only a newly provable internal reversal rejects. Resolved clocks compare instants
before wall time; unresolved folds retain all admissible mappings. Comparable
unzoned civil granules can prove ordering across precision levels. Incomparable
frames do not prove reversal. Valid overlaps, rough plans, parent-window excess,
missing meanings and incomplete times impose no saving assessment or warnings.

Existing unchanged stored endpoints survive later TZDB rule changes. Owner reads
include current-rule diagnostics and the bearer snapshot names `zoneRulesVersion`;
neither rewrites stored values. This trusted-storage exemption is not an import
validation exemption for WB2. Exact occupancy, deadlines, nights, dwell, estimates
and observed history are separate concepts; WB1 derives none of them.

## Owner reads and protected saves

`GET /api/trips/{id}` keeps its reusable projection, including when called by the
signed-in owner of a public Trip. `GET /api/trips/{id}/planning` requires the active
owner's bearer and returns `{ trip, planning }` from the existing full-read core in
one repeatable-read, read-only transaction. `planning` contains the Trip aggregate,
Region/Place/Area/Segment dictionaries, TZDB version and owner-only diagnostics.
There is no public fallback. The cookie editor's existing GET returns each
entity's aggregate/diagnostics with its graph in one repeatable-read transaction.

Set/Clear routes:

| Audience | PATCH route |
| --- | --- |
| Bearer owner | `/api/trips/{tripId}/planning/{kind}/{targetId}` |
| Cookie User + antiforgery | `/api/trips/{tripId}/editor/planning/{kind}/{targetId}` |

`kind` is lowercase `trip`, `region`, `place`, `area` or `segment`. Parent is the
Trip ID for Trip/Region/Segment, or the original Region ID for Place/Area. Requests
are bounded to 256 KiB. A Set request is:

```json
{
  "expected": { "parentId": "22222222-2222-4222-8222-222222222222", "planning": null },
  "operation": "set",
  "value": { "format": 1, "entries": [{
    "id": "11111111-1111-4111-8111-111111111111",
    "when": { "form": "at", "value": { "precision": "date", "local": "2027-03-02" } }
  }] }
}
```

Clear uses `operation: "clear"` and omits `value`, with complete current originals.
An absent original, null Set, empty collection or unknown operation is not Clear.
Omitting this operation leaves planning unchanged. Whole-aggregate comparison
includes absence, all IDs/order and all optional qualifiers. Independent visits on
one object can conflict; review and an explicit replacement resolve that conflict.

The service locks the target before its parent chain and Trip, then revalidates
active owner, bearer credential and scope after waits. It writes only Planning in
one transaction. Concurrent disjoint Notes/route/coordinate writes preserve it.
Planning-only writes do not update Trip `UpdatedAt`, public validators or events.
The tracked SaveChanges freshness path also excludes planning-only modifications.

Success contains `contract: "planning-1"`, `success`, `applied`, target kind/ID,
parent ID and the complete acknowledged `planning` (null for Clear). This confirms
that attempt, without a durable retry receipt. Stale original/scope returns 409;
malformed values return safe 400 field errors. Commit/transport/server/malformed
reply failures remain unknown. The Web retains original and desired content,
advances only the submitted comparison on acknowledgement, and keeps newer typing.
GET equality never settles a held attempt or authorizes replay.

## Privacy and other writers

Planning is excluded from System.Text.Json/Newtonsoft raw entity serialization
and MVC form binding. Only explicit owner DTOs include it. Public reads, native
v3/Google/PDF downloads, thumbnails and cross-owner clones remain redacted. Owner
public URLs keep the public audience. Private owner/editor/structural responses,
including errors, use `private, no-store` and omit public validators. No shared
planning output, generated-file cache or service-worker cache is introduced.

Normal Region reorder and Place move/reorder material originals contain complete
planning, including absence. Canonical Region content, children and Trip planning
stay outside Region ordering. Independent coordinate/route dependency projections
exclude Segment planning while retaining route/anchor/provenance/measurement proof. The existing
mapped-scalar deletion fingerprints include planning for Regions, children and
affected Segments, including kept children. Independent route fingerprints and
coordinate originals retain their narrower footprints. Area/Segment DELETE accepts
the Clear-shaped original body to bind planning loss; a missing body can delete
only a currently unplanned target. Their existing confirmation controls include
private planning in the deletion description.

Native v1–v4 replacement locks current child sets and parents, retains omitted
planning by exact type/ID including the Trip root, and restores/applies/clears it
inside the graph/tag/measurement transaction. Every update requires a file-bound
current-content review. Keep is the default for differences; Use imported and
explicit Clear are separate choices. Annotated removal/reidentification requires
loss consent. See the [HTTP and format contract](native-personal-interchange.md)
for permits, stale reviews, authority, cache separation and unknown-outcome holds.

## Optional Web checks (WB3)

`POST /api/trips/{tripId}/editor/planning/check` accepts `PlanningCheckSetup` from
an authenticated User cookie plus antiforgery, with a 256 KiB body limit. It calls
the existing repeatable-read owner editor reader once and compares that coherent
snapshot. The additive private editor fields `ownerId` and `zoneRulesVersion`
partition local setup and bind serving rules. No public DTO, planning writer,
import/export schema, database migration or bearer mutation contract changes.
The established private response middleware covers successes, denial, malformed
input and errors with `private, no-store`, without public validators. The service
has no persistence/provider dependency; editor capability projection resolves
configuration only and does not request directions.

The strict request has these optional check-only inputs (empty `{}` runs first):

| Field | Meaning |
| --- | --- |
| `selection` | Null/default includes every current entry; an explicit entry-key array narrows this review. Keys are `kind/targetUuid/entryUuid`, scoped to this snapshot. |
| `singleTraveller`, `travellerDependency` | Explicit premise and its selected-participant binding. Changed membership reopens it. |
| `entries[key]` | Missing meaning, interpreted zone, elapsed allocation, selected parent `context` keys, optional `occurrences` offsets for `value`/`start`/`end`, and dependency bindings. Saved facts win. |
| `transfers` | Explicit `from` → `to` keys, optional `segmentId` or manual `estimateMinutes`, `relevant`, allowance/connection treatments and minutes, and optional `whatIfRelease`. |
| `incompatible` | Explicit assessed incompatible-location pairs, keyed by sorted entry keys joined with `\|`; null means newly assessed, a returned binding supports later reuse. Distinct IDs alone are insufficient. |

For example, a transfer carries `relevant: true`, `estimateMinutes: 30`,
`allowance: "additional"`, `allowanceMinutes: 15`, `connection: "none"`. Treatments
are `unknown`, `none`, `included` in the selected estimate, or `additional`.
Unknown is never zero. Additional minutes must not already be counted in the
estimate or gap; other saved activities are not automatically deducted from a
chosen pair. Select the relevant release/arrival endpoints and account for rests
once. No minimum connection time or route estimate is invented.

The `planning-check-1` response contains owner/Trip/rules identity, complete
`inventory`, independent `findings`, revalidated `setup`, `reused` and `invalidated`
input descriptions. Every inventory row retains saved entry facts, identity,
selection and parent-window candidates. Findings carry participating keys and
`completed`, `mismatch`, `possible`, `unknown`, `inconsistent`, `contradiction`,
`risk` or `excluded`, with an explanation and minutes only where justified.
These are advisory results, never receipts or saved originals. Invalid request
structure/limits returns 400 `planning-check-invalid`; existing read authority and
availability failures retain their normal statuses and private policy.

`PlanningIntervals` uses serving NodaTime rules, retaining both fold mappings.
Exact occupied intervals use [start,end); At needs explicit allocation, including
zero for a moment. Coarse presence, mixed/unbounded coverage and missing necessary
zones stay Unknown. Explicit civil windows include their final named granule;
stay checkout is exclusive. Same-frame civil-window unions can compare civil
coverage without claiming elapsed minutes. Interpreting their edges in an explicit
zone supports instant comparisons with clocked children. Local occurrence choices
fill only absent saved choices and revalidate the clock/zone/rules before reuse.

Containment evaluates full child coverage against the explicitly selected union,
retaining gaps and overlapping each covered span only once. Unresolved context
never defaults from ownership. Every supported placement outside is definite;
some contained and some outside is possible. Inconsistent selected input prevents
a containment verdict rather than being silently dropped. Physical occupation
uses assessed presence plus explicit single-traveller/incompatible-location
premises; stays and windows do not occupy their complete availability spans.

Movement uses release End/known At allocation and target Start/At, never destination
End as a deadline. Negative exact gaps remain contradictions even without an
estimate. A relevant estimate can establish risk before unknown extras; 120 − 180
is a 60-minute risk. Alice's 60 − 30 − 15 is 15 minutes estimated slack, with source
and inclusion treatment displayed, without an arrival/attention guarantee.

Input bounds are 512 entry refinements, 64 transfers, 2048 location assessments,
4096 explicit selection keys and 128 context keys per entry. Computation supports
at most 256 admissible containment placements and 2048 physical pairs. Exceeding a
comparison bound produces explicit Unknown remainder; inventory is never truncated
or silently promoted to a whole-Trip consistency result.

The mounted `PlanCheck` owner separates displayed results from saved graph facts,
ordinary/Planning drafts and mutable check inputs. Dirty drafts offer **Save and
check**, **Check saved version**, or **Return to editing**. Save and check calls
the active ordinary form's existing protected save and the existing Planning
workspace saves; failure, unknown outcome or later typing leaves the draft hold
intact and prevents assessment as saved. A fresh review does not merge its snapshot
into any mutation original. No Save on entry action is offered here; durable
refinements use the existing Planning form and its protected save.

Exactly one latest setup is retained in localStorage under
`wayfarer.latest-owner-planning-check`, bounded to 256 KiB and owner/Trip keyed.
The next authenticated editor snapshot fences/clears another partition before
rendering private review data. Denied access or a foreign owner reply clears it.
Results and dismissals are never stored. Unavailable storage is explicit and
falls back to this editor lifetime. No cross-device reuse, history, relationship
graph or preferences framework is promised. Native/private exports and copies
consume planning only and never read this local record.

Revalidation binds meanings/frames/dwell to membership, location, saved qualifiers
and rules; occurrence choices also bind clocks. Context selection binds parent
membership/meaning while current bounds recompute coverage. Directional route,
anchors, locations, mode and estimate changes reopen transfer relevance/extras;
label-only edits reuse them. What-if releases bind timing/frame evidence and
expire after 30 minutes or a UTC-day change. No what-if changes planning, estimates
or observed history. New entries remain visible; removed/reparented links reopen.
Edits stale displayed reviews, and request/identity positions fence late replies.
Dismissal collapses a finding while retaining its count; the next review resurfaces
it. Clear latest private inputs removes only this bounded setup and presentation.

Qualification: pure `PlanningComparisonTests` and `PlanningCheckTests`, mounted
`planningReview.test.mjs`, the existing real PostgreSQL cookie route/privacy case,
and one managed actual-control smoke `--grep 'optional Check plan keeps known findings'`.
Calculation/state matrices stay at the lower seams. Reuse unchanged accepted
WB1/WB2 persistence, import/export and public-projection evidence at its recorded
scope; this package does not requalify mobile, deployment or production.

## Migration and qualification

`20261031191725_AddPrivatePlanning` adds five nullable JSONB columns without
backfill, inferred dates, new indexes or release-authority changes. Apply through
the normal maintained migration process before using WB1. Existing objects remain
unplanned. Back up the database before rollback: Down drops all five columns and
their private content. Old application binaries cannot provide the new import
preservation contract; operational rollback must retain the migrated data and avoid
destructive old-binary Trip replacement until preservation is restored.

Focused qualification lives in `PlanningValueTests`, `PlanningPostgresTests`, the
mounted `planningEditor.test.mjs`, and one managed Chromium smoke
`npm run test:e2e:shared-layout -- --grep 'private planning rough date'`.
Use the existing guarded PostgreSQL 18 `wayfarer_import_tests` prerequisite.
The existing Place structure, Region deletion, conditional scalar and native import
tests cover adjacent behavior; do not multiply their state matrices in the browser.

## Required WayfarerApp companion

Read-only inspection at WayfarerApp `e5f8353` confirms that `TripOwner` and
`TripControls` are synthetic Foundation selection/navigation surfaces. They are
not backend DTOs or full Trip editors and must not be expanded for WB1. The owning
Trip delivery phase after Foundation #2 must supply the required companion. The
live #2 explicitly excludes full Trip editing; this batch does not assign that
work to Foundation or invent a later implementation issue:

- Coherent bearer owner download/refresh using the new owner snapshot, public
  viewer redaction, and lossless DTO/domain/SQLDelight storage of every authored
  precision, zone/offset, qualifier, entry ID/order and absence through reopen.
- Trip/Region/Place/Area/Segment structured entry, repeated dates, literal summaries
  and refinement. No native date-control coercion or inferred local zone.
- MD03/MD05 durable authored intent and immutable attempts/originals, account/scope
  fencing, truthful local versus server-confirmed status, protected Set/Clear,
  later typing, unknown-outcome holds and explicit review/recovery.
- WB2 personal file/share/import preservation and privacy at supported surfaces;
  unsupported file UI must be stated as unsupported while transports preserve the
  model. Cross-owner cloning stays redacted; future own Keep/Start belongs to #911.
- WB3 supported checks and bounded owner/Trip-device input reuse in that package.

No mobile source was changed or Android/iOS qualification claimed. Mobile remains separately owned and incomplete; mobile agents implement and qualify
it. That work is not a backend/Web acceptance or #789 closure prerequisite. Final
combined backend/Web review remains separate from WB3 implementation.

## WB1 correction boundaries

The mounted workspace captures independent mutation dispatch order through the shared
API (including Segment ordering and Region deletion), using local object identity;
no ordering token enters server contracts or unrelated mutation comparisons. At App
publication, a local Planning attempt/acknowledgement fences replies dispatched
before it. Pending/uncertain attempts keep their published snapshot. Later current
responses can refresh clean Planning on all five types, including clean open forms;
dirty originals and later typing remain separate from the published server snapshot.
Sidebar dirty status and shared exits include Planning, with separate save scope.
Authoritative deleted IDs retain affected authored drafts in the workspace, exposed
for copying and individually confirmed discard. Deleted identities stay absent for the
mounted session, including after draft discard, so late affected replies cannot
recreate them. New identities still publish normally. A valid delayed Planning
acknowledgement records the earlier success and advances its comparison, but neither
restores the target nor releases its recovery/exit hold. Explicit individual discard
resolves that recovery; pending/uncertain attempts stay held. An explicit comparison
review also checks deletion and intervening operations after its read returns.
Negative receipts require a compatible HTTP/problem status, known code and no
contradictory or malformed outcome fields; redirects remain uncertain. Equality on
GET/remount never authorizes replay. WB2 replaces the earlier bounded import-loss refusal with the reviewed import
controls and retains files/choices on recoverable failures.

Public Region, Place, Area and Segment projections use identity as the tie-breaker
for equal DisplayOrder, with existing null Place orders still first. Unordered Tags
also project by identity. Stored orders and public freshness remain unchanged.
Waypoint positions already require a unique complete sequence at the strict read
boundary. Exact public bytes are compared across private writes without sorting or
normalizing responses in the assertion.
