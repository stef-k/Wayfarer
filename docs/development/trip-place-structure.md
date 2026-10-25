# Conditional Place structure (BD2-08/09/10)

The supported Docker backend/Web and rewritten mobile client use the contracts
below. They share the Place lifecycle owner and locked Segment reconciliation.
These operations are transient compare/apply actions, without durable receipts,
automatic rebasing, provider refresh or unknown-outcome recovery.

## Routes and authority

Let `B = /api/trips/{tripId}/conditional` and
`W = /api/trips/{tripId}/editor`. All IDs are GUIDs.

| Operation | Bearer apply | Web apply | Prepare |
| --- | --- | --- | --- |
| Coordinates | `POST B/places/{id}/coordinates` | Combined Web PUT below | POST at the apply URL plus `/prepare` |
| Same-Trip move | `POST B/places/{id}/move` | Combined Web PUT below | POST at the apply URL plus `/prepare` |
| Place order | `PUT B/regions/{id}/places/order` | `PUT W/regions/{id}/places/order` | POST at the apply URL plus `/prepare` |
| Combined Web intent | No bearer authoring parity | `PUT W/places/{id}` | `POST W/places/{id}/prepare` |

Bearer actions require the actual incoming active API credential and current
ownership; cookies do not supply bearer authority. Web actions require the User
cookie, ownership and antiforgery, including prepare. Authority is checked again
at apply's final lock boundary. Missing/foreign targets are concealed equally.
The existing unconditional bearer Place PUT returns 409
`mutation-originals-required`; a complete unconditional Web draft is invalid.
Scalar Name/Notes/icon/color edits continue to use their existing conditional
PATCH with selected originals, without prepare or any structural fallback.
Scalar edits omit location intent and preserve absent or historical non-finite
stored locations. The editor response projects a historical non-finite Place
location as null without repairing its stored point or enrichment.

## Originals, preparation and explicit review

Prepare accepts the operation's client originals and exact desired intent. It
reads a coherent snapshot, compares public originals and returns:

```json
{
  "contract": "bd2-1",
  "operation": "coordinates",
  "tripId": "<trip-guid>",
  "targetId": "<place-guid>",
  "requiresReview": true,
  "place": "<complete public material original, or null for order>",
  "placesByRegion": {},
  "affectedSegmentIds": [],
  "conditionToken": "<opaque protected conditions>"
}
```

`place` above denotes the object defined below, not a literal string. The response
contains no raw private enrichment/provenance snapshots. Conditions bind the
server's protection keys, owner, Trip, typed target, operation, exact desired
intent and complete material fingerprint. Coordinate, move and order purposes
are separate. Nondestructive conditions have no wall-clock expiry; current
material comparison remains decisive. They authorize neither access nor replay.

Prepare never replaces submitted originals. It rejects stale public evidence.
Because the current client view does not contain original private dependencies,
the prepared private base requires explicit review. Web presents that review
before apply and preserves the authored draft on cancellation. A mobile client
may acquire/review conditions before authoring the operation or explicitly review
current consequences later; it must preserve an offline intent that lacks adequate
original evidence until that review occurs. Do not silently treat a later prepare
as the base against which the user originally edited.

Apply repeats the same request with `conditionToken` and `reviewed: true` added.
It discovers locks, takes the existing profile → Segment → Place → Region → Trip
order, rediscoveres the scope after waits and compares the final material state.
New dependencies, changed profiles/anchors, membership/order drift or stale target
content reject without applying any partial operation. No automatic retry changes
expected values or exchanges a stale token for a fresh one.

## BD2-08: coordinates

Prepare and apply use this body, adding the reviewed conditions only for apply:

```json
{
  "expected": {
    "regionId": "<original-region-guid>",
    "location": { "latitude": 37.98, "longitude": 23.73 }
  },
  "location": { "latitude": 38.0, "longitude": 23.75 }
}
```

Original location may be null. Desired bearer coordinates must be a finite,
inclusive WGS84 pair; zero is valid. Bearer coordinate clearing is not selected.
The fingerprint binds original parent/location, the entire five-field enrichment
tuple and affected routes, waypoint associations/positions/vertex indices,
anchors, profiles, distance/duration/source and provenance. Unrelated scalar
Notes are not coordinate dependencies. Coordinate-only intent preserves the
current Address, including null, and does not contact providers.

Changed coordinates clear ResolvedFeatureName, ResolvedFeatureType,
AddressEnrichmentProvider, AddressEnrichmentStorageMode and AddressEnrichedAt
atomically. Unchanged coordinates retain all five. Existing reconciliation owns
route vertex/fallback changes, waypoint effects, Wayfarer measurements, Manual
duration preservation and provenance invalidation. Location, derived changes and
current-safe Trip freshness commit together.

## BD2-09: same-Trip move

