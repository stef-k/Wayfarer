# Four-way Region deletion

Issue [#848](https://github.com/stef-k/Wayfarer/issues/848) adds an explicit
preview/confirmed operation. The Region and its own name, notes, cover and center
are always deleted. Places and Areas each independently select `keep` or `delete`.
Vue defaults to Keep/Keep. Empty Regions still require confirmation.

| Places | Areas | Result |
| --- | --- | --- |
| keep | keep | Append both to canonical Unassigned Places |
| keep | delete | Append Places; delete Areas |
| delete | keep | Delete Places; append Areas |
| delete | delete | Delete both child sets |

Kept children retain their IDs and material contents. Kept Areas retain polygon
holes and SRID. Kept Places retain visit associations and all Segment aggregates,
route geometry, measurements and provenance. Deleting Places uses canonical
endpoint-Segment deletion and surviving waypoint reconciliation; retained visit
snapshots become unlinked. Keeping Areas does not preserve deleted Places/routes.

## HTTP contract

Both authorities use the same JSON DTOs and lifecycle implementation:

| Authority | Preview POST | Confirmed POST |
| --- | --- | --- |
| Editor cookie with User role | `/api/trips/{tripId}/editor/regions/{regionId}/deletion-preview` | `/api/trips/{tripId}/editor/regions/{regionId}/deletion` |
| Active incoming bearer token, no cookie fallback | `/api/trips/{tripId}/regions/{regionId}/deletion-preview` | `/api/trips/{tripId}/regions/{regionId}/deletion` |

Both editor POSTs require the `RequestVerificationToken` antiforgery header.
Ownership is checked before parsing; responses use `Cache-Control: no-store`.
Preview accepts exactly `{ "places": "keep", "areas": "keep" }`. Both strings are
required; extra fields, duplicates, numeric values and unsupported choices fail.
Confirmed POST additionally accepts `confirmationToken` from the matching preview.
No client destination, counts, child IDs or authored contents are accepted.

Preview returns contract version 1, Trip/source identity and name, both choices,
`destination: { mode, regionId, name }` (`existing`, `create`, `not-required`),
`regionMetadataLoss: { hasNotes, hasCover, hasCenter }`, and
`confirmation: { token, expiresAt }` (ten minutes). `consequences` contains
`movedPlaces`, `deletedPlaces`, `movedAreas`, `deletedAreas`, `deletedSegments`,
`updatedSegments`, `detachedWaypoints`. Each group has complete `count`, at most
five deterministic sampled `ids`, and `hasMore`; waypoint IDs are
`{ segmentId, placeId }`. Endpoint deletions and waypoint-only changes never overlap.
Preview inserts nothing and calls no provider or SaveChanges.

Success on both adapters is `EditorMutationResult<RegionDeletionResultDto>`:
`{ success, data, affected, deletedIds, warnings }`. Data contains `tripId`,
`sourceRegionId`, both choices, `destinationRegionId`, complete `movedPlaceIds`,
`movedAreaIds`, `updatedSegmentIds`, `detachedWaypointAssociations`. Deleted IDs
are complete. Authoritative slices include changed Region/child/order projections,
fresh surviving Segment aggregate tokens, metadata/timestamp and visit progress.
Slices are projected before commit; success is returned only after commit.

| Status | ProblemDetails `code` | Recovery |
| --- | --- | --- |
| 400 | `region-deletion-request-invalid` | Fix fields or obtain an untampered confirmation |
| 401/403/404 | Authority/role/protected source/ownership boundary | Authenticate or select an owned normal Region |
| 409 | `region-deletion-confirmation-required` | Review supplied fresh preview and explicitly confirm |
| 409 | `region-deletion-preview-stale` | Obtain fresh preview and explicitly confirm again |
| 409 | `unassigned_region_conflict` | Reconcile malformed affected ownership/canonical state |

Cookie antiforgery rejection retains framework behavior. Definite pre-commit
rejections make no domain change. Legacy DELETE routes, their dependency header,
empty-Region behavior and response shapes are unchanged. The new purpose-separated
token cannot authorize legacy DELETE, nor can a legacy token authorize this POST.

## Transaction, confirmation and recovery

Preview uses repeatable-read isolation. Confirmation protects a deterministic hash
of source metadata, complete authored Place/Area contents, geometry bytes/SRID,
Segment scalar contents/provenance, waypoint positions/vertex indices, anchor
coordinates, relevant profile inputs, canonical identity or proposed identity plus
verified absence, applicable destination membership/orders and ordering siblings.
It binds account, Trip, source and both choices; it is not a receipt.

Mutation discovers candidates and holds deterministic row locks through commit:
profiles → Segments → Places → Areas → Regions → Trip, IDs sorted within classes.
Region/Trip and Segment/Place FK parent barriers cover membership insertion, including
legacy writers. Final persisted rediscovery after waits must match the approval
and need no additional earlier-class locks. Changes conflict without retry.

Only the exact name `Unassigned Places` elects a canonical Region, independent of
historical order. Wrong ownership fails closed. Missing destinations are created
inside the transaction only when at least one child is actually kept. A concurrent
winner requires another preview. Places sort by order/null-last/ID; Areas use
order/null-last/database-collated name/ID. Append then normalize only kept child
types; normalize Segment order only when endpoint deletion occurs.

Caller EF state is isolated from saving the operation. Pre-commit recovery restores
Areas, siblings, destination absence and caller pending work; later saves cannot
resurrect staged changes. The current locked Trip is stamped by property through a
narrow save path, preserving independent Trip metadata. This guarantee applies to
this new operation; #853 retains ownership of broader writer correction.

## Unknown outcomes and editor behavior

Vue never probes with DELETE or automatically replays a mutation. Each selection
change invalidates the token; superseded previews cannot replace current evidence.
Final activation is disabled until a matching fresh preview arrives. Cancellation
before dispatch performs no mutation and leaves drafts intact. Successful slices
apply before the deleted Region selection/draft is cleared.

Before dispatch, Vue retains minimal intent in local browser storage scoped to
server/account/Trip/source. It includes dispositions and an attempt ID, without
credentials, authored child content or confirmation token. Lost/unclassified
responses show “Outcome unknown — refresh to inspect the current Trip.” A refresh
can show facts, but does not establish the earlier request's terminal state or
release the hold. A matched success clears it; otherwise explicit terminal-state
reconciliation is required. No durable receipt/status API is added. Maintainers
must establish terminality from authoritative request evidence before clearing a
held browser intent; absence of a Region alone is insufficient.

Moved Areas retain edit/delete/search/map access. Existing owned Areas can be
ordered within the canonical Region using complete-membership validation. Direct
Area creation and the canonical Region's metadata/reorder/delete remain protected.
Issue #847 separately owns Unassigned Place endpoint/waypoint picker eligibility;
full integrated Segment picker UX acceptance remains dependent on that issue.
