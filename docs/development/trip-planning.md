# Private Trip planning (WB1, #789)

WB1 adds optional owner planning to Trip, Region, Place, Area and Segment. It does
not implement Check plan, remembered check inputs, native v4 personal files,
future own cloning/versioning or the separately owned mobile companion.

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

Place move/reorder material originals now contain complete planning. The existing
mapped-scalar deletion fingerprints include planning for Regions, children and
affected Segments, including kept children. Independent route fingerprints and
coordinate originals retain their narrower footprints. Area/Segment DELETE accepts
the Clear-shaped original body to bind planning loss; a missing body can delete
only a currently unplanned target. Their existing confirmation controls include
private planning in the deletion description.

Native v1–v3 upsert locks current child sets and parents before replacement, retains
planning by exact same type/ID and restores it with the rebuilt graph inside the
existing graph/tag/measurement transaction. Root planning stays untouched. Newly
identified objects inherit nothing. Removing/reidentifying an annotated object
rejects atomically with `planning_loss_requires_review`; the owner may cancel or
CreateNew. WB2 supplies file-bound overwrite/removal/Keep/Use/Clear review and the
complete v4 round trip. WB1 never silently resets planning and does not disable
ordinary legacy imports or unplanned removals.

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

No mobile source was changed or Android/iOS qualification claimed. M1 remains
required, separately owned and incomplete; WB1 does not complete #789.
