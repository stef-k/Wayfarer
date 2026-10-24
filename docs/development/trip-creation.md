# Checked Region and Place creation — BD2-06/07

The coordinated Docker backend/Web and rewritten mobile consumer use these
contracts. Each request creates one child as its own atomic operation. This is
not a batch transaction, a full Trip baseline, or an idempotency/receipt protocol.
No separate mobile repository changes are included.

## Routes and authority

| Operation | Bearer route | Current Web route |
| --- | --- | --- |
| Region | `POST /api/trips/{tripId}/conditional/regions` | `POST /api/trips/{tripId}/editor/regions/conditional` |
| Place | `POST /api/trips/{tripId}/conditional/places` | `POST /api/trips/{tripId}/editor/regions/{regionId}/places/conditional` |

Bearer requires the active incoming API token/account and current owned scope;
cookies alone grant no bearer authority. Web requires the active User cookie,
ownership and `RequestVerificationToken` antiforgery header. After child/Region
locks and the Trip lock, the operation revalidates current account/token/ownership.
Sorted Place → Region → Trip locking follows the established lifecycle order.
Serializable isolation rejects membership phantoms/contention without replay.

The overlapping bearer `POST /api/trips/{tripId}/regions` and `.../places` reject
with 400 `mutation-request-invalid`. The old Web creation paths do not dispatch
creation. A client must fail closed when the protected action is unavailable.
Import/clone and new-Trip initialization remain separately owned graph operations.

## Originals and values

Every request has exactly `expected` and `value` objects. Reject unknown/duplicate
properties, missing required properties, empty GUIDs, noninteger stored orders,
and malformed values. Desired identities/orders are server-owned. JSON null,
absence and an empty collection are distinct.

Originals come from the client's complete authoritative starting view, before
a subsequent save/refresh can change the user's base. Do not reconstruct them
from a new server read during dispatch. A client without adequate evidence holds
the draft until explicit review/adoption of a new starting view.

Region creation requires the complete Trip Region set, including each exact
stored `displayOrder` and `isCanonical` (true only for the exact reserved name
`Unassigned Places`). The set comparison is independent of array order.

```http
POST /api/trips/11111111-1111-1111-1111-111111111111/conditional/regions
Authorization: Bearer <incoming-api-token>
Content-Type: application/json

{"expected":{"regions":[{"id":"22222222-2222-2222-2222-222222222222","displayOrder":0,"isCanonical":true}]},"value":{"name":"Athens","notes":"<p>Day one</p>"}}
```

Bearer Region values require a nonblank `name` and nullable `notes`. The reserved
name is forbidden. Web values retain required `name`, nullable `notesHtml`,
nullable `coverImage:{rawUrl}` and nullable `center:{latitude,longitude}`. Existing
Notes sanitization, external-image rules and HTTP(S) cover validation apply.

Existing-destination Place creation requires `expected.regionId` and every Place
ID/exact nullable stored order in that destination. It does not depend on
unrelated Region membership or sibling authored contents. The bearer desired
`value.regionId` must equal the original; the Web path owns the desired destination
and must match the original. Web drafts capture every selectable destination at
open, but submit only the selected destination's original evidence.

```json
{
  "expected": {
    "regionId": "33333333-3333-3333-3333-333333333333",
    "places": [
      {"id":"44444444-4444-4444-4444-444444444444","displayOrder":null},
      {"id":"55555555-5555-5555-5555-555555555555","displayOrder":9}
    ]
  },
  "value": {
    "regionId":"33333333-3333-3333-3333-333333333333",
    "name":"Current location", "notes":null,
    "location":{"latitude":0,"longitude":0},
    "iconName":"marker", "markerColor":"bg-blue"
  }
}
```

Bearer Place values require all six shown fields. `location` may be null or a
finite WGS84 pair (latitude ±90, longitude ±180 inclusive); zeroes are valid.
Empty/whitespace markers reset to `marker`/`bg-blue`. An unlocated Place is valid.
No address, provider directive, route or Location-history write is admitted.
“Add current location” supplies coordinates through this same contract.

For an absent canonical fallback, send `expected.regionId:null`, `places:[]` and
the complete original `regions` set proving canonical absence. Send
`value.regionId:null`. For example, a Trip with no Regions uses:

```json
{"expected":{"regionId":null,"places":[],"regions":[]},"value":{"regionId":null,"name":"First Place","notes":null,"location":null,"iconName":"marker","markerColor":"bg-blue"}}
```

An existing canonical destination must be selected by its original ID and Place
set. A concurrent fallback winner invalidates accepted absence: reject rather
than adopting that winner or inserting against freshly substituted originals.
The exact canonical uniqueness constraint remains the final collision safeguard.
Only a confirmed rollback permits a 409; there is no automatic second insert.

Web Place values retain required `name`, nullable `notesHtml`, nullable `address`,
nullable `location`, option-validated `iconName`/`markerColor` and boolean
`reverseGeocode`. Address resolution reuses the existing service before domain
locks, then final revalidation precedes persistence. Unavailable enrichment
retains manual address and the existing warning; successful enrichment retains
the five-field feature/provider provenance tuple. Bearer never invokes it.
The editor read DTO publishes `originalValues.displayOrder` separately from its
display default, so null originals are never reconstructed as zero.

## Atomic footprint and acknowledgements

Region creation writes one new GUID Region at max normal Region order + 1 (1
when empty), its supported fields and Trip freshness. It replaces no sibling
content, order, child, route or root metadata.

Place creation writes one new GUID Place at `(MAX(nonnull Place order) ?? 0)+1`,
its supported fields, and Trip freshness. If explicitly proven absent, one new
GUID canonical Region at order 0 joins that same transaction. No sibling
normalization occurs. Sparse, tied, negative and null historic orders remain;
no globally dense or unique ordering is promised. Integer overflow rejects.
A rollback removes all creation effects, including a staged fallback.

Only committed operations return 200 with `contract:"bd2-1"` and `success:true`.
Bearer adds `applied:true`, `tripId` and canonical `region` or `place` DTO. Region
creation includes complete resulting Region IDs in `regionOrder`. Place creation
includes complete destination `{id,displayOrder}` rows in `placeOrder` and
`createdFallback` (null or its ID/name/order). Coordinates in bearer DTOs are
`[longitude,latitude]`. Web returns the existing `data`, `affected`, empty
`deletedIds`, `warnings` envelope plus `contract`. Its affected slices contain
exact new entities, complete changed order vectors and, for Place, visit progress.
Web Region creation includes empty Place/Area vectors for the new Region.

Validate contract, success, scope, server identity, canonical values/originals,
complete expected membership/order effects and absence of unrelated effects
before publishing parent slices, adopting a baseline or reporting Saved. Preserve
newer draft fields; ignore replies from retired editing sessions. An ID by itself
is neither acknowledgement of the full footprint nor a durable retry receipt.

## Failure and unknown outcomes

- 400 `mutation-request-invalid`: malformed/presence/value failure, unapplied.
- 401: invalid/inactive authority. Web also retains its User/antiforgery failures.
- 404: absent or concealed foreign scope; it is not proof of deletion.
- 409 `mutation-base-stale`: original parent/membership/order/canonical mismatch.
- 409 `mutation-contention`: confirmed aborted serialization/deadlock or the exact
  canonical uniqueness collision. Review current state before a new attempt.

Nonleaking ProblemDetails contains no current private content or foreign IDs.
A conflict cannot report a partially applied creation. A commit/rollback failure,
cancellation, timeout, lost reply or malformed acknowledgement can leave the
outcome unknown; never infer noncommit from an ordinary refresh or blindly replay.
Current Web owners fence creation from dispatch until a valid acknowledgement or
definitive rejection. The unknown hold survives refresh, reset and retired replies
within that mounted editor. Leaving explicitly abandons local intent; it does not
undo or prove the outcome. No durable receipt or cross-reload recovery is claimed.

## Evidence boundary

Focused routed PostgreSQL tests own persistence, sparse/null originals, scope,
concurrency and rollback/fallback behavior. Mounted production editor tests own
pinned originals, acknowledgement adoption, newer intent and retired/lost replies.
No browser/platform/native Mobile/production acceptance is inferred. BD2-06/07
remain pending independent exact-head review, CI and maintainer merge acceptance;
other structural BD2 operations and final C1/C2/C5 reconciliation remain separate.