A material original has every key below, preserving nullable stored values:

```json
{
  "id": "<place-guid>", "regionId": "<source-guid>",
  "name": "Museum", "notes": null, "address": null,
  "location": { "latitude": 38.0, "longitude": 23.75 },
  "iconName": null, "markerColor": null, "displayOrder": null,
  "resolvedFeatureName": null, "resolvedFeatureType": null
}
```

Notes use the published normalized authored representation; null and empty are
distinct originals. Web exposes exact nullable Address and stored order in
`originalValues`; do not substitute display defaults. Bearer reads expose these
values in their existing nested Place DTOs. The prepared fingerprint additionally
binds the complete persisted moved-Place content, including private enrichment.

```json
{
  "expected": {
    "place": "<material original object>",
    "placesByRegion": {
      "<source-guid>": [{ "id": "<place-guid>", "displayOrder": null }],
      "<destination-guid>": []
    }
  },
  "destinationRegionId": "<destination-guid>",
  "placeIdsByRegion": {
    "<source-guid>": [],
    "<destination-guid>": ["<place-guid>"]
  }
}
```

Both Regions must already exist in the same owned Trip. Original sets include
all members and their exact nullable orders; array order is immaterial. Desired
vectors contain the complete union exactly once, moving only the selected Place.
Foreign, duplicated, omitted or additionally transferred members reject. Only
Region membership, both affected Place vectors and Trip freshness change. IDs,
content, visits, enrichment, routes, measurements and provenance survive. Sparse,
tied and null original orders remain evidence; normalization to 1..N affects only
the two submitted scopes. Canonical Region identity/stored order stays untouched.

## BD2-10: Place order

```json
{
  "expected": { "places": ["<every material original object in this Region>"] },
  "placeIds": ["<every desired Place ID exactly once>"]
}
```

The complete original membership, nullable stored orders and every reordered
Place's material content are checked. Duplicate, foreign, omitted and stale
vectors reject atomically. Only this Region's Place orders and Trip freshness
change; all unrelated fields/collections remain untouched. Bearer ordering may
target owned Unassigned Places; Web ordering retains its canonical restriction
(403). Neither action repairs or changes the canonical Region's own stored order.

## Combined Web intent

The Web PUT sends `{expected, changes, placeIdsByRegion?, conditionToken,
reviewed}`. Select only dirty `name`, `notesHtml`, `iconName`, `markerColor`,
`address`, `location`, `regionId` and explicit `reverseGeocode` intentions.
Without a move, `expected.regionId` and each intended field's exact original are
required; coordinate intent includes original nullable location. With a move,
use the move's complete `expected.place`, `expected.placesByRegion` and vectors;
put the desired destination in `changes.regionId`. It compares all applicable
originals and commits once, without PATCH followed by PUT.

Web retains explicit Address and metadata choices. Manual Address is trimmed,
null means empty, and the established RetainCompatible policy clears the tuple
when normalized Address or coordinates change. Explicit successful reverse
geocoding replaces Address and the complete trusted tuple; it requires original
Address/location evidence, freezes one provider result before domain locking and
never repeats provider work on transaction contention. An unavailable requested
provider uses the existing manual fallback/warning. Provider admission remains
consumed if persistence fails. Ordinary coordinate/move/order actions make zero
provider calls. No bearer Address/provider authoring capability is added.

## Acknowledgements and holds

200 follows commit and returns `contract: 'bd2-1'`, `success`, `data`, `affected`,
`deletedIds` and `warnings`; bearer additionally returns `applied: true`.
Place data is the complete editor Place DTO; order data is
`{regionId, placeOrder}`. Membership/order effects include every changed Place
and exact scoped vectors. Coordinate effects include complete affected Segment
DTOs with fresh route/aggregate conditions, Segment order and visit progress.
Acknowledgements are built inside the transaction before commit and adopted only
after validating the complete target/effect envelope. They are mutation effects,
not a full Trip baseline or a durable receipt.

Malformed/lost replies, disconnects, cancellation at commit and 5xx leave the
attempt unknown. Newer drafts and corrective reversals survive acknowledgement;
retired sessions cannot adopt late replies. Structural attempt facts and holds
survive draft reset within the mounted workspace. A refresh, matching state,
missing target or reset cannot establish noncommit or permit blind replay.
There is no cross-reload recovery promise.

400 `mutation-request-invalid` rejects malformed originals/intent/vectors;
401 rejects invalid authority; concealed 404 does not prove deletion; 403 retains
protected-operation restrictions. Known-unapplied 409 categories are
`mutation-review-required`, `mutation-dependency-stale`, `mutation-contention`
and the rejected unconditional route's `mutation-originals-required`. Keep the
draft and explicitly review an appropriate new base. Never infer noncommit from
an arbitrary network/HTTP failure or automatically retry structural intent.
