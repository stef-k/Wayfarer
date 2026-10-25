# Protected Region ordering (#853 BD2-11)

The coordinated backend/Web and rewritten mobile app use one complete,
original-aware normal-Region vector. This operation has no child, canonical-order,
scalar-content or historical repair write authority. Place deletion and
Region-deletion integration remain the following batch; final C1/C2 reconciliation
and C5 acceptance remain separate.

## Routes and authority

| Caller | Prepare (read only) | Apply |
| --- | --- | --- |
| Bearer | `POST /api/trips/{tripId}/conditional/regions/order/prepare` | `PUT /api/trips/{tripId}/conditional/regions/order` |
| Web | `POST /api/trips/{tripId}/editor/regions/order/prepare` | `PUT /api/trips/{tripId}/editor/regions/order` |

Bearer requires the actual incoming active credential and current owned Trip.
Web requires an authenticated User cookie and antiforgery token on both routes.
Both apply paths recheck current account, ownership and scope after lock waits;
bearer also revalidates the incoming credential. Sorted Region locks precede the
Trip lock, preserving the lifecycle lock order without acquiring child locks.
Apply uses Read Committed with final complete membership/material rediscovery
under those locks; preparation reads a Repeatable Read snapshot. Normal creation,
scalar and lifecycle writers coordinate through these same Region/Trip barriers.

## Original evidence and example

Capture the full authoritative starting view before authoring the order. Normal
Region originals contain every `id`, actual `displayOrder`, `name`, nullable
normalized `notes`, exact nullable `coverImageUrl`, and nullable
`center:{latitude,longitude}`. Canonical `Unassigned Places` is identified by its
exact stored name regardless of stored order. Its separate original is either
`null` (explicit absence) or `{id,displayOrder}`. Do not send canonical content or
its ID in the normal vector. Children are preserved, not content dependencies.

Example mobile preparation body (two normal Regions, historical canonical order):

```json
{
  "expected": {
    "regions": [
      {
        "id": "10000000-0000-0000-0000-000000000001",
        "displayOrder": 4,
        "name": "Athens",
        "notes": null,
        "coverImageUrl": null,
        "center": { "latitude": 37.98, "longitude": 23.73 }
      },
      {
        "id": "10000000-0000-0000-0000-000000000002",
        "displayOrder": 8,
        "name": "Thessaloniki",
        "notes": "<p>Station</p>",
        "coverImageUrl": "https://example.test/station.jpg",
        "center": null
      }
    ],
    "canonical": {
      "id": "10000000-0000-0000-0000-000000000003",
      "displayOrder": 17
    }
  },
  "regionIds": [
    "10000000-0000-0000-0000-000000000002",
    "10000000-0000-0000-0000-000000000001"
  ]
}
```

Prepare returns `contract:"bd2-1"`, `operation:"region-order"`, `tripId`,
`requiresReview:true`, the matching original `regions`/`canonical` and an opaque
`conditionToken`. Validate scope and echoed originals, explicitly review the
operation, then PUT the **same** expected/vector body with the returned
`conditionToken` and `reviewed:true`. Never replace stale client originals with a
fresh preparation response. No original proof means retain/hold the draft until
the user explicitly reviews a fresh starting view. Public full-read coordinate
arrays are `[longitude,latitude]`; convert them to the named pair above. Web uses
its published nullable `originalValues`, not display Notes/cover defaults.

Prepared conditions bind owner, Trip, Web/bearer dialect, exact intent and raw
normal-Region material under the distinct
`Wayfarer.RegionOrder.Conditions.v1` purpose. Place and deletion proofs cannot
substitute. Preparation rejects stale public originals rather than adopting a new
base; raw content is rechecked at final apply. Tokens are transient conditions,
without a durable receipt, terminality or replay guarantee.

## Atomic result and rejection

A successful apply writes normal `DisplayOrder` values `1..N` in desired sequence
and current-safe maximum Trip freshness in one transaction. It preserves canonical
stored order, every Region scalar, children, Place/Area orders, Segment routes,
waypoints, provenance, saved measurements, visits and unrelated Trip metadata.
An empty normal vector is valid only for an actually empty normal set. Existing
normal sparse/tied/negative orders are compared exactly before the explicitly
requested vector replaces them; no other order normalization occurs.

Both return `contract:"bd2-1"`, `success:true`, `data:{tripId,regionOrder}`, and
`affected.regions` containing exactly the updated normal Region DTOs, including
published originals. `affected.regionOrder` and `data.regionOrder` are the same
complete resulting Region sequence, sorted by actual stored order then ID,
including canonical at its preserved stored position. All other affected slices
are empty/null, deleted IDs are empty and warnings are empty. Bearer additionally
returns `applied:true`. These results describe committed effects only.

| Status | Meaning |
| --- | --- |
| 400 `mutation-request-invalid` | Malformed/missing/duplicate fields or IDs, incomplete vectors, or canonical/foreign members inconsistent with submitted originals. No partial writes. |
| 401 | Invalid authority, including credential/account invalidation observed after waiting. |
| 404 | Missing/foreign Trip scope, concealed before request parsing. |
| 409 `mutation-base-stale` | Changed membership, stored orders, material content or canonical identity/order; no current values are disclosed. |
| 409 `mutation-review-required` | Missing explicit review/prepared conditions. |
| 409 `mutation-contention` | Proven rolled-back PostgreSQL transaction abort before commit begins. |

A valid vector followed by a Notes edit can both succeed. A material Notes edit
first makes a previously retained order stale, symmetrically for Web/bearer.
Failed apply rolls back orders and freshness together. Commit, rollback, transport
or response uncertainty is never relabeled a definitive no-effect conflict.

## Web acknowledgement and participating writers

The mounted list pins material and stored orders at drag start, before prop
refresh can replace them. The Region owner retains that base before prompts/awaits, uses the
protected path, and validates returned Trip, identities, complete vector, material
preservation and unrelated empty effects before parent adoption. It does not reset
or acknowledge newer scalar intent. Retired sessions publish no effects. Unknown
apply outcomes retain the submitted order and a dirty navigation hold through
refresh/reset/session retirement, prevent blind replay and false Saved, and explain
explicit page abandonment. Ordinary refresh does not establish noncommit. No
cross-reload recovery is promised.

Unconditional Web `{regionIds}` bodies reject. Individual bearer Region PUT cannot
change an ordinary Region's stored order, even in a mixed scalar payload; identical
submitted orders remain no-ops and are never assigned. Existing canonical identity
protection remains. Other scalar/deletion coverage remains owned by its existing
operation and later reconciliation; this batch does not claim global all-writer
acceptance.

## Focused evidence

`RegionConditionalPostgresTests.Ordering.cs` exercises paired valid operations,
reciprocal Notes/order sequences, invalid vectors, normal/canonical drift, rollback,
current authority and real Region lock waits. A routed creation queued behind
reorder rejects its stale append base without a ghost Region. Fresh PostgreSQL
rows/versions prove populated children, canonical storage and unrelated root
metadata are preserved. The existing Region scalar and creation qualification is
reused outside the changed boundary.

`tests/client/regionConditionalEditing.test.mjs` mounts the production Region owner
with Vue scheduling and covers delayed/newer/retired replies, scope/material/effect
validation, definitive rejection and lost/malformed acknowledgement holds. These
are routed server and mounted-client executions, not browser, native mobile or
production qualification. No database migration is required.
