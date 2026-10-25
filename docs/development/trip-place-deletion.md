---
title: Protected Place deletion
---

# Protected Place deletion

BD2-12 ([#853](https://github.com/stef-k/Wayfarer/issues/853)) requires a current
content preview and explicit confirmation for **every** Place, including a Place
with no dependents. Both adapters use one lifecycle transaction.

| Authority | Preview POST | Confirmed POST |
| --- | --- | --- |
| Active incoming bearer, no cookie fallback | `/api/trips/{tripId}/conditional/places/{placeId}/deletion-preview` | `/api/trips/{tripId}/conditional/places/{placeId}/deletion` |
| Cookie with User role and ownership | `/api/trips/{tripId}/editor/places/{placeId}/deletion-preview` | `/api/trips/{tripId}/editor/places/{placeId}/deletion` |

Both cookie POSTs require `RequestVerificationToken`. Both adapters return
`Cache-Control: no-store` and check the complete owned scope before parsing.
Preview accepts exactly `{}`. Confirmation accepts only `confirmationToken`;
unknown or duplicate fields, non-string tokens and malformed JSON reject.

## Mobile request examples

Obtain the current consequences without mutation:

```http
POST /api/trips/{tripId}/conditional/places/{placeId}/deletion-preview
Authorization: Bearer <incoming-token>
Content-Type: application/json

{}
```

Preview returns `contractVersion: 1`, `tripId`, `placeId`, `regionId`,
`placeName`, `deletedSegments`, `updatedSegments`, `detachedWaypoints`,
`unlinkedVisits`, `deletedCandidates`, `placeOrderIds`, `segmentOrderIds`, and
`confirmation: {token, expiresAt}`. Effect groups contain `{count, ids, hasMore}`;
Place deletion publishes **complete** effect identities (`hasMore: false`).
Waypoint identities are `{segmentId, placeId}`. Place ordering lists the surviving
siblings in their resulting order. Segment ordering is null unless endpoint
Segments will be deleted, in which case it lists all resulting surviving Segments.
The opaque token expires after ten minutes. It is consent, never authority or a
receipt. No private route/profile/provenance content is published by preview.

After the user reviews these exact consequences and explicitly confirms:

```http
POST /api/trips/{tripId}/conditional/places/{placeId}/deletion
Authorization: Bearer <incoming-token>
Content-Type: application/json

{"confirmationToken":"<token-from-that-preview>"}
```

Success is the same `EditorMutationResult<PlaceDeletionResultDto>` envelope for
both authorities: `{success, data, affected, deletedIds, warnings}`. `data` contains
`tripId`, `placeId`, `regionId`, complete `updatedSegmentIds` and
`detachedWaypointAssociations`. Deleted IDs contain the target Place and exact
endpoint-dependent Segments; Region/Area/Tag deletion arrays are empty.
Affected slices contain all reordered sibling Places and their complete order,
updated/reordered surviving Segments with fresh route/aggregate tokens, applicable
Segment order, current Trip metadata/freshness and visit progress. Unrelated
Region/Area/Tag/options slices remain empty/null. Projection happens before commit;
success is sent only after confirmed commit.

## Consent, locking and effects

Approval binds server protection keys, owner, Trip, typed target, expiry, full
mapped target and parent contents, source membership/stored nullable orders,
endpoint-dependent and waypoint-only Segments, full route/measurement/provenance
contents, waypoint positions/indices, anchor coordinates and parent identities,
linked profile contents, relevant complete Segment ordering, and visit/candidate
identities. Same-ID edits cannot be replaced by identity-only approval.

The transaction locks profiles → Segments → Places → Regions → Trip, with sorted
IDs in each class, then the active account and incoming bearer credential where
applicable. It rediscovers persisted inputs after all waits. Changed contents,
new dependencies, missing required earlier-class locks or expired approval reject
before writes. Place/Segment/Region/Trip parent barriers also fence new membership.
There is no automatic destructive retry.

The established lifecycle deletes endpoint Segments once (including loops),
detaches deleted saved-Place waypoints from surviving Segments, retains anonymous
custom vertices, reconciles fallback routes/measurements and invalidates affected
route provenance without provider calls. It normalizes only source Place order
and, when endpoint Segments are deleted, Trip Segment order. Database FK behavior
retains visit snapshots with `PlaceId = null` and cascades ephemeral visit
candidates. These changes and property-only, nondecreasing current Trip freshness
commit together. Caller pending work is isolated; failed transactions restore
caller state or retire an incoherent context using existing lifecycle recovery.

## Rejections and uncertain outcomes

| Status/code | Meaning |
| --- | --- |
| 400 `place-deletion-request-invalid` | Invalid request or tampered/cross-purpose token |
| 401/403 | Invalid/currently revoked authority or forbidden role |
| Concealed 404 | Target/Trip unavailable in the owned scope; no dependency disclosure |
| 409 `place-deletion-confirmation-required` | Review preview and explicitly confirm, including an empty footprint |
| 409 `place-deletion-preview-stale` | Expiry or material/dependency/order drift; obtain fresh preview and explicitly confirm again |
| 409 `mutation-dependency-stale` | Malformed affected ownership/graph; review current state |
| 409 `mutation-confirmation-required` | Unconditional DELETE adapter rejects without mutation |

The participating Web/bearer Place and Region DELETE adapters cannot execute
identity-only or unconditional deletion. Region deletion continues to use the
existing [four-way preview/confirmed POST contract](region-deletion.md).
Canonical Region deletion stays forbidden. No client falls back to DELETE.

Definite precommit rejections leave the graph unchanged. A lost response, 5xx,
connection failure or ambiguous commit does **not** prove rollback. The editor
retains its immutable attempt in the existing Place draft hold owner, blocks
replay and validates exact response scope before adoption. Cancel before dispatch
preserves drafts; a successful response clears only the same unchanged target
draft. Newer intent, superseding sessions and unrelated drafts survive. Reset,
refresh, absence of the target and leaving without saving cannot prove noncommit
or undo a request. Place holds are scoped to the mounted workspace; there is no
cross-reload recovery promise, durable receipt or automatic recovery API. Mobile
must retain its own unknown intent under MD03/MD05 rather than replay destruction.

## BD2-13 integration evidence boundary

The delivered #848 four-way Place/Area matrix, conservative kept-child fingerprint,
canonical destination/append rules and transaction recovery are reused. The
integration adds final active-account/bearer checks, nondecreasing freshness,
closed unconditional adapters and checked client acknowledgements/newer-draft
retention. Focused routed tests cover prepared Place moves, source/destination
Place ordering and Region ordering against deletion in both save orders, plus
revocation during a physical Region wait. Global C1/C2 reconciliation and
independent C5 final acceptance remain separate; #853 stays open.
